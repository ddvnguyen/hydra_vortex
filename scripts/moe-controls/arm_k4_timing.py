#!/usr/bin/env python3
"""K_engine/w_engine timing arms (design note section 20). TIMING ONLY: k=4 output is meaningless.

Owner waiver (2026-09-22, architect s134): `--override-kv qwen4exp.expert_used_count=int:4` is permitted for these
arms only. Every k != 10 record is tagged TIMING_ONLY_K_OVERRIDE and must never enter a table that is not the
K_engine/w_engine fit. k = 10 arms carry no override (production config).
Usage: arm_k4_timing.py <out_dir> <arm:kK,...> <reps_per_boot>   e.g. ncm4:k10,ncm4:k4,allhost:k4
Arms: allhost | ncmK (K = expert layers kept on the GPU, taken from the last layers). PFILE / MIN_TOKENS from env.
"""
import hashlib, json, os, re, subprocess, sys, threading, time, urllib.request

WORK = "/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next"
BIN = f"{WORK}/src/llama-cpp/build-demand/bin"
MODEL = "/mnt/SSD/qwen3.8-flash-next-apex-mini/Qwen3.8-Flash-Next-APEX-I-Mini-00001-of-00006.gguf"
PFILE = os.environ.get("PFILE", "/tmp/opencode/controls/k4timing/prompt-short.txt")
DEV, CTX, PORT, OUTTOK = "1", 81920, 18437, 200
FOREIGN_PID = 3829406
N_LAYERS = 48
MIN_TOKENS = int(os.environ.get("MIN_TOKENS", "150"))
KV_KEY = "qwen4exp.expert_used_count"
LINK_MIN_GBPS = 3.0
SHARDS = sorted(__import__("glob").glob("/mnt/SSD/qwen3.8-flash-next-apex-mini/*.gguf"))


def pagecache_gb():
    tot = 0
    for line in subprocess.run(["fincore", "-b", "-n"] + SHARDS, capture_output=True, text=True).stdout.splitlines()[0:]:
        parts = line.split()
        if parts and parts[0].isdigit():
            tot += int(parts[0])
    return round(tot / 1e9, 1)

COMMON = ["-m", MODEL, "--split-mode", "layer", "-fit", "off", "-ngl", "99", "-c", str(CTX), "--parallel", "1",
          "--flash-attn", "on", "--jinja", "-t", "6", "--experimental-logs", "--load-mode", "none",
          "--spec-type", "none"]  # --decode-overlap dropped: needs a single-backend graph, fails with any host experts
PLE_OT = "per_layer_token_embd=CPU"


def arm_flags(arm):
    if arm in ("cache42", "cache42L"):   # L = per-plan ledger on (GGML_CUDA_MOE_LEDGER)
        return ["--override-tensor", PLE_OT, "--moe-expert-cache-size", "42"]
    if arm in ("cache42O", "cache42OL"):                # O = cache42 + --decode-overlap (on-file baseline flavour)
        return ["--override-tensor", PLE_OT, "--moe-expert-cache-size", "42", "--decode-overlap"]
    if arm == "allhost":
        return ["--override-tensor", PLE_OT, "--cpu-moe", "--moe-expert-cache-size", "0"]
    m = re.fullmatch(r"ncm(\d+)", arm)
    if m:
        return ["--override-tensor", PLE_OT, "--n-cpu-moe", str(N_LAYERS - int(m.group(1))),
                "--moe-expert-cache-size", "0"]
    m = re.fullmatch(r"ot(\d+)", arm)
    if m:
        k = int(m.group(1))
        layers = "|".join(str(i) for i in range(N_LAYERS - k, N_LAYERS))
        return ["--override-tensor", rf"{PLE_OT},blk\.({layers})\.ffn_.*_exps.*=CUDA0,ffn_.*_exps.*=CPU",
                "--moe-expert-cache-size", "0"]
    raise SystemExit(f"unknown arm {arm}")


def sh(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True).stdout.strip()


def gpu_rows():
    out = sh("nvidia-smi --query-gpu=index,memory.used,utilization.gpu,pcie.link.gen.current,"
             "pcie.link.width.current --format=csv,noheader,nounits")
    rows = {}
    for line in out.splitlines():
        p = [x.strip() for x in line.split(",")]
        if len(p) == 5:
            rows[int(p[0])] = dict(mem=int(p[1]), util=int(p[2]), gen=int(p[3]), width=int(p[4]))
    return rows


def cpu_times():
    with open("/proc/stat") as fh:
        v = list(map(int, fh.readline().split()[1:]))
    return sum(v), v[3] + v[4]  # total, idle+iowait


def pid_ticks(pid):
    try:
        with open(f"/proc/{pid}/stat") as fh:
            f = fh.read().rsplit(")", 1)[1].split()
        return int(f[11]) + int(f[12]), f[0]
    except (FileNotFoundError, IndexError, ProcessLookupError):
        return None, None


class Monitor(threading.Thread):
    def __init__(self, server_pid):
        super().__init__(daemon=True)
        self.stop_evt = threading.Event()
        self.samples = []
        self.server_pid = server_pid

    def run(self):
        hz = os.sysconf("SC_CLK_TCK")
        prev_t, prev_idle = cpu_times()
        prev_f, _ = pid_ticks(FOREIGN_PID)
        prev_s, _ = pid_ticks(self.server_pid)
        t_prev = time.monotonic()
        while not self.stop_evt.wait(1.0):
            now = time.monotonic()
            dt = now - t_prev
            t_prev = now
            tot, idle = cpu_times()
            busy = 100.0 * (1 - (idle - prev_idle) / max(1, tot - prev_t))
            prev_t, prev_idle = tot, idle
            f_t, f_state = pid_ticks(FOREIGN_PID)
            s_t, _ = pid_ticks(self.server_pid)
            f_pct = 100.0 * (f_t - prev_f) / hz / dt if f_t is not None and prev_f is not None else None
            s_pct = 100.0 * (s_t - prev_s) / hz / dt if s_t is not None and prev_s is not None else None
            prev_f, prev_s = f_t, s_t
            g = gpu_rows()
            mem_avail = 0
            with open("/proc/meminfo") as fh:
                for line in fh:
                    if line.startswith("MemAvailable"):
                        mem_avail = int(line.split()[1]) // 1024
            self.samples.append(dict(t=time.time(), cpu_busy=busy, foreign_pct=f_pct, foreign_state=f_state,
                                     server_pct=s_pct, mem_avail_mib=mem_avail,
                                     load1=float(open("/proc/loadavg").read().split()[0]), gpu=g))


def stream_request(port, prompt):
    body = json.dumps({"model": "x", "messages": [{"role": "user", "content": prompt}], "max_tokens": OUTTOK,
                       "temperature": 0, "stream": True, "timings_per_token": True,
                       "cache_prompt": True}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", data=body,
                                 headers={"Content-Type": "application/json"})
    t0 = time.monotonic()
    arrivals, text, timings = [], [], None
    with urllib.request.urlopen(req, timeout=3600) as r:
        for raw in r:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data: "):
                continue
            payload = line[6:]
            if payload == "[DONE]":
                break
            try:
                d = json.loads(payload)
            except ValueError:
                continue
            if d.get("timings"):
                timings = d["timings"]
            ch = (d.get("choices") or [{}])[0]
            delta = (ch.get("delta") or {})
            piece = (delta.get("content") or "") + (delta.get("reasoning_content") or "")
            if piece:
                arrivals.append((time.monotonic() - t0) * 1000.0)
                text.append(piece)
    return arrivals, "".join(text), timings


def run_arm(out_dir, spec, reps):
    arm, kspec = spec.split(":")
    k_used = int(kspec.lstrip("k"))
    tag = f"{arm}-k{k_used}"
    log_path = f"{out_dir}/server-{tag}.log"
    env = dict(os.environ)
    env.pop("GGML_CUDA_ENABLE_UNIFIED_MEMORY", None)
    env["CUDA_VISIBLE_DEVICES"] = DEV
    if arm.endswith("L"):
        env["GGML_CUDA_MOE_LEDGER"] = f"{out_dir}/ledger-{arm}.csv"
    env["LD_LIBRARY_PATH"] = f"{BIN}:/opt/software/cuda/13.2.1/lib64:" + env.get("LD_LIBRARY_PATH", "")
    flags = COMMON + arm_flags(arm) + ["--host", "127.0.0.1", "--port", str(PORT)]
    if k_used != 10:
        flags += ["--override-kv", f"{KV_KEY}=int:{k_used}"]
    rec = dict(tag=tag, arm=arm, k_used=k_used, TIMING_ONLY_K_OVERRIDE=(k_used != 10), flags=" ".join(flags), ctx=CTX, dev=DEV, out_tokens_requested=OUTTOK,
               start=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), reps=[])
    # The foreign Qwopus server (-ngl 0) still owns a CUDA context on both cards; it is a known constant offset.
    apps = [x.strip() for x in sh("nvidia-smi --query-compute-apps=pid --format=csv,noheader").splitlines()]
    busy = ",".join(sorted({a for a in apps if a and a != str(FOREIGN_PID)}))
    rec["foreign_gpu_ctx"] = sh(f"nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader | grep {FOREIGN_PID}").replace(chr(10), " ; ")
    if busy:
        rec.update(void=f"GPU busy before leg: {busy}")
        return rec
    rec["foreign_state_start"] = pid_ticks(FOREIGN_PID)[1]
    # covariates: 3060 H2D bandwidth probe + link state, and model page-cache residency (before boot)
    bw_out = sh(f"CUDA_VISIBLE_DEVICES={DEV} /tmp/opencode/controls/bw/bw")
    m = re.findall(r"pass1 pinned: best ([0-9.]+) GB/s", bw_out)
    rec["h2d_pinned_gbps"] = float(m[0]) if m else None
    rec["link_sysfs"] = sh("cat /sys/bus/pci/devices/0000:02:00.0/current_link_speed")
    rec["pagecache_resident_gb_before"] = pagecache_gb()
    if (rec["h2d_pinned_gbps"] or 0) < LINK_MIN_GBPS:
        rec["link_degraded"] = True
        if os.environ.get("ALLOW_DEGRADED_LINK") != "1":
            rec.update(void=f"3060 H2D {rec['h2d_pinned_gbps']} GB/s < {LINK_MIN_GBPS} (set ALLOW_DEGRADED_LINK=1 to record anyway)")
            return rec
    subprocess.run("cat " + " ".join(SHARDS) + " > /dev/null", shell=True)   # prewarm page cache
    rec["pagecache_resident_gb_prewarmed"] = pagecache_gb()
    rec["gpu_before"] = gpu_rows()
    with open(log_path, "w") as lf:
        srv = subprocess.Popen([f"{BIN}/llama-server"] + flags, stdout=lf, stderr=subprocess.STDOUT, env=env)
    mon = Monitor(srv.pid)
    mon.start()
    t_boot = time.monotonic()
    ready = False
    while time.monotonic() - t_boot < 900:
        if srv.poll() is not None:
            break
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/health", timeout=3) as r:
                if b'"ok"' in r.read():
                    ready = True
                    break
        except Exception:
            pass
        time.sleep(2)
    rec["load_s"] = round(time.monotonic() - t_boot, 1)
    if not ready:
        rec["void"] = "server failed to become ready"
        mon.stop_evt.set()
        srv.terminate()
        srv.wait(timeout=60)
        rec["log_tail"] = sh(f"tail -20 {log_path}")
        return rec
    time.sleep(3)
    rec["vram_after_load_mib"] = gpu_rows().get(1, {}).get("mem")
    prompt = open(PFILE).read()

    def avg(key, rows):
        v = [r[key] for r in rows if r.get(key) is not None]
        return round(sum(v) / len(v), 1) if v else None

    for rep in range(1, reps + 1):
        rr = dict(rep=rep, state="cold (fresh boot)" if rep == 1 else "warm (same server, prefix reused if cache_n>0)")
        t_req0 = time.time()
        try:
            arrivals, text, tm = stream_request(PORT, prompt)
        except Exception as exc:
            rr["void"] = f"request failed: {exc!r}"
            arrivals, text, tm = [], "", None
        t_req1 = time.time()
        n = len(arrivals)
        rr.update(tokens=n, output_sha=hashlib.sha256(text.encode()).hexdigest()[:16], request_s=round(t_req1 - t_req0, 1))
        if tm:
            rr.update(prompt_n=tm.get("prompt_n"), cache_n=tm.get("cache_n"), predicted_n=tm.get("predicted_n"),
                      prefill_tps=tm.get("prompt_per_second"), prefill_ms=tm.get("prompt_ms"),
                      decode_tps_server=tm.get("predicted_per_second"), decode_ms_server=tm.get("predicted_ms"))
        if n >= 2:
            rr["decode_tps_stream"] = round((n - 1) / ((arrivals[-1] - arrivals[0]) / 1000.0), 3)
        if n < MIN_TOKENS:
            rr["void"] = f"generated {n} < {MIN_TOKENS} tokens"
        dec = [s for s in mon.samples if t_req0 < s["t"] <= t_req1]
        tail_n = max(1, int((rr.get("decode_ms_server") or 20000) / 1000.0))
        dec_tail = dec[-tail_n:] if dec else []
        gpu1_req = [s["gpu"][1]["mem"] for s in dec if 1 in s["gpu"]]
        rr["vram_peak_mib_this_request"] = max(gpu1_req, default=None)
        rr["host_during_decode"] = dict(
            cpu_busy_pct=avg("cpu_busy", dec_tail), server_cpu_pct=avg("server_pct", dec_tail),
            foreign_cpu_pct=avg("foreign_pct", dec_tail), load1=avg("load1", dec_tail),
            gpu1_util_pct=avg("util", [dict(util=s["gpu"][1]["util"]) for s in dec_tail if 1 in s["gpu"]]),
            gpu1_pcie=sorted({(s["gpu"][1]["gen"], s["gpu"][1]["width"]) for s in dec_tail if 1 in s["gpu"]}))
        rec["reps"].append(rr)
        print("REP", tag, {k: rr.get(k) for k in ("rep", "prompt_n", "cache_n", "predicted_n", "decode_tps_server",
                                                  "decode_tps_stream", "prefill_tps", "request_s", "void")}, flush=True)
        time.sleep(3)
    mon.stop_evt.set()
    mon.join(timeout=5)
    srv.terminate()
    try:
        srv.wait(timeout=90)
    except subprocess.TimeoutExpired:
        srv.kill()
        rec["shutdown"] = "killed"
    gpu1 = [s["gpu"][1] for s in mon.samples if 1 in s["gpu"]]
    rec["vram_peak_mib"] = max((g["mem"] for g in gpu1), default=None)
    rec["mem_avail_min_mib"] = min((s["mem_avail_mib"] for s in mon.samples), default=None)
    rec["foreign_states_seen"] = sorted({str(s["foreign_state"]) for s in mon.samples})
    rec["foreign_max_pct"] = round(max((s["foreign_pct"] or 0 for s in mon.samples), default=0), 1)
    rec["cpu_busy_max_pct"] = round(max((s["cpu_busy"] for s in mon.samples), default=0), 1)
    log = open(log_path, errors="replace").read()
    pools = [float(x) for x in re.findall(r"CUDA_MoE_Cache_Pool\[[^\]]*\]\s*=\s*([\d.]+) MiB", log)]
    rec["pagecache_resident_gb_after"] = pagecache_gb()
    rec["cache_pool_total_mib"] = round(sum(pools), 1) if pools else None
    rec["log_cache_lines"] = [l[:200] for l in log.splitlines() if "moe-cache" in l][-8:]
    rec["log_errors"] = [l[:200] for l in log.splitlines() if re.search(r"out of memory|cudaMalloc.*failed|CUDA error", l)][:4]
    return rec


def main():
    out_dir, arms, reps = sys.argv[1], sys.argv[2].split(","), int(sys.argv[3])
    os.makedirs(out_dir, exist_ok=True)
    prov = dict(git=sh(f"git -C {WORK}/src/llama-cpp rev-parse HEAD"),
                libllama=sh(f"sha256sum {BIN}/libllama.so* | head -1 | cut -c1-16"),
                libggml_cuda=sh(f"sha256sum {BIN}/libggml-cuda.so* | head -1 | cut -c1-16"),
                prompt_bytes=os.path.getsize(PFILE))
    print("PROVENANCE", json.dumps(prov), flush=True)
    for arm in arms:
        rec = run_arm(out_dir, arm, reps)
        rec["provenance"] = prov
        with open(f"{out_dir}/results-k4-timing.jsonl", "a") as fh:
            fh.write(json.dumps(rec) + "\n")
        print("ARM_DONE", arm, "vram_after_load", rec.get("vram_after_load_mib"), "peak", rec.get("vram_peak_mib"),
              rec.get("void"), flush=True)
        time.sleep(8)


if __name__ == "__main__":
    main()

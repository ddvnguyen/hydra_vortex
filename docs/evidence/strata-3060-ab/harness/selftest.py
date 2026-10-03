#!/usr/bin/env python3
"""Self-test for bench3060.py + parse_arm_logs.py (no GPU, scratch port).

1. Embedded mock SSE /v1/chat/completions server on 127.0.0.1:18999.
2. Runs bench3060.py --reps 1 --warmup against the real prompts manifest.
3. Asserts bench JSON structure/fields.
4. Builds synthetic strata + fork engine logs matching the request count and
   asserts parse_arm_logs.py joins + alignment (exit 0, match=true).
"""
import json
import os
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
EV = os.path.abspath(os.path.join(HERE, "..", "..",
                                  "docs", "evidence", "strata-3060-ab"))
OUT = os.path.join(HERE, "selftest-out")
PORT = 18999

class Mock(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        req = json.loads(self.rfile.read(n))
        assert req["temperature"] == 0 and req.get("stream"), req
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        ptok = len(req["messages"][-1]["content"]) // 4 + 1

        def sse(obj):
            self.wfile.write(("data: " + json.dumps(obj) + "\n\n").encode())

        for i, piece in enumerate(["Hel", "lo ", "world", "!"]):
            delta = {"content": piece} if i else {"content": "", "reasoning_content": "think"}
            sse({"choices": [{"delta": {"content": piece} if i else delta, "finish_reason": None}]})
            time.sleep(0.01)
        sse({"choices": [{"delta": {}, "finish_reason": "length"}]})
        sse({"choices": [], "usage": {"prompt_tokens": ptok, "completion_tokens": 4,
                                      "total_tokens": ptok + 4,
                                      "prompt_tokens_details": {"cached_tokens": ptok // 2}}})
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

def main():
    os.makedirs(OUT, exist_ok=True)
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Mock)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    time.sleep(0.2)

    bench = os.path.join(OUT, "SELF.bench.json")
    if os.path.exists(bench):
        os.remove(bench)
    r = subprocess.run([
        sys.executable, os.path.join(HERE, "bench3060.py"),
        "--engine", "strata", "--arm", "SELF", "--port", str(PORT),
        "--prompts", EV + "/prompts", "--outdir", OUT,
        "--reps", "1", "--max-tokens", "64", "--warmup",
    ], capture_output=True, text=True, timeout=300)
    print(r.stdout)
    assert r.returncode == 0, "bench rc=%d err=%s" % (r.returncode, r.stderr)
    with open(bench) as f:
        b = json.load(f)
    assert b["status"] == "ok", b
    assert b["requests_total"] == 7, b["requests_total"]  # warmup + 3 + 3
    ids = [c["id"] for c in b["cells"]]
    assert ids == ["p1k", "p4k", "p12k", "prefix"], ids
    p1k = b["cells"][0]["reps"][0]
    assert p1k["ttft_s"] is not None and p1k["prefill_tps"] and p1k["decode_tps"], p1k
    assert p1k["cached_tokens"] is not None, p1k
    assert b["cells"][3]["reps"][2]["turn"] == 3, b["cells"][3]
    assert b["manifest_sha256_of_sha256sums"], "manifest sha missing"
    print("bench selftest OK")

    # ---- synthetic strata log (7 requests: warmup, p1k, p4k, p12k, prefix x3) ----
    slog = os.path.join(OUT, "SELF-strata.log")
    with open(slog, "w") as f:
        f.write("strata generate: expert cache auto: 9.00 GiB free, 700 MiB reserved (+218 MiB for the draft head) -> 3669 slots\n")
        f.write("strata generate: expert cache 4775 slots, 9.10 GiB of VRAM; policy is\n")
        f.write("strata serve: prompt chunk auto: 8192 tokens\n")
        f.write("strata serve: the prompt path borrows 2344 cache slots (4.43 GiB)\n")
        f.write("strata serve: 338 MiB of VRAM free with everything loaded\n")
        for i in range(7):
            prompt = 100 + i
            reused = 0 if i < 4 else prompt // 2
            f.write("strata serve: prompt %d tokens = %d reused + %d read in 402 ms (12.4 tok/s), "
                    "256 generated in 9661 ms (26.5 tok/s), drafts accepted 0 of 0, 1 checkpoints\n"
                    % (prompt, reused, prompt - reused))
            f.write("strata serve: decode expert cache hit rate: 80.4%% (%d hits / %d lookups)\n"
                    % (1000 + i, 1500 + i))
            if i >= 4:
                f.write("strata serve: suffix drafts: 9 windows, 26 of 27 drafts accepted\n")
            f.write("strata decode timing: 10 windows, avg T 1.00, 1.00 tokens/window, 20.00 ms/window = "
                    "10.00 ms/window (CPU 5.00 + stage 5.00) + commit/emit 2.00 + draft 0.50; x\n")
            f.write("strata prefill timing: 100 tokens, GPU timeline 400.0 ms, wall 401.0 ms: y\n")
    rs = subprocess.run([
        sys.executable, os.path.join(HERE, "parse_arm_logs.py"),
        "--engine", "strata", "--arm", "SELF", "--log", slog,
        "--bench", bench, "--out", os.path.join(OUT, "SELF.strata.logparse.json"),
    ], capture_output=True, text=True)
    print(rs.stdout, rs.stderr)
    assert rs.returncode == 0, rs.returncode
    with open(os.path.join(OUT, "SELF.strata.logparse.json")) as f:
        sp = json.load(f)
    assert sp["align"]["match"] is True, sp["align"]
    assert sp["parsed"]["fill"] == {"slots": 4775, "gib": 9.10}, sp["parsed"]["fill"]
    assert len(sp["parsed"]["joined"]) == 7, len(sp["parsed"]["joined"])
    assert sp["parsed"]["draft_cost"]["avg_draft_ms_per_window"] == 0.5, sp["parsed"]["draft_cost"]
    print("strata parse selftest OK")

    # ---- synthetic fork log ----
    flog = os.path.join(OUT, "SELF-fork.log")
    with open(flog, "w") as f:
        for i in range(7):
            f.write("1.00.%03d.000 I slot print_timing: id  0 | task %d | prompt eval time =   40626.94 ms /  %4d tokens (   15.78 ms per token,    63.38 tokens per second)\n" % (i, i, 100 + i))
            f.write("1.00.%03d.001 I slot print_timing: id  0 | task %d |        eval time =    9000.00 ms /  256 tokens (    35.00 ms per token,     28.44 tokens per second)\n" % (i, i))
            f.write("1.00.%03d.002 I slot print_timing: id  0 | task %d |       total time =   49626.94 ms /  356 tokens\n" % (i, i))
            f.write("1.00.%03d.003 I slot print_timing: id  0 | task %d |    graphs reused =          1\n" % (i, i))
        f.write("moe-cache-phase: phase=prefill ops=720 l1_hits=55336 l1_misses=29028 l1_evictions=13668 l1_hit_rate=65.59% h2d_copies=29028 h2d_mib=13382.20 h2d_enqueue_ms=38.011 prefetch_hits=50550\n")
        f.write("moe-cache-phase: phase=decode ops=100 l1_hits=900 l1_misses=100 l1_evictions=0 l1_hit_rate=90.00% h2d_copies=100 h2d_mib=100.00 h2d_enqueue_ms=1.0 prefetch_hits=0\n")
        f.write("moe-cache-experts: tensors=120 experts=30720 unique=22371 unique_pct=72.82 accesses=84364 first_touches=22371\n")
    rf = subprocess.run([
        sys.executable, os.path.join(HERE, "parse_arm_logs.py"),
        "--engine", "fork", "--arm", "SELFF", "--log", flog,
        "--bench", bench, "--out", os.path.join(OUT, "SELF.fork.logparse.json"),
    ], capture_output=True, text=True)
    print(rf.stdout, rf.stderr)
    assert rf.returncode == 0, rf.returncode
    with open(os.path.join(OUT, "SELF.fork.logparse.json")) as f:
        fp = json.load(f)
    assert fp["align"]["match"] is True, fp["align"]
    assert len(fp["parsed"]["joined"]) == 7
    assert fp["parsed"]["phase"]["decode"]["l1_hit_rate_pct"] == 90.0
    assert fp["parsed"]["experts"]["tensors"] == 120
    # derived cached = prompt_tokens - prompt_eval_tokens (mock: cached = ptok//2)
    j0 = fp["parsed"]["joined"][1]  # p1k rep0 (after warmup)
    assert j0["derived_cached_tokens"] is not None
    print("fork parse selftest OK")

    srv.shutdown()
    print("ALL SELFTESTS PASSED")
    return 0

if __name__ == "__main__":
    sys.exit(main())

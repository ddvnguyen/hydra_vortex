#!/usr/bin/env bash
# Stage 0g item 2: VALID decode rate from the server's own timings.
# Uses /v1/chat/completions streaming and reads timings.predicted_per_second, which
# excludes prefill. (Stage 0f's decode_tps was invalid: it divided by the whole request
# wall including prefill.) 256 generated tokens after the p4k prefill, -ub 2048/4096/8192,
# n=3 each, rotating order.
set -u
HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd -- "$HERE/../../.." && pwd)
EV=/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/.local/stage0g
mkdir -p "$EV"
export TMPDIR="$WT/.local/tmp"; mkdir -p "$TMPDIR"
BIN=/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/.local/stage1b-ci/34068ff9/bin
M=/mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
P4K=/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/.local/wt-stage0/docs/evidence/strata-gap/stage0/prompts/p4k.txt
export CUDA_VISIBLE_DEVICES=1
export LD_LIBRARY_PATH="$BIN:/opt/software/cuda/13.2.1/lib64"
OUT="$EV/decode.jsonl"; : > "$OUT"

for r in 1 2 3; do
  case $r in 1) ORD="2048 4096 8192";; 2) ORD="4096 8192 2048";; 3) ORD="8192 2048 4096";; esac
  for UB in $ORD; do
    LBL="dec-ub${UB}-r${r}"; LOG="$EV/$LBL.log"; : > "$LOG"
    "$BIN/llama-server" -m "$M" --split-mode layer -fit off -ngl 99 \
      --n-cpu-moe 99 --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0 \
      -c 16384 --parallel 1 --flash-attn on --jinja -t 6 --experimental-logs --load-mode none \
      --ple-prefill -b "$UB" -ub "$UB" --host 127.0.0.1 --port 8093 --alias gsq-rco-iq3_s \
      --spec-type none -lv 5 >> "$LOG" 2>&1 &
    SRV=$!
    OK=0
    for _ in $(seq 1 900); do curl -fsS -m 2 http://127.0.0.1:8093/health >/dev/null 2>&1 && { OK=1; break; }; sleep 1; done
    if [ "$OK" != 1 ]; then echo "[$LBL] STARTUP FAILED"; kill -9 $SRV 2>/dev/null; continue; fi
    sleep 5
    VR=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -i 1)
    CB=$(grep -a -oE 'CUDA0 compute buffer size is [0-9.]+ MiB' "$LOG" | tail -1)
    python3 - "$P4K" "$UB" "$r" "$VR" "$CB" "$OUT" <<'PY'
import json,sys,urllib.request,hashlib
p4k,ub,rnd,vram,cbuf,out=sys.argv[1:7]
body=json.dumps({"model":"gsq-rco-iq3_s","messages":[{"role":"user","content":open(p4k).read()}],
                 "max_tokens":256,"temperature":0,"stream":True,"stream_options":{"include_usage":True},
                 "cache_prompt":False}).encode()
rq=urllib.request.Request("http://127.0.0.1:8093/v1/chat/completions",body,
                          {"content-type":"application/json"})
txt=[];tim=None;usage=None
with urllib.request.urlopen(rq,timeout=1800) as resp:
    for raw in resp:
        line=raw.decode("utf-8","replace").strip()
        if not line.startswith("data:"): continue
        payload=line[5:].strip()
        if payload=="[DONE]": break
        try: d=json.loads(payload)
        except Exception: continue
        for ch in d.get("choices") or []:
            de=(ch.get("delta") or {})
            txt.append(de.get("content") or de.get("reasoning_content") or "")
        if d.get("timings"): tim=d["timings"]
        if d.get("usage"): usage=d["usage"]
full="".join(txt)
rec={"ub":int(ub),"round":int(rnd),"vram_mib":int(vram),"compute_buffer":cbuf,
     "predicted_n":(usage or {}).get("completion_tokens"),
     "predicted_ms":(tim or {}).get("predicted_ms"),
     "predicted_per_second":(tim or {}).get("predicted_per_second"),
     "prompt_ms":(tim or {}).get("prompt_ms"),
     "prompt_per_second":(tim or {}).get("prompt_per_second"),
     "text_len":len(full),
     "coarse_coherent":bool(full.strip()) and any(c.isalnum() for c in full) and len(set(full.strip()))>8,
     "sha12":hashlib.sha256(full.encode("utf-8","replace")).hexdigest()[:12]}
open(out,"a").write(json.dumps(rec)+"\n")
print("[ub%s r%s] decode predicted_per_second=%s tok/s  n=%s  prompt_per_second=%s  coherent=%s"%(
    ub,rnd,rec["predicted_per_second"],rec["predicted_n"],rec["prompt_per_second"],rec["coarse_coherent"]),flush=True)
PY
    kill -INT $SRV 2>/dev/null; sleep 3; kill -9 $SRV 2>/dev/null; sleep 2
  done
done
echo "DECODE SWEEP DONE"
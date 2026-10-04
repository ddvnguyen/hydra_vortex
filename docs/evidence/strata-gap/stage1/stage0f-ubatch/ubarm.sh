#!/usr/bin/env bash
# Stage 0f: paired interleaved -ub sweep. Config only, no fork change.
# One arm = one llama-server lifetime at a given -ub/-b. Emits a JSON per arm.
set -u
UB=${1:?usage: ubarm.sh <ub> <round> <bin>}
ROUND=${2:?}
BIN=${3:?}
HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd -- "$HERE/../../.." && pwd)
NSYS_UNUSED=1
GPU=1; PORT=8093; CTX=16384
MODEL=/mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
PROMPTS=/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/.local/wt-stage0/docs/evidence/strata-gap/stage0/prompts
H2DPROBE=/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/.local/wt-stage0/.local/pcie-probe/h2d
EV=/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/.local/stage0f
mkdir -p "$EV"
export TMPDIR="$WT/.local/tmp"; mkdir -p "$TMPDIR"
LBL="ub${UB}-r${ROUND}"
LOG="$EV/$LBL-engine.log"
JOUT="$EV/$LBL.json"

[ "$UB" -lt 2048 ] && { echo "ub must be >= 2048"; exit 2; }

export CUDA_VISIBLE_DEVICES="$GPU" CUDA_DEVICE_ORDER=PCI_BUS_ID
export LD_LIBRARY_PATH="$BIN:/opt/software/cuda/13.2.1/lib64"

probe() { LD_LIBRARY_PATH=/opt/software/cuda/13.2.1/lib64 "$H2DPROBE" 1 2>/dev/null | sed 's/.*pinned: //'; }

H2D_PRE=$(probe)
VRAM0=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -i $GPU)

: > "$LOG"
"$BIN/llama-server" -m "$MODEL" --split-mode layer -fit off -ngl 99 \
  --n-cpu-moe 99 --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0 \
  -c "$CTX" --parallel 1 --flash-attn on --jinja -t 6 \
  --experimental-logs --load-mode none --ple-prefetch \
  -b "$UB" -ub "$UB" --host 127.0.0.1 --port "$PORT" \
  --alias gsq-rco-iq3_s --spec-type none -lv 5 >> "$LOG" 2>&1 &
SRV=$!
trap 'kill -INT $SRV 2>/dev/null; sleep 2; kill -9 $SRV 2>/dev/null' EXIT

READY=0
for _ in $(seq 1 900); do
  curl -fsS -m 2 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1 && { READY=1; break; }
  sleep 1
done
if [ "$READY" != 1 ]; then
  echo "[$LBL] STARTUP FAILED (exit path)"; tail -12 "$LOG"
  printf '{"label":"%s","ub":%s,"round":%s,"ok":false,"stage":"startup","h2d_pre":"%s"}\n' \
    "$LBL" "$UB" "$ROUND" "$H2D_PRE" > "$JOUT"
  exit 5
fi
sleep 5
VRAM_LOAD=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -i $GPU)
CBUF=$(grep -a -o 'CUDA0 compute buffer size is [0-9.]* MiB' "$LOG" | tail -1 | grep -o '[0-9.]* MiB')
CTOTAL=$(grep -a -o '| - CUDA0 (RTX 3060) | [0-9]*' "$LOG" | tail -1 | grep -o '[0-9]*$')

python3 - "$PORT" "$PROMPTS" "$UB" "$LBL" "$VRAM_LOAD" "$CBUF" "$CTOTAL" "$H2D_PRE" "$JOUT" <<'PY'
import json,sys,time,urllib.request,os,hashlib
port,prompts,ub,lbl,vram,cbuf,ctot,h2dpre,jout = sys.argv[1:10]
ub=int(ub)
def ask(p,maxtok):
    body=json.dumps({"model":"gsq-rco-iq3_s","messages":[{"role":"user","content":p}],
                     "max_tokens":maxtok,"temperature":0,"stream":False,
                     "cache_prompt":False}).encode()
    r=urllib.request.Request("http://127.0.0.1:%s/v1/chat/completions"%port,body,
                             {"content-type":"application/json"})
    t0=time.time(); d=json.loads(urllib.request.urlopen(r,timeout=3600).read()); t1=time.time()
    u=d.get("usage") or {}
    txt=((d.get("choices") or [{}])[0].get("message") or {}).get("content") or ""
    rc=((d.get("choices") or [{}])[0].get("message") or {}).get("reasoning_content") or ""
    return {"ttft_s":round(t1-t0,3),
            "prompt_tokens":u.get("prompt_tokens"),
            "completion_tokens":u.get("completion_tokens"),
            "prefill_tps":round(u.get("prompt_tokens",0)/(t1-t0),2) if t1>t0 else None,
            "n_ubatches":-(-u.get("prompt_tokens",0)//ub) if u.get("prompt_tokens") else None,
            "text_len":len(txt)+len(rc),
            "sha12":hashlib.sha256((txt+rc).encode("utf-8","replace")).hexdigest()[:12]}
res={"label":lbl,"ub":ub,"round":int(os.environ.get("ROUND","0")),"ok":True,
     "vram_load_mib":int(vram),"compute_buffer":cbuf,"cuda_total_mib":int(ctot) if ctot else None,
     "h2d_pre":h2dpre,"cells":{}}
# warmup so kernels are resident before any measured cell
ask("warm",1)
for cell in ("p4k","p12k"):
    try:
        res["cells"][cell]=ask(open(os.path.join(prompts,cell+".txt")).read(),1)
        print(f"[{lbl}] {cell} prefill_tps={res['cells'][cell]['prefill_tps']} "
              f"ubatches={res['cells'][cell]['n_ubatches']} ttft={res['cells'][cell]['ttft_s']}s",flush=True)
    except Exception as e:
        res["cells"][cell]={"error":"%s: %s"%(type(e).__name__,e)}
        print(f"[{lbl}] {cell} ERROR {res['cells'][cell]['error']}",flush=True)
# decode sanity: 64 tokens on p4k, cache_prompt OFF so decode reads a fresh KV
try:
    p=open(os.path.join(prompts,"p4k.txt")).read()
    body=json.dumps({"model":"gsq-rco-iq3_s","messages":[{"role":"user","content":p}],
                     "max_tokens":64,"temperature":0,"stream":False,"cache_prompt":False}).encode()
    r=urllib.request.Request("http://127.0.0.1:%s/v1/chat/completions"%port,body,
                             {"content-type":"application/json"})
    t0=time.time(); d=json.loads(urllib.request.urlopen(r,timeout=3600).read()); t1=time.time()
    u=d.get("usage") or {}; n=u.get("completion_tokens") or 0
    m=(d.get("choices") or [{}])[0].get("message") or {}
    txt=(m.get("content") or "")+(m.get("reasoning_content") or "")
    # coherent = non-empty, has alnum, not a repetition of one char
    coherent=bool(txt.strip()) and any(c.isalnum() for c in txt) and len(set(txt.strip()))>8
    res["decode"]={"completion_tokens":n,"wall_s":round(t1-t0,3),
                   "decode_tps":round(max(0,n-1)/(t1-t0),2) if t1>t0 else None,
                   "text_len":len(txt),"coarse_coherent":coherent,
                   "sha12":hashlib.sha256(txt.encode("utf-8","replace")).hexdigest()[:12]}
    print(f"[{lbl}] decode tok/s={res['decode']['decode_tps']} coherent={coherent}",flush=True)
except Exception as e:
    res["decode"]={"error":"%s: %s"%(type(e).__name__,e)}
json.dump(res,open(jout,"w"),indent=2)
PY
RC=$?
VRAM_POST=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -i $GPU)
H2D_POST=$(probe)
python3 - "$JOUT" "$VRAM_POST" "$H2D_POST" <<'PY'
import json,sys
p,vr,h2=sys.argv[1:4]
try:
    d=json.load(open(p))
    d["vram_after_prefill_mib"]=int(vr); d["h2d_post"]=h2; json.dump(d,open(p,"w"),indent=2)
except Exception: pass
PY
echo "[$LBL] vram_load=${VRAM_LOAD} vram_after=${VRAM_POST} cbuf=${CBUF:-?} h2d_pre=${H2D_PRE} h2d_post=${H2D_POST} rc=$RC"
kill -INT $SRV 2>/dev/null; sleep 3; kill -9 $SRV 2>/dev/null
echo "[$LBL] DONE"
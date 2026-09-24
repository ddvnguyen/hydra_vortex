#!/usr/bin/env bash
# F1/F2 (sections 130-131): CPU-serve cost per expert vs j (F1) and vs a concurrent GPU decode workload (F2),
# at the design's real call spacing (I/48 = 0.85 ms, spin gap) with the engine's threadpool poll (50).
# Idle x3, control variants (sleep gap, 1.5 ms gap, poll 0), busy x3 (cache42 decoding on CUDA1), busy+hog x2
# (adds a 6 GB/s host-memory reader = Gen4 upload stream), idle x1 (drift bracket). Needs the rig lock.
# Output: $OUT/f1f2.txt (BENCH lines), $OUT/bench-stderr.txt, $OUT/busy-*.txt. Fit: fit_f1.py $OUT/f1f2.txt
set -u
W=$(cd "$(dirname "$0")/../.." && pwd)/src/llama-cpp
OUT=${OUT:-/tmp/opencode/controls/f1f2b}
S=/mnt/SSD/qwen3.8-flash-next-apex-mini/Qwen3.8-Flash-Next-APEX-I-Mini
GG=${S}-00003-of-00006.gguf,${S}-00004-of-00006.gguf,${S}-00005-of-00006.gguf,${S}-00006-of-00006.gguf
JS=${JS:-1,2,3,4,5,6,8,10}
GAP=${GAP:-850}
mkdir -p "$OUT"; : > "$OUT/f1f2.txt"; : > "$OUT/bench-stderr.txt"
BENCH=$OUT/moe_cpu_bench
g++ -O2 -std=c++17 -pthread -I "$W/ggml/include" "$(dirname "$0")/moe_cpu_bench.cpp" -o "$BENCH" \
  -L "$W/build-demand/bin" -lggml-cpu -lggml-base -Wl,-rpath,"$W/build-demand/bin" || exit 1
until TOK=$(/tmp/opencode/rig-lock.sh acquire controls123-f1f2b $$ 3600 2>/dev/null); do sleep 10; done
SP=""; CP=""
trap '/tmp/opencode/rig-lock.sh release "$TOK"; [ -n "$CP" ] && kill $CP 2>/dev/null; [ -n "$SP" ] && kill $SP 2>/dev/null' EXIT
run() { # label [extra bench args]
  local label=$1; shift
  echo "loadavg_before $label $(cut -d' ' -f1-3 /proc/loadavg)" | tee -a "$OUT/f1f2.txt"
  "$BENCH" --gguf "$GG" --threads 6 --layers 12 --iters 600 --js "$JS" --gap-us "$GAP" --label "$label" "$@" 2>>"$OUT/bench-stderr.txt" | tee -a "$OUT/f1f2.txt"
}
for i in 1 2 3; do run idle$i; done
run ctl-sleepgap --gap-mode sleep
run ctl-gap1500 --gap-us 1500
run ctl-poll0 --poll 0
export CUDA_VISIBLE_DEVICES=1 LD_LIBRARY_PATH=$W/build-demand/bin:/opt/software/cuda/13.2.1/lib64
$W/build-demand/bin/llama-server -m ${S}-00001-of-00006.gguf --split-mode layer -fit off -ngl 99 -c 8192 --parallel 1 --flash-attn on --jinja -t 6 \
  --experimental-logs --load-mode none --spec-type none --override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 42 \
  --host 127.0.0.1 --port 18441 > "$OUT/server.log" 2>&1 &
SP=$!
for i in $(seq 1 120); do curl -s http://127.0.0.1:18441/health | grep -q ok && break; sleep 5; done
python3 -c "import json;print(json.dumps({'prompt':'banana '*60,'n_predict':2500,'temperature':0,'cache_prompt':False,'ignore_eos':True,'grammar':'root ::= (\" banana\")+'}))" > "$OUT/busy-req.json"
curl -s http://127.0.0.1:18441/completion -d @"$OUT/busy-req.json" > "$OUT/busy-resp.json" &
CP=$!
sleep 20
for i in 1 2 3; do
  nvidia-smi -i 1 --query-gpu=utilization.gpu,pcie.link.gen.current,memory.used --format=csv,noheader > "$OUT/busy-gpu-$i.txt"
  top -bn1 -o %CPU | sed -n 7,10p > "$OUT/busy-top-$i.txt"
  run busy$i
done
for i in 1 2; do run busyhog$i --hog-gbps 6; done
kill $CP $SP 2>/dev/null; wait $SP 2>/dev/null; CP=""; SP=""
run idle4
echo F1F2_DONE

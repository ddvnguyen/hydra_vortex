#!/usr/bin/env bash
# F1/F2 (section 130 R4): CPU-serve cost per expert vs j (F1) and vs a concurrent GPU decode workload (F2).
# Runs idle x2, busy x3, idle x2 (bracket for drift). Busy = the cache42 server on CUDA1 decoding a grammar-forced request.
# Needs the rig lock (F2 uses the 3060 and the host CPU). Output: $OUT/f1f2.txt (BENCH lines) + $OUT/busy-*.txt (GPU samples).
set -u
W=$(cd "$(dirname "$0")/../.." && pwd)/src/llama-cpp
OUT=${OUT:-/tmp/opencode/controls/f1f2}
BENCH=${BENCH:-$OUT/moe_cpu_bench}
S=/mnt/SSD/qwen3.8-flash-next-apex-mini/Qwen3.8-Flash-Next-APEX-I-Mini
GG=${S}-00003-of-00006.gguf,${S}-00004-of-00006.gguf,${S}-00005-of-00006.gguf,${S}-00006-of-00006.gguf
JS=${JS:-1,2,4,6,10}
mkdir -p "$OUT"; : > "$OUT/f1f2.txt"
mkdir -p "$OUT"
[ -x "$BENCH" ] || g++ -O2 -std=c++17 -I "$W/ggml/include" "$(dirname "$0")/moe_cpu_bench.cpp" -o "$BENCH" \
  -L "$W/build-demand/bin" -lggml-cpu -lggml-base -Wl,-rpath,"$W/build-demand/bin" || exit 1
until TOK=$(/tmp/opencode/rig-lock.sh acquire controls123-f1f2 $$ 3600 2>/dev/null); do sleep 10; done
SP=""; CP=""
trap '/tmp/opencode/rig-lock.sh release "$TOK"; [ -n "$CP" ] && kill $CP 2>/dev/null; [ -n "$SP" ] && kill $SP 2>/dev/null' EXIT
run() { # label
  echo "loadavg_before $(cut -d' ' -f1-3 /proc/loadavg)" | tee -a "$OUT/f1f2.txt"
  "$BENCH" --gguf "$GG" --threads 6 --layers 12 --iters 600 --js "$JS" --gap-us 1500 --label "$1" 2>>"$OUT/bench-stderr.txt" | tee -a "$OUT/f1f2.txt"
}
for i in 1 2; do run idle$i; done
"$BENCH" --gguf "$GG" --threads 6 --layers 12 --iters 600 --js 4,10 --gap-us 0 --label idle-nogap 2>>"$OUT/bench-stderr.txt" | tee -a "$OUT/f1f2.txt"

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
kill $CP $SP 2>/dev/null; wait $SP 2>/dev/null; CP=""; SP=""
for i in 3 4; do run idle$i; done
echo F1F2_DONE

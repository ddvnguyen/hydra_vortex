# Per-layer model info via the fork's llama.cpp GGUF API

Tool: `tools/layer-info/layer_info.c` (links `libggml-base` from the fork build at
`.local/q2g/src/build/bin`, fork HEAD 86164424e; header-only read, CPU only, no GPU used).
Build: `gcc -O1 -o .local/layer_info tools/layer-info/layer_info.c -I<fork>/ggml/include -L<fork>/build/bin -lggml-base -lm`
Run: `.local/layer_info <all shards> > model.tsv 2> model.kv.txt`; `python3 summarize.py model.tsv`.

Files: `*.tsv` (every tensor: layer, name, type, ne0-3, bytes), `*.kv.txt` (GGUF metadata, chat
template/tokenizer stripped), `*.summary.txt` (per-layer table).

Models: APEX-I-Mini (6 shards) and GSQ-RCO "IQ3_S" (2 shards).

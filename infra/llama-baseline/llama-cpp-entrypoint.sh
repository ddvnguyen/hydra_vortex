#!/usr/bin/env bash
# llama-cpp-entrypoint.sh — Container entrypoint that reads a YAML params file
# and exec's llama-server with the constructed args. Mirrors the logic of
# infra/llama-baseline/run-with-params.sh so the bench-test workflow and the
# production container workflow share the same params-file schema.
#
# Usage (inside the container):
#   LLAMACPP_PARAMS_FILE=/etc/llama-baseline/<name>.yml \
#     /usr/local/bin/llama-cpp-entrypoint.sh
#
# The compose file sets LLAMACPP_PARAMS_FILE and mounts the params/ dir
# read-only into /etc/llama-baseline/. To fall back to a different pin
# (e.g. 079 instead of 083), change the env var or the file the env var
# points to — no compose-file change required.
#
# Requirements: bash, python3, pyyaml, llama-server at /usr/local/bin/llama-server
set -euo pipefail

PARAMS_FILE="${LLAMACPP_PARAMS_FILE:?Set LLAMACPP_PARAMS_FILE=/path/to/params.yml}"

if [[ ! -f "$PARAMS_FILE" ]]; then
  echo "ERROR: params file not found: $PARAMS_FILE" >&2
  exit 1
fi

# ── Parse YAML to shell vars via python3 (pyyaml) ─────────────────────────────
# Field names mirror the YAML schema in infra/llama-baseline/params/*.yml
# (same schema consumed by run-with-params.sh — keep them in sync).
eval "$(python3 - "$PARAMS_FILE" <<'PYEOF'
import sys, yaml, shlex
p = yaml.safe_load(open(sys.argv[1]))

def v(d, k, default=""):
    val = d.get(k, default)
    return "" if val is None else str(val)

def b(d, k):
    return "on" if str(d.get(k, "")).lower() in ("on", "true", "1", "yes") else "off"

vars_ = {
    "P_MODEL_PATH":         v(p, "model_path"),
    "P_SERVER_PORT":        v(p, "server_port", "18081"),
    "P_N_GPU_LAYERS":       v(p, "n_gpu_layers", "99"),
    "P_CTX":                v(p, "ctx"),
    "P_FLASH_ATTN":         b(p, "flash_attn"),
    "P_CACHE_TYPE_K":       v(p, "cache_type_k", "q8_0"),
    "P_CACHE_TYPE_V":       v(p, "cache_type_v", "q4_1"),
    "P_RPC_ENDPOINT":       v(p, "rpc_endpoint", "127.0.0.1"),
    "P_RPC_PORT":           v(p, "rpc_port", "18052"),
    "P_TENSOR_SPLIT":       v(p, "tensor_split", "27,38"),
    "P_OVERRIDE_TENSORS":   v(p, "override_tensors"),
    "P_UBATCH":             v(p, "ubatch", "512"),
    "P_ROPE_SCALING":       v(p, "rope_scaling", "yarn"),
    "P_ROPE_SCALE":         v(p, "rope_scale", "5"),
    "P_YARN_ORIG_CTX":      v(p, "yarn_orig_ctx", "32768"),
    "P_PARALLEL":           v(p, "parallel", "1"),
    "P_CONT_BATCHING":      b(p, "cont_batching"),
    "P_KV_UNIFIED":         b(p, "kv_unified"),
    "P_JINJA":              b(p, "jinja"),
    "P_SPEC_TYPE":          v(p, "spec_type", "draft-mtp"),
    "P_SPEC_DRAFT_N_MAX":   v(p, "spec_draft_n_max"),
    "P_SPEC_DRAFT_P_MIN":   v(p, "spec_draft_p_min"),
    "P_SPEC_DRAFT_MODEL":   v(p, "spec_draft_model"),
    "P_SPEC_DRAFT_DEVICE":  v(p, "spec_draft_device"),
    "P_SPEC_DRAFT_NGL":     v(p, "spec_draft_ngl"),
    "P_SPEC_DRAFT_TYPE_K":  v(p, "spec_draft_type_k"),
    "P_SPEC_DRAFT_TYPE_V":  v(p, "spec_draft_type_v"),
    "P_CACHE_PROMPT":       b(p, "cache_prompt"),
    "P_CACHE_REUSE":        v(p, "cache_reuse", "64"),
    "P_MLOCK":              b(p, "mlock"),
    "P_CHECKPOINT_MIN_STEP":v(p, "checkpoint_min_step"),
    "P_PRIO":               v(p, "prio"),
    "P_PRIO_BATCH":         v(p, "prio_batch", "1"),
    "P_CONTEXT_SHIFT":      b(p, "context_shift"),
    "P_CACHE_IDLE_SLOTS":   b(p, "cache_idle_slots"),
    "P_CACHE_RAM_MIB":      v(p, "cache_ram_mib"),
}
for k, val in vars_.items():
    # shlex.quote each value to be shell-safe
    print(f'{k}={shlex.quote(val)}')
PYEOF
)"

# ── Build llama-server command (mirrors run-with-params.sh §"Build llama-server") ─
LLAMA_ARGS=()
LLAMA_ARGS+=(-m "$P_MODEL_PATH")
LLAMA_ARGS+=(--host 0.0.0.0 --port "$P_SERVER_PORT")
LLAMA_ARGS+=(-ngl "${P_N_GPU_LAYERS}")
LLAMA_ARGS+=(--ctx-size "$P_CTX")

if [[ "$P_FLASH_ATTN" == "on" ]]; then
  LLAMA_ARGS+=(--flash-attn on)
elif [[ "$P_FLASH_ATTN" == "off" ]]; then
  LLAMA_ARGS+=(--flash-attn off)
fi

LLAMA_ARGS+=(--cache-type-k "${P_CACHE_TYPE_K}")
LLAMA_ARGS+=(--cache-type-v "${P_CACHE_TYPE_V}")

# RPC connection
if [[ "${P_RPC_PORT}" != "0" && -n "$P_RPC_ENDPOINT" ]]; then
  LLAMA_ARGS+=(--rpc "${P_RPC_ENDPOINT}:${P_RPC_PORT}")
  LLAMA_ARGS+=(-dev "RPC0,CUDA0")
fi

# Tensor split (RPC-split mode default)
if [[ -n "$P_TENSOR_SPLIT" ]]; then
  LLAMA_ARGS+=(--tensor-split "$P_TENSOR_SPLIT")
fi

# Override tensors
if [[ -n "$P_OVERRIDE_TENSORS" ]]; then
  LLAMA_ARGS+=(--override-tensor "$P_OVERRIDE_TENSORS")
fi

# Micro-batch
if [[ -n "$P_UBATCH" ]]; then
  LLAMA_ARGS+=(--ubatch-size "$P_UBATCH")
fi

# YaRN
if [[ -n "$P_ROPE_SCALING" ]]; then
  LLAMA_ARGS+=(--rope-scaling "$P_ROPE_SCALING")
fi
if [[ -n "$P_ROPE_SCALE" ]]; then
  LLAMA_ARGS+=(--rope-scale "$P_ROPE_SCALE")
fi
if [[ -n "$P_YARN_ORIG_CTX" ]]; then
  LLAMA_ARGS+=(--yarn-orig-ctx "$P_YARN_ORIG_CTX")
fi

# Parallel / cont-batching / kv-unified
if [[ -n "$P_PARALLEL" ]]; then
  LLAMA_ARGS+=(--parallel "$P_PARALLEL")
fi
if [[ "$P_CONT_BATCHING" == "on" ]]; then
  LLAMA_ARGS+=(--cont-batching)
fi
if [[ "$P_KV_UNIFIED" == "on" ]]; then
  LLAMA_ARGS+=(--kv-unified)
elif [[ "$P_KV_UNIFIED" == "off" ]]; then
  LLAMA_ARGS+=(--no-kv-unified)
fi

# Chat template
if [[ "$P_JINJA" == "on" ]]; then
  LLAMA_ARGS+=(--jinja)
fi

# Speculative decoding
if [[ -n "$P_SPEC_TYPE" ]]; then
  LLAMA_ARGS+=(--spec-type "$P_SPEC_TYPE")
fi
if [[ -n "$P_SPEC_DRAFT_N_MAX" ]]; then
  LLAMA_ARGS+=(--spec-draft-n-max "$P_SPEC_DRAFT_N_MAX")
fi
if [[ -n "$P_SPEC_DRAFT_P_MIN" ]]; then
  LLAMA_ARGS+=(--spec-draft-p-min "$P_SPEC_DRAFT_P_MIN")
fi
if [[ -n "$P_SPEC_DRAFT_MODEL" ]]; then
  LLAMA_ARGS+=(--spec-draft-model "$P_SPEC_DRAFT_MODEL")
fi
if [[ -n "$P_SPEC_DRAFT_DEVICE" ]]; then
  LLAMA_ARGS+=(--spec-draft-device "$P_SPEC_DRAFT_DEVICE")
fi
if [[ -n "$P_SPEC_DRAFT_NGL" ]]; then
  LLAMA_ARGS+=(--spec-draft-ngl "$P_SPEC_DRAFT_NGL")
fi
if [[ -n "$P_SPEC_DRAFT_TYPE_K" ]]; then
  LLAMA_ARGS+=(--spec-draft-type-k "$P_SPEC_DRAFT_TYPE_K")
fi
if [[ -n "$P_SPEC_DRAFT_TYPE_V" ]]; then
  LLAMA_ARGS+=(--spec-draft-type-v "$P_SPEC_DRAFT_TYPE_V")
fi

# Cache / mlock / checkpoint
if [[ "$P_CACHE_PROMPT" == "on" ]]; then
  LLAMA_ARGS+=(--cache-prompt)
elif [[ "$P_CACHE_PROMPT" == "off" ]]; then
  LLAMA_ARGS+=(--no-cache-prompt)
fi
if [[ -n "$P_CACHE_REUSE" ]]; then
  LLAMA_ARGS+=(--cache-reuse "$P_CACHE_REUSE")
fi
if [[ "$P_MLOCK" == "on" ]]; then
  LLAMA_ARGS+=(--mlock)
fi
if [[ -n "$P_CHECKPOINT_MIN_STEP" ]]; then
  LLAMA_ARGS+=(--checkpoint-min-step "$P_CHECKPOINT_MIN_STEP")
fi

# Prio
if [[ -n "$P_PRIO" ]]; then
  LLAMA_ARGS+=(--prio "$P_PRIO")
fi
if [[ -n "$P_PRIO_BATCH" ]]; then
  LLAMA_ARGS+=(--prio-batch "$P_PRIO_BATCH")
fi

# Context shift
if [[ "$P_CONTEXT_SHIFT" == "on" ]]; then
  LLAMA_ARGS+=(--context-shift)
elif [[ "$P_CONTEXT_SHIFT" == "off" ]]; then
  LLAMA_ARGS+=(--no-context-shift)
fi

# Cache idle slots (requires --cache-ram)
if [[ "$P_CACHE_IDLE_SLOTS" == "on" ]]; then
  LLAMA_ARGS+=(--cache-idle-slots)
elif [[ "$P_CACHE_IDLE_SLOTS" == "off" ]]; then
  LLAMA_ARGS+=(--no-cache-idle-slots)
fi

# Cache RAM limit (MiB) — host-RAM prompt cache for idle-slot swap
if [[ -n "$P_CACHE_RAM_MIB" ]]; then
  LLAMA_ARGS+=(--cache-ram "$P_CACHE_RAM_MIB")
fi

# Production observability flags
LLAMA_ARGS+=(--metrics --slots --log-verbosity 4)

echo "=== llama-cpp-entrypoint: $PARAMS_FILE ==="
echo "Args: ${LLAMA_ARGS[*]}"

exec /usr/local/bin/llama-server "${LLAMA_ARGS[@]}"

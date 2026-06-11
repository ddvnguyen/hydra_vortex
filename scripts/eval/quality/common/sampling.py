"""Sampling parameters for Qwen3.6-35B-A3B thinking mode.

From https://huggingface.co/Qwen/Qwen3.6-35B-A3B#quickstart
"""

# Thinking mode for general tasks (MMLU-Pro, GPQA, AIME)
# max_tokens=16384 — enough for chain-of-thought reasoning (P100 ≈ 10 min).
THINKING_PARAMS: dict = {
    "temperature": 1.0,
    "top_p": 0.95,
    "top_k": 20,
    "presence_penalty": 1.5,
    "repetition_penalty": 1.0,
    "max_tokens": 16384,
}

# Precise coding tasks (LiveCodeBench)
PRECISE_PARAMS: dict = {
    "temperature": 0.6,
    "top_p": 0.95,
    "top_k": 20,
    "presence_penalty": 0.0,
    "repetition_penalty": 1.0,
    "max_tokens": 16384,
}

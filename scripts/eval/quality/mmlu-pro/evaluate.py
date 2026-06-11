"""MMLU-Pro Evaluation — 10-option multiple choice knowledge benchmark.

Dataset: TIGER-Lab/MMLU-Pro on HuggingFace
Samples: 150 random (quick validation)
Scoring: Exact match on option letter (A-J)
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import random
import re
import sys
import time
from typing import Any

import requests
from datasets import load_dataset

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from common.hydra_client import HydraClient, LlamaMetrics
from common.pd_split_verify import verify
from common.sampling import THINKING_PARAMS

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
log = logging.getLogger("mmlu-pro")

NUM_SAMPLES = 150
REFERENCE_SCORE = 85.2  # Qwen3.6-35B-A3B on MMLU-Pro
HYDRA_BASE = "http://localhost:9000"
SLOT_PAD_S = 2.0  # Wait after each sample for P100 slot release


def _ensure_p100_idle() -> None:
    """Evict stuck P100 sessions so the slot pool is free before testing."""
    try:
        resp = requests.get(f"{HYDRA_BASE}/health", timeout=10)
        health: dict[str, Any] = resp.json()
        nodes = health.get("nodes", {})
        p100 = nodes.get("p100", {})
        if p100.get("slots_idle", 0) > 0:
            log.info("P100 slot is free — ready")
            return

        log.warning("P100 slot not idle — evicting stale sessions")
        sess_resp = requests.get(f"{HYDRA_BASE}/sessions", timeout=10)
        sessions: list[dict[str, Any]] = sess_resp.json()
        evicted = 0
        for s in sessions:
            if s.get("node") == "p100" and not s.get("slot_freed", True):
                requests.delete(
                    f"{HYDRA_BASE}/sessions/{s['session_id']}", timeout=10)
                evicted += 1
        log.info(f"Evicted {evicted} stale P100 sessions")
        time.sleep(1)
    except Exception as e:
        log.warning(f"Could not ensure P100 idle: {e}")


def format_mmlu_prompt(question: str, options: list[str]) -> str:
    """Format an MMLU-Pro question with 10 options as a chat message."""
    letters = "ABCDEFGHIJ"[: len(options)]
    opts = "\n".join(f"{l}. {o}" for l, o in zip(letters, options))
    return (
        f"{question}\n\nOptions:\n{opts}\n\n"
        "Answer with only the letter of the correct option (e.g., A)."
    )


def extract_answer(text: str) -> str | None:
    """Extract option letter from model response.

    Looks for patterns like 'ANSWER: A', '\boxed{A}', or standalone letter.
    """
    if not text:
        return None
    # Try explicit markers first
    for pat in [r"ANSWER:\s*([A-Ja-j])", r"\\boxed\{([A-Ja-j])\}"]:
        m = re.search(pat, text)
        if m:
            return m.group(1).upper()
    # Last resort: find the last single letter mention
    m = re.findall(r"\b([A-Ja-j])\b", text)
    if m:
        return m[-1].upper()
    return None


def load_dataset_sampled(seed: int = 42) -> list[dict]:
    """Load MMLU-Pro test set and sample NUM_SAMPLES."""
    log.info("Loading MMLU-Pro from TIGER-Lab/MMLU-Pro...")
    ds = load_dataset("TIGER-Lab/MMLU-Pro")["test"]
    items = list(ds)
    rng = random.Random(seed)
    rng.shuffle(items)
    sampled = items[:NUM_SAMPLES]
    log.info(f"Loaded {len(items)} total, sampled {len(sampled)}")
    return [dict(it) for it in sampled]


def run(limit: int = NUM_SAMPLES, seed: int = 42) -> dict:
    """Run MMLU-Pro evaluation and return result dict."""
    client = HydraClient()
    metrics = LlamaMetrics()

    # Verify Hydra is healthy and P100 slot is free
    health = client.health()
    log.info(f"Hydra health: {health['status']}")
    _ensure_p100_idle()

    items = load_dataset_sampled(seed)[:limit]

    # Capture pre-test metrics
    metrics.capture_pre()

    correct = 0
    total_cached = 0.0
    total_reasoning_len = 0
    total_content_len = 0
    count = 0
    start = time.monotonic()

    for i, item in enumerate(items):
        question = item.get("question", "")
        options_list = item.get("options", [])
        if isinstance(options_list, str):
            options_list = [x.strip() for x in options_list.split("\n") if x.strip()]
        answer_key = item.get("answer", "").strip().upper()

        prompt = format_mmlu_prompt(question, options_list)
        messages = [{"role": "user", "content": prompt}]

        try:
            resp = client.chat_completion(messages, params=THINKING_PARAMS)
            choice = resp.get("choices", [{}])[0]
            msg = choice.get("message", {})
            content = msg.get("content", "") or ""
            reasoning = msg.get("reasoning_content", "") or ""
            time.sleep(SLOT_PAD_S)  # Let P100 slot lease release
        except Exception as e:
            log.warning(f"Request {i} failed: {e}")
            continue

        # Extract answer from both content and reasoning
        predicted = extract_answer(content)
        if predicted is None:
            predicted = extract_answer(reasoning)

        if predicted == answer_key:
            correct += 1

        cached = resp.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
        prompt_tok = resp.get("usage", {}).get("prompt_tokens", 1)
        total_cached += (cached / max(prompt_tok, 1)) * 100

        total_reasoning_len += len(reasoning)
        total_content_len += len(content)
        count += 1

        if (i + 1) % 25 == 0:
            acc = (correct / count * 100) if count else 0
            log.info(f"  [{i + 1}/{len(items)}] acc={acc:.1f}%")

    elapsed = time.monotonic() - start
    score = (correct / count * 100) if count else 0
    cached_avg = total_cached / count if count else 0
    reason_avg = int(total_reasoning_len / count) if count else 0
    content_avg = int(total_content_len / count) if count else 0

    # Capture post-test metrics and verify P/D split
    time.sleep(SLOT_PAD_S)  # Allow final bg_save to complete
    deltas = metrics.capture_post()
    p100_n_past = metrics.get_slot_n_past("p100")
    pd_result = verify(deltas, p100_n_past, cached_avg)

    log.info(f"Score: {score:.1f}% ({correct}/{count})")
    log.info(f"P/D Split: {'PASS' if pd_result.pass_ else 'FAIL'}")
    log.info(pd_result.summary_line())

    result = {
        "name": "MMLU-Pro",
        "num_samples": count,
        "score": round(score, 1),
        "score_unit": "%",
        "reference_score": REFERENCE_SCORE,
        "pd_pass": pd_result.pass_,
        "pd_summary": pd_result.summary_line(),
        "pd_issues": pd_result.issues,
        "cached_avg": round(cached_avg, 1),
        "reasoning_avg": reason_avg,
        "content_avg": content_avg,
        "elapsed_s": round(elapsed, 1),
        "rtx_ppt_delta": pd_result.rtx_ppt_delta,
        "rtx_ppt_pass": pd_result.rtx_ppt_pass,
        "rtx_tpt_delta": pd_result.rtx_tpt_delta,
        "rtx_tpt_pass": pd_result.rtx_tpt_pass,
        "p100_ppt_delta": pd_result.p100_ppt_delta,
        "p100_ppt_pass": pd_result.p100_ppt_pass,
        "p100_tpt_delta": pd_result.p100_tpt_delta,
        "p100_tpt_pass": pd_result.p100_tpt_pass,
    }
    return result


def main():
    parser = argparse.ArgumentParser("MMLU-Pro evaluation")
    parser.add_argument("--limit", type=int, default=NUM_SAMPLES)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--output", default="/tmp/hydra-quality-results/mmlu-pro.json")
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.output), exist_ok=True)
    result = run(limit=args.limit, seed=args.seed)

    with open(args.output, "w") as f:
        json.dump(result, f, indent=2)
    print(f"Result: {args.output}")
    print(f"Score: {result['score']:.1f}% | P/D Split: {'PASS' if result['pd_pass'] else 'FAIL'}")


if __name__ == "__main__":
    main()

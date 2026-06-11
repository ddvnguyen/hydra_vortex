"""GPQA Evaluation — Graduate-level STEM multiple-choice benchmark.

Dataset: idavidrein/gpqa on HuggingFace (gated — requires HF login)
Samples: 150 random from gpqa_main (quick validation)
Scoring: Exact match on option letter (A-D)
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

from datasets import load_dataset

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from common.hydra_client import HydraClient, LlamaMetrics
from common.pd_split_verify import verify
from common.sampling import THINKING_PARAMS

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
log = logging.getLogger("gpqa")

NUM_SAMPLES = 150
REFERENCE_SCORE = 86.0  # Qwen3.6-35B-A3B on GPQA


def format_gpqa_prompt(question: str,
                       opt_a: str, opt_b: str, opt_c: str, opt_d: str) -> str:
    """Format a GPQA question with 4 options."""
    return (
        f"{question}\n\n"
        f"A. {opt_a}\nB. {opt_b}\nC. {opt_c}\nD. {opt_d}\n\n"
        "Think step by step, then answer with the letter of the correct option."
    )


def extract_answer(text: str) -> str | None:
    """Extract option letter from model response."""
    if not text:
        return None
    for pat in [r"ANSWER:\s*([A-Da-d])", r"\\boxed\{([A-Da-d])\}"]:
        m = re.search(pat, text)
        if m:
            return m.group(1).upper()
    m = re.findall(r"\b([A-Da-d])\b", text)
    if m:
        return m[-1].upper()
    return None


def load_dataset_sampled(seed: int = 42) -> list[dict]:
    """Load GPQA main set and sample NUM_SAMPLES."""
    log.info("Loading GPQA main set from idavidrein/gpqa...")
    try:
        ds = load_dataset("idavidrein/gpqa", "gpqa_main")["train"]
    except Exception:
        log.error(
            "Failed to load GPQA. This is a gated dataset. "
            "Run: huggingface-cli login"
        )
        raise
    items = []
    for row in ds:
        items.append({
            "question": row.get("Question", ""),
            "A": row.get("Correct Answer", ""),  # GPQA stores answer + wrong
            "B": row.get("Incorrect Answer 1", ""),
            "C": row.get("Incorrect Answer 2", ""),
            "D": row.get("Incorrect Answer 3", ""),
            "answer": "A",  # First option is always correct in GPQA format
        })
    rng = random.Random(seed)
    rng.shuffle(items)
    # Shuffle option order to avoid position bias
    shuffled = []
    for item in items[:NUM_SAMPLES]:
        letters = ["A", "B", "C", "D"]
        answers = [item["A"], item["B"], item["C"], item["D"]]
        indices = list(range(4))
        rng.shuffle(indices)
        new_item = {
            "question": item["question"],
            "opt_a": answers[indices[0]],
            "opt_b": answers[indices[1]],
            "opt_c": answers[indices[2]],
            "opt_d": answers[indices[3]],
            "answer": letters[indices.index(0)],
        }
        shuffled.append(new_item)
    log.info(f"Loaded {len(items)} total, sampled {len(shuffled)}")
    return shuffled


def run(limit: int = NUM_SAMPLES, seed: int = 42) -> dict:
    """Run GPQA evaluation and return result dict."""
    client = HydraClient()
    metrics = LlamaMetrics()

    health = client.health()
    log.info(f"Hydra health: {health['status']}")

    items = load_dataset_sampled(seed)[:limit]

    metrics.capture_pre()

    correct = 0
    total_cached = 0.0
    total_reasoning_len = 0
    total_content_len = 0
    count = 0
    start = time.monotonic()

    for i, item in enumerate(items):
        prompt = format_gpqa_prompt(
            item["question"],
            item["opt_a"], item["opt_b"], item["opt_c"], item["opt_d"]
        )
        messages = [{"role": "user", "content": prompt}]

        try:
            resp = client.chat_completion(messages, params=THINKING_PARAMS)
            msg = resp.get("choices", [{}])[0].get("message", {})
            content = msg.get("content", "") or ""
            reasoning = msg.get("reasoning_content", "") or ""
        except Exception as e:
            log.warning(f"Request {i} failed: {e}")
            continue

        predicted = extract_answer(content)
        if predicted is None:
            predicted = extract_answer(reasoning)

        if predicted == item["answer"]:
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

    deltas = metrics.capture_post()
    p100_n_past = metrics.get_slot_n_past("p100")
    pd_result = verify(deltas, p100_n_past, cached_avg)

    log.info(f"Score: {score:.1f}% ({correct}/{count})")
    log.info(f"P/D Split: {'PASS' if pd_result.pass_ else 'FAIL'}")

    return {
        "name": "GPQA",
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


def main():
    parser = argparse.ArgumentParser("GPQA evaluation")
    parser.add_argument("--limit", type=int, default=NUM_SAMPLES)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--output", default="/tmp/hydra-quality-results/gpqa.json")
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.output), exist_ok=True)
    result = run(limit=args.limit, seed=args.seed)

    with open(args.output, "w") as f:
        json.dump(result, f, indent=2)
    print(f"Result: {args.output}")
    print(f"Score: {result['score']:.1f}% | P/D Split: {'PASS' if result['pd_pass'] else 'FAIL'}")


if __name__ == "__main__":
    main()

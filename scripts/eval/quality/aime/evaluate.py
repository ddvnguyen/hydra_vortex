"""AIME 2026 Evaluation — Math competition benchmark.

Dataset: MathArena/aime_2026 on HuggingFace
Samples: 30 problems (all of AIME I + II 2026)
Scoring: Exact integer match, extracted from \boxed{NNN}
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import re
import sys
import time

from datasets import load_dataset

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from common.hydra_client import HydraClient, LlamaMetrics
from common.pd_split_verify import verify
from common.sampling import THINKING_PARAMS

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
log = logging.getLogger("aime")

REFERENCE_SCORE = 92.7  # Qwen3.6-35B-A3B on AIME26


def extract_answer(text: str) -> int | None:
    """Extract integer answer from \boxed{NNN} pattern in model response."""
    if not text:
        return None
    m = re.search(r"\\boxed\{(\d+)\}", text)
    if m:
        return int(m.group(1))
    # Fallback: last integer in the text
    m = re.findall(r"\b(\d{1,5})\b", text)
    if m:
        return int(m[-1])
    return None


def load_dataset() -> list[dict]:
    """Load all AIME 2026 problems."""
    log.info("Loading AIME 2026 from MathArena/aime_2026...")
    ds = load_dataset("MathArena/aime_2026")["train"]
    items = [dict(it) for it in ds]
    log.info(f"Loaded {len(items)} problems")
    return items


def run() -> dict:
    """Run AIME 2026 evaluation and return result dict."""
    client = HydraClient()
    metrics = LlamaMetrics()

    health = client.health()
    log.info(f"Hydra health: {health['status']}")

    items = load_dataset()

    metrics.capture_pre()

    correct = 0
    total_cached = 0.0
    total_reasoning_len = 0
    total_content_len = 0
    count = 0
    start = time.monotonic()

    for i, item in enumerate(items):
        problem = item.get("problem", item.get("question", ""))
        answer = item.get("answer", 0)

        prompt = (
            f"Solve the following math problem step by step. "
            f"Put your final integer answer inside \\boxed{{}}.\n\n{problem}"
        )
        messages = [{"role": "user", "content": prompt}]

        try:
            resp = client.chat_completion(messages, params=THINKING_PARAMS)
            msg = resp.get("choices", [{}])[0].get("message", {})
            content = msg.get("content", "") or ""
            reasoning = msg.get("reasoning_content", "") or ""
        except Exception as e:
            log.warning(f"Problem {i + 1} failed: {e}")
            continue

        predicted = extract_answer(content)
        if predicted is None:
            predicted = extract_answer(reasoning)

        if predicted is not None and predicted == int(answer):
            correct += 1

        cached = resp.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
        prompt_tok = resp.get("usage", {}).get("prompt_tokens", 1)
        total_cached += (cached / max(prompt_tok, 1)) * 100

        total_reasoning_len += len(reasoning)
        total_content_len += len(content)
        count += 1

        result_mark = chr(0x2713) if predicted == int(answer) else chr(0x2717)
        log.info(f"  [{i + 1}/{len(items)}] {result_mark}")

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
        "name": "AIME 2026",
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
    parser = argparse.ArgumentParser("AIME 2026 evaluation")
    parser.add_argument("--output", default="/tmp/hydra-quality-results/aime.json")
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.output), exist_ok=True)
    result = run()

    with open(args.output, "w") as f:
        json.dump(result, f, indent=2)
    print(f"Result: {args.output}")
    print(f"Score: {result['score']:.1f}% | P/D Split: {'PASS' if result['pd_pass'] else 'FAIL'}")


if __name__ == "__main__":
    main()

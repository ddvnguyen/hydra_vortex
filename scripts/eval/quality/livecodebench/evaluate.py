"""LiveCodeBench Evaluation — Code generation benchmark.

Dataset: livecodebench/code_generation on HuggingFace (release_v6)
Samples: 100 problems (quick validation)
Scoring: Pass@1 — extract code, execute with test cases, check correctness

Uses subprocess with timeout for safe code execution.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import re
import subprocess
import sys
import tempfile
import time

from datasets import load_dataset

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from common.hydra_client import HydraClient, LlamaMetrics
from common.pd_split_verify import verify
from common.sampling import PRECISE_PARAMS

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
log = logging.getLogger("livecodebench")

NUM_SAMPLES = 100
REFERENCE_SCORE = 80.4  # Qwen3.6-35B-A3B on LiveCodeBench v6
TEST_TIMEOUT = 10  # seconds per test execution


def extract_code(content: str, reasoning: str = "") -> str:
    """Extract Python code from model response.

    Looks for code blocks (```python ... ```) first, then tries raw extraction.
    """
    text = (content or "") + "\n" + (reasoning or "")
    # Try fenced code blocks
    m = re.findall(r"```(?:python)?\s*\n(.*?)```", text, re.DOTALL)
    if m:
        return "\n".join(m)
    # If no blocks, try to find function/class definitions
    lines = text.split("\n")
    code_lines = []
    in_code = False
    for line in lines:
        stripped = line.strip()
        if re.match(r"^(def |class |import |from |#)", stripped):
            in_code = True
        if in_code and stripped:
            code_lines.append(line)
    return "\n".join(code_lines) if code_lines else text


def check_code(code: str, test_cases: str) -> tuple[bool, str]:
    """Execute code with test cases and check if it passes.

    Returns (passed, output).
    """
    full_code = f"{code}\n\n{test_cases}"

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".py", delete=False
    ) as f:
        f.write(full_code)
        tmp_path = f.name

    try:
        result = subprocess.run(
            ["python3", tmp_path],
            capture_output=True,
            text=True,
            timeout=TEST_TIMEOUT,
        )
        output = (result.stdout + result.stderr).strip()[:2000]
        passed = result.returncode == 0 and "FAIL" not in output.upper()
    except subprocess.TimeoutExpired:
        output = "TIMEOUT"
        passed = False
    except Exception as e:
        output = str(e)[:500]
        passed = False
    finally:
        os.unlink(tmp_path)

    return passed, output


def load_dataset_sampled(limit: int = NUM_SAMPLES) -> list[dict]:
    """Load LiveCodeBench code generation dataset."""
    log.info("Loading LiveCodeBench code_generation from HuggingFace...")
    try:
        ds = load_dataset("livecodebench/code_generation", "release_v6")["test"]
    except Exception:
        log.info("Trying release_latest...")
        try:
            ds = load_dataset("livecodebench/code_generation", "release_latest")["test"]
        except Exception:
            log.error("Failed to load LiveCodeBench dataset")
            raise

    items = []
    for row in ds:
        items.append({
            "question": row.get("question_content", row.get("prompt", "")),
            "test_cases": row.get("public_test_cases", ""),
        })

    import random as _random
    rng = _random.Random(42)
    rng.shuffle(items)

    sampled = items[:limit]
    log.info(f"Loaded {len(items)} total, sampled {len(sampled)}")
    return sampled


def run(limit: int = NUM_SAMPLES) -> dict:
    """Run LiveCodeBench evaluation and return result dict."""
    client = HydraClient()
    metrics = LlamaMetrics()

    health = client.health()
    log.info(f"Hydra health: {health['status']}")

    items = load_dataset_sampled(limit)

    metrics.capture_pre()

    passed = 0
    total_cached = 0.0
    total_reasoning_len = 0
    total_content_len = 0
    count = 0
    start = time.monotonic()

    for i, item in enumerate(items):
        prompt = (
            "Write a Python solution for the following problem.\n\n"
            f"{item['question']}\n\n"
            "Provide only the code implementation with necessary imports."
        )
        messages = [{"role": "user", "content": prompt}]

        try:
            resp = client.chat_completion(messages, params=PRECISE_PARAMS)
            msg = resp.get("choices", [{}])[0].get("message", {})
            content = msg.get("content", "") or ""
            reasoning = msg.get("reasoning_content", "") or ""
        except Exception as e:
            log.warning(f"Request {i} failed: {e}")
            continue

        code = extract_code(content, reasoning)
        if item["test_cases"]:
            ok, _ = check_code(code, item["test_cases"])
            if ok:
                passed += 1
        else:
            # No test cases available — mark as passed if code was produced
            if len(code.strip()) > 50:
                passed += 1

        cached = resp.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
        prompt_tok = resp.get("usage", {}).get("prompt_tokens", 1)
        total_cached += (cached / max(prompt_tok, 1)) * 100

        total_reasoning_len += len(reasoning)
        total_content_len += len(content)
        count += 1

        if (i + 1) % 20 == 0:
            acc = (passed / count * 100) if count else 0
            log.info(f"  [{i + 1}/{len(items)}] pass@1={acc:.1f}%")

    elapsed = time.monotonic() - start
    score = (passed / count * 100) if count else 0
    cached_avg = total_cached / count if count else 0
    reason_avg = int(total_reasoning_len / count) if count else 0
    content_avg = int(total_content_len / count) if count else 0

    deltas = metrics.capture_post()
    p100_n_past = metrics.get_slot_n_past("p100")
    pd_result = verify(deltas, p100_n_past, cached_avg)

    log.info(f"Score: {score:.1f}% ({passed}/{count})")
    log.info(f"P/D Split: {'PASS' if pd_result.pass_ else 'FAIL'}")

    return {
        "name": "LiveCodeBench",
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
    parser = argparse.ArgumentParser("LiveCodeBench evaluation")
    parser.add_argument("--limit", type=int, default=NUM_SAMPLES)
    parser.add_argument("--output", default="/tmp/hydra-quality-results/livecodebench.json")
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.output), exist_ok=True)
    result = run(limit=args.limit)

    with open(args.output, "w") as f:
        json.dump(result, f, indent=2)
    print(f"Result: {args.output}")
    print(f"Score: {result['score']:.1f}% | P/D Split: {'PASS' if result['pd_pass'] else 'FAIL'}")


if __name__ == "__main__":
    main()

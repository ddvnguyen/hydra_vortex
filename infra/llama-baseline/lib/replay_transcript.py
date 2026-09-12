#!/usr/bin/env python3
"""replay_transcript.py — checkpoint-seeded replay for the llama-baseline harness.

Loads a transcript saved by multiturn-growth-test.sh --checkpoint-dir, rebuilds
the conversation prefix deterministically, and re-issues a deep turn (or the
last K turns) against the already-warm prefix. The common use is a max_tokens
sweep: one prompt prefill, then N cheap calls that reuse the server's prefix KV
cache — instead of N full context regrowths.

Invoked by checkpoint-replay.sh (thin wrapper) so the CLI lives in one place.
"""

import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from multiturn_common import (  # noqa: E402
    DEFAULT_TEMPERATURE, DEFAULT_TIMEOUT_S, build_messages, load_transcript,
    prefix_history, save_transcript, send_request, target_user_content,
    usage_fields, assistant_fields,
)


def parse_args(argv):
    p = argparse.ArgumentParser(
        prog="checkpoint-replay.sh",
        description="Replay a saved deep-context transcript against llama-server.")
    p.add_argument("server_port")
    p.add_argument("transcript")
    p.add_argument("--prefix-turns", type=int, default=None,
                   help="use the first N saved turns as the cached prefix "
                        "(default: all but the last)")
    p.add_argument("--replay-last", type=int, default=None, metavar="K",
                   help="replay the last K saved turns sequentially "
                        "(sets prefix to total-K; sweep requires K=1)")
    p.add_argument("--turn-prompt", default=None,
                   help="new final user turn content (overrides the saved next turn)")
    p.add_argument("--turn-prompt-file", default=None,
                   help="read the new final user turn from this file")
    p.add_argument("--max-tokens", type=int, default=None,
                   help="output budget (default: from the checkpoint)")
    p.add_argument("--sweep-max-tokens", default=None,
                   help="comma/space-separated budgets; one call each on the "
                        "same prefix (default: a single --max-tokens call)")
    p.add_argument("--temperature", type=float, default=DEFAULT_TEMPERATURE)
    p.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_S)
    p.add_argument("--regen-filler", action="store_true",
                   help="regenerate prefix user turns via gen_content() instead "
                        "of using stored user_content (Round-5 parity)")
    p.add_argument("--save-results", default=None,
                   help="write the replay results JSON to this path")
    return p.parse_args(argv)


def parse_budgets(args, default_budget):
    if args.sweep_max_tokens:
        raw = args.sweep_max_tokens.replace(",", " ").split()
        budgets = [int(x) for x in raw]
        if not budgets:
            raise ValueError("--sweep-max-tokens given but empty")
        return budgets
    return [args.max_tokens if args.max_tokens is not None else default_budget]


def main(argv):
    args = parse_args(argv)
    transcript = load_transcript(args.transcript)
    total = len(transcript["turns"])
    default_budget = int(transcript["config"].get("output_tokens_per_turn", 750))

    override = None
    if args.turn_prompt_file:
        with open(args.turn_prompt_file) as f:
            override = f.read()
    elif args.turn_prompt is not None:
        override = args.turn_prompt

    if args.replay_last is not None:
        if args.replay_last < 1:
            raise SystemExit("--replay-last must be >= 1")
        if args.prefix_turns is not None:
            raise SystemExit("use either --replay-last or --prefix-turns, not both")
        prefix_turns = total - args.replay_last
        if prefix_turns < 0:
            raise SystemExit(
                f"--replay-last {args.replay_last} exceeds transcript length {total}")
        target_indexes = list(range(prefix_turns, total))
    elif override is not None and args.prefix_turns is None:
        # A brand-new final turn on top of the whole saved history.
        prefix_turns = total
        target_indexes = [total]
    else:
        prefix_turns = (args.prefix_turns if args.prefix_turns is not None
                        else total - 1)
        target_indexes = [prefix_turns]

    if not 0 <= prefix_turns <= total:
        raise SystemExit(
            f"--prefix-turns {prefix_turns} out of range 0..{total}")

    budgets = parse_budgets(args, default_budget)
    if len(budgets) > 1 and len(target_indexes) > 1:
        raise SystemExit("--sweep-max-tokens is only supported with a single "
                         "re-issued turn (use --replay-last 1)")

    prefix = prefix_history(transcript, prefix_turns, args.regen_filler)

    print("=== checkpoint-replay ===")
    print(f"  server port:   {args.server_port}")
    print(f"  transcript:    {args.transcript}")
    print(f"  saved turns:   {total} (prefix {prefix_turns}, "
          f"re-issue {len(target_indexes)})")
    print(f"  regen filler:  {args.regen_filler}")
    print(f"  temperature:   {args.temperature}")
    print(f"  budgets:       {', '.join(str(b) for b in budgets)}")
    print("")

    trials = []
    history = list(prefix)
    failed = False
    for ti, index in enumerate(target_indexes):
        is_last = (ti == len(target_indexes) - 1)
        if override is not None and is_last:
            prompt = override
        else:
            prompt = target_user_content(transcript, index, args.regen_filler)
        messages = build_messages(history, prompt)

        # Multi-turn replay: a single budget, feed the response forward.
        sweep = budgets if (len(target_indexes) == 1) else [budgets[0]]
        for budget in sweep:
            try:
                data, wall = send_request(args.server_port, messages, budget,
                                          args.temperature, args.timeout)
                usage = usage_fields(data)
                assistant = assistant_fields(data)
                tok_s = usage["completion_tokens"] / wall if wall > 0 else 0.0
                trial = {
                    "turn_index": index + 1,
                    "max_tokens": budget,
                    "wall_s": wall,
                    "prompt_tokens": usage["prompt_tokens"],
                    "cached_tokens": usage["cached_tokens"],
                    "completion_tokens": usage["completion_tokens"],
                    "tok_s": tok_s,
                    "finish_reason": assistant["finish_reason"],
                    "content": assistant["assistant_content"],
                    "reasoning": assistant["assistant_reasoning"],
                }
                trials.append(trial)
                print(f"  turn {index + 1}  max_tokens={budget:<6d} "
                      f"wall={wall:7.2f}s  prompt_tok={usage['prompt_tokens']:<7d} "
                      f"cached={usage['cached_tokens']:<7d} "
                      f"comp_tok={usage['completion_tokens']:<5d} "
                      f"tok/s={tok_s:6.2f}  finish={assistant['finish_reason']}")
                if len(target_indexes) > 1:
                    history.append({"role": "user", "content": prompt})
                    history.append({"role": "assistant",
                                    "content": assistant["assistant_content"]})
            except Exception as exc:  # surface and keep failing loudly
                failed = True
                print(f"  turn {index + 1}  max_tokens={budget}  FAILED: {exc}")
                trials.append({"turn_index": index + 1, "max_tokens": budget,
                               "error": str(exc)})

    if trials:
        ok = [t for t in trials if "error" not in t]
        if ok:
            walls = [t["wall_s"] for t in ok]
            print(f"\n  summary: {len(ok)}/{len(trials)} calls ok  "
                  f"wall min/mean/max = {min(walls):.2f}/{sum(walls)/len(walls):.2f}/"
                  f"{max(walls):.2f}s  "
                  f"cached_tokens last={ok[-1]['cached_tokens']}")

    if args.save_results:
        results = {
            "format": "hydra-checkpoint-replay-results/v1",
            "created_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "transcript": args.transcript,
            "server_port": args.server_port,
            "config": {
                "prefix_turns": prefix_turns,
                "target_indexes": [i + 1 for i in target_indexes],
                "budgets": budgets,
                "temperature": args.temperature,
                "regen_filler": args.regen_filler,
            },
            "trials": trials,
        }
        save_transcript(args.save_results, results)
        print(f"  results: {args.save_results}")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

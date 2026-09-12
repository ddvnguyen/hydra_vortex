#!/usr/bin/env python3
"""multiturn_common.py — shared primitives for the llama-baseline multiturn harness.

Both consumers import this module so the saved-transcript replay path uses
*exactly* the same filler generation, message construction and request shape as
the original growth run. That is what makes a saved checkpoint reproducible.

Consumers:
  * multiturn-growth-test.sh   — growth run, optionally saves transcripts
  * checkpoint-replay.sh       — loads a transcript, re-issues a deep turn /
                                 sweeps max_tokens against the frozen prefix

Round-5 precedent (docs/arms/chat-template-sharp-quality-eval.md §8-9): a deep
76K history was saved once, deterministically rebuilt from saved assistant
content + the same filler logic (WORDS_PER_TURN), then only the final turn was
re-issued with `temperature 0` while max_tokens was swept. This module is that
pattern, generalised.

Do not change SYSTEM_PROMPT or gen_content() without bumping TRANSCRIPT_FORMAT:
saved transcripts carry their own config, but the filler text is regenerated
(unless the transcript already stores the exact user_content).
"""

import json
import os
import time
import urllib.error
import urllib.request

# Bump when the transcript schema or filler algorithm changes in a way that
# makes previously saved transcripts non-reproducible.
TRANSCRIPT_FORMAT = "hydra-multiturn-transcript/v1"

SYSTEM_PROMPT = (
    "You are a helpful coding assistant. Respond concisely with technical "
    "details. Generate realistic code snippets and explanations."
)

DEFAULT_TEMPERATURE = 0.0
DEFAULT_TIMEOUT_S = 300


def gen_content(turn, words):
    """Generate ~words words of synthetic content for a turn.

    Deterministic in (turn, words). Copied verbatim from the original
    multiturn-growth-test.sh inline helper so old and new transcripts are
    byte-identical for the same inputs. Varies content by turn number to avoid
    highly-repetitive text that inflates MTP acceptance artificially
    (per arm098 caveat).
    """
    templates = [
        "The implementation refactored the core module for turn {t} \
processing, introducing a new abstraction layer that handles buffered \
I/O operations with configurable retry semantics and exponential \
backoff strategies for transient network failures.",
        "Performance analysis of the distributed cache revealed that \
turn {t} latency improved by approximately {pct} percent after \
switching to a lock-free concurrent hash map with epoch-based \
reclamation for the hot path, reducing tail latency at p99.",
        "The code review for turn {t} identified several areas where \
memory allocation patterns could be optimized: arena-based allocation \
for short-lived objects, pool reuse for connection handlers, and \
prefetch-friendly layout for the main data structures in the query \
planner's critical section.",
        "Documentation update for turn {t} covers the new streaming \
interface, including backpressure handling, graceful degradation \
under load, and the circuit-breaker pattern applied to upstream \
service calls with configurable timeout and retry budgets.",
        "Test coverage expansion for turn {t} added integration tests \
for the authentication middleware, including token refresh flows, \
session invalidation across distributed nodes, and rate limiting \
with sliding window counters backed by the replicated store.",
        "Infrastructure changes for turn {t} migrated the deployment \
pipeline to a blue-green strategy with canary analysis, reducing \
rollback time from minutes to seconds while maintaining zero-downtime \
guarantees for the primary API endpoints under production traffic.",
        "The debugging session for turn {t} traced a race condition in \
the event bus dispatcher where concurrent publish operations could \
lose messages under high throughput, fixed by introducing a per-topic \
sequence number with compare-and-swap validation on the commit path.",
        "Database schema evolution for turn {t} added a materialized \
view for the analytics dashboard, pre-aggregating hourly metrics \
with incremental refresh, reducing query latency from 2.3 seconds to \
47 milliseconds for the most common dashboard access patterns.",
    ]
    paragraphs = []
    for i in range(words // 30 + 2):
        t = templates[(turn * 3 + i) % len(templates)]
        pct = 15 + (turn * 7 + i * 13) % 40
        paragraphs.append(t.format(t=turn, pct=pct))
    text = " ".join(paragraphs)
    word_list = text.split()
    return " ".join(word_list[:words])


def build_messages(history, new_user_content):
    """system prompt + prior {role, content} history + the new user turn."""
    messages = [{"role": "system", "content": SYSTEM_PROMPT}]
    messages.extend(history)
    messages.append({"role": "user", "content": new_user_content})
    return messages


def send_request(port, messages, max_tokens, temperature=DEFAULT_TEMPERATURE,
                 timeout=DEFAULT_TIMEOUT_S):
    """POST a non-streaming chat completion; return (response_dict, wall_s)."""
    payload = json.dumps({
        "messages": messages,
        "max_tokens": max_tokens,
        "temperature": temperature,
    }).encode("utf-8")
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/v1/chat/completions",
        data=payload,
        headers={"Content-Type": "application/json"},
    )
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode())
    except urllib.error.HTTPError as exc:  # surface server-side error body
        body = exc.read().decode(errors="replace")
        raise RuntimeError(f"HTTP {exc.code} from /v1/chat/completions: {body[:500]}") from exc
    wall = time.time() - t0
    return data, wall


def usage_fields(data):
    """Extract prompt/completion/cached token counts from a chat response."""
    usage = data.get("usage", {}) or {}
    cached = 0
    details = usage.get("prompt_tokens_details") or {}
    if isinstance(details, dict):
        cached = details.get("cached_tokens", 0) or 0
    if not cached:
        # Some llama.cpp builds expose these names instead.
        cached = usage.get("prompt_cache_hit_tokens", 0) or 0
    return {
        "prompt_tokens": usage.get("prompt_tokens", 0) or 0,
        "completion_tokens": usage.get("completion_tokens", 0) or 0,
        "cached_tokens": cached,
    }


def assistant_fields(data):
    """Extract the assistant message content + optional reasoning."""
    msg = (data.get("choices") or [{}])[0].get("message", {}) or {}
    finish = (data.get("choices") or [{}])[0].get("finish_reason", "")
    return {
        "assistant_content": msg.get("content", "") or "",
        "assistant_reasoning": msg.get("reasoning_content", "") or "",
        "finish_reason": finish,
    }


def target_depth_tokens(n_turns, new_tokens_per_turn, output_tokens_per_turn):
    """Same rough estimate the growth script prints (7000 seed + per-turn)."""
    return 7000 + (n_turns - 1) * (new_tokens_per_turn + output_tokens_per_turn)


def new_transcript(port, session_id, n_turns, new_tokens_per_turn,
                   words_per_turn, output_tokens_per_turn, temperature):
    return {
        "format": TRANSCRIPT_FORMAT,
        "created_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "session_id": session_id,
        "server_port": port,
        "config": {
            "n_turns": n_turns,
            "new_tokens_per_turn": new_tokens_per_turn,
            "words_per_turn": words_per_turn,
            "output_tokens_per_turn": output_tokens_per_turn,
            "temperature": temperature,
            "system_prompt": SYSTEM_PROMPT,
            "target_depth_tokens": target_depth_tokens(
                n_turns, new_tokens_per_turn, output_tokens_per_turn),
        },
        "turns": [],
    }


def save_transcript(path, transcript):
    """Atomic write so a crashed run never leaves a half-written checkpoint."""
    tmp = f"{path}.tmp.{os.getpid()}"
    with open(tmp, "w") as f:
        json.dump(transcript, f, indent=2)
        f.write("\n")
    os.replace(tmp, path)


def load_transcript(path):
    with open(path) as f:
        transcript = json.load(f)
    fmt = transcript.get("format")
    if fmt != TRANSCRIPT_FORMAT:
        raise ValueError(
            f"unsupported transcript format {fmt!r} in {path} "
            f"(expected {TRANSCRIPT_FORMAT!r})")
    if not transcript.get("turns"):
        raise ValueError(f"transcript {path} has no turns")
    return transcript


def prefix_history(transcript, prefix_turns, regen_filler=False):
    """Rebuild the {role, content} history for the first `prefix_turns` turns.

    Default uses the exact user_content stored in the checkpoint. With
    regen_filler=True the user side is regenerated from gen_content(turn,
    words_per_turn) instead — matching the Round-5 manual reconstruction where
    only assistant content was kept and filler was regenerated deterministically.
    """
    words = int(transcript["config"]["words_per_turn"])
    history = []
    for rec in transcript["turns"][:prefix_turns]:
        user = (gen_content(rec["turn"], words)
                if regen_filler else rec["user_content"])
        history.append({"role": "user", "content": user})
        history.append({"role": "assistant", "content": rec["assistant_content"]})
    return history


def target_user_content(transcript, index, regen_filler=False, override=None,
                        override_file=None):
    """User content for the re-issued turn at `index` (0-based saved turn index).

    Priority: override_file > override > saved turn at index > error.
    """
    if override_file:
        with open(override_file) as f:
            return f.read()
    if override is not None:
        return override
    turns = transcript["turns"]
    if index < len(turns):
        if regen_filler:
            return gen_content(turns[index]["turn"],
                               int(transcript["config"]["words_per_turn"]))
        return turns[index]["user_content"]
    raise ValueError(
        f"no saved turn at index {index} and no --turn-prompt/--turn-prompt-file "
        f"given; transcript has {len(turns)} turns")

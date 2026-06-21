"""
Workload generator 4 — long context (40K-80K).

Issue #306 generator 4: "Single-shot 40K-80K prompts. Tests chunked
dedup, VRAM pressure, OOM guards."

The user content is padded by repeating a fixed paragraph until the
target character count is hit (chars ≈ tokens × 4). Default 60K tokens
— run with `--context-tokens 80000` for the 80K case (S6).

Usage:
    python -m tests.bench.long_context --context-tokens 60000 \\
        --output results/long_context.json
"""

from __future__ import annotations

import asyncio
import os
from typing import Any

from tests.bench.harness import BenchmarkHarness, cli_entrypoint

SYSTEM_PROMPT = "You are a helpful assistant. Be concise."
_PADDING_PARA = (
    "Background: distributed GPU inference, KV cache reuse, prefill/decode "
    "split, prefix caching, request scheduling, backpressure, OOM guards, "
    "chunked dedup, and the cross-node migration path. Consider both happy "
    "paths and failure modes."
)


def build_messages(*, context_tokens: int = 60_000) -> list[dict[str, str]]:
    """Build a single message list whose user content is ~`context_tokens` tokens.

    Uses the conservative 4-chars-per-token ratio: a 60K-token request
    needs ~240K chars of body. We repeat the padding paragraph until we
    exceed that target, so the actual tokenizer sees a tiny bit more.
    """
    target_chars = context_tokens * 4
    paragraphs: list[str] = []
    while sum(len(p) for p in paragraphs) < target_chars:
        paragraphs.append(_PADDING_PARA)
    user = "\n\n".join(paragraphs)
    # Trim to the exact target so the request body isn't too long.
    user = user[:target_chars]
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user",   "content": user + "\n\nSummarise the above in two sentences."},
    ]


@cli_entrypoint(
    build_messages=lambda args: build_messages(),
    scenario_id="long_context",
    default_n=5,
    default_concurrency=1,
    default_warmup=1,
    default_max_tokens=200,
)
async def main() -> None:  # pragma: no cover
    raise RuntimeError("unreachable: cli_entrypoint injects the body")


async def run(
    *,
    context_tokens: int = 60_000,
    n: int = 5,
    max_tokens: int = 200,
    base_url: str | None = None,
    output: str | None = None,
) -> Any:
    """Programmatic entry point."""
    from uuid import uuid4
    harness = BenchmarkHarness(
        base_url=base_url or os.environ.get("COORD_URL", "http://localhost:9000"),
    )
    msgs = build_messages(context_tokens=context_tokens)
    for i in range(n):
        sid = f"longctx-{context_tokens // 1000}k-{i:02d}-{uuid4().hex[:6]}"
        await harness.submit(messages=msgs, session_id=sid, max_tokens=max_tokens)
    rep = harness.report()
    if output:
        harness.save(output, scenario_id="long_context")
    return rep


__all__ = ["build_messages", "run", "main"]


if __name__ == "__main__":
    import asyncio as _asyncio
    _asyncio.run(main())

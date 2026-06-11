"""P/D Split Verification — ported from run-niah.sh verify_pd_split().

Uses llama-server metrics as source of truth to confirm:
  1. RTX did prefill  (rtx:prompt_tokens_total delta > 100)
  2. P100 had KV hit  (p100:prompt_tokens_total delta <= 5)
  3. RTX didn't decode (rtx:tokens_predicted_total delta <= 5)
  4. P100 did decode   (p100:tokens_predicted_total delta >= 3)
  5. KV was restored   (P100 slot n_past > 100)
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class PdSplitResult:
    """Result of a P/D split verification check."""

    pass_: bool
    rtx_ppt_pre: int = 0
    rtx_ppt_post: int = 0
    rtx_ppt_delta: int = 0
    rtx_ppt_pass: bool = False

    rtx_tpt_pre: int = 0
    rtx_tpt_post: int = 0
    rtx_tpt_delta: int = 0
    rtx_tpt_pass: bool = False

    p100_ppt_pre: int = 0
    p100_ppt_post: int = 0
    p100_ppt_delta: int = 0
    p100_ppt_pass: bool = False

    p100_tpt_pre: int = 0
    p100_tpt_post: int = 0
    p100_tpt_delta: int = 0
    p100_tpt_pass: bool = False

    p100_n_past: int = 0
    p100_n_past_pass: bool = False

    cached_avg: float = 0.0

    issues: list[str] = field(default_factory=list)

    def summary_line(self) -> str:
        """Short one-line summary for the report."""
        icons = []
        for name, ok in [
            ("RTX-prefill", self.rtx_ppt_pass),
            ("P100-KV-hit", self.p100_ppt_pass),
            ("RTX-no-decode", self.rtx_tpt_pass),
            ("P100-decode", self.p100_tpt_pass),
            ("KV-restored", self.p100_n_past_pass),
        ]:
            icons.append(f"{name}={chr(0x2713) if ok else chr(0x2717)}")

        return (
            f"RTX +{self.rtx_ppt_delta}ppt/+{self.rtx_tpt_delta}tpt | "
            f"P100 +{self.p100_ppt_delta}ppt/+{self.p100_tpt_delta}tpt | "
            f"{self.cached_avg:.0f}% cached | "
            + " ".join(icons)
        )


def verify(deltas: dict[str, int], p100_n_past: int, cached_avg: float = 0.0) -> PdSplitResult:
    """Verify P/D split from metric deltas captured by LlamaMetrics.

    Args:
        deltas: Dict from LlamaMetrics.capture_post() with keys like
                rtx:llamacpp:prompt_tokens_total_delta, etc.
        p100_n_past: Post-test n_past from P100 slot 0.
        cached_avg: Average cached_tokens percentage across all requests.

    Returns:
        PdSplitResult with pass/fail for each check.
    """
    issues: list[str] = []

    rtx_ppt_d = deltas.get("rtx:llamacpp:prompt_tokens_total_delta", 0)
    rtx_tpt_d = deltas.get("rtx:llamacpp:tokens_predicted_total_delta", 0)
    p100_ppt_d = deltas.get("p100:llamacpp:prompt_tokens_total_delta", 0)
    p100_tpt_d = deltas.get("p100:llamacpp:tokens_predicted_total_delta", 0)

    # 1. RTX must have processed many prompt tokens (prefill happened)
    rtx_ppt_pass = rtx_ppt_d > 100
    if not rtx_ppt_pass:
        issues.append(f"RTX prompt_tokens delta too small (+{rtx_ppt_d}, expected >100)")

    # 2. P100 should NOT have processed many prompt tokens (KV cache hit)
    p100_ppt_pass = p100_ppt_d <= 5
    if not p100_ppt_pass:
        issues.append(f"P100 re-prefilled (+{p100_ppt_d} prompt tokens — KV cache NOT used)")

    # 3. RTX should NOT have generated many tokens (not decode)
    rtx_tpt_pass = rtx_tpt_d <= 5
    if not rtx_tpt_pass:
        issues.append(f"RTX decoded (+{rtx_tpt_d} tokens — RTX did the decode, not P100)")

    # 4. P100 must have generated tokens (actual decode)
    p100_tpt_pass = p100_tpt_d >= 3
    if not p100_tpt_pass:
        issues.append(f"P100 didn't decode (+{p100_tpt_d} tokens — no generation on P100)")

    # 5. P100 n_past must be reasonable (KV was restored)
    p100_n_past_pass = p100_n_past > 100
    if not p100_n_past_pass:
        issues.append(f"P100 n_past={p100_n_past} (too low, KV may not have been restored)")

    passed = len(issues) == 0

    return PdSplitResult(
        pass_=passed,
        rtx_ppt_pre=deltas.get("rtx:llamacpp:prompt_tokens_total_pre", 0),
        rtx_ppt_post=deltas.get("rtx:llamacpp:prompt_tokens_total_post", 0),
        rtx_ppt_delta=rtx_ppt_d,
        rtx_ppt_pass=rtx_ppt_pass,
        rtx_tpt_pre=deltas.get("rtx:llamacpp:tokens_predicted_total_pre", 0),
        rtx_tpt_post=deltas.get("rtx:llamacpp:tokens_predicted_total_post", 0),
        rtx_tpt_delta=rtx_tpt_d,
        rtx_tpt_pass=rtx_tpt_pass,
        p100_ppt_pre=deltas.get("p100:llamacpp:prompt_tokens_total_pre", 0),
        p100_ppt_post=deltas.get("p100:llamacpp:prompt_tokens_total_post", 0),
        p100_ppt_delta=p100_ppt_d,
        p100_ppt_pass=p100_ppt_pass,
        p100_tpt_pre=deltas.get("p100:llamacpp:tokens_predicted_total_pre", 0),
        p100_tpt_post=deltas.get("p100:llamacpp:tokens_predicted_total_post", 0),
        p100_tpt_delta=p100_tpt_d,
        p100_tpt_pass=p100_tpt_pass,
        p100_n_past=p100_n_past,
        p100_n_past_pass=p100_n_past_pass,
        cached_avg=cached_avg,
        issues=issues,
    )

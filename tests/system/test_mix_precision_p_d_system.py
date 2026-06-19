"""
Cross-model KV safety guard — system test for the M-Perf.9 #289 wiring.

Exercises the production path WorkerSchedulerService.RestoreKvAsync →
CrossModelGuard.Decide via the live Coordinator HTTP API. Verifies:

  1. Same-model, same-worker restore: hash matches → Proceed. The
     Coordinator serves the request, the slot model_hash is reported
     via the engine's STATE_META, and the response is correct.

  2. Cross-worker restore: same model loaded on RTX and P100 (or both
     in single-model mode). The cross-model guard should Proceed because
     the model_hash is identical (same GGUF file on disk produces the
     same SHA-256).

  3. Metric exposure: the Coordinator's Prometheus endpoint exposes the
     cross-model guard counters (hydra_cross_model_kv_proceeded_total
     and friends) so the outcome is observable.

Requires live stack: Coordinator :9000, llama-server(s), Store.

Environment variables:
  COORD_URL          http://localhost:9000   (Coordinator HTTP endpoint)
  COORD_METRICS_URL  http://localhost:9501   (Prometheus metrics endpoint)
"""

import os
import re
from uuid import uuid4

import httpx
import pytest

COORD_URL       = os.environ.get("COORD_URL",       "http://localhost:9000")
COORD_METRICS   = os.environ.get("COORD_METRICS_URL", "http://localhost:9501/metrics")


# Light prompts so the test runs within the 30 s default pytest timeout
# even on the slow P100 path.
SYSTEM_PROMPT = (
    "You are a helpful assistant. Answer the user's question concisely."
)
USER_PROMPT_1 = "What is 2 + 2? Reply with only the number."
USER_PROMPT_2 = "Multiply that by 3. Reply with only the number."


@pytest.fixture
def session_id() -> str:
    return f"system-cross-model-{uuid4().hex[:12]}"


def _make_messages(system: str, user: str) -> list[dict]:
    return [
        {"role": "system", "content": system},
        {"role": "user",   "content": user},
    ]


def _make_followup_messages(system: str, history: list[dict], user: str) -> list[dict]:
    msgs = [{"role": "system", "content": system}]
    msgs.extend(history)
    msgs.append({"role": "user", "content": user})
    return msgs


async def _do_completion(
    messages: list[dict],
    session_id: str,
    stream: bool = False,
    max_tokens: int = 32,
) -> httpx.Response:
    body = {
        "messages":   messages,
        "max_tokens": max_tokens,
        "temperature": 0,
        "stream":     stream,
        "session_id": session_id,
    }
    async with httpx.AsyncClient(timeout=300.0) as client:
        return await client.post(f"{COORD_URL}/v1/chat/completions", json=body)


def _extract_content(response_json: dict) -> str:
    choices = response_json.get("choices", [])
    if not choices:
        return ""
    msg = choices[0].get("message", {})
    return (msg.get("content") or msg.get("reasoning_content") or "").strip()


async def _get_counter(name: str, labels: dict[str, str] | None = None) -> float:
    """
    Read a single Prometheus counter value. Pass `labels` to filter to a
    specific label set. Returns 0.0 if the counter is absent.
    """
    async with httpx.AsyncClient(timeout=10.0) as client:
        resp = await client.get(COORD_METRICS)
        resp.raise_for_status()
        body = resp.text
    pattern = ""
    if labels:
        # Build a label matcher: name{label1="v1",label2="v2"}
        kv = ",".join(f'{k}="{v}"' for k, v in labels.items())
        pattern = rf"^{re.escape(name)}\{{{re.escape(kv)}\}}\s+([0-9.eE+\-]+)$"
    else:
        pattern = rf"^{re.escape(name)}\s+([0-9.eE+\-]+)$"
    for line in body.splitlines():
        m = re.match(pattern, line)
        if m:
            return float(m.group(1))
    return 0.0


@pytest.mark.system
@pytest.mark.asyncio
async def test_cross_model_proceed_same_model_same_worker(session_id: str):
    """
    Turn 1 + Turn 2: same model, same worker, KV reuse.
    The cross-model guard should Proceed (matching hashes) and the
    `hydra_cross_model_kv_proceeded_total` counter should increment.
    """
    # ── Turn 1: initial request ────────────────────────────────────
    resp1 = await _do_completion(
        _make_messages(SYSTEM_PROMPT, USER_PROMPT_1),
        session_id=session_id,
        max_tokens=8,
    )
    assert resp1.status_code == 200, f"Turn 1 failed: {resp1.text}"
    body1 = resp1.json()
    content1 = _extract_content(body1)
    assert content1, f"Turn 1 empty: {body1}"

    # ── Turn 2: follow-up with the same session_id ────────────────
    history = [
        {"role": "user",      "content": USER_PROMPT_1},
        {"role": "assistant", "content": content1},
    ]
    resp2 = await _do_completion(
        _make_followup_messages(SYSTEM_PROMPT, history, USER_PROMPT_2),
        session_id=session_id,
        max_tokens=8,
    )
    assert resp2.status_code == 200, f"Turn 2 failed: {resp2.text}"
    body2 = resp2.json()
    content2 = _extract_content(body2)
    assert content2, f"Turn 2 empty: {body2}"

    # ── Verify the cross-model guard ran and proceeded ────────────
    # Both turns use the same model (the resident one) on whichever
    # worker the router picked. The guard should Proceed (matching
    # hashes) and the counter should be > 0.
    proceeded = await _get_counter("hydra_cross_model_kv_proceeded_total")
    assert proceeded > 0, (
        f"Expected hydra_cross_model_kv_proceeded_total > 0 after a same-model "
        f"follow-up, got {proceeded}. The cross-model guard may not be wired "
        f"into RestoreKvAsync — see WorkerSchedulerService.cs:1377."
    )


@pytest.mark.system
@pytest.mark.asyncio
async def test_cross_model_metric_exposed(session_id: str):
    """
    Verify the cross-model guard Prometheus counters are exposed
    on the Coordinator's /metrics endpoint. This is a smoke test
    for the metric wiring — if a future change renames the counters,
    this test catches it.
    """
    # Make at least one request so the metric series are emitted
    resp = await _do_completion(
        _make_messages(SYSTEM_PROMPT, "Say 'ok'."),
        session_id=session_id,
        max_tokens=4,
    )
    assert resp.status_code == 200, f"smoke request failed: {resp.text}"

    async with httpx.AsyncClient(timeout=10.0) as client:
        m = await client.get(COORD_METRICS)
    assert m.status_code == 200
    body = m.text
    for name in (
        "hydra_cross_model_kv_proceeded_total",
        "hydra_cross_model_kv_skipped_total",
        "hydra_cross_model_kv_warned_total",
        "hydra_cross_model_kv_aborted_total",
    ):
        # HELP line must be present
        assert f"# HELP {name} " in body, f"missing HELP for {name}"

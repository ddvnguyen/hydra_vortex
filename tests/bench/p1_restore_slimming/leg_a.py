#!/usr/bin/env python3
"""P1 smoke leg (a): 6-turn fixed-session chat against one coordinator.

Mirrors A/B #5 workload shape: ~5.7k tokens/turn, max_tokens=64,
force_mode=solo (bypasses warm reuse -> every turn exercises the
chunked-KV restore path), fixed X-Session-Id.
"""
import json
import sys
import time
import urllib.request
import urllib.error

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 19000
SID = sys.argv[2] if len(sys.argv) > 2 else "p1-smoke-a"
N_TURNS = 6
MAX_TOKENS = 64
TOK_PER_TURN = 5700

SYSTEM = "You are a helpful, concise assistant. Answer in one short paragraph."


def filler(turn: int, target_tokens: int) -> str:
    sentence = (
        f"Segment {turn:02d}: The quick brown fox jumps over the lazy dog while "
        "maintaining a steady cadence across the open meadow under a pale sky. "
    )
    rep = max(1, (target_tokens * 4) // len(sentence))
    return (sentence * rep).strip()


def main() -> None:
    results = []
    history = []
    for t in range(1, N_TURNS + 1):
        user = filler(t, TOK_PER_TURN)
        msgs = [{"role": "system", "content": SYSTEM}, *history,
                {"role": "user", "content": user}]
        body = {
            "model": "qwen3.5-9b-test",
            "messages": msgs,
            "max_tokens": MAX_TOKENS,
            "temperature": 0,
            "force_mode": "solo",
        }
        req = urllib.request.Request(
            f"http://127.0.0.1:{PORT}/v1/chat/completions",
            data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json", "X-Session-Id": SID},
            method="POST")
        t0 = time.time()
        try:
            with urllib.request.urlopen(req, timeout=900) as r:
                resp = json.loads(r.read())
                dt = time.time() - t0
                usage = resp.get("usage", {})
                content = (resp.get("choices") or [{}])[0].get("message", {}).get("content", "") or ""
                history.append({"role": "user", "content": user})
                history.append({"role": "assistant", "content": content})
                results.append({"turn": t, "status": r.status, "elapsed": round(dt, 1),
                                "prompt_tokens": usage.get("prompt_tokens"),
                                "completion_tokens": usage.get("completion_tokens")})
                print(f"turn {t}: 200 {dt:.1f}s prompt={usage.get('prompt_tokens')} "
                      f"completion={usage.get('completion_tokens')}", flush=True)
        except urllib.error.HTTPError as e:
            dt = time.time() - t0
            detail = e.read()[:300].decode(errors="replace")
            results.append({"turn": t, "status": e.code, "elapsed": round(dt, 1), "error": detail})
            print(f"turn {t}: HTTP {e.code} {dt:.1f}s {detail[:150]}", flush=True)
            break
        except Exception as e:  # noqa: BLE001
            dt = time.time() - t0
            results.append({"turn": t, "status": None, "elapsed": round(dt, 1), "error": str(e)[:200]})
            print(f"turn {t}: FAIL {dt:.1f}s {str(e)[:150]}", flush=True)
            break
    out = f"/tmp/p1_smoke/leg_a_{PORT}.json"
    json.dump({"session": SID, "port": PORT, "results": results}, open(out, "w"), indent=1)
    print(f"WROTE {out}")


if __name__ == "__main__":
    main()

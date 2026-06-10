# System Test Results

Each system test run produces a JSON result file in this directory:
`{test_name}_{ISO-timestamp}.json`

## How to Run System Tests

**Prerequisites** — all 3 containers running:

| Service          | Location                          |
|------------------|-----------------------------------|
| llama-server RTX | `localhost:8080`                  |
| llama-server P100| `192.168.122.21:8086`             |
| Hydra.Core       | `localhost:9000` (HTTP) + `:9500` (Store RPC) |

```bash
# Full system test suite
python -m pytest tests/system/ -v -m system

# Individual suites
python -m pytest tests/system/test_full_workflow_system.py -v -m system
python -m pytest tests/system/test_stress_system.py -v -m system
python -m pytest tests/system/test_large_prompt_system.py -v -m system
```

## Result File Fields

| Field              | Description                                |
|--------------------|--------------------------------------------|
| test_name          | pytest node ID (last segment)              |
| nodeid             | Full pytest node ID                        |
| timestamp          | ISO 8601 UTC when test finished            |
| duration_s         | Wall-clock seconds                         |
| status             | passed / failed / error                    |
| error              | Error summary (only on failure)            |
| coordinator        | Hydra.Core status, routing stats, session count       |
| llama_rtx          | Prompt/token metrics + slots from RTX      |
| llama_p100         | Prompt/token metrics + slots from P100     |

## Reviewing Results

- Compare latest result against previous runs to spot regressions
- Failed tests include service snapshots for root cause analysis:
  - `prompt_ms` high -> KV cache not restored
  - `404 Session not found` -> session registration bug
  - `requests_processing > 0` -> stuck slot
  - `cached_tokens == 0` -> no cache reuse

## Improving Tests & Fixing Bugs

1. Run the failing test, check result file for service context
2. Fix source code (Hydra.Core or Store)
3. Re-run the specific test
4. Compare new result against old failure -- error should change/resolve
5. Run full suite to confirm no regressions
6. Commit test fix + add result file to the PR for reviewer context

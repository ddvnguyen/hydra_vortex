# S3 staleness cost: stale-vs-fresh h + throughput pricing (N=38, k=10)

Stale = session A's converged warm set (cumulative-count top-38 over A's full decodes, n=1000/1500). Fresh = B's own H5 warm prior frozen at the window start (n=600/1000). Eval = B decode [100,200), micro-averaged routed-slot recall (n=100x48x10). Fresh-warm sanity mean = 0.4009 (scorer_v4 warm@38 = 0.4009, n=18 — reproduces).

## Headline: stale vs fresh by pair type

| pair type | n | h(stale) | h(fresh) | delta | S(stale) | S(fresh) | S loss |
|---|---|---|---|---|---|---|---|
| same-subject | 54 | 0.3485 | 0.4009 | -0.0524 | 1.2344 | 1.2795 | +0.0451 |
| cross-subject | 144 | 0.2394 | 0.4009 | -0.1614 | 1.1501 | 1.2795 | +0.1295 |

_Estimators: h(stale) converged-warm full-budget; h(fresh) H5-warm full-budget; eval micro-recall [100,200). S(h)=1/(1-0.545h), O/D~0 (brief spec)._

## Same-subject split (stale from same subject, other session)

| B subject | n | h(stale) | h(fresh) | delta | S(stale) | S(fresh) |
|---|---|---|---|---|---|---|
| coding | 18 | 0.3811 | 0.4017 | -0.0206 | 1.2622 | 1.2803 |
| reasoning | 18 | 0.2954 | 0.3867 | -0.0913 | 1.1919 | 1.2670 |
| essay | 18 | 0.3689 | 0.4143 | -0.0453 | 1.2517 | 1.2916 |

## Cross-subject cells (A subject -> B subject)

| A -> B | n | h(stale) | h(fresh) | delta | S(stale) | S(fresh) |
|---|---|---|---|---|---|---|
| coding -> reasoning | 24 | 0.2620 | 0.3867 | -0.1247 | 1.1666 | 1.2670 |
| coding -> essay | 24 | 0.2277 | 0.4143 | -0.1866 | 1.1417 | 1.2916 |
| reasoning -> coding | 24 | 0.2639 | 0.4017 | -0.1378 | 1.1680 | 1.2803 |
| reasoning -> essay | 24 | 0.2119 | 0.4143 | -0.2024 | 1.1306 | 1.2916 |
| essay -> coding | 24 | 0.2456 | 0.4017 | -0.1561 | 1.1545 | 1.2803 |
| essay -> reasoning | 24 | 0.2255 | 0.3867 | -0.1611 | 1.1401 | 1.2670 |

Alpha-0.57 cross-check: same S(stale)=1.2479 S(fresh)=1.2962; cross S(stale)=1.1580 S(fresh)=1.2962.

Done in 21s. k=10 everywhere, no impl changes.

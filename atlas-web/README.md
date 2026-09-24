# atlas-web — Colibri Brain hosted as a separated Hydra service

Owner ruling d-075e1e3b9c: the Brain UI lives in a **separated service**, not
inside llama-server. The fork (`src/llama-cpp`) ships read-only endpoints only;
this service polls one or **many** engines (5060 Ti / 3060 / P100 VM) through
the Stage B read contract and serves the Brain page — one cortex view for the
whole rig.

Ported from upstream **JustVugg/colibri @ `a8f2ca62`** (`web/src/Brain.tsx`,
`web/src/lib/api.ts`, i18n, styles). JS runtime: **bun 1.4.2**.

## Run

```bash
cd atlas-web
bun install
bun run build          # vite build → dist/
# tools/atlas/out/*.{json,pin} are gitignored — generate them first (mock needs
# expert-ranks.json to populate the EMAP; without it the server still starts,
# degraded to an empty EMAP — see S2 graceful-degraded note in server/server.ts):
python3 ../tools/atlas/emit.py --help >/dev/null && echo "see tools/atlas/README.md for inputs"
bun run serve          # bun server/server.ts → http://localhost:8619
```

Dev UI loop (optional): `bun run dev` (vite :5173) proxies nothing — point the
Brain at the service by fetching same-origin; for dev use vite's server.proxy
or just run `bun run serve` and load the built page.

## Env

| Var | Default | Meaning |
|---|---|---|
| `ATLAS_WEB_PORT` | `8619` | HTTP port |
| `ATLAS_ENGINES` | one mock engine | `id=name=url[@experts.json],…`; url `-` = mock |
| `ATLAS_RANKS_JSON` | `../tools/atlas/out/expert-ranks.json` | ranks input for the mock EMAP — **run `tools/atlas/emit.py` first to generate it** (gitignored; server degrades to empty EMAP if absent) |
| `ATLAS_EXPERTS_JSON` | `../tools/atlas/out/experts.json` | default atlas artifact (observability tier) — **run `tools/atlas/emit.py` first** (gitignored) |

## Stage B HTTP contract (the seam between llama.cpp and Colibri)

`GET {engine}/experts` must return (Colibri byte encoding adopted verbatim,
design §B):

```json
{
  "seq": 17, "rows": 48, "cols": 512,
  "map": "<hex, 2 chars/cell: tier=byte>>6 (0=disk|1=RAM|2=VRAM), heat=byte&63>",
  "hits": "<hex bitmap, bit i set if expert i fired in the last step>",
  "geometry": {
    "engine_id": "qwen38", "model_hash": "<gguf file>",
    "dense_prefix": 0,
    "moe_rows": [0, 1, "...47"],
    "nextn_rows": [],
    "n_expert_used": 8
  }
}
```

- `geometry.moe_rows[]` maps grid row → **real layer index**; `nextn_rows[]`
  are trailing MTP grid rows (design: NextN reported separately, never mixed
  into trunk counts). This is what deletes Colibri's hardcoded GLM `row+3` /
  MTP-78 remap — the UI derives the grid from the engine.
- The mock adapter synthesizes exactly this shape from the draft artifacts
  (`tools/atlas/out/`), so the UI is verifiable before Stage B lands.
- **Synthetic semantics (mock only):** tier bands = top-53/layer → `VRAM`
  (pin export), next 53 → `RAM`, rest cold; hits = heat-weighted roulette,
  k=8 placeholders. Real tiers/counts come from Stage A/B counters.

## Endpoints (this service)

| Route | Meaning |
|---|---|
| `GET /experts?engine=ID` | Colibri ExpertMap shape (+ geometry block) for one engine |
| `GET /experts.json?engine=ID` | observability-tier atlas artifact (per-model, Colibri Brain schema + spec/reliability) |
| `GET /health` | per-engine ok/seq aggregate |
| `GET /api/engines` | engine registry |

## Deviations from upstream (kept minimal, tagged `hydra:` in source)

1. `src/main.tsx` — Brain-only entry (chats/profiling tabs out of scope,
   design §D); health poll every 5s via vendored `lib/api.ts`.
2. `Brain.tsx` — layer mapping from the geometry payload (upstream GLM
   fallback kept for direct-Colibri mode); optional `engineId` prop →
   `?engine=` query; tooltip appends the #175 `weak` qualifier when
   `spec < 0.7` with `reliability`.
3. Everything else (Brain render loop, i18n, api, styles) is vendored
   verbatim from upstream.

## Status

- Scaffold (S-D1, hydra_vortex#771): mock adapter + Brain port, visual
  verification against the draft qwen4exp atlas (2431 experts, 48 layers).
- Next: swap mock for the real Stage B endpoint when the fork lands it
  (Gate 0: Stage-1 dual-op store on `baseline-qwen4exp-mtp`).

# atlas-web — Colibri Brain hosted as a separated Hydra service

Owner ruling d-075e1e3b9c: the Brain UI lives in a **separated service**, not
inside llama-server. The fork (`src/llama-cpp`) ships read-only endpoints only;
this service polls one or **many** engines (5060 Ti / 3060 / P100 VM) through
the Stage B read contract and serves the Brain page — one cortex view for the
whole rig.

Ported from upstream **JustVugg/colibri @ `9d5d05de7f`** (v1.12.0), previously
pinned at `a8f2ca62`. Provenance: baseline `f4ea418cd` + provenance-only
merge `5542d1d7a` (`--allow-unrelated-histories`, tree unchanged) + port
commits under `atlas-web/`. JS runtime: **bun 1.4.2**.

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

1. **Separated-service shell (design §D).** Default view is **Brain**; Chat /
   Brio / Galaxy / Profiling / Settings are reachable via `NavigationDock`.
   Atlas engine selector + `/health` poll feed `engineId` into Brain/Galaxy/
   BrainWorkspace (`?engine=`). `lib/reasoning.ts` kept (upstream deleted it);
   Markdown uses `text=` prop (upstream 1.12.0 rename).
2. `Brain.tsx` / `BrainWorkspace.tsx` — layer mapping from the geometry payload
   (upstream GLM fallback kept for direct-Colibri mode); optional `engineId`
   → `?engine=`; explorer fetches `/experts.json?engine=` (never BASE_URL).
   Tooltip appends the #175 `weak` qualifier when `spec < 0.7` with
   `reliability`; Metrics panel reads expert-metrics schema v2.
3. `Galaxy.tsx` — hydra multi-engine + **explicit atlas-failure badge** on
   `/experts.json` 404/network error (retry button; no silent degrade).
4. `server/server.ts` — ours (Stage B contract); engine-id refusal discipline
   retained. Extend only if a new atlas endpoint is required.
5. CSS: shared upstream base + `chat-design.css` + `brain-design.css` + hydra
   metrics/Galaxy/engine-row/reasoning styles in `index.css`.

### Not ported (out of scope — do not touch engine/site)

Upstream `main` → `9d5d05de7f` includes non-web work that this port deliberately
skips (no `c/`, `site/`, `docs/` changes):

| Upstream area | Examples | Why skipped |
|---|---|---|
| `c/` engine | emap after every turn (`4baf7716d`), flush-on-SIGTERM (`becab2b58`), brio API (`37c54e11c`, `07b8d0653`) | owner: do not touch `src/llama-cpp` / rig |
| `site/` | searchable models, brio section, theme switch (`1f879b05a`) | not atlas-web |
| `docs/` | brio mode / Brain+Profiling readme notes | not atlas-web |

Web-only upstream deltas a8f2ca62…9d5d05de7f **are** ported: redesigned shell
(`chat-design`, `brain-design`, `NavigationDock`, `Brand`), `Brio.tsx`,
`lib/cortex.ts`, `lib/brain-topics.ts`, `askBrio` in `lib/api.ts`, fonts/icon,
i18n `ui.*` / `brio.*` / `topic.*` keys.

## Status

- Scaffold (S-D1, hydra_vortex#771): mock adapter + Brain port, visual
  verification against the draft qwen4exp atlas (2431 experts, 48 layers).
- Upstream v1.12.0 web redesign + Brio ported onto the hydra shell (this track).
- Next: swap mock for the real Stage B endpoint when the fork lands it
  (Gate 0: Stage-1 dual-op store on `baseline-qwen4exp-mtp`).

// atlas-web — separated service hosting the Colibri Brain port (design §D,
// owner ruling d-075e1e3b9c). Aggregates one or many llama.cpp-fork engines
// and re-serves the Stage B read contract in the exact Colibri ExpertMap shape
// the vendored Brain expects. The fork ships read-only endpoints only; the
// React build never enters the engine binary.
//
// Stage B HTTP contract (what a llama-server must emit on GET /experts —
// Colibri byte encoding adopted verbatim per design §B):
//   {
//     "seq": <int>, "rows": <int MoE grid rows>, "cols": <int experts/layer>,
//     "map": "<hex, 2 chars/cell: tier=byte>>6 (0=disk/1=RAM/2=VRAM), heat=byte&63>",
//     "hits": "<hex bitmap, bit i set if expert i fired in the last step(s)>",
//     "geometry": {
//       "engine_id": "<arch + FNV-1a(model hash)>", "model_hash": "<gguf>",
//       "dense_prefix": <int>, "moe_rows": [<real layer idx per grid row>],
//       "nextn_rows": [<real idx of trailing MTP grid rows, [] if none>],
//       "n_expert_used": <int top-k>
//     }
//   }
// The mock adapter below synthesizes exactly this shape from the offline
// artifacts in tools/atlas/out/ so the UI is verifiable before any engine
// endpoint exists (Stage B pending behind Gate 0).

import { join, dirname } from "node:path"
import { fileURLToPath } from "node:url"
import { readFileSync } from "node:fs"

const HERE = dirname(fileURLToPath(import.meta.url))
const ROOT = join(HERE, "..", "..")
const DIST = join(HERE, "..", "dist")

const PORT = Number(process.env.ATLAS_WEB_PORT ?? 8619)
// LAN-visible by default (0.0.0.0); opt into localhost-only via ATLAS_WEB_HOST
const HOST = process.env.ATLAS_WEB_HOST ?? "0.0.0.0"
const DEFAULT_ATLAS = join(ROOT, "tools", "atlas", "out", "experts.json")
const DEFAULT_RANKS = join(ROOT, "tools", "atlas", "out", "expert-ranks.json")

interface EngineCfg {
  id: string
  name: string
  mode: "mock" | "engine"
  url: string
  atlas: string
}

function parseEngines(): EngineCfg[] {
  const raw = process.env.ATLAS_ENGINES
  if (raw) {
    // "id=name=url[@atlas.json],id=name=url[@atlas.json]" — url "-"/"mock:" = mock
    return raw.split(",").filter(Boolean).map((spec) => {
      const parts = spec.split("=") // id=name=url[@atlas.json]
      const id = parts[0]
      const name = parts[1] ?? id
      const rest = parts.slice(2).join("=")
      const at = rest.lastIndexOf("@")
      const target = at === -1 ? rest : rest.slice(0, at)
      const atlas = at === -1 ? "" : rest.slice(at + 1)
      const mock = target === "-" || target.startsWith("mock:")
      const url = mock ? "" : target.replace(/\/+$/, "")
      return { id, name, mode: mock ? "mock" : "engine", url, atlas } as EngineCfg
    })
  }
  return [{ id: "mock-qwen38", name: "Mock Qwen3.8-Flash-Next", mode: "mock", url: "", atlas: DEFAULT_ATLAS }]
}

// ---------------------------------------------------------------------------
// Mock adapter — Stage B shape synthesized from the draft atlas artifacts.
interface RankLayer { experts: { id: number; heat: number; p: Record<string, number>; reap_saliency: number | null }[] }
interface RankFile { version: number; engine_id: string; model_hash: string; layers: Record<string, RankLayer> }

const PIN_VRAM = 53 // experts.pin: top-53/layer — synthetic VRAM (pinned) band
const PIN_RAM = 53  // next band — synthetic RAM tier; rest = cold

function mulberry32(seed: number) {
  let a = seed >>> 0
  return () => {
    a |= 0; a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

class MockEngine {
  private ranks: RankFile
  private sorted: Map<number, { id: number; heat: number }[]> = new Map()
  private rows: number[] = []
  private cols = 0
  private seq = 0

  constructor(public cfg: EngineCfg, ranksPath: string) {
    this.ranks = JSON.parse(readFileSync(ranksPath, "utf8")) as RankFile
    const layerKeys = Object.keys(this.ranks.layers).map(Number).sort((a, b) => a - b)
    this.rows = layerKeys
    for (const il of layerKeys) {
      const es = (this.ranks.layers[String(il)]?.experts ?? []).slice().sort((a, b) => b.heat - a.heat)
      this.sorted.set(il, es.map(e => ({ id: e.id, heat: e.heat })))
      this.cols = Math.max(this.cols, ...es.map(e => e.id + 1))
    }
  }

  expertMeta(): Record<string, unknown> {
    const rows = this.rows.length
    const cols = this.cols
    const map = new Uint8Array(rows * cols)
    const hits = new Uint8Array(Math.ceil((rows * cols) / 8))
    const rng = mulberry32(++this.seq * 2654435761)
    const k = 8 // n_expert_used — mock only; Stage A/B will report the real k
    for (let r = 0; r < rows; r++) {
      const ranked = this.sorted.get(this.rows[r]) ?? []
      // tier bands: hot-first rank → 2 (pinned/VRAM) | 1 (RAM) | 0 (cold)
      for (let i = 0; i < ranked.length && i < PIN_VRAM + PIN_RAM; i++) {
        const byte = i < PIN_VRAM ? (2 << 6) | Math.min(63, ranked[i].heat) : (1 << 6) | Math.min(63, ranked[i].heat)
        map[r * cols + ranked[i].id] = byte
      }
      // hits: heat-weighted roulette over the ranked list for k picks
      const total = ranked.reduce((s, e) => s + e.heat, 0)
      const chosen = new Set<number>()
      for (let n = 0; n < k && total > 0; n++) {
        let x = rng() * total
        for (const e of ranked) {
          x -= e.heat
          if (x <= 0) { chosen.add(e.id); break }
        }
      }
      for (const id of chosen) {
        const i = r * cols + id
        hits[i >> 3] |= 1 << (i & 7)
      }
    }
    const hex = (b: Uint8Array) => Buffer.from(b).toString("hex")
    return {
      seq: this.seq,
      rows,
      cols,
      map: hex(map),
      hits: hex(hits),
      geometry: {
        engine_id: this.ranks.engine_id,
        model_hash: this.ranks.model_hash,
        dense_prefix: 0,
        moe_rows: this.rows,
        nextn_rows: [] as number[],
        n_expert_used: k,
      },
    }
  }
}

// ---------------------------------------------------------------------------
interface Cached { payload: Record<string, unknown> | null; ok: boolean; ts: number }
const cache = new Map<string, Cached>()

async function pollEngine(cfg: EngineCfg, mock?: MockEngine): Promise<Cached> {
  if (cfg.mode === "mock") {
    const fresh = { payload: mock!.expertMeta(), ok: true, ts: Date.now() }
    cache.set(cfg.id, fresh)
    return fresh
  }
  try {
    const res = await fetch(`${cfg.url}/experts`, { signal: AbortSignal.timeout(2000) })
    if (!res.ok) throw new Error(`engine /experts ${res.status}`)
    const body = (await res.json()) as Record<string, unknown>
    if (typeof body.rows !== "number" || typeof body.cols !== "number" || typeof body.map !== "string") {
      throw new Error("engine payload is not Stage B shape")
    }
    // hydra: cache the success so a later engine outage keeps serving the
    // last frame with ok:false instead of a bare 502 (was: cache.get with
    // no matching cache.set — the fallback path was dead code).
    const fresh = { payload: body, ok: true, ts: Date.now() }
    cache.set(cfg.id, fresh)
    return fresh
  } catch (err) {
    const last = cache.get(cfg.id)
    if (last?.payload) return { ...last, ok: false, ts: Date.now() }
    throw err
  }
}

const engines = parseEngines()
const mocks = new Map<string, MockEngine>()
for (const cfg of engines) {
  if (cfg.mode === "mock") {
    const ranksPath = process.env.ATLAS_RANKS_JSON ?? DEFAULT_RANKS
    mocks.set(cfg.id, new MockEngine(cfg, ranksPath))
  }
  if (cfg.mode === "engine" && !cfg.atlas) cfg.atlas = ""; // engine-hosted artifact: proxy {url}/experts.json (engine-id refusal discipline — never serve another model's atlas)
  else if (!cfg.atlas) cfg.atlas = DEFAULT_ATLAS
}

function cors(): Record<string, string> {
  return { "Access-Control-Allow-Origin": "*", "Cache-Control": "no-store" }
}

async function route(req: Request): Promise<Response> {
  const url = new URL(req.url)
  const path = url.pathname

  // hydra: engine proxy (#776) — forwards <method> /engine-proxy/<id>/<path...>
  // to the configured engine's <url>/<path...>, streaming the body through
  // untouched (SSE included). Engines are server-configured and resolved by
  // id only — never an open proxy. Mock engines have no HTTP surface: 404.
  if (path === "/engine-proxy" || path.startsWith("/engine-proxy/")) {
    const segs = path.split("/").filter(Boolean)
    const cfg = engines.find(e => e.id === decodeURIComponent(segs[1] ?? ""))
    if (!cfg) return new Response(`unknown engine ${segs[1] ?? ""}`, { status: 404, headers: cors() })
    if (cfg.mode !== "engine" || !cfg.url) {
      return new Response(`engine ${cfg.id} has no HTTP surface (mock)`, { status: 404, headers: cors() })
    }
    const target = `${cfg.url}/${segs.slice(2).join("/")}${url.search}`
    const fwdHeaders = new Headers(req.headers)
    fwdHeaders.delete("host")
    fwdHeaders.delete("content-length")
    try {
      const upstream = await fetch(target, {
        method: req.method,
        headers: fwdHeaders,
        body: req.method === "GET" || req.method === "HEAD" ? undefined : req.body,
        // @ts-expect-error Bun supports duplex streaming on RequestInit
        duplex: req.method === "GET" || req.method === "HEAD" ? undefined : "half",
        signal: AbortSignal.timeout(1000 * 60 * 10),
      })
      const outHeaders = new Headers(upstream.headers)
      outHeaders.set("Access-Control-Allow-Origin", "*")
      outHeaders.set("Cache-Control", "no-store")
      return new Response(upstream.body, { status: upstream.status, headers: outHeaders })
    } catch (err) {
      return new Response(JSON.stringify({ error: `engine ${cfg.id} unreachable for ${segs.slice(2).join("/")}` }),
        { status: 504, headers: { ...cors(), "Content-Type": "application/json" } })
    }
  }

  if (path === "/api/engines") {
    return Response.json({ engines: engines.map(({ id, name, mode }) => ({ id, name, mode })) }, { headers: cors() })
  }

  if (path === "/experts" || path === "/v1/experts") {
    const id = url.searchParams.get("engine") ?? engines[0]?.id
    const cfg = engines.find(e => e.id === id)
    if (!cfg) return new Response(`unknown engine ${id}`, { status: 404, headers: cors() })
    try {
      const c = await pollEngine(cfg, mocks.get(cfg.id))
      const headers = { ...cors(), "x-atlas-engine-ok": String(c.ok) }
      return Response.json(c.payload, { headers })
    } catch (err) {
      return Response.json({ error: String(err) }, { status: 502, headers: cors() })
    }
  }

  if (path === "/experts.json" || path === "/v1/experts.json") {
    const id = url.searchParams.get("engine") ?? engines[0]?.id
    const cfg = engines.find(e => e.id === id)
    if (!cfg) return new Response(`unknown engine ${id}`, { status: 404, headers: cors() })
    if (cfg.mode === "engine" && !cfg.atlas) {
      // engine-hosted artifact (fork ships the two atlas files, design §C/D)
      let res: Response
      try {
        res = await fetch(`${cfg.url}/experts.json`, { signal: AbortSignal.timeout(2000) })
      } catch {
        // engine unreachable/timed out — surface 504, never an unhandled 500
        return new Response(JSON.stringify({ error: `engine ${cfg.id} unreachable for experts.json` }),
          { status: 504, headers: { ...cors(), "Content-Type": "application/json" } })
      }
      // propagate upstream status — an engine without its atlas artifact must
      // surface as 404, never as a misleading 200 (engine-id refusal discipline)
      return new Response(res.body, { status: res.status, headers: { ...cors(), "Content-Type": "application/json" } })
    }
    const f = Bun.file(cfg.atlas)
    if (!await f.exists()) return new Response("atlas artifact missing", { status: 404, headers: cors() })
    return new Response(f, { headers: { ...cors(), "Content-Type": "application/json" } })
  }

  if (path === "/health") {
    const out = await Promise.all(engines.map(async (cfg) => {
      try {
        const c = await pollEngine(cfg, mocks.get(cfg.id))
        return { id: cfg.id, mode: cfg.mode, ok: c.ok, seq: (c.payload?.seq as number) ?? null }
      } catch {
        return { id: cfg.id, mode: cfg.mode, ok: false, seq: null }
      }
    }))
    return Response.json({ status: out.some(e => e.ok) ? "ok" : "down", engines: out }, { headers: cors() })
  }

  // static Brain build — GET serves bytes; HEAD serves headers only
  // (health checkers and proxies probe assets with HEAD)
  if (req.method === "GET" || req.method === "HEAD") {
    const rel = path === "/" ? "index.html" : path.slice(1)
    const f = Bun.file(join(DIST, rel))
    if (await f.exists()) return new Response(req.method === "HEAD" ? null : f, { headers: { "Content-Type": f.type, "Content-Length": String(f.size) } })
    const index = Bun.file(join(DIST, "index.html"))
    if (await index.exists()) return new Response(req.method === "HEAD" ? null : index, { headers: { "Content-Type": index.type, "Content-Length": String(index.size) } })
  }
  return new Response("not found", { status: 404 })
}

Bun.serve({
  port: PORT,
  hostname: HOST,
  fetch(req) {
    return route(req).catch((err) => Response.json({ error: String(err) }, { status: 500 }))
  },
})

console.log(`atlas-web: http://${HOST}:${PORT} — engines: ${engines.map(e => `${e.id}(${e.mode})`).join(", ")}`)

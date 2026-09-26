import { useEffect, useRef, useState } from "react"
import { BrainCircuit, Flame, Layers } from "lucide-react"

import { endpoint } from "@/lib/api"
import { useLocale } from "./i18n"

// hydra: Stage B geometry block carried through the atlas-web transform layer
// (docs/design-colibri-expert-atlas.md §B/§D). moe_rows[] maps grid row → real
// layer index; nextn_rows[] are trailing MTP grid rows; dense_prefix is the
// Colibri-compatible `row + prefix` fallback for engines that omit moe_rows.
interface AtlasGeometry {
  engine_id: string
  model_hash: string
  dense_prefix: number
  moe_rows: number[]
  nextn_rows: number[]
  n_expert_used: number
}
interface ExpertMap { rows: number; cols: number; map: string; hits: string; seq: number; telemetry_enabled?: boolean; geometry?: AtlasGeometry }
// hydra: spec/reliability from our experts.json (observability tier, #175);
// schema v2 adds the expert-metrics families (ddvnguyen/expert-metrics):
// reap (Cerebras gate×activation saliency) and edge0 (prerouter predictability)
// — null until those observers are wired; the UI shows honest not-wired badges.
interface AtlasEntry {
  affinity: Record<string, number>; entropy: number; top: string; label: string
  spec?: number; reliability?: string; weak?: boolean
  reap?: { saliency?: number; layer_utilization?: number; routing_collapse_rank?: number } | null
  edge0?: { predictability?: number; prefetch_gain?: number } | null
}
// hydra: expert-metrics provenance block (schema v2) rendered in the Metrics panel
interface AtlasProvenance {
  engine_id?: string; model_hash?: string; telemetry_present?: boolean
  gates?: { replication?: boolean; category_floor?: boolean; lop_validated?: boolean; null_tested?: boolean }
  categories_missing?: string[]
}

// hydra (F4c): foreign/older experts.json shapes can carry entries without
// the fields the UI surfaces (label/affinity). Previously that crashed the
// Brain tab ("Cannot read properties of undefined (reading 'startsWith')").
// Drop unsurfable entries once at fetch time so every downstream consumer
// (metrics panel, tooltip) only ever sees safe entries.
function atlasEntryIsSafe(entry: unknown): entry is AtlasEntry {
  if (!entry || typeof entry !== "object") return false
  const e = entry as AtlasEntry
  return (
    typeof e.label === "string" &&
    e.label.length > 0 &&
    !!e.affinity &&
    typeof e.affinity === "object"
  )
}

function safeAtlas(d: unknown): Record<string, AtlasEntry> | null {
  if (!d || typeof d !== "object") return null
  const experts = (d as { experts?: unknown }).experts
  if (!experts || typeof experts !== "object") return null
  const out: Record<string, AtlasEntry> = {}
  for (const [key, entry] of Object.entries(experts as Record<string, unknown>)) {
    if (atlasEntryIsSafe(entry)) out[key] = entry
  }
  return out
}

const TIER_KEYS = ["tier.disk", "tier.ram", "tier.vram"] as const
const TIER_RGB: [number, number, number][] = [[58, 71, 80], [90, 155, 216], [78, 214, 165]]

function depthRoleKey(row: number, rows: number, isMtp: boolean): string {
  if (isMtp) return "brain.mtp"
  const f = row / Math.max(rows - 1, 1)
  if (f < 0.2) return "brain.early"
  if (f < 0.45) return "brain.lowerMiddle"
  if (f < 0.7) return "brain.upperMiddle"
  if (f < 0.9) return "brain.late"
  return "brain.final"
}

// hydra: engineId selects one engine when the atlas service aggregates many
export function Brain({ baseUrl, apiKey, connected, engineId }: { baseUrl: string; apiKey: string; connected: boolean; engineId?: string }) {
  const { t } = useLocale()
  const engQ = engineId ? `?engine=${encodeURIComponent(engineId)}` : ""
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const wrapRef = useRef<HTMLDivElement>(null)
  const [wrapSize, setWrapSize] = useState({ w: 1200, h: 700 })
  const [data, setData] = useState<ExpertMap | null>(null)
  const [probeErr, setProbeErr] = useState(false)   // (#U3) surface /experts failures instead of an endless spinner
  const [atlas, setAtlas] = useState<Record<string, AtlasEntry> | null>(null)
  // hydra: Metrics panel state (expert-metrics schema v2 provenance + summary)
  const [metrics, setMetrics] = useState<{ prov: AtlasProvenance; fams: { specialists: number; generalists: number; weak: number; meanSpec: number; reap: number; edge0: number } } | null>(null)
  // hydra: active telemetry-family tab in the Recorded metrics panel
  const [metricsTab, setMetricsTab] = useState<"colibri" | "reap" | "edge0">("colibri")
  const [tip, setTip] = useState<{ x: number; y: number; row: number; col: number; tier: number; heat: number } | null>(null)
  const pulseRef = useRef<Float32Array | null>(null)   // per-expert pulse intensity 0..1
  const lastSeq = useRef(0)
  const rafRef = useRef(0)

  // load the expert atlas if published (measured topic affinity, #175)
  useEffect(() => {
    // The atlas lives next to the engine's /experts endpoint, not on the page
    // origin: when the UI is hosted elsewhere (dev server, static hosting) a
    // root-relative fetch pointed at the wrong server and the atlas never
    // loaded. Same base + auth as the live expert map above.
    const base = baseUrl.replace(/\/v1\/?$/, "")
    fetch(endpoint(base, "/experts.json" + engQ), { headers: apiKey ? { Authorization: `Bearer ${apiKey}` } : {} })
      .then(r => r.ok ? r.json() : null).then(d => {
        // hydra (F4c): sanitize foreign atlas entries (skip entries lacking
        // label/affinity) instead of crashing on them downstream.
        const experts = safeAtlas(d)
        if (experts) setAtlas(experts)
        // hydra: schema v2 — derive the Metrics-panel family summary. Families
        // whose observers are not wired stay null in the artifact; count them
        // and show an honest not-wired badge rather than a zero.
        if (experts && d?.provenance) {
          const es = Object.values(experts) as AtlasEntry[]
          const specs = es.filter(e => e.label.startsWith("specialist"))
          // hydra (F4c): weak is an optional flag — guard the property access too
          const weak = es.filter(e => e.weak === true)
          const meanSpec = es.reduce((a, e) => a + (e.spec ?? 0), 0) / Math.max(es.length, 1)
          const reap = es.filter(e => e.reap).length
          const edge0 = es.filter(e => e.edge0).length
          setMetrics({ prov: d.provenance, fams: { specialists: specs.length, generalists: es.length - specs.length, weak: weak.length, meanSpec, reap, edge0 } })
        } else setMetrics(null)
      }).catch(() => {})
  }, [baseUrl, apiKey, engQ])

  // track container size for responsive cell sizing
  useEffect(() => {
    const el = wrapRef.current
    if (!el) return
    const ro = new ResizeObserver(() => {
      setWrapSize({ w: el.clientWidth - 24, h: el.clientHeight - 24 })
    })
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  // poll /experts
  useEffect(() => {
    if (!connected) return
    let disposed = false
    const base = baseUrl.replace(/\/v1\/?$/, "")
    const poll = async () => {
      try {
        const res = await fetch(endpoint(base, "/experts" + engQ), { headers: apiKey ? { Authorization: `Bearer ${apiKey}` } : {} })
        if (!res.ok) throw new Error(`/experts ${res.status}`)
        const next = (await res.json()) as ExpertMap
        if (disposed || !next.rows) return
        setData(next)
        setProbeErr(false)
        if (next.seq !== lastSeq.current && next.hits) {
          lastSeq.current = next.seq
          const n = next.rows * next.cols
          if (!pulseRef.current || pulseRef.current.length !== n) pulseRef.current = new Float32Array(n)
          const p = pulseRef.current
          for (let i = 0; i < n; i++) {
            const byte = parseInt(next.hits.substr((i >> 3) * 2, 2), 16) || 0
            if (byte & (1 << (i & 7))) p[i] = 1
          }
        }
      } catch { if (!disposed) setProbeErr(true) /* surface repeated failures; keep the last frame */ }
    }
    void poll()
    const t = window.setInterval(() => void poll(), 1500)
    return () => { disposed = true; window.clearInterval(t) }
  }, [baseUrl, apiKey, connected, engQ])

  // render loop: grid + decaying pulses
  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas || !data) return
    const ctx = canvas.getContext("2d")
    if (!ctx) return
    const { rows, cols, map } = data
    const cell = Math.max(2, Math.floor(Math.min(wrapSize.w / cols, wrapSize.h / rows)))
    const gap = cell >= 4 ? 1 : 0
    canvas.width = cols * (cell + gap)
    canvas.height = rows * (cell + gap)

    const draw = () => {
      ctx.clearRect(0, 0, canvas.width, canvas.height)
      const p = pulseRef.current
      for (let r = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
          const i = r * cols + c
          const byte = parseInt(map.substr(i * 2, 2), 16) || 0
          const tier = byte >> 6
          const heat = byte & 63
          const [R, G, B] = TIER_RGB[tier] ?? TIER_RGB[0]
          // heat scales brightness: cold experts dim, hot experts full colour
          const lum = 0.35 + 0.65 * Math.min(heat / 24, 1)
          let rr = R * lum, gg = G * lum, bb = B * lum
          const pulse = p ? p[i] : 0
          if (pulse > 0.01) { rr += (255 - rr) * pulse; gg += (255 - gg) * pulse; bb += (255 - bb) * pulse }
          ctx.fillStyle = `rgb(${rr | 0},${gg | 0},${bb | 0})`
          ctx.fillRect(c * (cell + gap), r * (cell + gap), cell, cell)
        }
      }
      let alive = false
      if (p) for (let i = 0; i < p.length; i++) { if (p[i] > 0.01) { p[i] *= 0.94; alive = true } else p[i] = 0 }
      if (alive) rafRef.current = requestAnimationFrame(draw)
    }
    draw()
    const keepalive = window.setInterval(() => { if (!rafRef.current) draw(); rafRef.current = 0 }, 400)
    return () => { cancelAnimationFrame(rafRef.current); window.clearInterval(keepalive) }
  }, [data, wrapSize])

  const onMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    if (!data) return
    const rect = e.currentTarget.getBoundingClientRect()
    const scaleX = e.currentTarget.width / rect.width
    const scaleY = e.currentTarget.height / rect.height
    const cell = Math.max(2, Math.floor(Math.min(wrapSize.w / data.cols, wrapSize.h / data.rows)))
    const gap = cell >= 4 ? 1 : 0
    const col = Math.floor(((e.clientX - rect.left) * scaleX) / (cell + gap))
    const row = Math.floor(((e.clientY - rect.top) * scaleY) / (cell + gap))
    if (row < 0 || row >= data.rows || col < 0 || col >= data.cols) { setTip(null); return }
    const byte = parseInt(data.map.substr((row * data.cols + col) * 2, 2), 16) || 0
    setTip({ x: e.clientX, y: e.clientY, row, col, tier: byte >> 6, heat: byte & 63 })
  }

  const totals = data ? (() => {
    const t = [0, 0, 0]
    for (let i = 0; i < data.rows * data.cols; i++) t[(parseInt(data.map.substr(i * 2, 2), 16) || 0) >> 6]++
    return t
  })() : [0, 0, 0]

  return (
    <div className="brain-page">
      <div className="brain-head">
        <div className="section-title"><BrainCircuit className="size-4" /> {t("brain.title")} — {data ? t("brain.layers", { rows: data.rows, cols: data.cols }) : t("brain.waiting")}</div>
        <div className="brain-legend">
          <span><i style={{ background: "#4ed6a5" }} /> {t("tier.vram")} {totals[2].toLocaleString()}</span>
          <span><i style={{ background: "#5a9bd8" }} /> {t("tier.ram")} {totals[1].toLocaleString()}</span>
          <span><i style={{ background: "#3a4750" }} /> {t("tier.disk")} {totals[0].toLocaleString()}</span>
          <span><Flame className="size-3" /> {t("brain.brightnessHint")}</span>
          <span className="brain-pulse-hint">{t("brain.flashHint")}</span>
        </div>
      </div>
      <div className="brain-canvas-wrap" ref={wrapRef}>
        <canvas ref={canvasRef} onMouseMove={onMove} onMouseLeave={() => setTip(null)} />
        {!connected && <p className="runtime-unavailable">{t("brain.connectHint")}</p>}
        {connected && data && (data.telemetry_enabled === false || totals[0] === data.rows * data.cols) && (
          <p className="runtime-unavailable">{t("brain.noTelemetry")}</p>
        )}
      </div>
      {/* hydra: Metrics panel — what expert-metrics (ddvnguyen/expert-metrics)
          has recorded for this model. Colibri numbers come from measured
          spectra; REAP/Edge0 show honest not-wired badges until their
          observers land (never fabricated zeros). */}
      {metrics && (
        <div className="brain-metrics">
          <div className="brain-metrics-prov">
            <span className="brain-metrics-title">{t("brain.metrics.title")}</span>
            <span>{metrics.prov.model_hash}</span>
            <span>engine {metrics.prov.engine_id}</span>
            <span className={metrics.prov.telemetry_present ? "brain-metrics-ok" : "brain-metrics-off"}>
              {metrics.prov.telemetry_present ? t("brain.metrics.telemetryOn") : t("brain.metrics.telemetryOff")}
            </span>
            {metrics.prov.gates && (
              <span>
                {(["replication", "category_floor", "lop_validated", "null_tested"] as const)
                  .map(g => <em key={g} className={metrics.prov.gates?.[g] ? "brain-metrics-ok" : "brain-metrics-off"}>{t(`brain.metrics.gate.${g}`)}</em>)}
              </span>
            )}
            {metrics.prov.categories_missing?.length ? (
              <span className="brain-metrics-off">{t("brain.metrics.missing", { cats: metrics.prov.categories_missing.join(", ") })}</span>
            ) : null}
          </div>
          {/* hydra: one tab per telemetry family (owner directive) — Colibri
              shows measured numbers; REAP/Edge0 tabs render their honest
              not-wired state until their observers land (never fabricated). */}
          <div className="view-tabs brain-metrics-tabs">
            {(["colibri", "reap", "edge0"] as const).map(fam => (
              <button key={fam} className={metricsTab === fam ? "active" : ""} onClick={() => setMetricsTab(fam)}>
                {t(`brain.metrics.${fam}`)}
                {fam !== "colibri" && metrics.fams[fam] === 0 ? <span className="brain-metrics-pending" title={t("brain.metrics.notWired")}>●</span> : null}
              </button>
            ))}
          </div>
          {metricsTab === "colibri" && (
            <div className="brain-metrics-card">
              <div className="brain-metrics-card-title">{t("brain.metrics.colibri")}</div>
              <div>{t("brain.metrics.specialists", { n: metrics.fams.specialists })} · {t("brain.metrics.generalists", { n: metrics.fams.generalists })}</div>
              <div>{t("brain.metrics.weak", { n: metrics.fams.weak })}</div>
              <div>{t("brain.metrics.meanSpec", { v: metrics.fams.meanSpec.toFixed(2) })}</div>
            </div>
          )}
          {metricsTab === "reap" && (
            <div className="brain-metrics-card">
              <div className="brain-metrics-card-title">{t("brain.metrics.reap")}</div>
              {metrics.fams.reap > 0
                ? <div>{t("brain.metrics.wired", { n: metrics.fams.reap })}</div>
                : <div className="brain-metrics-off">{t("brain.metrics.notWired")}</div>}
              <div className="brain-metrics-src">{t("brain.metrics.reapSrc")}</div>
            </div>
          )}
          {metricsTab === "edge0" && (
            <div className="brain-metrics-card">
              <div className="brain-metrics-card-title">{t("brain.metrics.edge0")}</div>
              {metrics.fams.edge0 > 0
                ? <div>{t("brain.metrics.wired", { n: metrics.fams.edge0 })}</div>
                : <div className="brain-metrics-off">{t("brain.metrics.notWired")}</div>}
              <div className="brain-metrics-src">{t("brain.metrics.edge0Src")}</div>
            </div>
          )}
        </div>
      )}
      {tip && data && (() => {
        // hydra: per-model layer mapping from the Stage B geometry payload
        // (design §D) — replaces Colibri's hardcoded GLM `row+3` / MTP-row 78
        // remap. Upstream math kept as the no-geometry (direct-Colibri) fallback.
        const geom = data.geometry
        const nGridMtp = geom ? geom.nextn_rows.length : 0
        const isMtp = geom ? nGridMtp > 0 && tip.row >= data.rows - nGridMtp : tip.row === data.rows - 1
        const realLayer = geom
          ? isMtp
            ? geom.nextn_rows[tip.row - (data.rows - nGridMtp)] ?? -1
            : geom.moe_rows[tip.row] ?? -1
          : (isMtp ? 78 : tip.row + 3)
        const entry = atlas?.[`${realLayer}:${tip.col}`]
        return (
        <div className="brain-tip" style={{ left: Math.min(tip.x + 14, window.innerWidth - 260), top: Math.min(tip.y + 14, window.innerHeight - 170) }}>
          <div className="brain-tip-title"><Layers className="size-3" /> Layer {realLayer}{isMtp ? " (MTP)" : ""} · Expert {tip.col}</div>
          <div>Tier: <strong style={{ color: ["#8b9aa3", "#5a9bd8", "#4ed6a5"][tip.tier] }}>{t(TIER_KEYS[tip.tier])}</strong></div>
          <div>Heat: <strong>{tip.heat === 0 ? t("brain.neverRouted") : t("brain.selections", { heat: tip.heat })}</strong></div>
          {/* hydra (F4c): entries were sanitized at fetch; belt-and-braces
              guard keeps the tooltip path crash-free if state ever changes
              between fetch and render */}
          {entry && atlasEntryIsSafe(entry) ? <>
            <div className={entry.label.startsWith("specialist") ? "brain-tip-spec" : undefined}>
              {entry.label.startsWith("specialist") ? t("brain.specialist", { top: entry.top }) : t("brain.generalist")}
              <small> (entropy {entry.entropy})</small>
            </div>
            {/* hydra: observability tier carries spec/reliability (#175); "weak"
                qualifier below spec~0.7 — margins are kernel-path-dependent */}
            {typeof entry.spec === "number" && entry.spec < 0.7 && (
              <div className="brain-tip-role">weak (spec {entry.spec.toFixed(2)}{entry.reliability ? `, ${entry.reliability}` : ""})</div>
            )}
            <div className="brain-tip-aff">{Object.entries(entry.affinity).sort((a, b) => b[1] - a[1]).slice(0, 3)
              .map(([c, p]) => `${c} ${Math.round(p * 100)}%`).join(" · ")}</div>
            {/* hydra: expert-metrics families in the tooltip when recorded */}
            {entry.reap && typeof entry.reap.saliency === "number" && (
              <div>REAP saliency <strong>{entry.reap.saliency.toExponential(2)}</strong></div>
            )}
            {entry.edge0 && typeof entry.edge0.predictability === "number" && (
              <div>Edge0 predict <strong>{Math.round(entry.edge0.predictability * 100)}%</strong></div>
            )}
          </> : <div className="brain-tip-role">{t(depthRoleKey(tip.row, data.rows, isMtp))}</div>}
        </div>
        )
      })()}
    </div>
  )
}

import { useEffect, useMemo, useState } from "react"
import { Activity, ChevronDown, Gauge, HardDrive, Timer } from "lucide-react"

import { getProfile, getTurns, getTurn, type ProfileTurn, type TurnSummary, type TurnDetail } from "@/lib/api"
import { useLocale } from "./i18n"

const PHASES = [
  { key: "expert_wait_s", i18n: "profile.ioWait", color: "#3987e5" },
  { key: "expert_matmul_s", i18n: "profile.expertMatmul", color: "#199e70" },
  { key: "attention_s", i18n: "profile.attention", color: "#c98500" },
  { key: "lm_head_s", i18n: "profile.lmHead", color: "#008300" },
  { key: "other_s", i18n: "profile.other", color: "#9085e9" },
] as const

interface Turn extends ProfileTurn { other_s: number; toks: number }

const derive = (turn: ProfileTurn): Turn => ({
  ...turn,
  other_s: Math.max(0, turn.wall_s - turn.expert_wait_s - turn.expert_matmul_s - turn.attention_s - turn.lm_head_s),
  toks: turn.wall_s > 0 ? turn.completion_tokens / turn.wall_s : 0,
})

const seconds = (value: number) => (value >= 10 ? value.toFixed(1) : value.toFixed(2)) + "s"

function DeltaBadge({ current, previous, label, format, invert }: {
  current: number
  previous: number
  label: string
  format: (v: number) => string
  invert?: boolean
}) {
  const delta = current - previous
  if (Math.abs(delta) < 0.001) return null
  const positive = delta > 0
  const improved = invert ? !positive : positive
  const sign = positive ? "+" : ""
  return (
    <span className={`prof-delta ${improved ? "prof-delta-up" : "prof-delta-down"}`} title={`${label}: ${format(previous)} → ${format(current)}`}>
      {sign}{format(delta)}
    </span>
  )
}

function PhaseDeltaBadges({ current, previous }: { current: Turn; previous: Turn }) {
  const { t } = useLocale()
  return (
    <div className="prof-deltas">
      <DeltaBadge current={current.wall_s} previous={previous.wall_s} label={t("profile.wallCol")} format={seconds} invert />
      <DeltaBadge current={current.forwards} previous={previous.forwards} label={t("profile.batching")} format={(v) => `${v > 0 ? "+" : ""}${v.toFixed(0)}`} invert />
      {PHASES.map((phase) => (
        <DeltaBadge key={phase.key} current={current[phase.key]} previous={previous[phase.key]} label={t(phase.i18n)} format={seconds} invert />
      ))}
    </div>
  )
}

function ShareBar({ label, turns }: { label: string; turns: Turn[] }) {
  const { t } = useLocale()
  const total = turns.reduce((sum, turn) => sum + turn.wall_s, 0)
  const parts = PHASES.map((phase) => ({ ...phase, name: t(phase.i18n), value: turns.reduce((sum, turn) => sum + turn[phase.key], 0) }))
  return (
    <div className="prof-share">
      <div className="prof-share-head"><span>{label}</span><code>{seconds(total)}</code></div>
      <div className="prof-share-bar" role="img" aria-label={parts.map((part) => `${part.name} ${seconds(part.value)}`).join(", ")}>
        {parts.map((part) => {
          const share = total > 0 ? part.value / total : 0
          return share > 0.001 ? (
            <span key={part.key} style={{ width: `${100 * share}%`, background: part.color }} title={`${part.name} — ${seconds(part.value)} (${(100 * share).toFixed(1)}%)`}>
              {share >= 0.09 ? `${Math.round(100 * share)}%` : ""}
            </span>
          ) : null
        })}
      </div>
    </div>
  )
}

function TurnColumns({ turns, stacked, height, format, footLabel, footLabelOne }: { turns: Turn[]; stacked: boolean; height: number; format: (turn: Turn) => string; footLabel: string; footLabelOne: string }) {
  const [hover, setHover] = useState<number | null>(null)
  const peak = Math.max(...turns.map((turn) => (stacked ? turn.wall_s : turn.toks)), 1e-9)
  const gap = 2
  const width = Math.max(1, (100 - gap * (turns.length - 1)) / turns.length)
  return (
    <div className="prof-plot" onMouseLeave={() => setHover(null)}>
      <svg viewBox={`0 0 100 ${height}`} preserveAspectRatio="none" aria-hidden="true">
        {[0.25, 0.5, 0.75].map((line) => <line key={line} x1="0" x2="100" y1={height * line} y2={height * line} className="prof-grid" />)}
        {turns.map((turn, index) => {
          const x = index * (width + gap)
          if (!stacked) {
            const h = (height * turn.toks) / peak
            return <rect key={index} x={x} y={height - h} width={width} height={h} rx="1" fill="var(--primary)" opacity={hover === null || hover === index ? 1 : 0.45} />
          }
          let y = height
          return PHASES.map((phase) => {
            const h = (height * turn[phase.key]) / peak
            y -= h
            return h > 0.1 ? <rect key={`${index}-${phase.key}`} x={x} y={y + 0.35} width={width} height={Math.max(h - 0.7, 0.35)} fill={phase.color} opacity={hover === null || hover === index ? 1 : 0.45} /> : null
          })
        })}
        {turns.map((_, index) => <rect key={index} x={index * (width + gap) - gap / 2} y="0" width={width + gap} height={height} fill="transparent" onMouseEnter={() => setHover(index)} />)}
      </svg>
      <div className="prof-plot-foot">
        <span>{turns.length > 1 ? footLabel : footLabelOne}</span>
        <code>{hover !== null && turns[hover] ? format(turns[hover]) : `peak ${stacked ? seconds(peak) : peak.toFixed(1) + " tok/s"}`}</code>
      </div>
    </div>
  )
}

export function Profiling({ baseUrl, apiKey, connected }: { baseUrl: string; apiKey: string; connected: boolean }) {
  const { t } = useLocale()
  // --- turn list (polled from /turns) ---
  const [turnsList, setTurnsList] = useState<TurnSummary[]>([])
  const [selectedSeq, setSelectedSeq] = useState<number | null>(null)
  // --- profile data for selected turn ---
  const [turns, setTurns] = useState<Turn[]>([])
  // --- detailed turn data (routing, hits) ---
  const [turnDetail, setTurnDetail] = useState<TurnDetail | null>(null)
  // --- endpoint state ---
  const [noEndpoint, setNoEndpoint] = useState(false)
  const [detailLoading, setDetailLoading] = useState(false)

  // Poll /turns for the turn list (every 2s while connected)
  useEffect(() => {
    if (!connected) return
    let disposed = false
    const poll = async () => {
      if (document.visibilityState === "hidden") return
      try {
        const result = await getTurns(baseUrl, apiKey)
        if (!disposed) {
          setTurnsList(result.turns)
          setNoEndpoint(false)
          // Auto-select latest turn if nothing selected or current was evicted
          if (result.turns.length > 0) {
            setSelectedSeq((prev) => {
              if (prev === null) return result.turns[result.turns.length - 1].turn_seq
              const exists = result.turns.some((t) => t.turn_seq === prev)
              return exists ? prev : result.turns[result.turns.length - 1].turn_seq
            })
          }
        }
      } catch (err) {
        if (!disposed && /404|503/.test(String(err))) setNoEndpoint(true)
      }
    }
    void poll()
    const timer = window.setInterval(() => void poll(), 2000)
    return () => { disposed = true; window.clearInterval(timer) }
  }, [baseUrl, apiKey, connected])

  // Fetch /profile + /turns/:seq when selectedSeq changes
  useEffect(() => {
    if (!connected || selectedSeq === null) return
    let disposed = false
    const fetch = async () => {
      setDetailLoading(true)
      try {
        const [profileResult, detailResult] = await Promise.all([
          getProfile(baseUrl, apiKey).catch(() => null),
          getTurn(baseUrl, selectedSeq, apiKey).catch(() => null),
        ])
        if (!disposed) {
          if (profileResult) {
            // Find the matching turn in profile response by seq
            const match = profileResult.turns.find((_, i) => profileResult.seq - i === selectedSeq)
            if (match) {
              setTurns([derive(match)])
            } else {
              // Fallback: derive from the raw profile data with the selected turn's summary
              const summary = turnsList.find((t) => t.turn_seq === selectedSeq)
              if (summary) {
                setTurns([derive({
                  wall_s: summary.wall_s,
                  prompt_tokens: summary.prompt_tokens,
                  completion_tokens: summary.completion_tokens,
                  expert_disk_s: 0,
                  expert_wait_s: 0,
                  expert_matmul_s: 0,
                  attention_s: 0,
                  lm_head_s: 0,
                  forwards: summary.forwards,
                })])
              }
            }
          }
          setTurnDetail(detailResult)
        }
      } catch {
        // keep last state
      } finally {
        if (!disposed) setDetailLoading(false)
      }
    }
    void fetch()
    const timer = window.setInterval(() => void fetch(), 3000)
    return () => { disposed = true; window.clearInterval(timer) }
  }, [baseUrl, apiKey, connected, selectedSeq, turnsList])

  const latest = turns[turns.length - 1]
  const recent = turns.slice(-40)
  const diskService = turns.reduce((sum, turn) => sum + turn.expert_disk_s, 0)
  const selectedSummary = turnsList.find((t) => t.turn_seq === selectedSeq)
  const prevSummary = useMemo(() => {
    if (!selectedSeq || !turnsList.length) return null
    const idx = turnsList.findIndex((t) => t.turn_seq === selectedSeq)
    return idx > 0 ? turnsList[idx - 1] : null
  }, [selectedSeq, turnsList])

  return (
    <div className="prof-page">
      <div className="prof-head">
        <div className="section-title"><Gauge className="size-4" /> {t("profile.title")}</div>
        <div className="prof-legend">
          {PHASES.map((phase) => <span key={phase.key}><i style={{ background: phase.color }} />{t(phase.i18n)}</span>)}
        </div>
      </div>

      {/* Turn selector */}
      {turnsList.length > 0 && (
        <div className="prof-turn-selector">
          <label className="prof-turn-label">
            <Timer className="size-3.5" /> {t("profile.selectTurn")}
            <div className="prof-turn-select-wrap">
              <select
                value={selectedSeq ?? ""}
                onChange={(e) => setSelectedSeq(Number(e.target.value))}
              >
                {turnsList.slice().reverse().map((turn) => (
                  <option key={turn.turn_seq} value={turn.turn_seq}>
                    #{turn.turn_seq} — {seconds(turn.wall_s)} · {turn.completion_tokens} tokens
                  </option>
                ))}
              </select>
              <ChevronDown className="size-3.5 prof-turn-chevron" />
            </div>
          </label>
          {selectedSummary && prevSummary && (
            <PhaseDeltaBadges
              current={derive({
                wall_s: selectedSummary.wall_s,
                prompt_tokens: selectedSummary.prompt_tokens,
                completion_tokens: selectedSummary.completion_tokens,
                expert_disk_s: 0,
                expert_wait_s: 0,
                expert_matmul_s: 0,
                attention_s: 0,
                lm_head_s: 0,
                forwards: selectedSummary.forwards,
              })}
              previous={derive({
                wall_s: prevSummary.wall_s,
                prompt_tokens: prevSummary.prompt_tokens,
                completion_tokens: prevSummary.completion_tokens,
                expert_disk_s: 0,
                expert_wait_s: 0,
                expert_matmul_s: 0,
                attention_s: 0,
                lm_head_s: 0,
                forwards: prevSummary.forwards,
              })}
            />
          )}
        </div>
      )}

      {!latest && !noEndpoint && !connected ? (
        <p className="runtime-unavailable">{t("profile.connectHint")}</p>
      ) : noEndpoint ? (
        <p className="runtime-unavailable">{t("profile.noEndpoint")}</p>
      ) : !latest ? (
        <p className="runtime-unavailable">{t("profile.empty")}</p>
      ) : (
        <>
          <div className="prof-tiles">
            <div><span><Gauge className="size-3" /> {t("profile.lastTurn")}</span><strong>{latest.toks.toFixed(1)}</strong><small>tok/s</small></div>
            <div><span><Timer className="size-3" /> {t("profile.wallTime")}</span><strong>{seconds(latest.wall_s)}</strong><small>{latest.prompt_tokens} → {latest.completion_tokens} tokens</small></div>
            <div><span><Activity className="size-3" /> {t("profile.batching")}</span><strong>{latest.forwards > 0 ? (latest.completion_tokens / latest.forwards).toFixed(2) : "—"}</strong><small>{t("profile.tokensPerForward")}</small></div>
            <div><span><HardDrive className="size-3" /> {t("profile.diskService")}</span><strong>{seconds(latest.expert_disk_s)}</strong><small>{t("profile.overlapped")}</small></div>
          </div>

          {/* EMAP tier bytes — read-only, residency invariant */}
          {turnDetail && (
            <div className="prof-emap">
              <div className="prof-emap-head">
                <span className="section-title"><HardDrive className="size-3.5" /> {t("profile.emapTitle")}</span>
                <span className="prof-emap-note">{t("profile.emapReadonly")}</span>
              </div>
              <div className="prof-emap-grid">
                <div className="prof-emap-cell"><span>{t("profile.emapRows")}</span><strong>{turnDetail.routing?.length ?? 0}</strong></div>
                <div className="prof-emap-cell"><span>{t("profile.emapSlot")}</span><strong>{turnDetail.slot}</strong></div>
                <div className="prof-emap-cell"><span>{t("profile.emapTs")}</span><strong>{new Date(turnDetail.ts * 1000).toLocaleTimeString()}</strong></div>
                {turnDetail.hits && (
                  <div className="prof-emap-cell prof-emap-hits"><span>{t("profile.emapHits")}</span><code>{turnDetail.hits.slice(0, 32)}{turnDetail.hits.length > 32 ? "..." : ""}</code></div>
                )}
              </div>
            </div>
          )}

          <div className="prof-shares">
            <ShareBar label={t("profile.lastTurn")} turns={[latest]} />
            {turns.length > 1 ? <ShareBar label={t("profile.window", { n: turns.length })} turns={turns} /> : null}
          </div>

          <div className="prof-charts">
            <div className="prof-chart">
              <div className="prof-chart-title">{t("profile.throughputTitle")}</div>
              <TurnColumns turns={recent} stacked={false} height={36} footLabel={t("profile.turnsLabel", { n: recent.length })} footLabelOne={t("profile.oneTurn")} format={(turn) => `${turn.toks.toFixed(1)} tok/s · ${turn.completion_tokens} tokens`} />
            </div>
            <div className="prof-chart">
              <div className="prof-chart-title">{t("profile.phaseTitle")}</div>
              <TurnColumns turns={recent} stacked height={36} footLabel={t("profile.turnsLabel", { n: recent.length })} footLabelOne={t("profile.oneTurn")} format={(turn) => `${seconds(turn.wall_s)} · ${PHASES.map((phase) => `${t(phase.i18n)} ${seconds(turn[phase.key])}`).join(" · ")}`} />
            </div>
          </div>

          <div className="prof-table-wrap">
            <table className="prof-table">
              <thead><tr><th>{t("profile.turnCol")}</th><th>{t("profile.tokensCol")}</th><th>tok/s</th><th>{t("profile.wallCol")}</th>{PHASES.map((phase) => <th key={phase.key}><i style={{ background: phase.color }} />{t(phase.i18n)}</th>)}<th>{t("profile.diskService")}</th></tr></thead>
              <tbody>
                {recent.slice().reverse().map((turn, index) => (
                  <tr key={turns.length - index}>
                    <td>{turns.length - index}</td>
                    <td>{turn.prompt_tokens} → {turn.completion_tokens}</td>
                    <td>{turn.toks.toFixed(1)}</td>
                    <td>{seconds(turn.wall_s)}</td>
                    {PHASES.map((phase) => <td key={phase.key}>{seconds(turn[phase.key])}</td>)}
                    <td>{seconds(turn.expert_disk_s)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {diskService > 0 ? <p className="prof-note">{t("profile.diskNote")}</p> : null}
          </div>
        </>
      )}
    </div>
  )
}

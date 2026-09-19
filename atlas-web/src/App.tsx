// hydra: atlas shell — Colibri App.tsx visual shell ported verbatim-in-structure
// (JustVugg/colibri @ a8f2ca62), with the chat/profiling tabs scoped OUT per the
// separated-service design (docs/design-colibri-expert-atlas.md §D: "Brain +
// health poll, chats/profiling out"). The Connection section becomes an engine
// selector over the atlas service's aggregated engines; everything the Brain
// page needs is served by atlas-web, so no /v1 endpoint or API key exists here.
import { useCallback, useEffect, useState } from "react"
import { Activity, BrainCircuit, Feather, Gauge, Link2, MessageSquareText, MonitorDot, RefreshCw } from "lucide-react"

import { useLocale } from "./i18n"
import { Brain } from "./Brain"

// atlas service /health shape (server/server.ts) — deliberately not the
// Colibri HealthResponse: the atlas aggregates engines instead of exposing one.
interface AtlasEngine { id: string; mode: "mock" | "engine"; ok: boolean; seq: number }
interface AtlasHealth { status: string; engines: AtlasEngine[] }

export default function App() {
  const { t, locale, setLocale, locales } = useLocale()

  const [health, setHealth] = useState<AtlasHealth | null>(null)
  const [healthError, setHealthError] = useState("")
  // ?engine=<id> is the initial selection; the sidebar selector overrides it.
  const [engineId, setEngineId] = useState<string | undefined>(() =>
    new URLSearchParams(window.location.search).get("engine") ?? undefined)

  const refreshHealth = useCallback(() => {
    fetch("/health")
      .then(r => { if (!r.ok) throw new Error(String(r.status)); return r.json() })
      .then((h: AtlasHealth) => { setHealth(h); setHealthError("") })
      .catch(err => setHealthError(String(err)))
  }, [])

  useEffect(() => {
    refreshHealth()
    const iv = window.setInterval(refreshHealth, 5000)
    return () => window.clearInterval(iv)
  }, [refreshHealth])

  const engines = health?.engines ?? []
  const selected = engines.find(e => e.id === engineId) ?? engines[0]
  // When the URL didn't pin an engine, follow the first healthy one but keep
  // the selection stable across health refreshes once the user picked one.
  const activeId = engineId ?? selected?.id

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand-row">
          <div className="brand-mark"><Feather className="size-5" /></div>
          <div><h1>colibrì</h1><p>{t("brand.tagline")}</p></div>
        </div>

        <section className="side-section">
          <div className="section-title"><Link2 className="size-3.5" /> {t("sidebar.connection")}</div>
          <label>
            {t("sidebar.atlasEngine")}
            <select
              value={activeId ?? ""}
              onChange={event => { setEngineId(event.target.value); const u = new URL(window.location.href); u.searchParams.set("engine", event.target.value); window.history.replaceState(null, "", u) }}
              disabled={!engines.length}
            >
              {engines.length
                ? engines.map(e => <option key={e.id} value={e.id}>{e.id} ({e.mode})</option>)
                : <option>—</option>}
            </select>
            <span className="field-help">{t("sidebar.atlasEngineHelp")}</span>
          </label>
        </section>

        <section className="side-section">
          <div className="section-title"><Activity className="size-3.5" /> {t("sidebar.runtime")}</div>
          {engines.length ? (
            <div className="side-engines">
              {engines.map(e => (
                <div key={e.id} className="runtime-foot">
                  <span className={e.ok ? "runtime-dot" : "runtime-dot down"} />
                  <span>{e.id}</span>
                  <code>{e.mode}</code>
                </div>
              ))}
            </div>
          ) : <p className="runtime-unavailable">{healthError ? String(healthError) : t("sidebar.runtimeProbe")}</p>}
        </section>

        <div className="sidebar-foot">
          <div><MonitorDot className="size-3.5" /><span>{t("sidebar.transport")}</span></div>
          <div className="locale-switcher">
            <select value={locale} onChange={e => setLocale(e.target.value)} aria-label="locale">
              {locales.map(l => <option key={l.code} value={l.code}>{l.label}</option>)}
            </select>
          </div>
        </div>
      </aside>

      <main className="chat-panel">
        <header className="topbar">
          <div>
            <span className="eyebrow">{t("topbar.activeEngine")}</span>
            <strong>{activeId ?? "—"}</strong>
          </div>
          <div className="view-tabs">
            {/* chat/profiling stay visible-but-disabled: this separated service
                serves the Brain atlas only (design §D) — honest chrome, no fake
                features. */}
            <button disabled title={t("nav.outOfScope")}><MessageSquareText className="size-3.5" /> {t("nav.chat")}</button>
            <button className="active"><BrainCircuit className="size-3.5" /> {t("nav.brain")}</button>
            <button disabled title={t("nav.outOfScope")}><Gauge className="size-3.5" /> {t("nav.profiling")}</button>
          </div>
          <div className="top-actions">
            <button className="atlas-refresh" onClick={refreshHealth} title={t("sidebar.atlasRefresh")}><RefreshCw className="size-3.5" /></button>
          </div>
        </header>

        <Brain baseUrl="" apiKey="" connected={!!health?.status} engineId={activeId} />
      </main>
    </div>
  )
}

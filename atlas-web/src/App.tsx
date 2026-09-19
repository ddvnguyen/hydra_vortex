// hydra: atlas shell — Colibri App.tsx visual shell ported verbatim-in-structure
// (JustVugg/colibri @ a8f2ca62). Chat (#776) and Profiling (#777) tabs are now
// live: Chat talks to the selected engine through the server-side
// /engine-proxy/<id>/v1 route; Profiling polls the same proxy base and shows
// the honest empty state when the engine has no /profile endpoint.
import { useCallback, useEffect, useState } from "react"
import { Activity, BrainCircuit, Feather, Gauge, Link2, MessageSquareText, MonitorDot, RefreshCw } from "lucide-react"

import { useLocale } from "./i18n"
import { Brain } from "./Brain"
import { Chat } from "./Chat"
import { Profiling } from "./Profiling"

// atlas service /health shape (server/server.ts) — deliberately not the
// Colibri HealthResponse: the atlas aggregates engines instead of exposing one.
interface AtlasEngine { id: string; mode: "mock" | "engine"; ok: boolean; seq: number }
interface AtlasHealth { status: string; engines: AtlasEngine[] }

export default function App() {
  const { t, locale, setLocale, locales } = useLocale()

  const [view, setView] = useState<"chat" | "brain" | "profiling">("brain")
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
  const active = engines.find(e => e.id === activeId)
  // hydra: chat/profiling speak to the engine through the server-side proxy
  // (browser cannot reach the localhost-only engine). Mock engines have no
  // HTTP surface — Chat shows the honest not-connected state for those.
  const proxyBase = active && active.mode === "engine" ? `/engine-proxy/${active.id}/v1` : ""

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
            <button className={view === "chat" ? "active" : ""} onClick={() => setView("chat")}><MessageSquareText className="size-3.5" /> {t("nav.chat")}</button>
            <button className={view === "brain" ? "active" : ""} onClick={() => setView("brain")}><BrainCircuit className="size-3.5" /> {t("nav.brain")}</button>
            <button className={view === "profiling" ? "active" : ""} onClick={() => setView("profiling")}><Gauge className="size-3.5" /> {t("nav.profiling")}</button>
          </div>
          <div className="top-actions">
            <button className="atlas-refresh" onClick={refreshHealth} title={t("sidebar.atlasRefresh")}><RefreshCw className="size-3.5" /></button>
          </div>
        </header>

        {view === "chat"
          ? <Chat baseUrl={proxyBase} apiKey="" connected={!!health?.status && !!proxyBase} />
          : view === "profiling"
            ? <Profiling baseUrl={proxyBase} apiKey="" connected={!!health?.status && !!proxyBase} />
            : <Brain baseUrl="" apiKey="" connected={!!health?.status} engineId={activeId} />}
      </main>
    </div>
  )
}

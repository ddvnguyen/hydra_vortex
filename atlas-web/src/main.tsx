// hydra: atlas-web entry — Colibri upstream web/src/main.tsx renders the full
// chat/brain/profiling tab App; the separated atlas service hosts the Brain
// page only (design §D scope: Brain + health poll, chats/profiling out).
import { StrictMode, useEffect, useState } from "react"
import { createRoot } from "react-dom/client"

import { Brain } from "./Brain"
import { ErrorBoundary } from "./ErrorBoundary"
import { LocaleProvider } from "./i18n"
import { getHealth } from "./lib/api"

function Root() {
  const [connected, setConnected] = useState(false)
  // hydra: ?engine=<id> selects one engine when the atlas service aggregates
  // many (server-side selection exists; the UI must pass it through too).
  const [engineId] = useState(() => new URLSearchParams(window.location.search).get("engine") ?? undefined)

  useEffect(() => {
    let alive = true
    const poll = () => {
      getHealth("", "")
        .then(() => { if (alive) setConnected(true) })
        .catch(() => { if (alive) setConnected(false) })
    }
    poll()
    const t = window.setInterval(poll, 5000)
    return () => { alive = false; window.clearInterval(t) }
  }, [])

  return (
    <LocaleProvider>
      <ErrorBoundary>
        <Brain baseUrl="" apiKey="" connected={connected} engineId={engineId} />
      </ErrorBoundary>
    </LocaleProvider>
  )
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
)

// hydra: atlas-web entry — Colibri upstream web/src/main.tsx renders the full
// chat/brain/profiling tab App; the separated atlas service hosts the Brain
// page only (design §D scope: Brain + health poll, chats/profiling out).
import { StrictMode } from "react"
import { createRoot } from "react-dom/client"

import App from "./App"
import "./index.css"
import { ErrorBoundary } from "./ErrorBoundary"
import { LocaleProvider } from "./i18n"

function Root() {
  // Connection/health polling moved into the atlas shell (App.tsx); main keeps
  // locale + error boundary only.
  return (
    <LocaleProvider>
      <ErrorBoundary>
        <App />
      </ErrorBoundary>
    </LocaleProvider>
  )
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
)

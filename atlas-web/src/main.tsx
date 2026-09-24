import { StrictMode } from "react"
import { createRoot } from "react-dom/client"

// hydra: atlas-web entry — Colibri upstream web/src/main.tsx (1.12.0) renders
// the redesigned workspace; the separated atlas service defaults to the Brain
// view (design §D) with chat/brio/profiling/galaxy reachable via the dock.
import App from "./App"
import "./index.css"
import "./chat-design.css"
import { ErrorBoundary } from "./ErrorBoundary"
import { LocaleProvider } from "./i18n"

function Root() {
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

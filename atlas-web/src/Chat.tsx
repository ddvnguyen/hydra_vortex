// hydra: Chat view — ported from Colibri App.tsx chat section (JustVugg/colibri
// @ a8f2ca62, lines 44-296 + 381-461). Upstream sidebar/topbar chrome lives in
// the atlas shell (App.tsx); this component owns only the conversation state,
// inference controls, SSE streaming via vendored streamChat, and composer.
// baseUrl points at the server-side engine proxy (/engine-proxy/<id>/v1) so the
// browser never calls the localhost-only engine directly (issue #776).
import { useEffect, useMemo, useRef, useState } from "react"
import {
  ArrowUp,
  BrainCircuit,
  CircleStop,
  Feather,
  Gauge,
  ImagePlus,
  MessageSquareText,
  Timer,
} from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Textarea } from "@/components/ui/textarea"
import { listModels, getHealth, streamChat, type ChatMessage, type HealthResponse, type StreamChatResult } from "@/lib/api"
import { supportsCacheSlots } from "@/lib/runtime"
import { persistPublicSettings, stored } from "@/lib/storage"
import { Markdown } from "@/components/Markdown"
import { cn } from "@/lib/utils"
import { useLocale } from "./i18n"
import { REASONING_EFFORT, modelForcesReasoning, reasoningLevelsFor, type ReasoningLevel } from "@/lib/reasoning"

const message = (role: ChatMessage["role"], content: string): ChatMessage => {
  let id: string
  try { id = crypto.randomUUID() } catch { id = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16) }) }
  return { id, role, content }
}

export function Chat({ baseUrl, apiKey, connected }: { baseUrl: string; apiKey: string; connected: boolean }) {
  const { t } = useLocale()

  const [models, setModels] = useState<string[]>([])
  const [model, setModel] = useState(() => stored(localStorage, "colibri.model", ""))
  const [temperature, setTemperature] = useState(0.7)
  const [maxTokens, setMaxTokens] = useState(4096)
  const [reasoning, setReasoning] = useState<ReasoningLevel>("off")
  const [cacheSlot, setCacheSlot] = useState(0)
  const [conversations, setConversations] = useState<Record<number, ChatMessage[]>>({ 0: [] })
  const [health, setHealth] = useState<HealthResponse | null>(null)
  const [chatConnected, setChatConnected] = useState(false)
  const [lastRun, setLastRun] = useState<StreamChatResult | null>(null)
  const [draft, setDraft] = useState("")
  const [attachments, setAttachments] = useState<{ name: string; url: string }[]>([])
  const fileInputRef = useRef<HTMLInputElement>(null)

  const attachFiles = async (files: FileList | File[] | null) => {
    if (!files) return
    const images = Array.from(files).filter((file) => file.type.startsWith("image/"))
    if (!images.length) return
    const read = await Promise.all(
      images.map(
        (file) =>
          new Promise<{ name: string; url: string }>((resolve, reject) => {
            const reader = new FileReader()
            reader.onload = () => resolve({ name: file.name, url: String(reader.result) })
            reader.onerror = () => reject(reader.error)
            reader.readAsDataURL(file)
          }),
      ),
    )
    setAttachments((current) => [...current, ...read])
  }
  const [loading, setLoading] = useState(false)
  const [tokenCount, setTokenCount] = useState(0)
  const [tokPerSec, setTokPerSec] = useState<number | null>(null)
  const [ttft, setTtft] = useState<number | null>(null)
  const [totalTokens, setTotalTokens] = useState({ prompt: 0, completion: 0 })
  const [connecting, setConnecting] = useState(false)
  const [error, setError] = useState("")
  const autoConnected = useRef("")
  const abortRef = useRef<AbortController | null>(null)
  const probeRef = useRef<AbortController | null>(null)
  const bottomRef = useRef<HTMLDivElement>(null)
  const messages = conversations[cacheSlot] || []
  const kvSlots = Math.max(1, health?.kv_slots || 1)

  const updateMessages = (next: ChatMessage[] | ((current: ChatMessage[]) => ChatMessage[])) =>
    setConversations((current) => ({
      ...current,
      [cacheSlot]: typeof next === "function" ? next(current[cacheSlot] || []) : next,
    }))

  useEffect(() => {
    persistPublicSettings(localStorage, baseUrl, model)
  }, [baseUrl, model])

  useEffect(() => () => {
    probeRef.current?.abort()
    abortRef.current?.abort()
  }, [])

  // health poll once connected (mirrors upstream EFFECT #4)
  useEffect(() => {
    if (!chatConnected) return
    let disposed = false
    const poll = async () => {
      if (document.visibilityState === "hidden") return
      try {
        const result = await getHealth(baseUrl, apiKey)
        if (!disposed) setHealth(result)
      } catch { /* engine busy — keep last snapshot */ }
    }
    const timer = window.setInterval(() => void poll(), 5000)
    return () => { disposed = true; window.clearInterval(timer) }
  }, [apiKey, baseUrl, chatConnected])

  useEffect(() => {
    if (cacheSlot >= kvSlots) setCacheSlot(0)
  }, [cacheSlot, kvSlots])

  useEffect(() => { setLastRun(null) }, [cacheSlot])

  useEffect(() => {
    if (modelForcesReasoning(model))
      setReasoning((level) => (level === "off" || level === "medium" ? "high" : level))
  }, [model])

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: "smooth" }) }, [messages])

  const connect = async () => {
    if (!baseUrl) return
    probeRef.current?.abort()
    const controller = new AbortController()
    probeRef.current = controller
    setConnecting(true)
    setError("")
    try {
      const found = await listModels(baseUrl, apiKey, controller.signal)
      setModels(found)
      if (found.length && !found.includes(model)) setModel(found[0])
      setChatConnected(true)
      try {
        setHealth(await getHealth(baseUrl, apiKey, controller.signal))
      } catch {
        if (!controller.signal.aborted) setHealth(null)
      }
    } catch (cause) {
      if (controller.signal.aborted) return
      setChatConnected(false)
      setError(cause instanceof Error ? cause.message : "status.serverError")
    } finally {
      if (probeRef.current === controller) { probeRef.current = null; setConnecting(false) }
    }
  }

  // auto-connect when the shell reports the engine reachable (or baseUrl changes)
  useEffect(() => {
    if (connected && baseUrl && autoConnected.current !== baseUrl) {
      autoConnected.current = baseUrl
      void connect()
    }
    if (!baseUrl) { autoConnected.current = ""; setChatConnected(false); setModels([]); setHealth(null) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connected, baseUrl])

  const canSend = useMemo(() => (draft.trim() || attachments.length > 0) && model && !loading, [draft, attachments, model, loading])

  const send = async () => {
    const content = draft.trim()
    if ((!content && !attachments.length) || loading) return
    const user = message("user", content)
    if (attachments.length) user.images = attachments.map((item) => item.url)
    const assistant = message("assistant", "")
    const history = [...messages, user]
    setDraft("")
    setAttachments([])
    setError("")
    updateMessages([...history, assistant])
    setLoading(true)
    setTokenCount(0)
    setTokPerSec(null)
    setTtft(null)
    const t0 = performance.now()
    let firstToken = true
    let count = 0
    const controller = new AbortController()
    abortRef.current = controller
    try {
      const result = await streamChat({
        baseUrl,
        apiKey,
        model,
        messages: history,
        temperature,
        maxTokens,
        enableThinking: reasoning !== "off",
        reasoningEffort: reasoning === "off" ? undefined : REASONING_EFFORT[reasoning],
        cacheSlot: supportsCacheSlots(health) ? cacheSlot : undefined,
        signal: controller.signal,
        onReasoning: (delta) => {
          if (firstToken) { setTtft(performance.now() - t0); firstToken = false }
          count++
          setTokenCount(count)
          const elapsed = (performance.now() - t0) / 1000
          if (elapsed > 0.3) setTokPerSec(count / elapsed)
          updateMessages((current) => current.map((item) =>
            item.id === assistant.id ? { ...item, reasoning: (item.reasoning ?? "") + delta } : item,
          ))
        },
        onDelta: (delta) => {
          if (firstToken) { setTtft(performance.now() - t0); firstToken = false }
          count++
          setTokenCount(count)
          const elapsed = (performance.now() - t0) / 1000
          if (elapsed > 0.3) setTokPerSec(count / elapsed)
          updateMessages((current) => current.map((item) =>
            item.id === assistant.id ? { ...item, content: item.content + delta } : item,
          ))
        },
      })
      const finalElapsed = (performance.now() - t0) / 1000
      if (count > 0 && finalElapsed > 0) setTokPerSec(count / finalElapsed)
      if (result.usage) setTotalTokens(prev => ({
        prompt: prev.prompt + (result.usage?.prompt_tokens || 0),
        completion: prev.completion + (result.usage?.completion_tokens || 0),
      }))
      setLastRun(result)
      setChatConnected(true)
    } catch (cause) {
      if (controller.signal.aborted) {
        updateMessages((current) => current.filter((item) => item.id !== assistant.id || item.content || item.reasoning))
      } else {
        setError(cause instanceof Error ? cause.message : "status.generationFailed")
        updateMessages((current) => current.filter((item) => item.id !== assistant.id || item.content || item.reasoning))
      }
    } finally {
      abortRef.current = null
      setLoading(false)
    }
  }

  if (!baseUrl) {
    return <p className="runtime-unavailable">{t("status.notConnected")}</p>
  }

  return (
    <div className="chat-view">
      <div className="chat-controls">
        <label>{t("sidebar.model")}
          <select value={model} onChange={(event) => setModel(event.target.value)} disabled={loading || connecting}>
            {models.length ? models.map((id) => <option key={id}>{id}</option>) : <option>{model || "—"}</option>}
          </select>
        </label>
        <label><span className="label-line"><span>{t("sidebar.temperature")}</span><code>{temperature.toFixed(1)}</code></span>
          <input className="range" type="range" min="0" max="2" step="0.1" value={temperature} onChange={(event) => setTemperature(Number(event.target.value))} />
        </label>
        <label>{t("sidebar.maxTokens")}
          <Input type="number" min={1} max={32768} value={maxTokens} onChange={(event) => { const value = Number(event.target.value); if (Number.isFinite(value)) setMaxTokens(Math.min(32768, Math.max(1, Math.round(value)))) }} />
        </label>
        <label><span className="label-line"><span><BrainCircuit className="size-4" /> {t("sidebar.reasoning")}</span></span>
          <select value={reasoning} onChange={(event) => setReasoning(event.target.value as ReasoningLevel)} disabled={loading}>
            {reasoningLevelsFor(model).map((level) => <option key={level} value={level}>{t(`sidebar.reasoning.${level}`)}</option>)}
          </select>
        </label>
        {!chatConnected && (
          <Button type="button" variant="secondary" onClick={() => void connect()} disabled={connecting}>
            {connecting ? t("status.notConnected") : t("sidebar.probe")}
          </Button>
        )}
        <div className="chat-run-stats">
          {!loading && tokPerSec != null ? <Badge className="badge-speed"><Gauge className="size-3" /> {t("topbar.tokPerSec", { n: tokPerSec.toFixed(1) })}</Badge> : null}
          {!loading && ttft != null ? <Badge><Timer className="size-3" /> TTFT {(ttft / 1000).toFixed(1)}s</Badge> : null}
          {!loading && lastRun?.usage ? <Badge>{lastRun.usage.prompt_tokens}→{lastRun.usage.completion_tokens}</Badge> : null}
          {totalTokens.prompt + totalTokens.completion > 0
            ? <span className="session-stats">{t("dashboard.session")} <strong>{totalTokens.prompt.toLocaleString()}</strong> {t("dashboard.prompt")} + <strong>{totalTokens.completion.toLocaleString()}</strong> {t("dashboard.completion")}</span>
            : null}
        </div>
      </div>

      <div className="conversation">
        {!messages.length ? (
          <div className="empty-state">
            <div className="orb"><Feather /></div>
            <span className="eyebrow">{t("hero.title")}</span>
            <h2>{t("hero.subtitle")}<br /><em>{t("hero.tagline")}</em></h2>
            <p>{t("hero.description")}</p>
            <div className="suggestions">
              {[t("prompts.routing"), t("prompts.benchmark"), t("prompts.caching")].map((item) => <button key={item} onClick={() => setDraft(item)}>{item}<ArrowUp className="size-3.5 rotate-45" /></button>)}
            </div>
          </div>
        ) : (
          <div className="message-list">
            {messages.map((item) => (
              <article key={item.id} className={cn("message", item.role)}>
                <div className="avatar">{item.role === "user" ? "Y" : <Feather className="size-4" />}</div>
                <div><div className="message-meta">{item.role === "user" ? t("chat.you") : t("chat.colibri")}</div><div className="message-body">{item.reasoning
                  ? <details className="reasoning" open={!item.content}>
                      <summary>{t("sidebar.reasoning")}</summary>
                      <div className="reasoning-body">{item.reasoning}</div>
                    </details>
                  : null}{item.content
                  ? (item.role === "assistant"
                      ? <Markdown source={item.content} />
                      : item.content)
                  : <span className="typing" aria-label="Generating"><i /><i /><i /></span>}</div></div>
              </article>
            ))}
            <div ref={bottomRef} />
          </div>
        )}
      </div>

      <div className="composer-wrap">
        {error && <div className="error-banner" role="alert">{t(error)}</div>}
        <div className="composer">
          <Textarea value={draft}
            onPaste={(event) => { const files = Array.from(event.clipboardData.files); if (files.length) { event.preventDefault(); void attachFiles(files) } }}
            onDragOver={(event) => event.preventDefault()}
            onDrop={(event) => { if (event.dataTransfer.files.length) { event.preventDefault(); void attachFiles(event.dataTransfer.files) } }} onChange={(event) => setDraft(event.target.value)} placeholder={t("chat.placeholder")} onKeyDown={(event) => { if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); void send() } }} />
          {attachments.length > 0 && (
            <div className="attachments">
              {attachments.map((item, index) => (
                <span key={item.url + index} className="attachment">
                  <img src={item.url} alt={item.name} />
                  <button type="button" aria-label={t("chat.removeImage")}
                    onClick={() => setAttachments((current) => current.filter((_, at) => at !== index))}>x</button>
                </span>
              ))}
            </div>
          )}
          <div className="composer-foot"><span><MessageSquareText className="size-3.5" /> {t("chat.inputHint")}</span><input ref={fileInputRef} type="file" accept="image/*" multiple hidden onChange={(event) => { void attachFiles(event.target.files); event.target.value = "" }} /><Button variant="ghost" size="icon" aria-label={t("chat.attachImage")} onClick={() => fileInputRef.current?.click()}><ImagePlus className="size-4" /></Button>{loading ? <Button variant="destructive" size="icon" aria-label={t("chat.stop")} onClick={() => abortRef.current?.abort()}><CircleStop className="size-4" /></Button> : <Button size="icon" aria-label={t("chat.send")} disabled={!canSend} onClick={() => void send()}><ArrowUp className="size-4" /></Button>}</div>
        </div>
      </div>
    </div>
  )
}

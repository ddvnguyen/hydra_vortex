import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import * as THREE from "three"
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js"
import { Layers, Orbit, RotateCcw, TriangleAlert } from "lucide-react"

import { endpoint, getTurns, type TurnSummary } from "@/lib/api"
import { useLocale } from "./i18n"

/* ---- Types (self-contained, mirrors Brain.tsx types for /experts) ---- */

interface AtlasGeometry {
  engine_id: string
  model_hash: string
  dense_prefix: number
  moe_rows: number[]
  nextn_rows: number[]
  n_expert_used: number
}
interface ExpertMap {
  rows: number
  cols: number
  map: string
  hits: string
  seq: number
  telemetry_enabled?: boolean
  geometry?: AtlasGeometry
}
interface AtlasEntry {
  affinity: Record<string, number>
  entropy: number
  top: string
  label: string
  spec?: number
  reliability?: string
  weak?: boolean
}

interface GalaxyNode {
  index: number
  row: number
  col: number
  tier: number
  heat: number
  realLayer: number
  isMtp: boolean
  entry?: AtlasEntry
  position: THREE.Vector3
}

/* ---- Helpers ---- */

const TIER_COLOR = [0x3a4750, 0x5a9bd8, 0x4ed6a5] // disk, ram, vram
const TIER_CSS = ["#3a4750", "#5a9bd8", "#4ed6a5"]
const TIER_KEYS = ["tier.disk", "tier.ram", "tier.vram"] as const

/** hydra (F4c): true only when the atlas entry carries the fields the Galaxy
 *  surfaces — label + non-empty affinity. Foreign-model atlases (older or
 *  differently-shaped experts.json) previously crashed the tab with
 *  "Cannot read properties of undefined (reading 'startsWith')". */
function entryIsSafe(entry: AtlasEntry | undefined | null): entry is AtlasEntry {
  return (
    !!entry &&
    typeof entry.label === "string" &&
    entry.label.length > 0 &&
    !!entry.affinity &&
    typeof entry.affinity === "object"
  )
}

function resolveLayer(
  row: number,
  rows: number,
  geom?: AtlasGeometry,
): { realLayer: number; isMtp: boolean } {
  if (!geom) {
    const isMtp = row === rows - 1
    return { realLayer: isMtp ? 78 : row + 3, isMtp }
  }
  const nGridMtp = geom.nextn_rows.length
  const isMtp = nGridMtp > 0 && row >= rows - nGridMtp
  if (isMtp) {
    return { realLayer: geom.nextn_rows[row - (rows - nGridMtp)] ?? -1, isMtp }
  }
  return { realLayer: geom.moe_rows[row] ?? -1, isMtp: false }
}

/** Map 2D expert grid into 3-D galaxy coordinates.
 *  - Y axis = layer depth (bottom = early, top = late)
 *  - XZ plane = experts wrap around a cylinder per layer
 *  - Specialists cluster tighter (smaller radius), generalists spread
 *  - Heat affects radial offset (hot experts drift outward slightly) */
function layoutGalaxy(
  data: ExpertMap,
  atlas: Record<string, AtlasEntry> | null,
): GalaxyNode[] {
  const { rows, cols, map } = data
  const nodes: GalaxyNode[] = []
  const yScale = 12 / Math.max(rows, 1) // normalize layer height
  const baseRadius = Math.max(cols * 0.12, 3)

  for (let r = 0; r < rows; r++) {
    const { realLayer, isMtp } = resolveLayer(r, rows, data.geometry)
    const y = (r - rows / 2) * yScale

    for (let c = 0; c < cols; c++) {
      const i = r * cols + c
      const byte = parseInt(map.substr(i * 2, 2), 16) || 0
      const tier = byte >> 6
      const heat = byte & 63

      // hydra (F4c): skip atlas entries lacking label/affinity instead of crashing
      const entry = atlas?.[`${realLayer}:${c}`]
      const safeEntry = entryIsSafe(entry) ? entry : undefined
      const isSpecialist = safeEntry?.label.startsWith("specialist") ?? false
      const entropy = safeEntry?.entropy ?? 0.5

      // Angle around the cylinder for this expert column
      const angle = (c / cols) * Math.PI * 2
      // Specialists cluster inward, generalists spread outward
      const radiusJitter = isSpecialist ? 0.6 : 1.0 + entropy * 0.4
      // Heat pushes nodes slightly outward
      const heatOffset = (heat / 63) * 0.3
      const radius = baseRadius * radiusJitter * (1 + heatOffset)

      const x = Math.cos(angle) * radius
      const z = Math.sin(angle) * radius

      nodes.push({
        index: i,
        row: r,
        col: c,
        tier,
        heat,
        realLayer,
        isMtp,
        entry: safeEntry,
        position: new THREE.Vector3(x, y, z),
      })
    }
  }
  return nodes
}

/* ---- Component ---- */

export function Galaxy({
  baseUrl,
  apiKey,
  connected,
  engineId,
}: {
  baseUrl: string
  apiKey: string
  connected: boolean
  engineId?: string
}) {
  const { t } = useLocale()
  const engQ = engineId ? `?engine=${encodeURIComponent(engineId)}` : ""
  const containerRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const tooltipRef = useRef<HTMLDivElement>(null)

  const [data, setData] = useState<ExpertMap | null>(null)
  const [atlas, setAtlas] = useState<Record<string, AtlasEntry> | null>(null)
  const [atlasFailed, setAtlasFailed] = useState(false)
  const [atlasRetry, setAtlasRetry] = useState(0)
  // hydra (N2): honest notice when the atlas exists but nothing in it is
  // displayable (entries dropped by the F4c safety rule) — same rule as Brain.
  const [atlasNotice, setAtlasNotice] = useState<string | null>(null)
  const [probeErr, setProbeErr] = useState(false)
  const [selectedNode, setSelectedNode] = useState<GalaxyNode | null>(null)
  const [hoverNode, setHoverNode] = useState<GalaxyNode | null>(null)
  const [turn, setTurn] = useState<number | null>(null)
  const [autoRotate, setAutoRotate] = useState(true)
  // hydra (F5): live ring turn inventory from GET /turns — the picker used to
  // be hardcoded 0..19 while the engine ring actually holds a different,
  // moving window of turns (e.g. 16..271).
  const [turnsList, setTurnsList] = useState<TurnSummary[]>([])

  // Three.js refs (not state to avoid re-renders)
  const sceneRef = useRef<THREE.Scene | null>(null)
  const cameraRef = useRef<THREE.PerspectiveCamera | null>(null)
  const rendererRef = useRef<THREE.WebGLRenderer | null>(null)
  const controlsRef = useRef<OrbitControls | null>(null)
  const pointsRef = useRef<THREE.Points | null>(null)
  const raycasterRef = useRef(new THREE.Raycaster())
  const mouseRef = useRef(new THREE.Vector2())
  const nodesRef = useRef<GalaxyNode[]>([])
  const frameRef = useRef(0)

  // Load atlas metadata (expert labels, affinity, etc.).
  // Step 3: never silent-degrade on 404 — surface atlasFailed + badge + retry.
  useEffect(() => {
    const base = baseUrl.replace(/\/v1\/?$/, "")
    const controller = new AbortController()
    setAtlasFailed(false)
    setAtlasNotice(null)
    fetch(endpoint(base, "/experts.json" + engQ), {
      headers: apiKey ? { Authorization: `Bearer ${apiKey}` } : {},
      signal: controller.signal,
    })
      .then((r) => {
        if (!r.ok) throw new Error(`/experts.json ${r.status}`)
        return r.json()
      })
      .then((d) => {
        if (controller.signal.aborted) return
        // hydra (F4c+N2): sanitize at fetch, same rule as Brain — an entry
        // without label/affinity is dropped, and ANY dropped entry (or an
        // empty map) makes the atlas not displayable: honest notice instead
        // of a misleading partial galaxy.
        if (!(d?.experts && typeof d.experts === "object")) {
          throw new Error("Atlas unavailable: bad payload")
        }
        const kept: Record<string, AtlasEntry> = {}
        let dropped = 0
        for (const [key, value] of Object.entries(d.experts as Record<string, AtlasEntry>)) {
          if (entryIsSafe(value)) kept[key] = value
          else dropped++
        }
        if (Object.keys(kept).length === 0 || dropped > 0) {
          setAtlas(null)
          setAtlasNotice(`atlas not displayable (${dropped} entries dropped)`)
          return
        }
        setAtlas(kept)
        setAtlasFailed(false)
      })
      .catch((err) => {
        if (controller.signal.aborted || err?.name === "AbortError") return
        setAtlas(null)
        setAtlasFailed(true)
      })
    return () => controller.abort()
  }, [baseUrl, apiKey, engQ, atlasRetry])

  // hydra (F5): poll /turns for the live ring inventory (2s while connected).
  // hydra (N1): when /turns is unavailable the picker shows "Live" only —
  // no legacy 0..19 list (the fork's 404 body is opaque, so availability is
  // signalled by the list simply staying empty).
  useEffect(() => {
    if (!connected) return
    let disposed = false
    const poll = async () => {
      if (document.visibilityState === "hidden") return
      try {
        const result = await getTurns(baseUrl, apiKey)
        if (disposed) return
        setTurnsList(result.turns)
      } catch {
        if (!disposed) setTurnsList([])
      }
    }
    void poll()
    const t = window.setInterval(() => void poll(), 2000)
    return () => {
      disposed = true
      window.clearInterval(t)
    }
  }, [baseUrl, apiKey, connected])

  // hydra (B2): an evicted turn must fall back to live — the /experts
  // poller keeps the last frame on failure, and the picker would otherwise
  // show "Live" while stale per-turn data renders.
  useEffect(() => {
    if (turn === null) return
    if (turnsList.length > 0 && !turnsList.some((t) => t.turn_seq === turn)) {
      setTurn(null)
    }
  }, [turn, turnsList])

  // Poll /experts for live data
  useEffect(() => {
    if (!connected) return
    let disposed = false
    const base = baseUrl.replace(/\/v1\/?$/, "")
    const poll = async () => {
      try {
        const turnParam = turn !== null ? `?turn=${turn}` : engQ || ""
        const res = await fetch(endpoint(base, "/experts" + turnParam), {
          headers: apiKey ? { Authorization: `Bearer ${apiKey}` } : {},
        })
        if (!res.ok) throw new Error(`/experts ${res.status}`)
        const next = (await res.json()) as ExpertMap
        if (disposed || !next.rows) return
        setData(next)
        setProbeErr(false)
      } catch (err) {
        if (!disposed) {
          setProbeErr(true)
          // hydra (B2): 404 on /experts?turn=N = the selected turn was
          // evicted from the ring — drop back to live instead of freezing
          // on the last frame.
          if (turn !== null && /\/experts 404/.test(String(err))) setTurn(null)
        }
      }
    }
    void poll()
    const t = window.setInterval(() => void poll(), 2000)
    return () => {
      disposed = true
      window.clearInterval(t)
    }
  }, [baseUrl, apiKey, connected, engQ, turn])

  // Compute galaxy nodes
  const nodes = useMemo(
    () => (data ? layoutGalaxy(data, atlas) : []),
    [data, atlas],
  )

  // Initialize Three.js scene
  useEffect(() => {
    const container = containerRef.current
    const canvas = canvasRef.current
    if (!container || !canvas) return

    const scene = new THREE.Scene()
    scene.background = new THREE.Color(0x07090a)

    const camera = new THREE.PerspectiveCamera(
      50,
      container.clientWidth / container.clientHeight,
      0.1,
      200,
    )
    camera.position.set(0, 8, 20)

    const renderer = new THREE.WebGLRenderer({
      canvas,
      antialias: true,
      alpha: false,
    })
    renderer.setSize(container.clientWidth, container.clientHeight)
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))

    const controls = new OrbitControls(camera, canvas)
    controls.enableDamping = true
    controls.dampingFactor = 0.08
    controls.autoRotate = true
    controls.autoRotateSpeed = 0.6
    controls.minDistance = 5
    controls.maxDistance = 60
    controls.target.set(0, 0, 0)

    // Subtle ambient + point lights for depth
    const ambient = new THREE.AmbientLight(0x4ed6a5, 0.15)
    scene.add(ambient)
    const point = new THREE.PointLight(0xffffff, 0.4, 50)
    point.position.set(10, 15, 10)
    scene.add(point)

    sceneRef.current = scene
    cameraRef.current = camera
    rendererRef.current = renderer
    controlsRef.current = controls

    // Resize observer
    const ro = new ResizeObserver(() => {
      const w = container.clientWidth
      const h = container.clientHeight
      camera.aspect = w / h
      camera.updateProjectionMatrix()
      renderer.setSize(w, h)
    })
    ro.observe(container)

    return () => {
      ro.disconnect()
      controls.dispose()
      renderer.dispose()
      sceneRef.current = null
      cameraRef.current = null
      rendererRef.current = null
      controlsRef.current = null
    }
  }, [])

  // Update auto-rotate
  useEffect(() => {
    if (controlsRef.current) controlsRef.current.autoRotate = autoRotate
  }, [autoRotate])

  // Update nodes → Three.js points
  useEffect(() => {
    const scene = sceneRef.current
    if (!scene) return
    nodesRef.current = nodes

    // Remove old points
    if (pointsRef.current) {
      scene.remove(pointsRef.current)
      pointsRef.current.geometry.dispose()
      ;(pointsRef.current.material as THREE.PointsMaterial).dispose()
    }
    if (nodes.length === 0) return

    const geometry = new THREE.BufferGeometry()
    const positions = new Float32Array(nodes.length * 3)
    const colors = new Float32Array(nodes.length * 3)
    const sizes = new Float32Array(nodes.length)

    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i]
      positions[i * 3] = n.position.x
      positions[i * 3 + 1] = n.position.y
      positions[i * 3 + 2] = n.position.z

      const color = new THREE.Color(TIER_COLOR[n.tier] ?? TIER_COLOR[0])
      // Heat brightens the color
      const heatFactor = 0.4 + 0.6 * Math.min(n.heat / 24, 1)
      colors[i * 3] = color.r * heatFactor
      colors[i * 3 + 1] = color.g * heatFactor
      colors[i * 3 + 2] = color.b * heatFactor

      // Size: specialists are slightly larger, heat adds size (F4c: entry
      // is pre-guarded in layoutGalaxy, so label access is safe here)
      const base = n.entry?.label.startsWith("specialist") ? 0.22 : 0.14
      sizes[i] = base + (n.heat / 63) * 0.12
    }

    geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3))
    geometry.setAttribute("color", new THREE.BufferAttribute(colors, 3))
    geometry.setAttribute("size", new THREE.BufferAttribute(sizes, 1))

    const material = new THREE.PointsMaterial({
      size: 0.2,
      vertexColors: true,
      transparent: true,
      opacity: 0.9,
      sizeAttenuation: true,
      depthWrite: false,
    })

    const points = new THREE.Points(geometry, material)
    scene.add(points)
    pointsRef.current = points
  }, [nodes])

  // Animation loop
  useEffect(() => {
    let running = true
    const animate = () => {
      if (!running) return
      const renderer = rendererRef.current
      const scene = sceneRef.current
      const camera = cameraRef.current
      const controls = controlsRef.current
      if (renderer && scene && camera && controls) {
        controls.update()
        renderer.render(scene, camera)
      }
      frameRef.current = requestAnimationFrame(animate)
    }
    animate()
    return () => {
      running = false
      cancelAnimationFrame(frameRef.current)
    }
  }, [])

  // Raycasting for hover/click
  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      const canvas = canvasRef.current
      const camera = cameraRef.current
      const points = pointsRef.current
      if (!canvas || !camera || !points) return

      const rect = canvas.getBoundingClientRect()
      mouseRef.current.x = ((e.clientX - rect.left) / rect.width) * 2 - 1
      mouseRef.current.y = -((e.clientY - rect.top) / rect.height) * 2 + 1

      raycasterRef.current.setFromCamera(mouseRef.current, camera)
      const intersects = raycasterRef.current.intersectObject(points)
      if (intersects.length > 0) {
        const idx = intersects[0].index ?? -1
        if (idx >= 0 && idx < nodesRef.current.length) {
          setHoverNode(nodesRef.current[idx])
          canvas.style.cursor = "pointer"
          return
        }
      }
      setHoverNode(null)
      canvas.style.cursor = "grab"
    },
    [],
  )

  const onPointerClick = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      const canvas = canvasRef.current
      const camera = cameraRef.current
      const points = pointsRef.current
      if (!canvas || !camera || !points) return

      const rect = canvas.getBoundingClientRect()
      mouseRef.current.x = ((e.clientX - rect.left) / rect.width) * 2 - 1
      mouseRef.current.y = -((e.clientY - rect.top) / rect.height) * 2 + 1

      raycasterRef.current.setFromCamera(mouseRef.current, camera)
      const intersects = raycasterRef.current.intersectObject(points)
      if (intersects.length > 0) {
        const idx = intersects[0].index ?? -1
        if (idx >= 0 && idx < nodesRef.current.length) {
          setSelectedNode(nodesRef.current[idx])
          return
        }
      }
      setSelectedNode(null)
    },
    [],
  )

  // Summary stats
  const stats = useMemo(() => {
    if (!data) return null
    const tiers = [0, 0, 0]
    let totalHeat = 0
    let specialists = 0
    for (const n of nodes) {
      tiers[n.tier]++
      totalHeat += n.heat
      if (n.entry?.label.startsWith("specialist")) specialists++
    }
    return {
      tiers,
      specialists,
      generalists: nodes.length - specialists,
      avgHeat: nodes.length > 0 ? totalHeat / nodes.length : 0,
    }
  }, [nodes, data])

  // Tooltip position follows cursor
  const tooltipPos = useMemo(() => {
    if (!hoverNode) return null
    // Use the canvas-relative hover position
    const canvas = canvasRef.current
    const camera = cameraRef.current
    if (!canvas || !camera) return null

    // Project node position to screen
    const vec = hoverNode.position.clone()
    vec.project(camera)
    const rect = canvas.getBoundingClientRect()
    return {
      x: ((vec.x + 1) / 2) * rect.width + rect.left,
      y: ((-vec.y + 1) / 2) * rect.height + rect.top,
    }
  }, [hoverNode])

  return (
    <div className="galaxy-page">
      <div className="galaxy-head">
        <div className="section-title">
          <Orbit className="size-4" /> {t("galaxy.title")} —{" "}
          {data
            ? t("galaxy.layers", { rows: data.rows, cols: data.cols })
            : t("galaxy.waiting")}
        </div>
        <div className="galaxy-controls">
          <button
            className={autoRotate ? "galaxy-btn active" : "galaxy-btn"}
            onClick={() => setAutoRotate(!autoRotate)}
            title={t("galaxy.autoRotate")}
          >
            <RotateCcw className="size-3.5" />
            {t("galaxy.spin")}
          </button>
          <label className="galaxy-turn-select">
            <Layers className="size-3.5" />
            {t("galaxy.turn")}
            <select
              value={turn ?? ""}
              onChange={(e) =>
                setTurn(e.target.value === "" ? null : Number(e.target.value))
              }
            >
              <option value="">{t("galaxy.live")}</option>
              {/* hydra (F5): real ring inventory from GET /turns, newest
                  first. Eviction falls back to live via the B2 effect. */}
              {turnsList
                .slice()
                .reverse()
                .map((t) => (
                  <option key={t.turn_seq} value={t.turn_seq}>
                    #{t.turn_seq} — {t.completion_tokens} tok
                  </option>
                ))}
            </select>
          </label>
        </div>
      </div>

      <div className="galaxy-legend">
        <span>
          <i style={{ background: TIER_CSS[2] }} /> {t("tier.vram")}{" "}
          {stats?.tiers[2].toLocaleString()}
        </span>
        <span>
          <i style={{ background: TIER_CSS[1] }} /> {t("tier.ram")}{" "}
          {stats?.tiers[1].toLocaleString()}
        </span>
        <span>
          <i style={{ background: TIER_CSS[0] }} /> {t("tier.disk")}{" "}
          {stats?.tiers[0].toLocaleString()}
        </span>
        <span>
          specialists {stats?.specialists} · generalists {stats?.generalists}
        </span>
        <span className="galaxy-drag-hint">{t("galaxy.dragHint")}</span>
      </div>

      <div className="galaxy-canvas-wrap" ref={containerRef}>
        {atlasFailed && (
          <div className="galaxy-atlas-badge" role="alert">
            <TriangleAlert className="size-3.5" />
            <span>{t("galaxy.atlasMissing")}</span>
            <button type="button" onClick={() => setAtlasRetry((n) => n + 1)}>
              {t("galaxy.atlasRetry")}
            </button>
          </div>
        )}
        {atlasNotice && (
          <p className="runtime-unavailable" role="status">
            {atlasNotice}
          </p>
        )}
        <canvas
          ref={canvasRef}
          onPointerMove={onPointerMove}
          onPointerLeave={() => setHoverNode(null)}
          onClick={onPointerClick}
        />
        {!connected && (
          <p className="runtime-unavailable">{t("galaxy.connectHint")}</p>
        )}
        {connected && data && data.telemetry_enabled === false && (
          <p className="runtime-unavailable">{t("galaxy.noTelemetry")}</p>
        )}
      </div>

      {/* Hover tooltip */}
      {hoverNode && tooltipPos && (
        <div
          className="galaxy-tooltip"
          style={{
            left: Math.min(tooltipPos.x + 14, window.innerWidth - 280),
            top: Math.min(tooltipPos.y + 14, window.innerHeight - 170),
          }}
        >
          <div className="galaxy-tooltip-title">
            <Layers className="size-3" /> Layer {hoverNode.realLayer}
            {hoverNode.isMtp ? " (MTP)" : ""} · Expert {hoverNode.col}
          </div>
          <div>
            Tier:{" "}
            <strong style={{ color: TIER_CSS[hoverNode.tier] }}>
              {t(TIER_KEYS[hoverNode.tier])}
            </strong>
          </div>
          <div>
            Heat:{" "}
            <strong>
              {hoverNode.heat === 0
                ? t("galaxy.neverRouted")
                : t("galaxy.selections", { heat: hoverNode.heat })}
            </strong>
          </div>
          {entryIsSafe(hoverNode.entry) ? (
            <>
              <div className="galaxy-tooltip-label">
                {hoverNode.entry.label.startsWith("specialist")
                  ? t("galaxy.specialist", { top: hoverNode.entry.top })
                  : t("galaxy.generalist")}
                <small> (entropy {hoverNode.entry.entropy})</small>
              </div>
              {typeof hoverNode.entry.spec === "number" &&
                hoverNode.entry.spec < 0.7 && (
                  <div className="galaxy-tooltip-weak">
                    weak (spec {hoverNode.entry.spec.toFixed(2)}
                    {hoverNode.entry.reliability
                      ? `, ${hoverNode.entry.reliability}`
                      : ""}
                    )
                  </div>
                )}
              <div className="galaxy-tooltip-aff">
                {Object.entries(hoverNode.entry.affinity)
                  .sort((a, b) => b[1] - a[1])
                  .slice(0, 3)
                  .map(([c, p]) => `${c} ${Math.round(p * 100)}%`)
                  .join(" · ")}
              </div>
            </>
          ) : (
            <div className="galaxy-tooltip-role">
              {t("galaxy.unknownExpert")}
            </div>
          )}
        </div>
      )}

      {/* Selected expert detail panel */}
      {selectedNode && (
        <div className="galaxy-detail">
          <div className="galaxy-detail-head">
            <span className="galaxy-detail-title">
              <Layers className="size-3.5" /> Layer {selectedNode.realLayer}
              {selectedNode.isMtp ? " (MTP)" : ""} · Expert{" "}
              {selectedNode.col}
            </span>
            <button
              className="galaxy-detail-close"
              onClick={() => setSelectedNode(null)}
            >
              ×
            </button>
          </div>
          <div className="galaxy-detail-grid">
            <div>
              <span>{t("galaxy.tier")}</span>
              <strong style={{ color: TIER_CSS[selectedNode.tier] }}>
                {t(TIER_KEYS[selectedNode.tier])}
              </strong>
            </div>
            <div>
              <span>{t("galaxy.heat")}</span>
              <strong>{selectedNode.heat}</strong>
            </div>
            {entryIsSafe(selectedNode.entry) && (
              <>
                <div>
                  <span>{t("galaxy.label")}</span>
                  <strong>{selectedNode.entry.label}</strong>
                </div>
                <div>
                  <span>{t("galaxy.entropy")}</span>
                  <strong>{selectedNode.entry.entropy}</strong>
                </div>
                <div>
                  <span>{t("galaxy.topCategory")}</span>
                  <strong>{selectedNode.entry.top}</strong>
                </div>
                {typeof selectedNode.entry.spec === "number" && (
                  <div>
                    <span>{t("galaxy.specialization")}</span>
                    <strong>{selectedNode.entry.spec.toFixed(3)}</strong>
                  </div>
                )}
              </>
            )}
          </div>
          {entryIsSafe(selectedNode.entry) && (
            <div className="galaxy-detail-affinity">
              <div className="galaxy-detail-aff-title">
                {t("galaxy.affinityProfile")}
              </div>
              {Object.entries(selectedNode.entry.affinity)
                .sort((a, b) => b[1] - a[1])
                .map(([cat, prob]) => (
                  <div key={cat} className="galaxy-detail-aff-row">
                    <span>{cat}</span>
                    <div className="galaxy-detail-aff-bar">
                      <div
                        style={{ width: `${Math.round(prob * 100)}%` }}
                      />
                    </div>
                    <code>{Math.round(prob * 100)}%</code>
                  </div>
                ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

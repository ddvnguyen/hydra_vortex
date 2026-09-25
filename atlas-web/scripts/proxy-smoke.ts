// atlas-web/scripts/proxy-smoke.ts — B4 engine-proxy allowlist smoke test.
//
// This package has no test runner (package.json has no "test"), so a small
// self-contained script stands in. It spawns a stub engine + the real
// server.ts, then proves:
//   1. GET  /engine-proxy/<id>/slots  -> 403 {"error":"forbidden"} (architect repro)
//   2. GET  /engine-proxy/<id>/props   -> 403 {"error":"forbidden"} (architect repro)
//   3. GET  /engine-proxy/<id>/health  -> 200 (allowlisted route proxies to engine)
//   4. POST /engine-proxy/<id>/v1/chat/completions -> forwarded (allowlisted)
//   5. non-allowlisted method/path + traversal never reach the engine
//
// Run: bun run scripts/proxy-smoke.ts   (exit 0 = pass, 1 = fail)
import { spawn } from "node:child_process"
import { join, dirname } from "node:path"
import { fileURLToPath } from "node:url"

const HERE = dirname(fileURLToPath(import.meta.url))
const ATLAS_WEB = join(HERE, "..") // atlas-web/

function fail(msg: string): never {
  console.error(`FAIL: ${msg}`)
  process.exit(1)
}
function ok(msg: string) { console.log(`  ok  ${msg}`) }

// A stub engine: /health returns 200, /experts returns a Stage-B-shaped body
// so server.ts's own /health aggregate stays happy, everything else 200 JSON.
const stub = Bun.serve({
  port: 0,
  hostname: "127.0.0.1",
  fetch(req) {
    const p = new URL(req.url).pathname
    if (p === "/health") return Response.json({ status: "ok" })
    if (p === "/experts") return Response.json({ seq: 0, rows: 0, cols: 0, map: "", hits: "" })
    return Response.json({ ok: true, path: p, method: req.method })
  },
})

async function freePort(): Promise<number> {
  const s = Bun.serve({ port: 0, hostname: "127.0.0.1", fetch: () => new Response() })
  const p = s.port
  await s.stop(true)
  return p
}

const webPort = await freePort()
const child = spawn(process.execPath, [join(ATLAS_WEB, "server", "server.ts")], {
  env: {
    ...process.env,
    ATLAS_WEB_PORT: String(webPort),
    ATLAS_WEB_HOST: "127.0.0.1",
    // engine mode (not mock) — no ranks file needed; engine id "smoke"
    ATLAS_ENGINES: `smoke=SmokeStub=http://127.0.0.1:${stub.port}`,
  },
  stdio: ["ignore", "pipe", "pipe"],
})
child.stderr.on("data", (d) => process.stderr.write(`[server] ${d}`))

const base = `http://127.0.0.1:${webPort}`

// wait for readiness (server.ts /health always answers 200 once listening)
let ready = false
for (let i = 0; i < 100 && !ready; i++) {
  try { ready = (await fetch(`${base}/health`)).status === 200 } catch { /* not up yet */ }
  if (!ready) await Bun.sleep(50)
}
if (!ready) { child.kill(); stub.stop(true); fail("server did not become ready") }

try {
  // 1. /slots must be blocked (architect repro: was 200 before B4)
  let r = await fetch(`${base}/engine-proxy/smoke/slots`)
  let body = await r.json().catch(() => null)
  if (r.status !== 403) fail(`GET /slots -> ${r.status}, expected 403`)
  if (body?.error !== "forbidden") fail(`GET /slots body -> ${JSON.stringify(body)}, expected {"error":"forbidden"}`)
  ok(`GET  /engine-proxy/smoke/slots -> 403 ${JSON.stringify(body)}`)

  // 2. /props must be blocked (architect repro)
  r = await fetch(`${base}/engine-proxy/smoke/props`)
  if (r.status !== 403) fail(`GET /props -> ${r.status}, expected 403`)
  ok(`GET  /engine-proxy/smoke/props -> 403`)

  // 3. allowlisted GET must proxy to the engine (200)
  r = await fetch(`${base}/engine-proxy/smoke/health`)
  body = await r.json().catch(() => null)
  if (r.status !== 200) fail(`GET /health -> ${r.status}, expected 200`)
  if (body?.status !== "ok") fail(`GET /health body -> ${JSON.stringify(body)}, expected stub {"status":"ok"}`)
  ok(`GET  /engine-proxy/smoke/health -> 200 ${JSON.stringify(body)}`)

  // 4. allowlisted POST must forward
  r = await fetch(`${base}/engine-proxy/smoke/v1/chat/completions`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: "{}",
  })
  if (r.status !== 200) fail(`POST /v1/chat/completions -> ${r.status}, expected 200`)
  ok(`POST /engine-proxy/smoke/v1/chat/completions -> 200`)

  // 4b. colibri-1120 rebuild additions (read-only surface, allowlisted):
  //     GET experts.json (engine-hosted atlas artifact) + POST v1/brio (1.12.0)
  r = await fetch(`${base}/engine-proxy/smoke/experts.json`)
  if (r.status !== 200) fail(`GET /experts.json -> ${r.status}, expected 200 (allowlisted for the 1.12.0 port)`)
  ok(`GET  /engine-proxy/smoke/experts.json -> 200`)
  r = await fetch(`${base}/engine-proxy/smoke/v1/brio`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: "{}",
  })
  if (r.status !== 200) fail(`POST /v1/brio -> ${r.status}, expected 200 (allowlisted for the 1.12.0 port)`)
  ok(`POST /engine-proxy/smoke/v1/brio -> 200`)

  // 5. non-allowlisted method/path + traversal never reach the engine
  r = await fetch(`${base}/engine-proxy/smoke/completion`, { method: "POST", body: "{}" })
  if (r.status !== 403) fail(`POST /completion -> ${r.status}, expected 403`)
  ok(`POST /engine-proxy/smoke/completion -> 403`)

  // Traversal probes. Bun normalizes dot-segments (and decodes %2e) BEFORE the
  // handler sees Request.url, so a traversal request either 403s (guard) or
  // normalizes to a path outside /engine-proxy (SPA fallback / 404). The
  // security property we assert: the stub engine must NEVER answer a traversal
  // request — if a regression ever forwards raw dots, the stub echo appears.
  const traversalUrls = [
    `${base}/engine-proxy/smoke/%2e%2e/%2e%2e/etc/passwd`,
    `${base}/engine-proxy/smoke/health/%2e%2e/%2e%2e/%2e%2e/etc/passwd`,
  ]
  for (const u of traversalUrls) {
    r = await fetch(u)
    const text = await r.text()
    if (text.includes('"ok":true') || text.includes('"path":')) {
      fail(`traversal ${u} was PROXIED to the engine (stub echo in response)`)
    }
    ok(`GET  ${u.replace(`${base}/`, "")} -> ${r.status} (not proxied: no stub echo)`)
  }

  // unknown engine on an allowlisted path is still not open (404, not proxy)
  r = await fetch(`${base}/engine-proxy/nope/health`)
  if (r.status !== 404) fail(`GET unknown-engine /health -> ${r.status}, expected 404`)
  ok(`GET  /engine-proxy/nope/health -> 404 (unknown engine)`)

  console.log("\nPASS: B4 allowlist smoke (403 blocked, 200 allowlisted)")
} finally {
  child.kill("SIGTERM")
  stub.stop(true)
}

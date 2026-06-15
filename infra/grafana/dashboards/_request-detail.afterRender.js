// afterRender for the Business Text (marcusolsson-dynamictext) panel.
// Renders the composition/aligned request-timeline with side-panel detail view
// against the Loki request_timeline frame. Source of truth lives here; the
// dashboard JSON embeds a copy (build with scripts/build-request-detail.py).
try {
  let el = context.element;
  if (el && el.jquery) el = el.get(0);
  if (!el) return;
  const root = (el.querySelector && el.querySelector('#hydra-tl-root')) || el;
  // The root's height is set in render() to the panel's content height
  // (measured via getBoundingClientRect on the closest sized ancestor).
  // No min-height forcing the panel taller, no JS measurement loops.
  if (root.style) {
    root.style.boxSizing = 'border-box';
    root.style.position = 'relative';
    root.style.width = '100%';
    root.style.display = 'block';
  }

  // Normalize data — Business Text panel may deliver per-row objects or
  // columnar frames (DataFrameView with .fields). Flatten both into an
  // array of scalar-keyed rows.
  const num = function (x) { const k = Number(x); return isFinite(k) ? k : 0; };
  // Try multiple shapes: array, .series, .fields, or the data itself
  let rawFrames = null;
  if (Array.isArray(context.data)) {
    rawFrames = context.data;
  } else if (context.data && Array.isArray(context.data.series)) {
    rawFrames = context.data.series;
  } else if (context.data && Array.isArray(context.data.fields)) {
    // DataFrameView shape: convert .fields (array of {name, values})
    // into a columnar object {fieldName: [...values]}
    const f = context.data.fields;
    const col = {};
    f.forEach(function (field) { col[field.name] = field.values; });
    rawFrames = [col];
  } else if (context.dataFrame) {
    rawFrames = [context.dataFrame];
  }
  const arr = rawFrames || [];
  const flat = [];
  arr.forEach(function (fr) {
    if (!fr || typeof fr !== 'object') return;
    const probe = fr.trace_id !== undefined ? fr.trace_id : fr.total_ms;
    if (Array.isArray(probe)) {
      for (let i = 0; i < probe.length; i++) {
        const o = {};
        Object.keys(fr).forEach(function (k) { o[k] = Array.isArray(fr[k]) ? fr[k][i] : fr[k]; });
        flat.push(o);
      }
    } else {
      flat.push(fr);
    }
  });

  // ── Helpers ─────────────────────────────────────────────────────────
  const ms = function (v) { return (v / 1000).toFixed(2); };
  const esc = function (s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); };
  const fmtTime = function (ts) {
    if (ts === undefined || ts === null || ts === '') return '';
    let ms = Number(ts);
    if (!isFinite(ms) || isNaN(ms)) return String(ts);
    // Loki's `Time` field arrives as nanoseconds; `timestamp_ms` is already
    // in milliseconds. Detect by magnitude and normalise.
    if (ms > 1e15) ms = Math.floor(ms / 1e6);
    const d = new Date(ms);
    if (isNaN(d.getTime())) return String(ts);
    return String(d.getHours()).padStart(2, '0') + ':'
      + String(d.getMinutes()).padStart(2, '0') + ':'
      + String(d.getSeconds()).padStart(2, '0') + '.'
      + String(d.getMilliseconds()).padStart(3, '0');
  };
  const typeOf = function (rt, prefixHit) {
    rt = (rt || '').toLowerCase();
    if (rt === 'migration')      return { t: 'RESUME', c: '#a371f7', d: 'Full KV restore from Store' };
    if (rt === 'cold_atomic')    return { t: 'ATOMIC', c: '#db6d28', d: 'Single node — prefill+decode on same GPU' };
    if (rt === 'cold_concurrency') return { t: 'SPLIT',  c: '#d29922', d: 'Split — prefill on one node, decode on another' };
    if (rt.indexOf('warm') >= 0 || rt.indexOf('affinity') >= 0) return { t: 'WARM', c: '#2f81f7', d: 'Warm slot affinity — no prefill needed' };
    if (prefixHit === 'true')    return { t: 'PREFIX', c: '#58a6ff', d: 'Prefix checkpoint reused, partial prefill' };
    return { t: 'COLD', c: '#db6d28', d: 'Fresh full prefill from scratch' };
  };

  // ── Phase model ─────────────────────────────────────────────────────
  const PHASE = {
    queue:      { key: 'queue_wait_ms', label: 'Queue',         color: '#6e7681' },
    prefill:    { key: 'prefill_ms',    label: 'Prefill',       color: '#388bfd' },
    save_kv:    { key: 'save_kv_ms',    label: 'Save cache',    color: '#d29922' },
    restore_kv: { key: 'restore_kv_ms', label: 'Restore cache', color: '#a371f7' },
    decode:     { key: 'decode_ms',     label: 'Decode',        color: '#3fb950' },
  };
  const ORDER = ['queue', 'prefill', 'save_kv', 'restore_kv', 'decode'];

  // Return the server name for a phase given the row's actual node data.
  const phaseServer = function (p, row) {
    if (p.k === 'prefill') return (row.prefillNode || '?').toUpperCase();
    if (p.k === 'decode')  return (row.decodeNode || '?').toUpperCase();
    return 'HYDRA';
  };

  // ── Build row model ─────────────────────────────────────────────────
    const rows = [];
  const merged = {};  // trace_id → merged row
  flat.forEach(function (d, i) {
    const traceId = String(d.trace_id || '');
    const sessionId = String(d.session_id || '');
    if (!traceId && !sessionId) return;

    const phases = [];
    let cum = 0;
    ORDER.forEach(function (k) {
      const meta = PHASE[k];
      const dur = num(d[meta.key]);
      if (dur <= 0) return;
      if (k === 'restore_kv' && num(d.kv_bytes) === 0) return;
      phases.push({ k: k, label: meta.label, color: meta.color, dur: dur, start: cum });
      cum += dur;
    });
    const total = num(d.total_ms) || cum || 0;
    let tsMs = d.timestamp_ms || d.Time || d.time || d.ts || d.timestamp;
    if (tsMs && Number(tsMs) > 1e15) tsMs = Math.floor(Number(tsMs) / 1e6);
    if (!Number(tsMs)) return;

    const key = traceId || sessionId;
    const row = {
      id: traceId ? traceId.slice(0, 8) : sessionId.slice(0, 12),
      traceId: traceId,
      sessionId: sessionId,
      route: String(d.route_type || ''),
      prefixHit: String(d.prefix_hit || ''),
      prefillNode: String(d.prefill_node || '-'),
      decodeNode: String(d.decode_node || '-'),
      prefillModel: String(d.prefill_model || ''),
      decodeModel: String(d.decode_model || ''),
      timestamp: fmtTime(tsMs),
      tokensIn: num(d.tokens_in),
      tokensOut: num(d.tokens_out),
      kvBytes: num(d.kv_bytes),
      phases: phases,
      total: total,
      status: String(d.status || ''),
      _tsMs: Number(tsMs),
    };

    // Merge multiple entries for same trace_id — later entry wins for fields
    // that are non-zero, phases accumulate.
    const cur = merged[key];
    if (cur && row._tsMs > cur._tsMs) {
      const isFinal = row.status === 'done' || total > 0;
      if (isFinal) {
        // Final entry: replace phases and totals with complete data
        merged[key] = row;
      } else {
        // Partial update: merge fields but keep phases from final only
        Object.keys(row).forEach(function (k) {
          if (k === 'phases' || k === 'total' || k === '_tsMs') return;
          if (row[k] !== undefined && row[k] !== '' && row[k] !== 0) cur[k] = row[k];
        });
        cur._tsMs = row._tsMs;
        cur.timestamp = row.timestamp;
      }
    } else if (!cur) {
      merged[key] = row;
    }
  });

  Object.values(merged).forEach(function (r) {
    // Compute total from phases if not set (in-flight requests)
    if (!r.total) {
      r.total = r.phases.reduce(function (s, p) { return s + p.dur; }, 0);
    }
    rows.push(r);
  });

  const S = (window.__hydraTL = window.__hydraTL || { view: 'composition', sel: 0, detail: true });
  // Sort rows by resolved timestamp (newest first). Done in JS because the
  // Grafana sortBy transform only sees `timestamp_ms` (the C# field), and
  // older log lines lack it — leaving them unsorted at the top.
  rows.sort(function (a, b) { return b._tsMs - a._tsMs; });
  const domainMax = Math.max.apply(null, [1].concat(rows.map(function (r) { return r.total; })));

  // ── Render ──────────────────────────────────────────────────────────
  function render() {
    if (!rows.length) {
      // Debug: surface what data fields are actually present
      const sample = arr[0];
      const keys = sample ? Object.keys(sample).join(',') : 'none';
      root.innerHTML = '<div style="padding:14px;color:#7d8590;font-size:12px;">No request_timeline data. Data keys: ' + keys + '</div>';
      return;
    }
    if (S.sel >= rows.length) S.sel = 0;
    const isComp = S.view === 'composition';

    // Fixed row height — stable across auto-refresh, no reflow jumps.
    // The row container is internally scrollable, so all rows in the
    // current time range are accessible without changing panel size.
    const rowH = 36;
    const headerH = 38;

    let html = '<div style="display:flex;gap:0;font-family:-apple-system,system-ui,sans-serif;color:#e6edf3;background:#0d1117;box-sizing:border-box;height:100%;min-height:0;">';

    // ── LEFT: request list ────────────────────────────────────────────
    html += '<div style="flex:1;min-width:0;display:flex;flex-direction:column;min-height:0;box-sizing:border-box;' + (S.detail ? 'border-right:1px solid #21262d;' : '') + '">';

    // Mini header — toggle only
    html += '<div style="display:flex;align-items:center;gap:10px;padding:6px 12px;border-bottom:1px solid #21262d;flex:none;height:' + headerH + 'px;box-sizing:border-box;font-size:9px;color:#ffffff;overflow:hidden;">';
    html += '<span style="font-size:11px;color:#ffffff;font-weight:600;">' + rows.length + ' requests</span>';
    html += '<div style="margin-left:auto;display:flex;gap:2px;background:#161b22;border:1px solid #30363d;border-radius:6px;padding:2px;">';
    [['composition', 'Composition'], ['aligned', 'Aligned']].forEach(function (vv) {
      const a = S.view === vv[0];
      html += '<button data-view="' + vv[0] + '" style="border:none;cursor:pointer;font-size:11px;font-weight:500;padding:3px 10px;border-radius:4px;background:' + (a ? '#30363d' : 'transparent') + ';color:#ffffff;">' + vv[1] + '</button>';
    });
    html += '</div>';
    html += '<span style="font-size:10px;color:#ffffff;font-family:monospace;">' + (isComp ? '% of latency' : 'ms') + '</span>';
    html += '</div>';

    // Row list — fills remaining space with internal scroll. All requests
    // in the time range are accessible by scrolling within this container;
    // no scrollbar on the outer Grafana panel or page.
    html += '<div style="flex:1 1 0;min-height:0;overflow-y:auto;overflow-x:hidden;scrollbar-width:thin;scrollbar-color:#7d8590 transparent;box-sizing:border-box;">';
    rows.forEach(function (r, ri) {
      const tc = typeOf(r.route, r.prefixHit);
      const sel = ri === S.sel;
      html += '<div data-row="' + ri + '" style="display:flex;align-items:center;height:' + rowH + 'px;box-sizing:border-box;cursor:pointer;border-bottom:1px solid #161b22;border-left:2px solid ' + (sel ? '#388bfd' : 'transparent') + ';background:' + (sel ? 'rgba(56,139,253,0.07)' : 'transparent') + ';">';
      html += '<div style="width:180px;flex:none;padding:0 10px;min-width:0;">';
      html += '<div style="display:flex;align-items:center;gap:5px;"><span style="font-family:monospace;font-size:11px;font-weight:600;color:#ffffff;">' + esc(r.id) + '</span><span style="font-size:8px;font-weight:700;color:#ffffff;background:' + tc.c + '1f;border:1px solid ' + tc.c + '4d;border-radius:3px;padding:1px 4px;font-family:monospace;">' + tc.t + '</span>' + (r.prefixHit === 'true' ? '<span style="font-size:7px;font-weight:700;color:#3fb950;border:1px solid #3fb9504d;border-radius:2px;padding:1px 3px;font-family:monospace;">CACHE</span>' : '') + '</div>';
      html += '<div style="font-size:10px;color:#ffffff;font-family:monospace;">' + r.timestamp;
      if (r.status && r.status !== 'done') html += ' <span style="color:#d29922;">' + esc(r.status) + '</span>';
      else html += ' &middot; ' + ms(r.total) + 's';
      html += '</div></div>';

      // Phase bars
      html += '<div style="flex:1;position:relative;height:' + rowH + 'px;min-width:0;">';
      r.phases.forEach(function (p) {
        const leftPct = isComp ? (p.start / r.total * 100) : (p.start / domainMax * 100);
        const wPct = isComp ? (p.dur / r.total * 100) : (p.dur / domainMax * 100);
        const w = Math.max(wPct, 1.5);
        let txt = '';
        if (wPct > 14) txt = esc(p.label) + ' ' + ms(p.dur) + 's';
        else if (wPct > 4) txt = ms(p.dur) + 's';
        const srv = phaseServer(p, r);
        html += '<div title="' + esc(p.label) + ' ' + ms(p.dur) + 's" style="position:absolute;left:' + leftPct + '%;width:' + w + '%;top:8px;height:20px;background:' + p.color + ';border-radius:3px;display:flex;align-items:center;padding:0 4px;overflow:hidden;box-shadow:' + (srv !== 'HYDRA' ? 'inset 0 1px 0 rgba(255,255,255,0.3)' : 'inset 0 0 0 1px rgba(0,0,0,0.15)') + ';">';
        if (txt) html += '<span style="font-size:9px;font-weight:600;color:#ffffff;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">' + txt + '</span>';
        html += '</div>';
      });
      html += '</div></div>';
    });
    html += '</div></div>';

    // ── RIGHT: detail side panel ──────────────────────────────────────
    if (S.detail) {
    const r = rows[S.sel];
    if (r) {
      const tc = typeOf(r.route, r.prefixHit);
      const ttft = r.phases.filter(function (p) { return p.k !== 'decode'; }).reduce(function (a, p) { return a + p.dur; }, 0);
      const dec = r.phases.find(function (p) { return p.k === 'decode'; });
      const tps = (dec && r.tokensOut > 0) ? Math.round(r.tokensOut / (dec.dur / 1000)) : null;
      const kvMiB = r.kvBytes > 0 ? (r.kvBytes / 1048576).toFixed(1) : '\u2014';
      const dom = r.phases.slice().sort(function (a, b) { return b.dur - a.dur; })[0] || { label: '\u2014', color: '#6e7681', dur: 0 };

      html += '<div style="width:300px;flex:none;background:#0b0f14;overflow:hidden;box-sizing:border-box;">';

      // Header — trace_id (preferred) + session_id + route type badge + close button
      html += '<div style="display:flex;align-items:flex-start;gap:8px;padding:10px 14px;border-bottom:1px solid #21262d;">';
      html += '<div style="flex:1;min-width:0;">';
      html += '<div style="display:flex;align-items:center;gap:6px;">';
      html += '<span style="font-family:monospace;font-size:13px;font-weight:700;color:#ffffff;">' + esc(r.id) + '</span>';
      html += '<span style="font-size:9px;font-weight:700;color:#ffffff;background:' + tc.c + '1f;border:1px solid ' + tc.c + '4d;border-radius:4px;padding:2px 6px;font-family:monospace;">' + tc.t + '</span>';
      html += '<span style="font-size:10px;color:#ffffff;">' + tc.d + '</span>';
      html += '<button data-close-detail style="margin-left:auto;border:none;cursor:pointer;background:#21262d;color:#8b949e;border-radius:4px;width:22px;height:22px;display:flex;align-items:center;justify-content:center;font-size:13px;line-height:1;">\u00d7</button>';
      html += '</div>';
      html += '<div style="display:flex;flex-wrap:wrap;gap:8px;margin-top:4px;font-size:10px;color:#7d8590;font-family:monospace;">';
      if (r.traceId) html += '<span title="trace id">trace: <span style="color:#c9d1d9;">' + esc(r.traceId) + '</span></span>';
      if (r.sessionId) html += '<span title="session id">session: <span style="color:#c9d1d9;">' + esc(r.sessionId) + '</span></span>';
      if (r.prefixHit === 'true' || r.prefixHit === 'false') {
        const phColor = r.prefixHit === 'true' ? '#3fb950' : '#db6d28';
        html += '<span style="color:' + phColor + ';">prefix_hit=' + esc(r.prefixHit) + '</span>';
      }
      html += '</div>';
      html += '</div></div>';

      // Metric tiles
      const tpsColor = tps == null ? '#8b949e' : (tps >= 20 ? '#3fb950' : (tps >= 10 ? '#d29922' : '#f85149'));
      const tiles = [
        ['Total latency', ms(r.total), 's', '#e6edf3'],
        ['TTFT', ms(ttft), 's', '#58a6ff'],
        ['Throughput', tps == null ? '\u2014' : tps, tps == null ? '' : 'tok/s', tpsColor],
        ['Tokens in / out', r.tokensIn + ' / ' + r.tokensOut, '', '#c9d1d9'],
        ['KV cache', kvMiB, kvMiB === '\u2014' ? '' : 'MiB', '#d29922'],
      ];
      html += '<div style="display:flex;gap:1px;background:#21262d;border-radius:6px;overflow:hidden;flex-wrap:wrap;margin:10px 14px;">';
      tiles.forEach(function (t) { html += '<div style="flex:1;min-width:80px;background:#0d1117;padding:8px 10px;"><div style="font-size:9px;color:#ffffff;text-transform:uppercase;letter-spacing:0.05em;">' + t[0] + '</div><div style="font-family:monospace;font-size:13px;font-weight:600;color:' + t[3] + ';margin-top:3px;">' + t[1] + '<span style="font-size:10px;color:' + t[3] + ';margin-left:2px;">' + t[2] + '</span></div></div>'; });
      html += '</div>';

      // Phase breakdown
      html += '<div style="margin:0 14px 10px;">';
      r.phases.forEach(function (p) {
        const pct = Math.round(p.dur / r.total * 100);
        const srv = phaseServer(p, r);
        html += '<div style="padding:5px 0;border-top:1px solid #161b22;display:flex;align-items:center;gap:6px;">';
        html += '<span style="width:8px;height:8px;border-radius:2px;background:' + p.color + ';"></span>';
        html += '<span style="font-size:11px;color:#ffffff;">' + esc(p.label) + '</span>';
        html += '<span style="font-size:8px;font-weight:600;color:#ffffff;border:1px solid #30363d;border-radius:3px;padding:1px 4px;font-family:monospace;">' + srv + '</span>';
        html += '<span style="margin-left:auto;font-family:monospace;font-size:11px;font-weight:600;color:#ffffff;">' + ms(p.dur) + ' s</span>';
        html += '<span style="font-family:monospace;font-size:10px;color:#ffffff;width:30px;text-align:right;">' + pct + '%</span>';
        html += '</div>';
      });
      html += '<div style="font-size:10px;color:#ffffff;margin-top:6px;padding-top:6px;border-top:1px solid #161b22;">Dominated by <span style="color:#ffffff;font-weight:600;">' + esc(dom.label) + '</span> (' + Math.round(dom.dur / r.total * 100) + '% of latency)</div>';
      html += '</div>';

      // Servers info (with model alias when present)
      const isAtomic = r.prefillNode === '-';
      const pNode = isAtomic ? r.decodeNode : r.prefillNode;
      const pModel = isAtomic ? r.decodeModel : r.prefillModel;
      html += '<div style="margin:10px 14px;padding:8px 10px;background:#161b22;border-radius:6px;">';
      html += '<div style="font-size:9px;color:#ffffff;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:4px;">Servers</div>';
      html += '<div style="display:flex;gap:8px;font-size:11px;align-items:center;"><span style="color:#ffffff;font-weight:600;min-width:48px;">Prefill</span><span style="color:#ffffff;">:</span> <span style="color:#ffffff;">' + esc(pNode) + '</span>';
      if (pModel) html += '<span style="margin-left:6px;font-family:monospace;font-size:9px;color:#a371f7;background:#a371f71f;border:1px solid #a371f74d;border-radius:3px;padding:1px 5px;">' + esc(pModel) + '</span>';
      if (isAtomic) html += '<span style="margin-left:6px;font-size:8px;font-weight:700;color:#d29922;background:#d299221f;border:1px solid #d299224d;border-radius:3px;padding:1px 5px;font-family:monospace;">ATOMIC</span>';
      html += '</div>';
      html += '<div style="display:flex;gap:8px;font-size:11px;margin-top:2px;align-items:center;"><span style="color:#ffffff;font-weight:600;min-width:48px;">Decode</span><span style="color:#ffffff;">:</span> <span style="color:#ffffff;">' + esc(r.decodeNode) + '</span>';
      if (r.decodeModel) html += '<span style="margin-left:6px;font-family:monospace;font-size:9px;color:#a371f7;background:#a371f71f;border:1px solid #a371f74d;border-radius:3px;padding:1px 5px;">' + esc(r.decodeModel) + '</span>';
      html += '</div>';
      html += '</div>';

      html += '</div>';
    }
    }

    html += '</div>';
    root.innerHTML = html;
    // After innerHTML, the panel has its natural size. Use the panel's
    // content-wrapper height (the real allocated space from gridPos.h) to
    // set our root height explicitly. This keeps the flex layout stable
    // across auto-refresh — no reflow jumps when data changes.
    const panelEl = (el && el.parentElement) ? el.parentElement.parentElement : null;
    if (panelEl && root.style) {
      // The Business Text panel wraps the content. Walk up to the panel root
      // (the element with `data-panelid` or class `panel-container`).
      let p = panelEl;
      while (p && p !== document.body) {
        const r = p.getBoundingClientRect ? p.getBoundingClientRect() : null;
        if (r && r.height > 100) {
          // Subtract a buffer to prevent sub-pixel rounding overflow
          // that would trigger an unwanted scrollbar.
          const h = Math.floor(r.height) - 4;
          root.style.height = h + 'px';
          root.dataset.measuredH = String(h);
          break;
        }
        p = p.parentElement;
      }
    }
    root.querySelectorAll('[data-row]').forEach(function (b) { b.addEventListener('click', function () { S.sel = parseInt(b.getAttribute('data-row'), 10); S.detail = true; render(); }); });
    root.querySelectorAll('[data-view]').forEach(function (b) { b.addEventListener('click', function () { S.view = b.getAttribute('data-view'); render(); }); });
    root.querySelectorAll('[data-close-detail]').forEach(function (b) { b.addEventListener('click', function () { S.detail = false; render(); }); });
  }
  render();
} catch (e) {
  try {
    let el = context.element; if (el && el.jquery) el = el.get(0);
    el.innerHTML = '<pre style="color:#f85149;padding:12px;white-space:pre-wrap;font:11px monospace;">' + String((e && e.stack) || e) + '</pre>';
  } catch (_) { }
}

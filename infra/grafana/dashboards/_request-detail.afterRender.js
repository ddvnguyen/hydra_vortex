// afterRender for the Business Text (marcusolsson-dynamictext) panel.
// Renders the Composition / Aligned request-timeline + click-row detail panel
// against the Loki request_timeline frame. Source of truth lives here; the
// dashboard JSON embeds a copy (build with scripts/build-request-detail.py).
try {
  let el = context.element;
  if (el && el.jquery) el = el.get(0);
  if (!el) return;
  const root = (el.querySelector && el.querySelector('#hydra-tl-root')) || el;

  // The Business Text panel exposes context.data as an array. Each element is
  // either a per-row object (keys = field names, scalar values) or a columnar
  // frame (keys = field names, array values). Normalize both into flat rows.
  const num = function (x) { const k = Number(x); return isFinite(k) ? k : 0; };
  const arr = Array.isArray(context.data) ? context.data
    : ((context.data && context.data.series) || (context.dataFrame ? [context.dataFrame] : []));
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
  const dbg = function (extra) {
    root.innerHTML = '<pre style="color:#7d8590;padding:14px;white-space:pre-wrap;font:11px monospace;">DEBUG ' + (extra || '') +
      '\\ncontext.data len: ' + (Array.isArray(context.data) ? context.data.length : 'n/a') +
      '\\nflat rows: ' + flat.length +
      '\\nrow0 keys: ' + (flat[0] ? JSON.stringify(Object.keys(flat[0])) : '-') + '</pre>';
  };

  const PHASE = {
    queue:      { key: 'queue_wait_ms', label: 'Queue',         color: '#6e7681', rtx: false },
    prefill:    { key: 'prefill_ms',    label: 'Prefill',       color: '#388bfd', rtx: true  },
    save_kv:    { key: 'save_kv_ms',    label: 'Save cache',    color: '#d29922', rtx: false },
    restore_kv: { key: 'restore_kv_ms', label: 'Restore cache', color: '#a371f7', rtx: false },
    decode:     { key: 'decode_ms',     label: 'Decode',        color: '#3fb950', rtx: true  },
  };
  const ORDER = ['queue', 'prefill', 'save_kv', 'restore_kv', 'decode'];
  const typeOf = function (rt) {
    rt = (rt || '').toLowerCase();
    if (rt.indexOf('migration') >= 0) return { t: 'RESUME', c: '#a371f7', d: 'Full cache resume' };
    if (rt.indexOf('affinity') >= 0 || rt.indexOf('warm') >= 0) return { t: 'WARM', c: '#2f81f7', d: 'Prefix cache hit' };
    return { t: 'COLD', c: '#db6d28', d: 'Fresh prompt' };
  };
  const esc = function (s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); };

  const rows = [];
  flat.forEach(function (d, i) {
    const phases = [];
    let cum = 0;
    ORDER.forEach(function (k) {
      const meta = PHASE[k];
      const dur = num(d[meta.key]);
      if (dur <= 0) return;
      phases.push({ k: k, label: meta.label, color: meta.color, rtx: meta.rtx, dur: dur, start: cum });
      cum += dur;
    });
    const total = num(d.total_ms) || cum || 1;
    rows.push({
      id: String(d.trace_id || ('req-' + i)).slice(0, 8),
      route: String(d.route_type || ''),
      decodeNode: String(d.decode_node || '-'),
      tokensIn: num(d.tokens_in), tokensOut: num(d.tokens_out),
      kvBytes: num(d.kv_bytes), phases: phases, total: total,
    });
  });

  const S = (window.__hydraTL = window.__hydraTL || { view: 'composition', sel: 0 });
  const domainMax = Math.max.apply(null, [1].concat(rows.map(function (r) { return r.total; })));

  function render() {
    if (!rows.length) { dbg('rows=0'); return; }
    if (S.sel >= rows.length) S.sel = 0;
    const isComp = S.view === 'composition';
    let html = '<div style="font-family:-apple-system,system-ui,sans-serif;color:#e6edf3;background:#0d1117;">';

    html += '<div style="display:flex;align-items:center;gap:14px;padding:10px 14px;border-bottom:1px solid #21262d;">';
    html += '<span style="font-size:13px;font-weight:600;">Request Timeline</span>';
    html += '<span style="font-size:11px;color:#7d8590;">' + rows.length + ' requests</span>';
    html += '<div style="margin-left:auto;display:flex;gap:3px;background:#161b22;border:1px solid #30363d;border-radius:8px;padding:3px;">';
    [['composition', 'Composition'], ['aligned', 'Aligned']].forEach(function (vv) {
      const a = S.view === vv[0];
      html += '<button data-view="' + vv[0] + '" style="border:none;cursor:pointer;font-size:12px;font-weight:600;padding:4px 12px;border-radius:6px;background:' + (a ? '#30363d' : 'transparent') + ';color:' + (a ? '#e6edf3' : '#7d8590') + ';">' + vv[1] + '</button>';
    });
    html += '</div>';
    html += '<span style="font-size:10px;color:#484f58;font-family:monospace;">' + (isComp ? '% of latency' : 'ms · since arrival (max ' + Math.round(domainMax) + ')') + '</span>';
    html += '</div>';

    html += '<div>';
    rows.forEach(function (r, ri) {
      const tc = typeOf(r.route);
      const sel = ri === S.sel;
      html += '<div data-row="' + ri + '" style="display:flex;align-items:center;min-height:46px;cursor:pointer;border-bottom:1px solid #161b22;border-left:2px solid ' + (sel ? '#388bfd' : 'transparent') + ';background:' + (sel ? 'rgba(56,139,253,0.07)' : 'transparent') + ';">';
      html += '<div style="width:184px;flex:none;padding:0 12px;min-width:0;">';
      html += '<div style="display:flex;align-items:center;gap:6px;"><span style="font-family:monospace;font-size:11.5px;font-weight:600;">' + esc(r.id) + '</span><span style="font-size:9px;font-weight:700;color:' + tc.c + ';background:' + tc.c + '1f;border:1px solid ' + tc.c + '4d;border-radius:4px;padding:1px 5px;font-family:monospace;">' + tc.t + '</span></div>';
      html += '<div style="font-size:10px;color:#6e7681;font-family:monospace;">' + esc(r.decodeNode) + ' · ' + r.total + ' ms</div></div>';
      html += '<div style="flex:1;position:relative;height:46px;min-width:0;">';
      r.phases.forEach(function (p) {
        const leftPct = isComp ? (p.start / r.total * 100) : (p.start / domainMax * 100);
        const wPct = isComp ? (p.dur / r.total * 100) : (p.dur / domainMax * 100);
        const w = Math.max(wPct, 0.4);
        const showLbl = wPct > 7;
        const txt = wPct > 14 ? (esc(p.label) + ' ' + p.dur + 'ms') : esc(p.label);
        html += '<div title="' + esc(p.label) + ' ' + p.dur + 'ms" style="position:absolute;left:' + leftPct + '%;width:' + w + '%;top:12px;height:22px;background:' + p.color + ';border-radius:3px;display:flex;align-items:center;padding:0 5px;overflow:hidden;box-shadow:' + (p.rtx ? 'inset 0 2px 0 rgba(255,255,255,0.4)' : 'inset 0 0 0 1px rgba(0,0,0,0.15)') + ';">';
        if (showLbl) html += '<span style="font-size:10px;font-weight:600;color:' + (p.k === 'save_kv' ? '#3d2c00' : 'rgba(255,255,255,0.92)') + ';white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">' + txt + '</span>';
        html += '</div>';
      });
      html += '</div></div>';
    });
    html += '</div>';

    const r = rows[S.sel];
    if (r) {
      const tc = typeOf(r.route);
      const ttft = r.phases.filter(function (p) { return p.k !== 'decode'; }).reduce(function (a, p) { return a + p.dur; }, 0);
      const dec = r.phases.find(function (p) { return p.k === 'decode'; });
      const tps = (dec && r.tokensOut > 0) ? Math.round(r.tokensOut / (dec.dur / 1000)) : null;
      const kvMiB = r.kvBytes > 0 ? (r.kvBytes / 1048576).toFixed(1) : '—';
      const dom = r.phases.slice().sort(function (a, b) { return b.dur - a.dur; })[0] || { label: '—', color: '#6e7681', dur: 0 };
      html += '<div style="border-top:1px solid #21262d;padding:14px;background:#0b0f14;">';
      html += '<div style="display:flex;align-items:center;gap:9px;margin-bottom:10px;"><span style="font-family:monospace;font-size:15px;font-weight:700;">' + esc(r.id) + '</span><span style="font-size:10px;font-weight:700;color:' + tc.c + ';background:' + tc.c + '1f;border:1px solid ' + tc.c + '4d;border-radius:5px;padding:2px 7px;font-family:monospace;">' + tc.t + '</span><span style="font-size:11px;color:#7d8590;">' + tc.d + '</span></div>';
      const tiles = [
        ['Total latency', r.total, 'ms', '#e6edf3'],
        ['TTFT', ttft, 'ms', '#58a6ff'],
        ['Throughput', tps == null ? '—' : tps, tps == null ? '' : 'tok/s', '#3fb950'],
        ['Tokens in / out', r.tokensIn + ' / ' + r.tokensOut, '', '#c9d1d9'],
        ['KV cache', kvMiB, kvMiB === '—' ? '' : 'MiB', '#d29922'],
      ];
      html += '<div style="display:flex;gap:1px;background:#21262d;border-radius:8px;overflow:hidden;flex-wrap:wrap;">';
      tiles.forEach(function (t) { html += '<div style="flex:1;min-width:108px;background:#0d1117;padding:10px 12px;"><div style="font-size:10px;color:#7d8590;text-transform:uppercase;letter-spacing:0.06em;">' + t[0] + '</div><div style="font-family:monospace;font-size:16px;font-weight:600;color:' + t[3] + ';margin-top:4px;">' + t[1] + '<span style="font-size:11px;color:#6e7681;margin-left:3px;">' + t[2] + '</span></div></div>'; });
      html += '</div>';
      html += '<div style="margin-top:12px;">';
      r.phases.forEach(function (p) {
        const pct = Math.round(p.dur / r.total * 100);
        html += '<div style="padding:6px 0;border-top:1px solid #161b22;display:flex;align-items:center;gap:8px;"><span style="width:9px;height:9px;border-radius:3px;background:' + p.color + ';"></span><span style="font-size:12px;color:#c9d1d9;">' + esc(p.label) + '</span>' + (p.rtx ? '<span style="font-size:8px;font-weight:700;color:#8b949e;border:1px solid #30363d;border-radius:3px;padding:1px 3px;font-family:monospace;">RTX</span>' : '') + '<span style="margin-left:auto;font-family:monospace;font-size:12px;font-weight:600;">' + p.dur + ' ms</span><span style="font-family:monospace;font-size:11px;color:#6e7681;width:38px;text-align:right;">' + pct + '%</span></div>';
      });
      html += '<div style="font-size:11px;color:#7d8590;margin-top:8px;">Dominated by <span style="color:' + dom.color + ';font-weight:600;">' + esc(dom.label) + '</span> (' + Math.round(dom.dur / r.total * 100) + '% of latency)</div>';
      html += '</div></div>';
    }

    html += '</div>';
    root.innerHTML = html;
    root.querySelectorAll('[data-view]').forEach(function (b) { b.addEventListener('click', function () { S.view = b.getAttribute('data-view'); render(); }); });
    root.querySelectorAll('[data-row]').forEach(function (b) { b.addEventListener('click', function () { S.sel = parseInt(b.getAttribute('data-row'), 10); render(); }); });
  }
  render();
} catch (e) {
  try {
    let el = context.element; if (el && el.jquery) el = el.get(0);
    el.innerHTML = '<pre style="color:#f85149;padding:12px;white-space:pre-wrap;font:11px monospace;">' + String((e && e.stack) || e) + '</pre>';
  } catch (_) { }
}

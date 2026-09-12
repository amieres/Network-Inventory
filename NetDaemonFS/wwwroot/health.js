// ── Health dashboard ──────────────────────────────────────────────────────────
// Two views over the same data: a device table (faults first) and a dependency
// diagram. The diagram is the point of the topology — a failed parent visibly
// explains its children instead of showing six unrelated red rows.
//
// Loaded as a separate file from app.js so the inventory view keeps working
// even if this one throws.

const HEALTH_JS_VERSION = 8;

let healthData   = null;   // /api/health/devices
let topoData     = null;   // /api/health/topology
let healthFilter = '';     // '' | ok | stale | unavailable | retired
let showHelpers  = false;  // helpers are monitored but are not things you can fix

// Diagram view state. Node positions are user-draggable and persisted, because
// an auto-layout can only guess at a physical arrangement the user knows.
let diagramZoom = 1;
let diagramPan  = { x: 0, y: 0 };
let nodePos     = JSON.parse(localStorage.getItem('healthNodePos') || '{}');
function saveNodePos() { localStorage.setItem('healthNodePos', JSON.stringify(nodePos)); }

const ESC_MAP = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return ESC_MAP[c]; });
}

function fmtSecs(s) {
  if (s == null) return '';
  if (s < 60)    return Math.round(s) + 's';
  if (s < 3600)  return Math.round(s / 60) + 'm';
  if (s < 86400) return (s / 3600).toFixed(1) + 'h';
  return (s / 86400).toFixed(1) + 'd';
}

function switchView(view) {
  document.querySelectorAll('.view-tab').forEach(function (b) {
    b.classList.toggle('active', b.dataset.view === view);
  });
  const inv = view === 'inventory';
  document.getElementById('main').hidden      = !inv;
  document.getElementById('stats-bar').hidden = !inv;
  document.getElementById('cat-bar').hidden   = !inv;
  document.getElementById('health').hidden    = inv;
  if (!inv) loadHealth();
}

async function loadHealth() {
  try {
    const results = await Promise.all([
      api('GET', '/api/health/devices'),
      api('GET', '/api/health/topology')
    ]);
    healthData = results[0];
    topoData   = results[1];
    renderHealth();
  } catch (e) {
    const b = document.getElementById('h-banner');
    b.className = 'h-banner bad';
    b.textContent = 'Health API unavailable: ' + e.message;
  }
}

function renderHealth() {
  const d = healthData;
  if (!d) return;

  // ── Banner: lead with root causes, not a bare fault count ──
  const banner = document.getElementById('h-banner');
  const roots  = (topoData && topoData.rootCauses) || [];
  const faults = d.stale + d.unavailable;

  if (!d.ready) {
    banner.className = 'h-banner';
    banner.textContent = 'Learning reporting patterns from history…';
  } else if (faults === 0) {
    banner.className = 'h-banner good';
    banner.innerHTML = 'All <b>' + d.total + '</b> devices reporting normally.';
  } else {
    banner.className = 'h-banner bad';
    let html = '<b>' + faults + '</b> device' + (faults === 1 ? '' : 's') + ' not reporting.';
    roots.forEach(function (r) {
      html += '<div class="h-root">Root cause: <code>' + esc(r.label) + '</code>' +
              (r.affected.length
                 ? ' — explains ' + r.affected.length + ': ' + esc(r.affected.join(', '))
                 : '') +
              (r.remedy ? '<div class="h-remedy">→ ' + esc(r.remedy) + '</div>' : '') +
              '</div>';
    });
    banner.innerHTML = html;
  }

  // ── Blind spots: failures that would take HA down, so nothing gets reported ──
  const bs = (topoData && topoData.blindSpots) || [];
  const bsEl = document.getElementById('h-blind');
  if (bs.length) {
    bsEl.hidden = false;
    bsEl.innerHTML = '<b>Blind spots</b> — these would take Home Assistant down too, ' +
      'so this dashboard would simply stop rather than warn you:' +
      bs.map(function (n) {
        return '<div class="h-root">' + esc(n.label) +
               (n.remedy ? '<div class="h-remedy">→ ' + esc(n.remedy) + '</div>' : '') +
               '</div>';
      }).join('');
  } else {
    bsEl.hidden = true;
  }

  // ── Stat chips (click to filter) ──
  const chips = [
    ['',            'All',         d.total,       ''],
    ['ok',          'OK',          d.ok,          'ok'],
    ['stale',       'Stale',       d.stale,       'stale'],
    ['unavailable', 'Unavailable', d.unavailable, 'unav'],
    ['retired',     'Retired',     d.retired,     'retired']
  ];
  document.getElementById('h-stats').innerHTML = chips.map(function (c) {
    return '<span class="h-stat ' + c[3] + ' ' + (healthFilter === c[0] ? 'active' : '') +
           '" data-hfilter="' + c[0] + '">' + c[1] + '<b>' + c[2] + '</b></span>';
  }).join('');
  document.getElementById('h-stats').innerHTML +=
    '<span class="h-stat ' + (showHelpers ? 'active' : '') + '" id="h-helpers" ' +
    'title="Alerts, template sensors and integration rows - monitored, but not physical devices">' +
    'Helpers<b>' + (d.helpers || 0) + '</b></span>';
  document.querySelectorAll('[data-hfilter]').forEach(function (el) {
    el.addEventListener('click', function () {
      healthFilter = el.dataset.hfilter;
      renderHealth();
    });
  });
  const helpEl = document.getElementById('h-helpers');
  if (helpEl) helpEl.addEventListener('click', function () {
    showHelpers = !showHelpers;
    renderHealth();
  });

  // ── Device table ──
  // Helpers (alerts, templates, integration rows) are monitored but are not
  // things you can go fix, so they are hidden unless explicitly asked for.
  const rows = d.devices.filter(function (x) {
    if (!showHelpers && x.rowKind === 'helper') return false;
    return !healthFilter || x.state === healthFilter;
  });
  document.getElementById('h-tbody').innerHTML = rows.map(function (x) {
    const idSub = x.deviceId.indexOf('.') >= 0
      ? '<div class="h-sub">' + esc(x.deviceId) + '</div>' : '';
    const age = x.staleSecs ? ' <span class="h-sub">(' + fmtSecs(x.staleSecs) + ')</span>' : '';
    return '<tr>' +
      '<td>' + esc(x.name) + idSub + '</td>' +
      '<td><span class="pill ' + x.state + '">' + x.state + '</span></td>' +
      '<td class="h-detail">' + esc(x.area) + '</td>' +
      '<td class="h-detail">' + esc(x.detail) + age + '</td>' +
      '<td class="h-detail">' + x.liveCount + '/' + x.entityCount + '</td>' +
      '</tr>';
  }).join('');

  renderDiagram();
}

// ── Dependency diagram ────────────────────────────────────────────────────────
// Layered layout: depth = longest path from a root (a node with no parents), so
// power sources and routers sit on the left and dependants flow rightwards.

function renderDiagram() {
  const host = document.getElementById('diagram');
  if (!topoData) { host.innerHTML = ''; return; }

  const nodes = topoData.nodes;
  const edges = topoData.edges;

  function parentsOf(k) {
    return edges.filter(function (e) { return e.child === k; })
                .map(function (e) { return e.parent; });
  }

  const memo = new Map();
  function depth(k, seen) {
    seen = seen || new Set();
    if (memo.has(k)) return memo.get(k);
    if (seen.has(k)) return 0;                 // cycle guard
    seen.add(k);
    const ps = parentsOf(k);
    const v = ps.length
      ? Math.max.apply(null, ps.map(function (p) { return depth(p, seen); })) + 1
      : 0;
    memo.set(k, v);
    return v;
  }

  const byDepth = new Map();
  nodes.forEach(function (n) {
    const dp = depth(n.key);
    if (!byDepth.has(dp)) byDepth.set(dp, []);
    byDepth.get(dp).push(n);
  });

  const COL_W = 190, ROW_H = 46, BOX_W = 150, BOX_H = 32, PAD = 18;

  // Auto-layout is only the starting point; a saved drag always wins, because
  // the physical arrangement is something only the user knows.
  const pos = new Map();
  byDepth.forEach(function (list, dp) {
    list.forEach(function (n, i) {
      const saved = nodePos[n.key];
      pos.set(n.key, saved ? { x: saved.x, y: saved.y }
                           : { x: PAD + dp * COL_W, y: PAD + i * ROW_H });
    });
  });

  const xs = Array.from(pos.values()).map(function (p) { return p.x; });
  const ys = Array.from(pos.values()).map(function (p) { return p.y; });
  const W = Math.max.apply(null, xs) + BOX_W + PAD * 2;
  const H = Math.max.apply(null, ys) + BOX_H + PAD * 2 + 26;

  function edgePath(e) {
    const a = pos.get(e.parent), b = pos.get(e.child);
    if (!a || !b) return '';
    const x1 = a.x + BOX_W, y1 = a.y + BOX_H / 2;
    const x2 = b.x,         y2 = b.y + BOX_H / 2;
    const mx = (x1 + x2) / 2;
    return '<path class="e-' + e.kind + '" data-child="' + e.child +
           '" data-parent="' + e.parent + '" d="M' + x1 + ',' + y1 +
           ' C' + mx + ',' + y1 + ' ' + mx + ',' + y2 + ' ' + x2 + ',' + y2 + '">' +
           '<title>' + esc(e.kind) + (e.note ? ': ' + esc(e.note) : '') + '</title></path>';
  }

  function nodeGroup(n) {
    const p = pos.get(n.key);
    const cls = n.verdict === 'root-cause' ? 'n-fault'
              : n.verdict === 'suppressed' ? 'n-sup'
              : 'n-ok';
    let tip;
    if (n.verdict === 'suppressed')      tip = 'suppressed - because ' + n.because;
    else if (n.verdict === 'root-cause') tip = 'ROOT CAUSE' + (n.affected.length ? ' - affects ' + n.affected.join(', ') : '');
    else                                 tip = 'healthy';
    if (n.remedy) tip += ' | ' + n.remedy;
    const label = n.label.length > 24 ? n.label.slice(0, 23) + '…' : n.label;
    const blind = n.blindSpot
      ? '<text class="n-blind" x="' + (p.x + BOX_W - 10) + '" y="' + (p.y + 14) + '">◍</text>'
      : '';
    return '<g class="n-g" data-key="' + n.key + '">' +
      '<title>' + esc(n.label) + ' - ' + esc(tip) + '</title>' +
      '<rect class="n-box ' + cls + '" x="' + p.x + '" y="' + p.y +
        '" width="' + BOX_W + '" height="' + BOX_H + '"/>' +
      '<text class="n-label" x="' + (p.x + 8) + '" y="' + (p.y + 14) + '">' + esc(label) + '</text>' +
      '<text class="n-kind"  x="' + (p.x + 8) + '" y="' + (p.y + 26) + '">' + esc(n.kind) + '</text>' +
      blind + '</g>';
  }

  const legendItems = [['e-power', 'power'], ['e-network', 'network'],
                       ['e-bt-host', 'BLE'], ['e-host', 'host']];
  const legend = legendItems.map(function (L, i) {
    const x = PAD + i * 110;
    return '<line class="' + L[0] + '" x1="' + x + '" y1="' + (H - 12) +
           '" x2="' + (x + 22) + '" y2="' + (H - 12) + '"/>' +
           '<text class="legend" x="' + (x + 28) + '" y="' + (H - 8) + '">' + L[1] + '</text>';
  }).join('');

  const vb = [diagramPan.x, diagramPan.y, W / diagramZoom, H / diagramZoom].join(' ');
  host.innerHTML =
    '<div class="diag-toolbar">' +
      '<button data-zoom="in"       title="Zoom in">+</button>' +
      '<button data-zoom="out"      title="Zoom out">&minus;</button>' +
      '<button data-zoom="reset"    title="Reset zoom and pan">reset view</button>' +
      '<button data-zoom="relayout" title="Discard dragged positions and auto-layout again">re-layout</button>' +
      '<span class="diag-hint">drag nodes to arrange &middot; scroll to zoom &middot; drag background to pan</span>' +
    '</div>' +
    '<svg id="diag-svg" viewBox="' + vb + '" preserveAspectRatio="xMinYMin meet" xmlns="http://www.w3.org/2000/svg">' +
      '<g id="diag-edges">' + edges.map(edgePath).join('') + '</g>' +
      '<g id="diag-nodes">' + nodes.map(nodeGroup).join('') + '</g>' +
      legend +
    '</svg>';

  wireDiagram(pos, BOX_W, BOX_H, W, H);
}

// ── Diagram interaction: drag nodes, zoom, pan ────────────────────────────────
function wireDiagram(pos, BOX_W, BOX_H, W, H) {
  const svg = document.getElementById('diag-svg');
  if (!svg) return;

  document.querySelectorAll('[data-zoom]').forEach(function (b) {
    b.addEventListener('click', function () {
      const a = b.dataset.zoom;
      if (a === 'in')            diagramZoom = Math.min(4, diagramZoom * 1.25);
      else if (a === 'out')      diagramZoom = Math.max(0.25, diagramZoom / 1.25);
      else if (a === 'reset')    { diagramZoom = 1; diagramPan = { x: 0, y: 0 }; }
      else if (a === 'relayout') { nodePos = {}; saveNodePos(); diagramZoom = 1; diagramPan = { x: 0, y: 0 }; }
      renderDiagram();
    });
  });

  // Convert a mouse event to SVG user units so dragging tracks the cursor at
  // any zoom level.
  function toSvg(evt) {
    const r = svg.getBoundingClientRect();
    const vbw = W / diagramZoom, vbh = H / diagramZoom;
    return {
      x: diagramPan.x + (evt.clientX - r.left) / r.width  * vbw,
      y: diagramPan.y + (evt.clientY - r.top)  / r.height * vbh
    };
  }

  function applyViewBox() {
    svg.setAttribute('viewBox',
      [diagramPan.x, diagramPan.y, W / diagramZoom, H / diagramZoom].join(' '));
  }

  // Live geometry update while dragging, without a full re-render.
  function redrawPositions() {
    Object.keys(nodePos).forEach(function (k) { pos.set(k, nodePos[k]); });
    document.querySelectorAll('.n-g').forEach(function (g) {
      const p = pos.get(g.dataset.key);
      if (!p) return;
      const rect = g.querySelector('rect');
      const txts = g.querySelectorAll('text');
      rect.setAttribute('x', p.x);
      rect.setAttribute('y', p.y);
      if (txts[0]) { txts[0].setAttribute('x', p.x + 8);         txts[0].setAttribute('y', p.y + 14); }
      if (txts[1]) { txts[1].setAttribute('x', p.x + 8);         txts[1].setAttribute('y', p.y + 26); }
      if (txts[2]) { txts[2].setAttribute('x', p.x + BOX_W - 10); txts[2].setAttribute('y', p.y + 14); }
    });
    document.querySelectorAll('#diag-edges path').forEach(function (path) {
      const a = pos.get(path.dataset.parent), b = pos.get(path.dataset.child);
      if (!a || !b) return;
      const x1 = a.x + BOX_W, y1 = a.y + BOX_H / 2;
      const x2 = b.x,         y2 = b.y + BOX_H / 2;
      const mx = (x1 + x2) / 2;
      path.setAttribute('d', 'M' + x1 + ',' + y1 + ' C' + mx + ',' + y1 +
                             ' ' + mx + ',' + y2 + ' ' + x2 + ',' + y2);
    });
  }

  let drag = null;

  svg.addEventListener('mousedown', function (evt) {
    const g = evt.target.closest ? evt.target.closest('.n-g') : null;
    const p = toSvg(evt);
    if (g) {
      const key = g.dataset.key;
      const cur = pos.get(key);
      drag = { kind: 'node', key: key, dx: p.x - cur.x, dy: p.y - cur.y };
      g.classList.add('dragging');
    } else {
      drag = { kind: 'pan', x0: p.x, y0: p.y, px: diagramPan.x, py: diagramPan.y };
    }
    evt.preventDefault();
  });

  window.addEventListener('mousemove', function (evt) {
    if (!drag) return;
    const p = toSvg(evt);
    if (drag.kind === 'node') {
      nodePos[drag.key] = { x: Math.round(p.x - drag.dx), y: Math.round(p.y - drag.dy) };
      redrawPositions();
    } else {
      diagramPan.x = drag.px - (p.x - drag.x0);
      diagramPan.y = drag.py - (p.y - drag.y0);
      applyViewBox();
    }
  });

  window.addEventListener('mouseup', function () {
    if (drag && drag.kind === 'node') saveNodePos();
    document.querySelectorAll('.dragging').forEach(function (e) { e.classList.remove('dragging'); });
    drag = null;
  });

  svg.addEventListener('wheel', function (evt) {
    evt.preventDefault();
    const before = toSvg(evt);
    diagramZoom = evt.deltaY < 0 ? Math.min(4, diagramZoom * 1.1)
                                 : Math.max(0.25, diagramZoom / 1.1);
    const after = toSvg(evt);
    diagramPan.x += before.x - after.x;      // keep the cursor anchored
    diagramPan.y += before.y - after.y;
    applyViewBox();
  }, { passive: false });
}

// ── Init ──────────────────────────────────────────────────────────────────────

document.querySelectorAll('.view-tab').forEach(function (b) {
  b.addEventListener('click', function () { switchView(b.dataset.view); });
});

setInterval(function () {
  if (!document.getElementById('health').hidden) loadHealth();
}, 60000);

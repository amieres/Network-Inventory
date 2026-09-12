// ── Health dashboard ──────────────────────────────────────────────────────────
// Two views over the same data: a device table (faults first) and a dependency
// diagram. The diagram is the point of the topology — a failed parent visibly
// explains its children instead of showing six unrelated red rows.
//
// Loaded as a separate file from app.js so the inventory view keeps working
// even if this one throws.

const HEALTH_JS_VERSION = 4;

let healthData   = null;   // /api/health/devices
let topoData     = null;   // /api/health/topology
let healthFilter = '';     // '' | ok | stale | unavailable | retired

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
              '</div>';
    });
    banner.innerHTML = html;
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
  document.querySelectorAll('[data-hfilter]').forEach(function (el) {
    el.addEventListener('click', function () {
      healthFilter = el.dataset.hfilter;
      renderHealth();
    });
  });

  // ── Device table ──
  const rows = d.devices.filter(function (x) {
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

  const COL_W = 190, ROW_H = 46, BOX_W = 150, BOX_H = 30, PAD = 20;
  const depths  = Array.from(byDepth.keys());
  const maxRows = Math.max.apply(null, Array.from(byDepth.values()).map(function (a) { return a.length; }));
  const W = (Math.max.apply(null, depths) + 1) * COL_W + PAD * 2;
  const H = maxRows * ROW_H + PAD * 2 + 26;

  const pos = new Map();
  byDepth.forEach(function (list, dp) {
    list.forEach(function (n, i) {
      pos.set(n.key, { x: PAD + dp * COL_W, y: PAD + i * ROW_H });
    });
  });

  const edgeSvg = edges.map(function (e) {
    const a = pos.get(e.parent), b = pos.get(e.child);
    if (!a || !b) return '';
    const x1 = a.x + BOX_W, y1 = a.y + BOX_H / 2;
    const x2 = b.x,         y2 = b.y + BOX_H / 2;
    const mx = (x1 + x2) / 2;
    return '<path class="e-' + e.kind + '" d="M' + x1 + ',' + y1 +
           ' C' + mx + ',' + y1 + ' ' + mx + ',' + y2 + ' ' + x2 + ',' + y2 + '">' +
           '<title>' + esc(e.kind) + (e.note ? ': ' + esc(e.note) : '') + '</title></path>';
  }).join('');

  const nodeSvg = nodes.map(function (n) {
    const p = pos.get(n.key);
    const cls = n.verdict === 'root-cause' ? 'n-fault'
              : n.verdict === 'suppressed' ? 'n-sup'
              : 'n-ok';
    let tip;
    if (n.verdict === 'suppressed') {
      tip = 'suppressed — because ' + n.because;
    } else if (n.verdict === 'root-cause') {
      tip = 'ROOT CAUSE' + (n.affected.length ? ' — affects ' + n.affected.join(', ') : '');
    } else {
      tip = 'healthy';
    }
    const label = n.label.length > 22 ? n.label.slice(0, 21) + '…' : n.label;
    return '<g><title>' + esc(n.label) + ' — ' + esc(tip) + '</title>' +
      '<rect class="n-box ' + cls + '" x="' + p.x + '" y="' + p.y +
        '" width="' + BOX_W + '" height="' + BOX_H + '"/>' +
      '<text class="n-label" x="' + (p.x + 8) + '" y="' + (p.y + 13) + '">' + esc(label) + '</text>' +
      '<text class="n-kind"  x="' + (p.x + 8) + '" y="' + (p.y + 24) + '">' + esc(n.kind) + '</text>' +
      '</g>';
  }).join('');

  const legendItems = [['e-power', 'power'], ['e-network', 'network'],
                       ['e-bt-host', 'BLE'], ['e-host', 'host']];
  const legend = legendItems.map(function (L, i) {
    const x = PAD + i * 110;
    return '<line class="' + L[0] + '" x1="' + x + '" y1="' + (H - 12) +
           '" x2="' + (x + 22) + '" y2="' + (H - 12) + '"/>' +
           '<text class="legend" x="' + (x + 28) + '" y="' + (H - 8) + '">' + L[1] + '</text>';
  }).join('');

  host.innerHTML = '<svg viewBox="0 0 ' + W + ' ' + H + '" xmlns="http://www.w3.org/2000/svg">' +
                   edgeSvg + nodeSvg + legend + '</svg>';
}

// ── Init ──────────────────────────────────────────────────────────────────────

document.querySelectorAll('.view-tab').forEach(function (b) {
  b.addEventListener('click', function () { switchView(b.dataset.view); });
});

setInterval(function () {
  if (!document.getElementById('health').hidden) loadHealth();
}, 60000);

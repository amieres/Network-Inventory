// ── Health dashboard ──────────────────────────────────────────────────────────
// Two views over the same data: a device table (faults first) and a dependency
// diagram. The diagram is the point of the topology — a failed parent visibly
// explains its children instead of showing six unrelated red rows.
//
// Loaded as a separate file from app.js so the inventory view keeps working
// even if this one throws.

const HEALTH_JS_VERSION = 16;

let healthData   = null;   // /api/health/devices
let topoData     = null;   // /api/health/topology
let healthFilter = '';     // '' | ok | stale | unavailable | retired
let showHelpers  = false;  // helpers are monitored but are not things you can fix

// Diagram view state. Node positions are user-draggable and persisted, because
// an auto-layout can only guess at a physical arrangement the user knows.
let diagramZoom = 1;
let diagramPan  = { x: 0, y: 0 };
let nodePos     = JSON.parse(localStorage.getItem('healthNodePos') || '{}');

// Hand-arranged positions are real work and easy to destroy with one careless
// clear, so every save also keeps a rolling backup and a timestamped snapshot.
// `healthRestore()` from the console brings the last layout back.
function saveNodePos() {
  const prev = localStorage.getItem('healthNodePos');
  if (prev && prev !== '{}') localStorage.setItem('healthNodePosPrev', prev);
  localStorage.setItem('healthNodePos', JSON.stringify(nodePos));
  localStorage.setItem('healthNodePosAt', new Date().toISOString());
  // Also persist server-side so a layout is not trapped in one browser.
  clearTimeout(saveNodePos._t);
  saveNodePos._t = setTimeout(function () {
    const ps = Object.keys(nodePos).map(function (k) {
      return { key: k, x: nodePos[k].x, y: nodePos[k].y };
    });
    if (ps.length) api('POST', '/api/health/positions', ps).catch(function () {});
  }, 800);
}

function healthRestore() {
  const prev = localStorage.getItem('healthNodePosPrev');
  if (!prev) { console.warn('no previous layout saved'); return false; }
  nodePos = JSON.parse(prev);
  localStorage.setItem('healthNodePos', prev);
  renderDiagram();
  return Object.keys(nodePos).length + ' node positions restored';
}

/// Copy the current layout to the clipboard / console so it can be kept
/// outside the browser.
function healthExportLayout() {
  const json = JSON.stringify(nodePos);
  console.log(json);
  if (navigator.clipboard) navigator.clipboard.writeText(json);
  return Object.keys(nodePos).length + ' positions exported (also copied to clipboard)';
}

function healthImportLayout(json) {
  nodePos = typeof json === 'string' ? JSON.parse(json) : json;
  saveNodePos();
  renderDiagram();
  return Object.keys(nodePos).length + ' positions imported';
}

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
    // Server-stored positions win on first load, so a layout follows the user
    // between browsers instead of living only in localStorage.
    if (topoData.positions && topoData.positions.length && !Object.keys(nodePos).length) {
      topoData.positions.forEach(function (p) { nodePos[p.key] = { x: p.x, y: p.y }; });
    }
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
    banner.innerHTML = 'All <b>' + d.total + '</b> devices reporting normally.' +
      // Helper faults are real but not actionable device problems, so they are
      // mentioned rather than counted as devices.
      (d.helperFaults
        ? '<div class="h-root">' + d.helperFaults + ' helper sensor' +
          (d.helperFaults === 1 ? '' : 's') + ' also stale — see the Helpers filter.</div>'
        : '');
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

// ── Diagram ───────────────────────────────────────────────────────────────────
// Draws the dependency graph. Deliberately direct-manipulation: an auto-layout
// cannot know the physical arrangement, so nodes are dragged, snapped to a grid
// and persisted, and connections can be added by hand.

const GRID = 10;                       // snap step for dragging
const snap = function (v) { return Math.round(v / GRID) * GRID; };

// Which edge kinds are visible. Electrical / wifi / bluetooth toggled separately
// because a power problem and a radio problem look nothing alike.
let layerOn = JSON.parse(localStorage.getItem('healthLayers') || '{"power":true,"network":true,"bt-host":true,"host":true}');
function saveLayers() { localStorage.setItem('healthLayers', JSON.stringify(layerOn)); }

// User-added connections, kept client-side so the graph can be extended without
// a redeploy. Merged with the server's edges at render time.
let userEdges = JSON.parse(localStorage.getItem('healthUserEdges') || '[]');
function saveUserEdges() { localStorage.setItem('healthUserEdges', JSON.stringify(userEdges)); }

let connectFrom = null;                // node key while adding a connection

// SSID -> colour. Wired and powerline keep the dashed neutral line.
const LINK_COLOR = {
  'AbeEero':       '#a78bfa',
  'ABEWNETG':      '#38bdf8',
  'ABEWNETG-5G':   '#22d3ee',
  'ABEWNETG-GAR':  '#fbbf24'
};

// Compact inline SVG glyphs, drawn at 14x14 from the box's top-left.
function iconFor(kind, x, y, dim) {
  const c = dim ? '#475569' : '#cbd5e1';
  const g = function (inner) {
    return '<g transform="translate(' + x + ',' + y + ')" fill="none" stroke="' + c +
           '" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round">' + inner + '</g>';
  };
  switch (kind) {
    case 'grid':     return g('<path d="M5 0 L2 6 h4 L3 12"/><path d="M9 1 v10 M11 3 v6"/>');
    case 'battery':  return g('<rect x="1" y="3" width="10" height="7" rx="1"/><path d="M12 5.5v2"/><path d="M3.5 5.5h3"/>');
    case 'plug':     return g('<path d="M4 1v3 M8 1v3"/><rect x="2" y="4" width="8" height="4" rx="1"/><path d="M6 8v3"/>');
    case 'camera':   return g('<rect x="1" y="3" width="8" height="7" rx="1"/><path d="M9 6l3-2v6l-3-2"/>');
    case 'ap':       return g('<path d="M1 5a7 7 0 0 1 10 0"/><path d="M3.5 7.5a3.5 3.5 0 0 1 5 0"/><circle cx="6" cy="10" r="1"/>');
    case 'modem':    return g('<rect x="1" y="6" width="10" height="5" rx="1"/><path d="M6 6V2"/><circle cx="3.5" cy="8.5" r=".6"/>');
    case 'pi':       return g('<rect x="2" y="2" width="8" height="8" rx="1"/><path d="M4 0v2 M8 0v2 M4 10v2 M8 10v2 M0 4h2 M0 8h2 M10 4h2 M10 8h2"/>');
    case 'computer': return g('<rect x="1" y="2" width="10" height="7" rx="1"/><path d="M4 11h4"/>');
    case 'esp32':    return g('<rect x="2" y="2" width="8" height="8" rx="1"/><path d="M4 0v2 M8 0v2 M0 5h2 M10 5h2"/>');
    case 'circuit':  return g('<path d="M2 6h3 l1.5-3 1.5 6 1-3h1.5"/>');
    case 'ev':       return g('<rect x="1" y="5" width="8" height="4" rx="1"/><path d="M2.5 5l1-2h4l1 2"/><path d="M11 4v4"/>');
    case 'opener':   return g('<rect x="1" y="4" width="10" height="7" rx="1"/><path d="M1 7h10"/>');
    case 'host':     return g('<rect x="1" y="2" width="10" height="3" rx="1"/><rect x="1" y="7" width="10" height="3" rx="1"/>');
    case 'sensor':   return g('<circle cx="6" cy="6" r="4"/><path d="M6 3v3l2 1"/>');
    case 'zone':     return g('<rect x="1" y="2" width="10" height="8" rx="1"/>');
    default:         return g('<circle cx="6" cy="6" r="4"/>');
  }
}

// One icon per PHYSICAL interface, read from the inventory - a device with both
// ethernet and wifi (the Mac Studio) gets two icons with different MACs and IPs,
// and a BLE-capable device gets a bluetooth icon alongside. Tooltips carry the
// MAC and IP so the diagram ties back to the inventory entry.
function ifaceGlyph(kind, c) {
  if (kind === 'wifi') {
    return '<path d="M0 4a6 6 0 0 1 8 0"/><path d="M2 6.2a3 3 0 0 1 4 0"/>' +
           '<circle cx="4" cy="8.4" r=".8" fill="' + c + '" stroke="none"/>';
  }
  if (kind === 'bluetooth') {
    return '<path d="M3 2.5l4 3.5-4 3.5V1l4 3.5-4 3.5"/>';
  }
  // wired: a socket with a cable running out of it
  return '<rect x="0" y="2" width="5" height="5" rx="1"/>' +
         '<path d="M5 4.5h2.5a2 2 0 0 1 2 2V9"/>' +
         '<circle cx="9.5" cy="9.6" r=".9" fill="' + c + '" stroke="none"/>';
}

function ifaceIcons(n, rightX, y, up) {
  const list = (n.ifaces && n.ifaces.length) ? n.ifaces : [];

  // No inventory match: fall back to the declared LAN kind so a node still shows
  // something meaningful. `none` means not a smart device - no icon at all.
  if (!list.length) {
    const lan = n.lan || 'none';
    if (lan === 'none') return '';
    const wifi = lan.indexOf('wifi:') === 0;
    const ssid = wifi ? lan.slice(5) : null;
    const c = !up ? '#475569' : (wifi ? (LINK_COLOR[ssid] || '#94a3b8') : '#94a3b8');
    const tip = wifi ? 'WiFi: ' + ssid
              : lan.indexOf('powerline:') === 0 ? 'Wired via powerline (' + lan.slice(10) + ')'
              : lan.indexOf('wired:') === 0 ? 'Wired from ' + lan.slice(6)
              : lan;
    return '<g class="lan-icon" transform="translate(' + (rightX - 12) + ',' + y + ')" fill="none" stroke="' + c +
           '" stroke-width="1.4" stroke-linecap="round"><title>' + esc(tip) + '</title>' +
           ifaceGlyph(wifi ? 'wifi' : 'wired', c) + '</g>';
  }

  const ssid = (n.lan || '').indexOf('wifi:') === 0 ? n.lan.slice(5) : null;
  return list.map(function (f, idx) {
    const c = !up ? '#475569'
            : f.kind === 'bluetooth' ? '#c084fc'
            : f.kind === 'wifi'      ? (LINK_COLOR[ssid] || '#38bdf8')
            : '#94a3b8';
    const label = f.kind === 'bluetooth' ? 'Bluetooth'
                : f.kind === 'wifi'      ? ('WiFi' + (ssid ? ': ' + ssid : ''))
                : 'Wired';
    const tip = label +
                (f.ip  ? ' — ' + f.ip  : '') +
                (f.mac ? ' — ' + f.mac : '') +
                (f.conn ? ' (' + f.conn + ')' : '');
    // Lay the icons out right-to-left so the box label is never overlapped.
    const x = rightX - 12 - idx * 13;
    return '<g class="lan-icon" transform="translate(' + x + ',' + y + ')" fill="none" stroke="' + c +
           '" stroke-width="1.4" stroke-linecap="round"><title>' + esc(tip) + '</title>' +
           ifaceGlyph(f.kind, c) + '</g>';
  }).join('');
}

function renderDiagram() {
  const host = document.getElementById('diagram');
  if (!topoData) { host.innerHTML = ''; return; }

  const nodes = topoData.nodes;
  const edges = topoData.edges.concat(userEdges);
  const areas = topoData.areas || [];

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

  const COL_W = 210, ROW_H = 54, PAD = 24;
  // Box size scales with node.size (1 small / 2 normal / 3 large).
  const boxW = function (n) { return n.size === 1 ? 120 : n.size === 3 ? 190 : 155; };
  const boxH = function (n) { return n.size === 1 ? 26  : n.size === 3 ? 40  : 32; };

  const pos = new Map();
  byDepth.forEach(function (list, dp) {
    list.forEach(function (n, i) {
      const saved = nodePos[n.key];
      pos.set(n.key, saved ? { x: saved.x, y: saved.y }
                           : { x: PAD + dp * COL_W, y: PAD + i * ROW_H });
    });
  });

  const byKey = new Map(nodes.map(function (n) { return [n.key, n]; }));
  const xs = nodes.map(function (n) { return pos.get(n.key).x + boxW(n); });
  const ys = nodes.map(function (n) { return pos.get(n.key).y + boxH(n); });
  const W = Math.max.apply(null, xs) + PAD * 2;
  const H = Math.max.apply(null, ys) + PAD * 2 + 30;

  // ── Area rectangles: bounding box of each area's nodes ──
  const areaSvg = areas.map(function (a) {
    const ns = a.keys.map(function (k) { return byKey.get(k); }).filter(Boolean);
    if (ns.length < 2) return '';
    const x1 = Math.min.apply(null, ns.map(function (n) { return pos.get(n.key).x; })) - 10;
    const y1 = Math.min.apply(null, ns.map(function (n) { return pos.get(n.key).y; })) - 18;
    const x2 = Math.max.apply(null, ns.map(function (n) { return pos.get(n.key).x + boxW(n); })) + 10;
    const y2 = Math.max.apply(null, ns.map(function (n) { return pos.get(n.key).y + boxH(n); })) + 10;
    return '<g class="area-g"><rect class="area-box" x="' + x1 + '" y="' + y1 +
           '" width="' + (x2 - x1) + '" height="' + (y2 - y1) + '" rx="6"/>' +
           '<text class="area-label" x="' + (x1 + 8) + '" y="' + (y1 + 12) + '">' + esc(a.name) + '</text></g>';
  }).join('');

  function edgePath(e, i) {
    if (!layerOn[e.kind]) return '';
    // Wireless association is conveyed by the node's coloured radio icon, not by
    // a line - otherwise every device fans into one of three APs and the diagram
    // becomes unreadable. Only genuinely wired links get a network line.
    const childNode = byKey.get(e.child);
    if (e.kind === 'network' && childNode && childNode.link &&
        (e.note || '').indexOf('wired') < 0) return '';
    const a = pos.get(e.parent), b = pos.get(e.child);
    const na = byKey.get(e.parent), nb = byKey.get(e.child);
    if (!a || !b || !na || !nb) return '';
    const x1 = a.x + boxW(na), y1 = a.y + boxH(na) / 2;
    const x2 = b.x,            y2 = b.y + boxH(nb) / 2;
    const mx = (x1 + x2) / 2;
    // Wireless links take the child's SSID colour; wired/powerline stay dashed grey.
    const wireless = e.kind === 'network' && nb.link;
    const stroke = wireless ? ' style="stroke:' + (LINK_COLOR[nb.link] || '#94a3b8') + '"' : '';
    const cls = 'e-' + e.kind + (e.user ? ' e-user' : '') + (wireless ? ' e-wireless' : '');
    return '<path class="' + cls + '" data-child="' + e.child + '" data-parent="' + e.parent +
           '" data-i="' + i + '"' + stroke + ' d="M' + x1 + ',' + y1 +
           ' C' + mx + ',' + y1 + ' ' + mx + ',' + y2 + ' ' + x2 + ',' + y2 + '">' +
           '<title>' + esc(e.kind) + (e.note ? ': ' + esc(e.note) : '') +
           (e.user ? ' (added by you - click to remove)' : '') + '</title></path>';
  }

  function nodeGroup(n) {
    const p = pos.get(n.key);
    const w = boxW(n), h = boxH(n);
    // No power => greyed out entirely. Radio down => only the link icon greys.
    const noPower = n.powered === false;
    const cls = noPower                     ? 'n-dead'
              : n.verdict === 'root-cause'  ? 'n-fault'
              : n.verdict === 'suppressed'  ? 'n-sup'
              : 'n-ok';
    let tip;
    if (noPower)                         tip = 'NO POWER';
    else if (n.verdict === 'suppressed') tip = 'suppressed - because ' + n.because;
    else if (n.verdict === 'root-cause') tip = 'ROOT CAUSE' + (n.affected.length ? ' - affects ' + n.affected.join(', ') : '');
    else                                 tip = 'healthy';
    if (n.outputOn === false) tip += ' | output OFF';
    if (n.remedy)             tip += ' | ' + n.remedy;

    const maxChars = n.size === 1 ? 14 : n.size === 3 ? 26 : 20;
    const label = n.label.length > maxChars ? n.label.slice(0, maxChars - 1) + '…' : n.label;
    const sel = connectFrom === n.key ? ' connect-src' : '';

    // Output-off pip: the device is fine, its OUTPUT is switched off.
    const outPip = n.outputOn === false
      ? '<circle class="pip-off" cx="' + (p.x + w - 8) + '" cy="' + (p.y + h - 7) + '" r="3"/>' : '';
    const blind = n.blindSpot
      ? '<text class="n-blind" x="' + (p.x + w - 6) + '" y="' + (p.y + 11) + '">◍</text>' : '';

    return '<g class="n-g' + sel + '" data-key="' + n.key + '">' +
      '<title>' + esc(n.label) + ' - ' + esc(tip) + '</title>' +
      '<rect class="n-box ' + cls + '" x="' + p.x + '" y="' + p.y + '" width="' + w + '" height="' + h + '" rx="5"/>' +
      iconFor(n.kind, p.x + 6, p.y + (h - 12) / 2, noPower) +
      '<text class="n-label' + (noPower ? ' dim' : '') + '" x="' + (p.x + 23) + '" y="' + (p.y + (n.size === 1 ? 17 : 14)) + '">' + esc(label) + '</text>' +
      (n.size === 1 ? '' :
        '<text class="n-kind" x="' + (p.x + 23) + '" y="' + (p.y + 26) + '">' + esc(n.kind) + '</text>') +
      ifaceIcons(n, p.x + w - 6, p.y + 4, n.radioUp !== false) +
      outPip + blind + '</g>';
  }

  const vb = [diagramPan.x, diagramPan.y, W / diagramZoom, H / diagramZoom].join(' ');
  const layerBtn = function (k, label) {
    return '<button class="lyr' + (layerOn[k] ? ' on' : '') + '" data-layer="' + k + '">' + label + '</button>';
  };

  host.innerHTML =
    '<div class="diag-toolbar">' +
      '<button data-zoom="in" title="Zoom in">+</button>' +
      '<button data-zoom="out" title="Zoom out">&minus;</button>' +
      '<button data-zoom="reset" title="Reset zoom and pan">reset view</button>' +
      '<button data-zoom="relayout" title="Discard dragged positions">re-layout</button>' +
      '<span class="lyr-sep"></span>' +
      layerBtn('power', '⚡ power') + layerBtn('network', '📶 wifi/wired') + layerBtn('bt-host', 'ᛒ bluetooth') +
      '<span class="lyr-sep"></span>' +
      '<button id="btn-add-node" title="Add a device to the diagram">+ device</button>' +
      '<button id="btn-connect" class="' + (connectFrom ? 'on' : '') + '" ' +
        'title="Click this, then click two nodes to connect them">+ connection</button>' +
      '<span class="diag-hint">' +
        (connectFrom ? 'click the PARENT (source) node…' : 'drag nodes · double-click to edit · scroll to zoom · ◍ = blind spot') +
      '</span>' +
    '</div>' +
    '<svg id="diag-svg" viewBox="' + vb + '" preserveAspectRatio="xMinYMin meet" xmlns="http://www.w3.org/2000/svg">' +
      '<g id="diag-areas">' + areaSvg + '</g>' +
      '<g id="diag-edges">' + edges.map(edgePath).join('') + '</g>' +
      '<g id="diag-nodes">' + nodes.map(nodeGroup).join('') + '</g>' +
    '</svg>';

  wireDiagram(pos, byKey, boxW, boxH, W, H);
}


// ── Node settings editor ─────────────────────────────────────────────────────
// Edits are stored server-side (SQLite, alongside the inventory) so a name,
// SSID, area or size survives a redeploy instead of being overwritten by the
// next build of Topology.fs.

let editingKey = null;

function openNodeEditor(key) {
  const n = (topoData.nodes || []).find(function (x) { return x.key === key; });
  if (!n) return;
  editingKey = key;

  const links = (topoData.links || []).slice();
  ['AbeEero', 'ABEWNETG', 'ABEWNETG-5G', 'ABEWNETG-GAR'].forEach(function (l) {
    if (links.indexOf(l) < 0) links.push(l);
  });
  const areas = (topoData.knownAreas || []).slice();
  const kinds = (topoData.kinds || []).slice();
  const deviceKeys = (topoData.nodes || []).map(function (x) { return x.key; }).sort();

  // Split the stored "kind:value" strings so the selects can be pre-filled.
  const pf = n.powerFrom || '';
  const pfKind = pf.indexOf(':') > 0 ? pf.split(':')[0] : '';
  const pfVal  = pf.indexOf(':') > 0 ? pf.slice(pf.indexOf(':') + 1) : '';
  const lanS = n.lan || 'none';
  const lanKind = lanS.indexOf(':') > 0 ? lanS.split(':')[0] : lanS;
  const lanVal  = lanS.indexOf(':') > 0 ? lanS.slice(lanS.indexOf(':') + 1) : '';

  const opts = function (list, cur, blankLabel) {
    return '<option value="">' + blankLabel + '</option>' +
      list.map(function (v) {
        return '<option value="' + esc(v) + '"' + (v === cur ? ' selected' : '') + '>' + esc(v) + '</option>';
      }).join('');
  };

  document.getElementById('node-editor-body').innerHTML =
    '<label>Name<input id="ed-label" value="' + esc(n.label) + '"></label>' +
    '<label>Area<select id="ed-area">' + opts(areas, n.area, '(none)') + '</select>' +
      '<input id="ed-area-new" placeholder="or type a new area"></label>' +
    // Power: one selector. "the area" means THIS node's area - no second picker.
    '<label>Power from<select id="ed-power">' +
      '<option value=""' + (!pf ? ' selected' : '') + '>(none / unknown)</option>' +
      '<option value="area"' + (pfKind === 'area' ? ' selected' : '') + '>the area it is in</option>' +
      deviceKeys.map(function (k) {
        return '<option value="device:' + esc(k) + '"' +
               (pfKind === 'device' && pfVal === k ? ' selected' : '') + '>' + esc(k) + '</option>';
      }).join('') +
      '</select></label>' +
    // LAN: one selector. `none` is NOT `wired` - it means no network at all.
    '<label>LAN access<select id="ed-lan">' +
      '<option value="none"' + (lanKind === 'none' ? ' selected' : '') + '>none (not a smart device)</option>' +
      '<option value="powerline"' + (lanKind === 'powerline' ? ' selected' : '') + '>powerline (via the area)</option>' +
      // Only access points / routers can be a wired source.
      (topoData.wiredSources || deviceKeys).map(function (k) {
        return '<option value="wired:' + esc(k) + '"' +
               (lanKind === 'wired' && lanVal === k ? ' selected' : '') +
               '>wired from ' + esc(k) + '</option>';
      }).join('') +
      links.map(function (l) {
        return '<option value="wifi:' + esc(l) + '"' +
               (lanKind === 'wifi' && lanVal === l ? ' selected' : '') + '>WiFi: ' + esc(l) + '</option>';
      }).join('') +
      '</select></label>' +
    '<label>Type<select id="ed-kind">' + opts(kinds, n.kind, '(unchanged)') + '</select></label>' +
    '<label>Box size<select id="ed-size">' +
      '<option value="1"' + (n.size === 1 ? ' selected' : '') + '>small</option>' +
      '<option value="2"' + (n.size === 2 ? ' selected' : '') + '>normal</option>' +
      '<option value="3"' + (n.size === 3 ? ' selected' : '') + '>large</option>' +
      '</select></label>' +
    '<div class="ed-meta">key: <code>' + esc(n.key) + '</code>' +
      (n.device ? ' · inventory: ' + esc(n.device) : ' · <i>no inventory link</i>') +
      ((n.ifaces || []).length
        ? '<div class="ed-ifaces">' + n.ifaces.map(function (f) {
            return '<div>' + esc(f.kind) + ': ' + esc(f.mac) + (f.ip ? ' · ' + esc(f.ip) : '') + '</div>';
          }).join('') + '</div>'
        : '') +
      '</div>';

  document.getElementById('node-editor-title').textContent = 'Edit ' + n.label;
  document.getElementById('node-editor').hidden = false;


}

async function deleteCurrentNode() {
  if (!editingKey) return;
  if (!confirm('Remove this device from the diagram?')) return;
  try {
    await api('POST', '/api/health/node/del', { key: editingKey });
    closeNodeEditor();
    await loadHealth();
  } catch (e) { alert('Delete failed: ' + e.message); }
}

async function addNewDevice() {
  const label = prompt('Name of the new device');
  if (!label) return;
  const key = label.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '');
  try {
    await api('POST', '/api/health/node/new', { key: key, label: label, kind: 'device', size: 2 });
    await loadHealth();
    openNodeEditor(key);      // straight into the editor to fill in the details
  } catch (e) { alert('Could not add device: ' + e.message); }
}

function closeNodeEditor() {
  document.getElementById('node-editor').hidden = true;
  editingKey = null;
}

async function saveNodeEditor() {
  if (!editingKey) return;
  const newArea = document.getElementById('ed-area-new').value.trim();
  const pvRaw = document.getElementById('ed-power').value;
  const areaNow = newArea || document.getElementById('ed-area').value || '';
  // "area" means the node's own area, so it is resolved here rather than asking
  // the user to pick an area twice.
  const powerFrom = pvRaw === 'area' ? (areaNow ? 'area:' + areaNow : '') : pvRaw;
  const lanRaw = document.getElementById('ed-lan').value;
  const lan = lanRaw === 'powerline' ? (areaNow ? 'powerline:' + areaNow : 'none') : lanRaw;

  const body = {
    nodeKey: editingKey,
    label:   document.getElementById('ed-label').value.trim(),
    area:    newArea || document.getElementById('ed-area').value,
    link:    lan.indexOf('wifi:') === 0 ? lan.slice(5) : '',
    kind:    document.getElementById('ed-kind').value || null,
    size:    parseInt(document.getElementById('ed-size').value, 10),
    powerFrom: powerFrom,
    lan:       lan
  };
  try {
    await api('POST', '/api/health/node', body);
    closeNodeEditor();
    await loadHealth();
  } catch (e) {
    alert('Save failed: ' + e.message);
  }
}

// ── Diagram interaction: drag (grid-snapped), zoom, pan, layers, connect ─────
function wireDiagram(pos, byKey, boxW, boxH, W, H) {
  const svg = document.getElementById('diag-svg');
  if (!svg) return;

  document.querySelectorAll('[data-zoom]').forEach(function (b) {
    b.addEventListener('click', function () {
      const a = b.dataset.zoom;
      if (a === 'in')            diagramZoom = Math.min(4, diagramZoom * 1.25);
      else if (a === 'out')      diagramZoom = Math.max(0.25, diagramZoom / 1.25);
      else if (a === 'reset')    { diagramZoom = 1; diagramPan = { x: 0, y: 0 }; }
      else if (a === 'relayout') {
        if (Object.keys(nodePos).length &&
            !confirm('Discard your arranged positions and auto-layout again? healthRestore() in the console can undo this.')) return;
        nodePos = {}; saveNodePos(); diagramZoom = 1; diagramPan = { x: 0, y: 0 };
      }
      renderDiagram();
    });
  });

  // Layer visibility: electrical, wifi/wired and bluetooth are independent
  // because a power fault and a radio fault look nothing alike.
  document.querySelectorAll('[data-layer]').forEach(function (b) {
    b.addEventListener('click', function () {
      const k = b.dataset.layer;
      layerOn[k] = !layerOn[k];
      saveLayers();
      renderDiagram();
    });
  });

  const abtn = document.getElementById('btn-add-node');
  if (abtn) abtn.addEventListener('click', addNewDevice);

  const cbtn = document.getElementById('btn-connect');
  if (cbtn) cbtn.addEventListener('click', function () {
    connectFrom = connectFrom ? null : '__await__';
    renderDiagram();
  });

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

  function redrawPositions() {
    Object.keys(nodePos).forEach(function (k) { pos.set(k, nodePos[k]); });
    document.querySelectorAll('.n-g').forEach(function (g) {
      const n = byKey.get(g.dataset.key);
      const p = pos.get(g.dataset.key);
      if (!p || !n) return;
      const w = boxW(n), h = boxH(n);
      const rect = g.querySelector('rect');
      rect.setAttribute('x', p.x); rect.setAttribute('y', p.y);
      // Icon, labels and badges all hang off the box origin.
      const icon = g.querySelector('g');
      if (icon) icon.setAttribute('transform', 'translate(' + (p.x + 6) + ',' + (p.y + (h - 12) / 2) + ')');
      const txts = g.querySelectorAll('text');
      let ti = 0;
      if (txts[ti] && !txts[ti].classList.contains('n-blind')) {
        txts[ti].setAttribute('x', p.x + 23);
        txts[ti].setAttribute('y', p.y + (n.size === 1 ? 17 : 14)); ti++;
      }
      if (txts[ti] && txts[ti].classList.contains('n-kind')) {
        txts[ti].setAttribute('x', p.x + 23); txts[ti].setAttribute('y', p.y + 26); ti++;
      }
      const blindT = g.querySelector('.n-blind');
      if (blindT) { blindT.setAttribute('x', p.x + w - 6); blindT.setAttribute('y', p.y + 11); }
      const pip = g.querySelector('.pip-off');
      if (pip) { pip.setAttribute('cx', p.x + w - 8); pip.setAttribute('cy', p.y + h - 7); }
    });
    document.querySelectorAll('#diag-edges path').forEach(function (path) {
      const na = byKey.get(path.dataset.parent), nb = byKey.get(path.dataset.child);
      const a = pos.get(path.dataset.parent),    b = pos.get(path.dataset.child);
      if (!a || !b || !na || !nb) return;
      const x1 = a.x + boxW(na), y1 = a.y + boxH(na) / 2;
      const x2 = b.x,            y2 = b.y + boxH(nb) / 2;
      const mx = (x1 + x2) / 2;
      path.setAttribute('d', 'M' + x1 + ',' + y1 + ' C' + mx + ',' + y1 +
                             ' ' + mx + ',' + y2 + ' ' + x2 + ',' + y2);
    });
  }

  // Click a user-added edge to remove it.
  svg.addEventListener('click', function (evt) {
    const p = evt.target.closest ? evt.target.closest('.e-user') : null;
    if (!p) return;
    const i = parseInt(p.dataset.i, 10) - topoData.edges.length;
    if (i >= 0 && i < userEdges.length) {
      userEdges.splice(i, 1);
      saveUserEdges();
      renderDiagram();
    }
  });

  // Double-click opens the settings editor for that node.
  svg.addEventListener('dblclick', function (evt) {
    const g = evt.target.closest ? evt.target.closest('.n-g') : null;
    if (g) { openNodeEditor(g.dataset.key); evt.preventDefault(); }
  });

  let drag = null;

  svg.addEventListener('mousedown', function (evt) {
    const g = evt.target.closest ? evt.target.closest('.n-g') : null;

    // Connection mode: first click picks the parent, second the child.
    if (connectFrom && g) {
      const key = g.dataset.key;
      if (connectFrom === '__await__') {
        connectFrom = key;
      } else if (connectFrom !== key) {
        const kind = prompt('Connection type: power, network or bt-host', 'power');
        if (kind && ['power', 'network', 'bt-host', 'host'].indexOf(kind) >= 0) {
          api('POST', '/api/health/edge',
              { child: key, parent: connectFrom, kind: kind, note: 'added by you' })
            .then(function () { loadHealth(); })
            .catch(function (e) { alert('Could not add connection: ' + e.message); });
        }
        connectFrom = null;
      }
      renderDiagram();
      evt.preventDefault();
      return;
    }

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
      // Snap to a grid so hand-arranged layouts stay tidy.
      nodePos[drag.key] = { x: snap(p.x - drag.dx), y: snap(p.y - drag.dy) };
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

['ed-close', 'ed-cancel'].forEach(function (id) {
  const el = document.getElementById(id);
  if (el) el.addEventListener('click', closeNodeEditor);
});
const edSave = document.getElementById('ed-save');
if (edSave) edSave.addEventListener('click', saveNodeEditor);
const edDel = document.getElementById('ed-delete');
if (edDel) edDel.addEventListener('click', deleteCurrentNode);
const edBg = document.querySelector('#node-editor .ed-bg');
if (edBg) edBg.addEventListener('click', closeNodeEditor);

setInterval(function () {
  if (!document.getElementById('health').hidden) loadHealth();
}, 60000);

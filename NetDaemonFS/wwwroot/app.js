const JS_VERSION = 89;

// ── State ─────────────────────────────────────────────────────────────────────

let allDevices  = [];
const expandedAttrPrefixes = new Set();
let selectedDeviceId = null;
let filter      = {
  text: '',
  categories: new Set(),
  tri: { online: 0, wired: 0, '2.4G': 0, '5G': 0, webui: 0, bt: 0, unknown: 0 }
};
let sort        = { col: 'isOnline', dir: -1, history: [] };   // history: [{col,dir}, ...]

// Tri-state filter keys grouped: includes within a group OR together;
// excludes always subtract; groups ANDed. State: 0=off, 1=include, -1=exclude.
const TRI_GROUPS = {
  status:  ['online'],
  kind:    ['wired', '2.4G', '5G', 'webui'],
  special: ['bt', 'unknown']
};

function matchesPred(d, k) {
  switch (k) {
    case 'online':  return d.isOnline;
    case 'wired':   return currentConn(d) === 'Wired';
    case '2.4G':    return currentConn(d).includes('2.4');
    case '5G':      return currentConn(d).includes('5G');
    case 'webui':   return !!d.webUiUrl;
    case 'bt':      return !!d.addrs?.some(a => a.addrType === 'bluetooth' || a.addrType === 'irk' || a.addrType === 'beacon_id');
    case 'unknown': return d.category === 'Unknown';
    default:        return false;
  }
}

// ── API helpers ───────────────────────────────────────────────────────────────

const API_BASE = window.location.pathname.startsWith('/inventory') ? '/inventory' : '';

async function api(method, path, body) {
  const opts = { method, headers: { 'Content-Type': 'application/json' } };
  if (body !== undefined) opts.body = JSON.stringify(body);
  const r = await fetch(API_BASE + path, opts);
  if (!r.ok) throw new Error(`${method} ${path} → ${r.status}`);
  if (r.status === 204) return null;
  return r.json();
}

// ── Device helpers ────────────────────────────────────────────────────────────

function currentIp(d)   { return d.ips?.filter(i => i.isCurrent).sort((a,b) => (b.lastSeen??'').localeCompare(a.lastSeen??''))[0]?.ip ?? ''; }
function currentConn(d) { return d.ips?.filter(i => i.isCurrent).sort((a,b) => (b.lastSeen??'').localeCompare(a.lastSeen??''))[0]?.connType ?? ''; }
function primaryMac(d)  { return d.addrs?.find(a => a.addrType === 'mac')?.address       ?? ''; }
function primaryBt(d)   { return d.addrs?.find(a => a.addrType === 'bluetooth')?.address ?? ''; }
function scanAttr(d, k) { return d.scanAttrs?.find(a => a.key === k)?.value || ''; }
function deviceName(d)  {
  return d.name
      || scanAttr(d, 'eero.nickname')
      || scanAttr(d, 'bermuda.name')
      || d.ips?.find(i => i.isCurrent)?.hostname
      || d.ips?.find(i => i.hostname)?.hostname
      || currentIp(d)
      || primaryMac(d)
      || primaryBt(d)
      || '';
}
function deviceModelWithSource(d) {
  if (d.model) return { value: d.model, source: '' };
  for (const [key, label] of [
    ['netgear.model',      'Netgear'],
    ['eero.model',         'Eero'],
    ['eero.device_type',   'Eero'],
  ]) {
    const v = scanAttr(d, key);
    if (v) return { value: v, source: label };
  }
  return { value: '', source: '' };
}
function deviceModel(d) { return deviceModelWithSource(d).value; }

// Returns current IPs ordered consistently with orderedMacAddrs for row alignment.
function orderedCurrentIps(d) {
  return (d.ips ?? [])
    .filter(i => i.isCurrent)
    .sort((a, b) => (a.pairedMac ?? '').localeCompare(b.pairedMac ?? '') || (b.lastSeen ?? '').localeCompare(a.lastSeen ?? ''));
}

// Returns MAC addrs with paired-IP MACs first (in same order as orderedCurrentIps), then remaining.
function orderedMacAddrs(d) {
  const ips      = orderedCurrentIps(d);
  const paired   = ips.map(i => i.pairedMac).filter(Boolean);
  const allMacs  = (d.addrs ?? []).filter(a => a.addrType === 'mac');
  const inOrder  = paired.map(mac => allMacs.find(a => a.address === mac)).filter(Boolean);
  const rest     = allMacs.filter(a => !paired.includes(a.address))
                          .sort((a, b) => (b.lastSeen ?? '').localeCompare(a.lastSeen ?? ''));
  return [...inOrder, ...rest];
}

function fmtAge(iso) {
  if (!iso) return '—';
  // Normalize: truncate sub-millisecond digits (>3 decimal places) that some browsers reject
  const normalized = iso.replace(/(\.\d{3})\d+/, '$1');
  const ms = Date.now() - new Date(normalized).getTime();
  if (isNaN(ms)) return iso;
  const diff = ms / 1000;
  if (diff < 0 || diff < 120)  return 'just now';
  if (diff < 3600)   return `${Math.round(diff / 60)}m ago`;
  if (diff < 86400)  return `${Math.round(diff / 3600)}h ago`;
  if (diff < 604800) return `${Math.round(diff / 86400)}d ago`;
  return new Date(normalized).toLocaleDateString();
}

function addrTip(source, firstSeen, lastSeen) {
  const parts = [];
  if (source) parts.push(`Source: ${source}`);
  if (lastSeen) parts.push(`Last seen: ${fmtAge(lastSeen)}`);
  if (firstSeen) parts.push(`First seen: ${fmtAge(firstSeen)}`);
  return parts.join(' · ');
}

function connBadge(ct) {
  if (!ct) return `<span class="conn conn-unknown">—</span>`;
  if (ct.startsWith('Wired'))  return `<span class="conn conn-wired">Wired</span>`;
  if (ct.includes('2.4'))      return `<span class="conn conn-24">2.4G</span>`;
  if (ct.includes('5G'))       return `<span class="conn conn-5">5G</span>`;
  if (ct.includes('Bluetooth'))return `<span class="conn conn-bt"><svg width="10" height="12" viewBox="0 0 10 14" fill="currentColor" style="vertical-align:middle"><path d="M5 0v5.5L2 3 .5 4.5 4 7 .5 9.5 2 11l3-2.5V14l5-4.5L7 7l3-2.5L5 0zm1.5 4v2L8 4.5 6.5 4zm0 6l1.5-1.5L6.5 8v2z"/></svg></span>`;
  return `<span class="conn conn-unknown">${ct}</span>`;
}

// ── Filtering ─────────────────────────────────────────────────────────────────

function matchesFilter(d) {
  const { text, categories, tri } = filter;

  for (const keys of Object.values(TRI_GROUPS)) {
    const includes = keys.filter(k => tri[k] === 1);
    const excludes = keys.filter(k => tri[k] === -1);
    if (includes.length > 0 && !includes.some(k => matchesPred(d, k))) return false;
    if (excludes.some(k => matchesPred(d, k))) return false;
  }

  if (categories.size > 0 && !categories.has(d.category)) return false;

  // text search — searches all visible columns + notes + scan attrs
  if (text) {
    const q = text.toLowerCase();
    const vis = visibleCols();
    const parts = [
      deviceName(d), d.category, deviceModel(d), deviceManufacturer(d),
      ...(d.ips ?? []).filter(i => i.isCurrent).map(i => i.ip),
      ...(d.ips ?? []).filter(i => i.isCurrent).map(i => i.hostname ?? ''),
      primaryMac(d), primaryBt(d),
      currentConn(d), d.webUiUrl ?? '',
      ...(d.haEntities ?? []),
      ...(d.notes ?? []).map(n => n.note),
      ...(d.scanAttrs ?? []).map(a => a.value),
      ...vis.filter(c => c.id.startsWith('attr:')).map(c => scanAttr(d, c.id.slice(5)))
    ];
    const haystack = parts.join(' ').toLowerCase();
    if (!haystack.includes(q)) return false;
  }

  return true;
}

// ── Column definitions ───────────────────────────────────────────────────────

function deviceManufacturerWithSource(d) {
  for (const [key, label] of [
    ['eero.manufacturer',    'Eero'],
    ['bermuda.manufacturer', 'Bermuda'],
    ['oui.manufacturer',     'OUI'],
    ['bt.manufacturer',      'BT OUI'],
  ]) {
    const v = scanAttr(d, key);
    if (v) return { value: v, source: label };
  }
  return { value: '', source: '' };
}
function deviceManufacturer(d) { return deviceManufacturerWithSource(d).value; }

const CELL_RENDERERS = {
  online: d => {
    const bleOnly = d.isOnline && !currentIp(d) && d.addrs?.some(a => a.addrType === 'bluetooth' || a.addrType === 'irk');
    return `<span class="dot ${bleOnly ? 'ble' : d.isOnline ? 'online' : ''}"></span>`;
  },
  name: d => {
    const editOpen = !!document.querySelector(`.edit-row[data-editfor="${d.id}"]`);
    const name = deviceName(d);
    return `${esc(name) || '<em style="color:#4b5563">—</em>'}<button class="edit-btn${editOpen?' open':''}" data-id="${d.id}" title="Edit device">✎</button>`;
  },
  category: d => esc(d.category),
  ip:       d => {
    const current    = orderedCurrentIps(d);
    const deviceMacs = (d.addrs ?? []).filter(a => a.addrType === 'mac');
    const showHint   = deviceMacs.length > 1;
    return current.map(i => {
      const mac = showHint && i.pairedMac ? `<span style="color:#4b5563;font-size:0.68rem"> ${esc(i.pairedMac.slice(-5))}</span>` : '';
      const tip = addrTip(i.source, i.firstSeen, i.lastSeen);
      return `<div style="font-family:monospace;font-size:0.78rem;overflow:hidden;text-overflow:ellipsis" title="${esc(tip)}">${esc(i.ip)}${mac}</div>`;
    }).join('') || '';
  },
  connType: d => {
    const current = (d.ips ?? []).filter(i => i.isCurrent).sort((a,b) => (b.lastSeen??'').localeCompare(a.lastSeen??''));
    const types = [...new Set(current.map(i => i.connType).filter(Boolean))];
    const hasBt = d.addrs?.some(a => a.addrType === 'bluetooth');
    if (hasBt && !types.some(t => t.includes('Bluetooth'))) types.push('Bluetooth');
    return types.map(ct => connBadge(ct)).join(' ') || connBadge('');
  },
  mac: d => {
    const byRecent  = (a, b) => (b.lastSeen ?? '').localeCompare(a.lastSeen ?? '');
    const macAddrs  = orderedMacAddrs(d);
    const activeBts = (d.addrs ?? []).filter(a => a.addrType === 'bluetooth' && a.isActive).sort(byRecent);
    const allBts    = (d.addrs ?? []).filter(a => a.addrType === 'bluetooth').sort(byRecent);
    const bts = activeBts.length > 0 ? activeBts : allBts.slice(0, 1);
    const addrDot = (a, isBt) => {
      const cls = (a.isActive && d.isOnline) ? (isBt ? 'ble' : 'online') : '';
      return `<span class="dot ${cls}" style="display:inline-block;width:6px;height:6px;margin-right:3px;vertical-align:middle"></span>`;
    };
    const isLocalMac = addr => addr.length >= 2 && '2367ABEFabef'.includes(addr[1]);
    // BLE: only first char 0-3 = public IEEE address (static); 4-F = random/private (transient)
    const isTransientBt = addr => {
      if (addr.length < 2) return true;
      const c = addr[0];
      return c >= '4' && c <= '7';   // 4-7 = RPA (resolvable private, rotates); C-F = static random (stable)
    };
    const irks     = (d.addrs ?? []).filter(a => a.addrType === 'irk').sort(byRecent);
    const beacons  = (d.addrs ?? []).filter(a => a.addrType === 'beacon_id').sort(byRecent);
    const parts = [
      ...macAddrs.map(a => {
        const s = isLocalMac(a.address) ? 'font-style:italic;' : '';
        const pairedIp = orderedCurrentIps(d).find(i => i.pairedMac === a.address)?.ip ?? null;
        const lockTip  = a.isReserved ? `Reserved: ${a.reservedIp}` : pairedIp ? 'Click to reserve IP' : '';
        const lockCls  = a.isReserved ? 'lock-btn locked' : 'lock-btn';
        const lock     = pairedIp || a.isReserved
          ? `<button class="${lockCls}" data-device="${d.id}" data-address="${esc(a.address)}" data-reserved-ip="${esc(a.isReserved ? (a.reservedIp ?? '') : pairedIp)}" data-is-reserved="${a.isReserved}" title="${esc(lockTip)}">🔒</button>`
          : '';
        const del  = a.source === 'manual' ? `<button class="addr-del-btn" data-device="${d.id}" data-address="${esc(a.address)}" title="Remove manual address">×</button>` : '';
        return `<div style="font-family:monospace;font-size:0.75rem;color:#94a3b8;overflow:hidden;text-overflow:ellipsis;${s}" title="${esc(addrTip(a.source, a.firstSeen, a.lastSeen))}">${addrDot(a, false)}${esc(a.address)}${lock}${del}</div>`;
      }),
      ...bts.map(a => {
        const s   = isTransientBt(a.address) ? 'font-style:italic;' : '';
        const del = a.source === 'manual' ? `<button class="addr-del-btn" data-device="${d.id}" data-address="${esc(a.address)}" title="Remove manual address">×</button>` : '';
        return `<div style="font-family:monospace;font-size:0.72rem;color:#38bdf8;overflow:hidden;text-overflow:ellipsis;${s}" title="Bluetooth · ${esc(addrTip(a.source, a.firstSeen, a.lastSeen))}">${addrDot(a, true)}${esc(a.address)}${del}</div>`;
      }),
      ...irks.map(a => {
        const abbr = a.address.slice(0, 8) + '…' + a.address.slice(-4);
        const del  = a.source === 'manual' ? `<button class="addr-del-btn" data-device="${d.id}" data-address="${esc(a.address)}" title="Remove manual address">×</button>` : '';
        return `<div style="font-family:monospace;font-size:0.72rem;color:#fbbf24;overflow:hidden;text-overflow:ellipsis" title="IRK · ${esc(a.address)} · ${esc(addrTip(a.source, a.firstSeen, a.lastSeen))}">${addrDot(a, false)}${esc(abbr)}${del}</div>`;
      }),
      ...beacons.map(a => {
        const abbr = a.address.slice(0, 8) + '…' + a.address.slice(-4);
        return `<div style="font-family:monospace;font-size:0.72rem;color:#a78bfa;overflow:hidden;text-overflow:ellipsis" title="iBeacon UUID · ${esc(a.address)} · ${esc(addrTip(a.source, a.firstSeen, a.lastSeen))}">${addrDot(a, false)}${esc(abbr)}</div>`;
      }),
    ];
    return parts.join('') || '<span style="color:#4b5563">—</span>';
  },
  manufacturer: d => { const m = deviceManufacturerWithSource(d); return m.value ? `<span style="font-size:0.78rem" title="${esc('Source: ' + m.source)}">${esc(m.value)}</span>` : ''; },
  model: d => { const m = deviceModelWithSource(d); return m.value ? (m.source ? `<span style="font-size:0.78rem" title="${esc('Source: ' + m.source)}">${esc(m.value)}</span>` : esc(m.value)) : ''; },
  webui: d => d.webUiUrl ? `<a href="${d.webUiUrl}" target="_blank" rel="noopener">${esc(d.webUiUrl.replace(/^https?:\/\//, ''))}</a>` : '—',
  firstSeen: d => `<span style="font-size:0.75rem;color:#64748b" title="${d.firstSeen ? esc(new Date(d.firstSeen).toLocaleString()) : ''}">${fmtAge(d.firstSeen)}</span>`,
  lastSeen: d => `<span style="font-size:0.75rem;color:#64748b" title="${d.lastSeen ? esc(new Date(d.lastSeen).toLocaleString()) : ''}">${fmtAge(d.lastSeen)}</span>`,
  notes:    d => renderNotes(d),
};

const DEFAULT_COLUMNS = [
  { id: 'rownum',   label: '#',          sortKey: null,       width:  36, visible: false },
  { id: 'online',   label: '●',          sortKey: 'isOnline', width:  32, visible: true },
  { id: 'name',     label: 'Name',       sortKey: 'name',     width: 220, visible: true },
  { id: 'category', label: 'Category',   sortKey: 'category', width: 110, visible: true },
  { id: 'ip',       label: 'IP',         sortKey: 'ip',       width: 130, visible: true },
  { id: 'connType', label: 'Connection', sortKey: 'connType', width:  90, visible: true },
  { id: 'mac',      label: 'MAC / BT / IRK / Beacon', sortKey: 'mac',  width: 170, visible: true },
  { id: 'manufacturer', label: 'Manufacturer', sortKey: 'manufacturer', width: 150, visible: true },
  { id: 'model',    label: 'Model',      sortKey: 'model',    width: 140, visible: true },
  { id: 'webui',    label: 'Web UI',     sortKey: null,       width: 160, visible: true },
  { id: 'firstSeen', label: 'First Seen', sortKey: 'firstSeen', width: 100, visible: false },
  { id: 'lastSeen', label: 'Last Seen',  sortKey: 'lastSeen', width: 100, visible: true },
  { id: 'notes',    label: 'Notes',      sortKey: null,       width: 300, visible: true },
];

function loadColumns() {
  try {
    const saved = JSON.parse(localStorage.getItem('cols') || 'null');
    if (Array.isArray(saved) && saved.length > 0) {
      // Rebuild from DEFAULT_COLUMNS so label/sortKey are always current,
      // but preserve user prefs (visible, width) and ordering from saved.
      const savedMap = new Map(saved.map(c => [c.id, c]));
      const merged = DEFAULT_COLUMNS.map(def => {
        const s = savedMap.get(def.id);
        return s ? { ...def, visible: s.visible, width: s.width } : { ...def };
      });
      // Append any saved attr: columns not in DEFAULT_COLUMNS
      const attrCols = saved.filter(c => c.id.startsWith('attr:') && !merged.some(m => m.id === c.id));
      // Restore ordering from saved (by saved index)
      const order = saved.map(c => c.id);
      const reordered = [
        ...order.map(id => merged.find(c => c.id === id) ?? attrCols.find(c => c.id === id)).filter(Boolean),
        ...merged.filter(c => !order.includes(c.id))
      ];
      return reordered;
    }
  } catch {}
  return DEFAULT_COLUMNS.map(c => ({ ...c }));
}
function saveColumns() { localStorage.setItem('cols', JSON.stringify(columns)); }
let columns = loadColumns();
const visibleCols = () => columns.filter(c => c.visible !== false);
const DATE_ATTR_KEYS = new Set(['bermuda.last_seen']);
const cellFor = (col, d) => {
  if (col.id.startsWith('attr:')) {
    const key = col.id.slice(5);
    const val = scanAttr(d, key);
    if (DATE_ATTR_KEYS.has(key)) {
      const normalized = val.replace(/(\.\d{3})\d+/, '$1');
      const tip = val ? esc(new Date(normalized).toLocaleString()) : '';
      return `<span style="font-size:0.75rem;color:#64748b" title="${tip}">${fmtAge(val)}</span>`;
    }
    return esc(val);
  }
  return CELL_RENDERERS[col.id]?.(d) ?? '';
};

function sortVal(d, col) {
  switch (col) {
    case 'isOnline':  return d.isOnline ? 1 : 0;
    case 'name':      return deviceName(d).toLowerCase();
    case 'category':  return d.category.toLowerCase();
    case 'ip':        return currentIp(d).split('.').map(n => n.padStart(3,'0')).join('.');
    case 'connType':  return currentConn(d).toLowerCase();
    case 'mac':       return (primaryMac(d) || primaryBt(d)).toLowerCase();
    case 'manufacturer': return deviceManufacturer(d).toLowerCase();
    case 'model':     return deviceModel(d).toLowerCase();
    case 'firstSeen': return d.firstSeen ?? '';
    case 'lastSeen':  return d.lastSeen ?? '';
    default:
      if (col && col.startsWith('attr:')) return scanAttr(d, col.slice(5)).toLowerCase();
      return '';
  }
}

// ── Render ────────────────────────────────────────────────────────────────────

function updateTableWidth() {
  const vis = visibleCols();
  const total = vis.reduce((s, c) => s + (c.width || 120), 0);
  document.getElementById('tbl').style.width = total + 'px';
}

function buildHeader() {
  const vis = visibleCols();
  const cg = document.getElementById('colgroup');
  cg.innerHTML = vis.map(c => `<col style="width:${c.width||120}px">`).join('');

  const tr = document.getElementById('thead-row');
  tr.innerHTML = vis.map(c => {
    const sortAttr = c.sortKey ? ` data-col="${c.sortKey}"` : '';
    const sortedCls = (c.sortKey && c.sortKey === sort.col) ? ' sorted' : '';
    const rtl   = c.id.startsWith('attr:') ? ' style="direction:rtl"' : '';
    const title = c.id.startsWith('attr:') ? ` title="${esc(c.label)}"` : '';
    return `<th class="${sortedCls}" data-id="${c.id}"${sortAttr}${rtl}${title}>${esc(c.label)}</th>`;
  }).join('');

  updateTableWidth();
  attachHeaderHandlers();
}

let justResized = false;

let dragSrcIdx = null;
let didDrag = false;

function attachHeaderHandlers() {
  document.querySelectorAll('#thead-row th').forEach((th, idx) => {
    // Sort on click
    if (th.dataset.col) {
      th.addEventListener('click', e => {
        if (justResized || didDrag) return;
        if (e.target.classList.contains('col-resizer')) return;
        const col = th.dataset.col;
        if (sort.col === col) sort.dir = -sort.dir;
        else {
          // Push current sort to history (remove duplicates of new col)
          sort.history = [{ col: sort.col, dir: sort.dir }, ...sort.history.filter(h => h.col !== col)].slice(0, 5);
          sort.col = col; sort.dir = 1;
        }
        document.querySelectorAll('#thead-row th').forEach(h => h.classList.remove('sorted'));
        th.classList.add('sorted');
        render();
      });
    }

    // Drag to reorder
    th.draggable = true;
    th.addEventListener('dragstart', e => {
      if (e.target.classList.contains('col-resizer')) { e.preventDefault(); return; }
      dragSrcIdx = idx;
      didDrag = false;
      th.classList.add('dragging-col');
      e.dataTransfer.effectAllowed = 'move';
    });
    th.addEventListener('dragover', e => {
      if (dragSrcIdx === null || dragSrcIdx === idx) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      th.classList.add('drag-over-col');
    });
    th.addEventListener('dragleave', () => { th.classList.remove('drag-over-col'); });
    th.addEventListener('drop', e => {
      e.preventDefault();
      th.classList.remove('drag-over-col');
      if (dragSrcIdx === null || dragSrcIdx === idx) return;
      // Reorder in the full columns array using visible indices
      const vis = visibleCols();
      const srcCol = vis[dragSrcIdx];
      const dstCol = vis[idx];
      const srcFull = columns.indexOf(srcCol);
      const dstFull = columns.indexOf(dstCol);
      columns.splice(srcFull, 1);
      columns.splice(dstFull, 0, srcCol);
      saveColumns();
      didDrag = true;
      dragSrcIdx = null;
      buildHeader();
      render();
      setTimeout(() => { didDrag = false; }, 100);
    });
    th.addEventListener('dragend', () => {
      th.classList.remove('dragging-col');
      document.querySelectorAll('.drag-over-col').forEach(el => el.classList.remove('drag-over-col'));
      dragSrcIdx = null;
    });

    // Resize handle
    const handle = document.createElement('div');
    handle.className = 'col-resizer';
    th.appendChild(handle);
    handle.addEventListener('mousedown', e => {
      e.preventDefault();
      e.stopPropagation();
      handle.classList.add('dragging');
      const colEl = document.querySelectorAll('#colgroup col')[idx];
      const colDef = visibleCols()[idx];
      const startX = e.clientX;
      const startW = colEl.offsetWidth || parseInt(colDef.width) || 120;
      const onMove = ev => {
        const w = Math.max(10, startW + (ev.clientX - startX));
        colEl.style.width = w + 'px';
        colDef.width = w;
        updateTableWidth();
      };
      const onUp = () => {
        handle.classList.remove('dragging');
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        saveColumns();
        justResized = true;
        setTimeout(() => { justResized = false; }, 300);
      };
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  });
}

function render() {
  const vis = visibleCols();
  const visible = allDevices
    .filter(matchesFilter)
    .sort((a, b) => {
      const va = sortVal(a, sort.col), vb = sortVal(b, sort.col);
      if (va < vb) return -sort.dir;
      if (va > vb) return sort.dir;
      // Tiebreak: walk through sort history
      for (const h of sort.history) {
        const ha = sortVal(a, h.col), hb = sortVal(b, h.col);
        if (ha < hb) return -h.dir;
        if (ha > hb) return h.dir;
      }
      return 0;
    });

  const tbody = document.getElementById('tbody');
  const empty = document.getElementById('empty');

  const countEl = document.getElementById('shown-count');
  if (countEl) countEl.textContent = `Showing ${visible.length} / ${allDevices.length}`;

  if (visible.length === 0) {
    tbody.innerHTML = '';
    empty.hidden = false;
    return;
  }
  empty.hidden = true;

  tbody.innerHTML = visible.map((d, rowIdx) => {
    const tds = vis.map(c => {
      if (c.id === 'rownum') return `<td style="color:#4b5563;font-size:0.72rem;text-align:right">${rowIdx + 1}</td>`;
      const cls = c.id === 'notes' ? ' class="note-cell"' : c.id === 'mac' ? ' class="addr-cell"' : '';
      const content = cellFor(c, d);
      const plain = content.replace(/<[^>]*>/g, '').replace(/&amp;/g,'&').replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&quot;/g,'"');
      const title = (c.id !== 'notes' && c.id !== 'online' && plain) ? ` title="${esc(plain)}"` : '';
      return `<td${cls}${title}>${content}</td>`;
    }).join('');
    const sel = d.id === selectedDeviceId ? ' class="row-selected"' : '';
    return `<tr data-id="${d.id}"${sel}>${tds}</tr>`;
  }).join('');
}

function renderNotes(d) {
  const items = (d.notes ?? []).map(n =>
    `<li>
       <span>${esc(n.note)}</span>
       <button class="note-del" data-device="${d.id}" data-note="${n.id}" title="Delete note">×</button>
     </li>`
  ).join('');
  const attrGroups = {};
  for (const a of (d.scanAttrs ?? [])) {
    const dot    = a.key.indexOf('.');
    const prefix = dot >= 0 ? a.key.slice(0, dot) : '';
    (attrGroups[prefix] = attrGroups[prefix] ?? []).push(a);
  }
  const attrHtml = Object.entries(attrGroups).map(([prefix, attrs]) => {
    const expanded = expandedAttrPrefixes.has(prefix);
    const label    = prefix || 'other';
    const arrow    = expanded ? '▾' : '▸';
    const tooltip  = expanded ? '' : ` title="${esc(attrs.map(a => {
      const shortKey = prefix ? a.key.slice(prefix.length + 1) : a.key;
      const val = a.key === 'bermuda.last_seen' ? fmtAge(a.value) : a.value;
      return shortKey + ': ' + val;
    }).join('\n'))}"`;
    const header   = `<li class="attr-group-hdr" data-prefix="${esc(prefix)}"${tooltip}>${arrow} ${esc(label)}</li>`;
    if (!expanded) return header;
    const rows = attrs.map(a => {
      const isDateAttr = a.key === 'bermuda.last_seen';
      const display    = isDateAttr ? fmtAge(a.value) : esc(a.value);
      const updated    = a.updatedAt ? `updated ${fmtAge(a.updatedAt)}` : '';
      const valTip     = isDateAttr ? new Date(a.value).toLocaleString() : a.value;
      const tip        = `title="${esc(valTip + (updated ? '\n' + updated : ''))}"`;
      const shortKey   = prefix ? a.key.slice(prefix.length + 1) : a.key;
      return `<li class="attr-row"><span class="attr-k">${esc(shortKey)}</span><span class="attr-v" ${tip}>${display}</span></li>`;
    }).join('');
    return header + rows;
  }).join('');
  return `<ul class="note-list">${items}</ul>
    ${attrHtml ? `<ul class="attr-list">${attrHtml}</ul>` : ''}`;
}

function esc(s) {
  return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// ── Category bar ──────────────────────────────────────────────────────────────

function buildCatBar() {
  const cats = [...new Set(allDevices.map(d => d.category))].sort();
  const bar = document.getElementById('cat-bar');
  const sel = filter.categories;
  bar.innerHTML = `<button class="cat-btn ${sel.size === 0 ? 'active' : ''}" data-cat="">All</button>` +
    cats.map(c => `<button class="cat-btn ${sel.has(c) ? 'active' : ''}" data-cat="${esc(c)}">${esc(c)}</button>`).join('');
}

// ── Stats bar ─────────────────────────────────────────────────────────────────

async function loadStats() {
  try {
    const s = await api('GET', '/api/stats');
    document.getElementById('s-total').textContent      = s.total;
    document.getElementById('s-online').textContent  = s.online;
    document.getElementById('s-wired').textContent  = s.wired;
    document.getElementById('s-24').textContent     = s.wireless24;
    document.getElementById('s-5').textContent      = s.wireless5;
    document.getElementById('s-bt').textContent     = s.bluetooth;
    document.getElementById('s-webui').textContent  = s.withWebUi;
    document.getElementById('s-unk').textContent    = s.unknown;
  } catch (_) {}
}

// ── Data loading ──────────────────────────────────────────────────────────────

async function loadDevices() {
  try {
    const data = await api('GET', '/api/devices');
    allDevices.length = 0;
    allDevices.push(...data);
    buildCatBar();
    if (!document.querySelector('.edit-row')) render();
    loadStats();
  } catch (e) {
    console.error('loadDevices:', e);
  }
}

// ── Column config modal ───────────────────────────────────────────────────────

function allAttrKeys() {
  const set = new Set();
  for (const d of allDevices)
    for (const a of (d.scanAttrs ?? []))
      set.add(a.key);
  return [...set].sort();
}

function openColModal() {
  const list = document.getElementById('col-list');
  list.innerHTML = columns.map((c, i) => `
    <li data-idx="${i}">
      <input type="checkbox" ${c.visible !== false ? 'checked' : ''} data-toggle="${i}">
      <span class="col-label">${esc(c.label)}</span>
      <button class="col-move" data-up="${i}" title="Move up">▲</button>
      <button class="col-move" data-down="${i}" title="Move down">▼</button>
      ${c.id.startsWith('attr:') ? `<button class="col-move" data-remove="${i}" title="Remove">✕</button>` : ''}
    </li>`).join('');

  const existingIds = new Set(columns.map(c => c.id));
  const attrList = document.getElementById('attr-list');
  const avail = allAttrKeys().filter(k => !existingIds.has('attr:' + k));
  attrList.innerHTML = avail.length === 0
    ? `<li style="color:#4b5563;font-size:0.75rem">All available attributes are added.</li>`
    : avail.map(k => `
        <li>
          <span class="col-label" style="font-family:monospace;font-size:0.75rem;color:#94a3b8">${esc(k)}</span>
          <button class="col-move" data-add="${esc(k)}" title="Add as column">+</button>
        </li>`).join('');

  document.getElementById('col-modal').hidden = false;
}

function closeColModal() { document.getElementById('col-modal').hidden = true; }

document.getElementById('btn-cols').addEventListener('click', openColModal);
document.getElementById('col-modal-close').addEventListener('click', closeColModal);
document.querySelector('#col-modal .col-modal-bg').addEventListener('click', closeColModal);
document.getElementById('col-reset').addEventListener('click', () => {
  columns = DEFAULT_COLUMNS.map(c => ({ ...c }));
  saveColumns();
  buildHeader();
  render();
  openColModal();
});

document.getElementById('col-modal').addEventListener('click', e => {
  const t = e.target;
  if (t.dataset.toggle !== undefined) {
    const i = +t.dataset.toggle;
    columns[i].visible = t.checked;
  } else if (t.dataset.up !== undefined) {
    const i = +t.dataset.up;
    if (i > 0) { [columns[i-1], columns[i]] = [columns[i], columns[i-1]]; }
  } else if (t.dataset.down !== undefined) {
    const i = +t.dataset.down;
    if (i < columns.length - 1) { [columns[i+1], columns[i]] = [columns[i], columns[i+1]]; }
  } else if (t.dataset.remove !== undefined) {
    columns.splice(+t.dataset.remove, 1);
  } else if (t.dataset.add !== undefined) {
    const key = t.dataset.add;
    columns.push({ id: 'attr:' + key, label: key, sortKey: 'attr:' + key, width: 140, visible: true });
  } else {
    return;
  }
  saveColumns();
  buildHeader();
  render();
  openColModal();
});

// ── Search input ──────────────────────────────────────────────────────────────

document.getElementById('search').addEventListener('input', e => {
  filter.text = e.target.value.trim();
  updateBadgeClasses();
  render();
});

// ── Stat badge click ──────────────────────────────────────────────────────────

function updateBadgeClasses() {
  document.querySelectorAll('.stat-badge[data-filter]').forEach(b => {
    const s = filter.tri[b.dataset.filter] || 0;
    b.classList.toggle('tri-include', s === 1);
    b.classList.toggle('tri-exclude', s === -1);
  });
  const anyTri  = Object.values(filter.tri).some(v => v !== 0);
  const clearAll = !anyTri && !filter.text && filter.categories.size === 0;
  document.querySelector('.stat-badge[data-action="reset"]')
          ?.classList.toggle('active', clearAll);
}

document.getElementById('stats-bar').addEventListener('click', e => {
  const badge = e.target.closest('.stat-badge');
  if (!badge) return;

  if (badge.dataset.action === 'reset') {
    Object.keys(filter.tri).forEach(k => filter.tri[k] = 0);
    filter.text = '';
    filter.categories.clear();
    document.getElementById('search').value = '';
    document.querySelectorAll('.cat-btn').forEach(b => b.classList.remove('active'));
    document.querySelector('.cat-btn[data-cat=""]')?.classList.add('active');
    updateBadgeClasses();
    render();
    return;
  }

  const k = badge.dataset.filter;
  if (!(k in filter.tri)) return;
  filter.tri[k] = filter.tri[k] === 0 ? 1 : filter.tri[k] === 1 ? -1 : 0;
  updateBadgeClasses();
  render();
});

// ── Category click ────────────────────────────────────────────────────────────

document.getElementById('cat-bar').addEventListener('click', e => {
  const btn = e.target.closest('[data-cat]');
  if (!btn) return;
  const cat = btn.dataset.cat;
  if (cat === '') filter.categories.clear();
  else if (filter.categories.has(cat)) filter.categories.delete(cat);
  else filter.categories.add(cat);
  document.querySelectorAll('.cat-btn').forEach(b => {
    const c = b.dataset.cat;
    const on = c === '' ? filter.categories.size === 0 : filter.categories.has(c);
    b.classList.toggle('active', on);
  });
  updateBadgeClasses();
  render();
});

// ── Inline device edit ────────────────────────────────────────────────────────

function toggleEditRow(id) {
  const existing = document.querySelector(`.edit-row[data-editfor="${id}"]`);
  if (existing) { existing.remove(); render(); return; }

  const d = allDevices.find(x => x.id === id);
  if (!d) return;
  const deviceRow = document.querySelector(`tr[data-id="${id}"]`);
  if (!deviceRow) return;

  const cats = [...new Set(allDevices.map(x => x.category))].sort();
  const catsHtml = cats.map(c => `<option value="${esc(c)}">`).join('');

  const tr = document.createElement('tr');
  tr.className = 'edit-row';
  tr.dataset.editfor = id;
  tr.innerHTML = `
    <td colspan="${visibleCols().length}">
      <div class="edit-form">
        <div class="edit-field">
          <label>Name</label>
          <input name="name" value="${esc(d.name ?? '')}" placeholder="Device name" />
        </div>
        <div class="edit-field">
          <label>Category</label>
          <input name="category" value="${esc(d.category ?? '')}" list="catlist-${id}" placeholder="Category" />
          <datalist id="catlist-${id}">${catsHtml}</datalist>
        </div>
        <div class="edit-field">
          <label>Model</label>
          <input name="model" value="${esc(d.model ?? '')}" placeholder="Model" />
        </div>
        <div class="edit-field" style="min-width:180px">
          <label>Web UI URL</label>
          <input name="webUiUrl" value="${esc(d.webUiUrl ?? '')}" placeholder="http://…" />
        </div>
        <div class="edit-actions">
          <button class="edit-save"   data-id="${id}">Save</button>
          <button class="edit-cancel" data-id="${id}">Cancel</button>
        </div>
      </div>
    </td>`;
  deviceRow.after(tr);
  tr.querySelector('input[name="name"]').focus();
  // mark the pencil button as open
  deviceRow.querySelector('.edit-btn')?.classList.add('open');
}

// ── Notes ─────────────────────────────────────────────────────────────────────

document.getElementById('tbody').addEventListener('click', async e => {
  // Track selected row
  const clickedRow = e.target.closest('tr[data-id]');
  if (clickedRow) {
    selectedDeviceId = clickedRow.dataset.id;
    document.querySelectorAll('tr.row-selected').forEach(r => r.classList.remove('row-selected'));
    clickedRow.classList.add('row-selected');
  }

  // ── Merge mode: intercept all clicks ──
  if (mergeSource) {
    const row = e.target.closest('tr[data-id]');
    if (!row) return;
    const targetId = row.dataset.id;
    if (targetId === mergeSource) return;

    const keepDev  = allDevices.find(x => x.id === targetId);
    const mergeDev = allDevices.find(x => x.id === mergeSource);
    const keepName  = deviceName(keepDev)  || targetId.slice(0, 8);
    const mergeName = deviceName(mergeDev) || mergeSource.slice(0, 8);

    if (!confirm(`Merge "${mergeName}" into "${keepName}"?\n\nAll addresses, IPs, notes, and attributes from "${mergeName}" will be moved to "${keepName}", then "${mergeName}" will be deleted.`)) {
      cancelMerge();
      return;
    }
    try {
      const updated = await api('POST', '/api/devices/merge', { keepId: targetId, mergeId: mergeSource });
      const mergeIdx = allDevices.findIndex(x => x.id === mergeSource);
      if (mergeIdx >= 0) allDevices.splice(mergeIdx, 1);
      const keepIdx = allDevices.findIndex(x => x.id === targetId);
      if (keepIdx >= 0) allDevices[keepIdx] = updated;
      cancelMerge();
      buildCatBar();
      render();
      loadStats();
    } catch (ex) {
      console.error('mergeDevices:', ex);
      alert('Merge failed: ' + ex.message);
      cancelMerge();
    }
    return;
  }

  // Padlock — toggle IP reservation on MAC address
  const lockBtn = e.target.closest('.lock-btn');
  if (lockBtn) {
    const deviceId  = lockBtn.dataset.device;
    const address   = lockBtn.dataset.address;
    const isReserved = lockBtn.dataset.isReserved === 'true';
    const reservedIp = isReserved ? null : lockBtn.dataset.reservedIp;
    try {
      const updated = await api('POST', `/api/devices/${deviceId}/addrs/reserve`, { address, reservedIp });
      const idx = allDevices.findIndex(x => x.id === deviceId);
      if (idx >= 0) allDevices[idx] = updated;
      render();
    } catch (ex) { console.error('reserveAddr:', ex); }
    return;
  }

  // Edit button — toggle edit row
  const editBtn = e.target.closest('.edit-btn');
  if (editBtn) { toggleEditRow(editBtn.dataset.id); return; }

  // Save edit
  const saveBtn = e.target.closest('.edit-save');
  if (saveBtn) {
    const id  = saveBtn.dataset.id;
    const row = document.querySelector(`.edit-row[data-editfor="${id}"]`);
    const val = name => { const v = row.querySelector(`input[name="${name}"]`).value.trim(); return v || null; };
    try {
      const updated = await api('PUT', `/api/devices/${id}`, {
        name: val('name'), category: val('category'), model: val('model'), webUiUrl: val('webUiUrl')
      });
      const idx = allDevices.findIndex(x => x.id === id);
      if (idx >= 0) allDevices[idx] = updated;
      row.remove();
      buildCatBar();
      render();
    } catch (ex) { console.error('updateDevice:', ex); }
    return;
  }

  // Cancel edit
  const cancelBtn = e.target.closest('.edit-cancel');
  if (cancelBtn) {
    const id = cancelBtn.dataset.id;
    document.querySelector(`.edit-row[data-editfor="${id}"]`)?.remove();
    render();
    return;
  }

  // Delete manual address
  const addrDelBtn = e.target.closest('.addr-del-btn');
  if (addrDelBtn) {
    const deviceId = addrDelBtn.dataset.device;
    const address  = addrDelBtn.dataset.address;
    try {
      const updated = await api('DELETE', `/api/devices/${deviceId}/addrs/${encodeURIComponent(address)}`);
      const idx = allDevices.findIndex(x => x.id === deviceId);
      if (idx >= 0) allDevices[idx] = updated;
      render();
    } catch (ex) { console.error('deleteAddr:', ex); }
    return;
  }

  // Attr group header — toggle prefix expansion globally
  const attrHdr = e.target.closest('.attr-group-hdr');
  if (attrHdr) {
    const prefix = attrHdr.dataset.prefix;
    if (expandedAttrPrefixes.has(prefix)) expandedAttrPrefixes.delete(prefix);
    else expandedAttrPrefixes.add(prefix);
    render();
    return;
  }

  // Delete note
  const del = e.target.closest('.note-del');
  if (del) {
    const deviceId = del.dataset.device;
    const noteId   = del.dataset.note;
    try {
      await api('DELETE', `/api/devices/${deviceId}/notes/${noteId}`);
      const d = allDevices.find(x => x.id === deviceId);
      if (d) d.notes = d.notes.filter(n => n.id !== parseInt(noteId));
      render();
    } catch (ex) { console.error('deleteNote:', ex); }
    return;
  }

});


// ── Add-note modal ────────────────────────────────────────────────────────────

const noteModal      = document.getElementById('note-modal');
const noteModalInput = document.getElementById('note-modal-input');
let   noteModalDeviceId = null;

function openNoteModal(deviceId) {
  noteModalDeviceId    = deviceId;
  noteModalInput.value = '';
  noteModal.showModal();
  noteModalInput.focus();
}

document.getElementById('note-modal-cancel').addEventListener('click', () => noteModal.close());

document.getElementById('note-modal-add').addEventListener('click', async () => {
  const text = noteModalInput.value.trim();
  if (!text || !noteModalDeviceId) return;
  try {
    const updated = await api('POST', `/api/devices/${noteModalDeviceId}/notes`, { note: text });
    const idx = allDevices.findIndex(x => x.id === noteModalDeviceId);
    if (idx >= 0) allDevices[idx] = updated;
    noteModal.close();
    render();
  } catch (ex) { console.error('addNote:', ex); }
});

noteModalInput.addEventListener('keydown', e => {
  if (e.key === 'Enter') document.getElementById('note-modal-add').click();
  if (e.key === 'Escape') noteModal.close();
});

// ── Add-address modal ─────────────────────────────────────────────────────────

const addrModal      = document.getElementById('addr-modal');
const addrModalInput = document.getElementById('addr-modal-input');
const addrModalType  = document.getElementById('addr-modal-type');
let   addrModalDeviceId = null;

function openAddrModal(deviceId) {
  addrModalDeviceId    = deviceId;
  addrModalInput.value = '';
  addrModal.showModal();
  addrModalInput.focus();
}

document.getElementById('addr-modal-cancel').addEventListener('click', () => addrModal.close());

document.getElementById('addr-modal-add').addEventListener('click', async () => {
  const rawAddr = addrModalInput.value.trim();
  if (!rawAddr || !addrModalDeviceId) return;
  const typeVal  = addrModalType.value;
  const addrType = typeVal.startsWith('mac') ? 'mac' : typeVal;
  const label    = typeVal === 'mac-ethernet' ? 'ethernet' : typeVal === 'mac-wifi' ? 'wifi' : '';
  try {
    const updated = await api('POST', `/api/devices/${addrModalDeviceId}/addrs`, { addrType, address: rawAddr, label });
    const idx = allDevices.findIndex(x => x.id === addrModalDeviceId);
    if (idx >= 0) allDevices[idx] = updated;
    addrModal.close();
    render();
  } catch (ex) { console.error('addAddr:', ex); alert('Add address failed: ' + ex.message); }
});

addrModalInput.addEventListener('keydown', e => {
  if (e.key === 'Enter') document.getElementById('addr-modal-add').click();
  if (e.key === 'Escape') addrModal.close();
});

// ── Scan button ───────────────────────────────────────────────────────────────

const btnScan    = document.getElementById('btn-scan');
const scanStatus = document.getElementById('scan-status');
let   pollTimer  = null;

async function pollScanStatus() {
  try {
    const s = await api('GET', '/api/scan/status');
    const startedTip   = s.lastScanStarted   ? new Date(s.lastScanStarted).toLocaleString()   : '';
    const completedTip = s.lastScanCompleted ? new Date(s.lastScanCompleted).toLocaleString() : '';
    const startedAge   = fmtAge(s.lastScanStarted);
    const completedAge = fmtAge(s.lastScanCompleted);

    if (s.isRunning) {
      scanStatus.className = 'running dot-spin';
      scanStatus.innerHTML = `Scanning… started <span title="${esc(startedTip)}">${startedAge}</span>`;
      btnScan.disabled = true;
      if (!pollTimer) pollTimer = setInterval(pollScanStatus, 2000);
    } else {
      scanStatus.className = '';
      scanStatus.innerHTML = `Last scan: <span title="${esc(completedTip)}">${completedAge}</span> · ${s.devicesFound} devices`;
      btnScan.disabled = false;
      clearInterval(pollTimer);
      pollTimer = null;
      await loadDevices();   // refresh table after scan
    }
  } catch (_) {}
}

btnScan.addEventListener('click', async () => {
  try {
    await api('POST', '/api/scan/trigger');
    btnScan.disabled = true;
    scanStatus.className = 'running dot-spin';
    scanStatus.textContent = 'Scan queued…';
    if (!pollTimer) pollTimer = setInterval(pollScanStatus, 2000);
  } catch (ex) { console.error('triggerScan:', ex); }
});

// ── Add Device button ────────────────────────────────────────────────────────

document.getElementById('btn-add-device').addEventListener('click', async () => {
  try {
    const dev = await api('POST', '/api/devices');
    allDevices.unshift(dev);
    render();
    toggleEditRow(dev.id);
    document.querySelector(`tr[data-id="${dev.id}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  } catch (ex) {
    console.error('add-device:', ex);
  }
});

// ── Cleanup button ───────────────────────────────────────────────────────────

const btnCleanup = document.getElementById('btn-cleanup');
btnCleanup.addEventListener('click', async () => {
  try {
    btnCleanup.disabled = true;
    btnCleanup.textContent = 'Cleaning…';
    const age = parseInt(document.getElementById('cleanup-age').value) || 240;
    const purgeAttrs = document.getElementById('cleanup-purge-attrs').checked;
    const res = await api('POST', `/api/cleanup?maxAgeMinutes=${age}${purgeAttrs ? '&purgeAttrs=true' : ''}`);
    btnCleanup.textContent = `Purged ${res.purgedDevices}dev ${res.purgedAddresses}addr${purgeAttrs ? ` ${res.purgedAttrs}attrs` : ''}`;
    if (res.purgedDevices > 0 || res.purgedAddresses > 0) await loadDevices();
    setTimeout(() => { btnCleanup.textContent = 'Cleanup'; btnCleanup.disabled = false; }, 3000);
  } catch (ex) {
    console.error('cleanup:', ex);
    btnCleanup.textContent = 'Error';
    setTimeout(() => { btnCleanup.textContent = 'Cleanup'; btnCleanup.disabled = false; }, 3000);
  }
});

// ── CSV export (link, not fetch) ──────────────────────────────────────────────
// Exposed via a plain link in the header if desired; also callable as:
// window.location.href = '/api/export/csv';

// ── Context menu & merge ──────────────────────────────────────────────────────

const ctxMenu = document.getElementById('ctx-menu');
let mergeSource = null;   // device id of "merge with…" initiator

function hideCtx() { ctxMenu.hidden = true; }
document.addEventListener('click', hideCtx);
document.addEventListener('scroll', hideCtx, true);

document.getElementById('tbody').addEventListener('contextmenu', e => {
  const row = e.target.closest('tr[data-id]');
  if (!row) return;
  e.preventDefault();
  const id = row.dataset.id;
  const d  = allDevices.find(x => x.id === id);
  if (!d) return;
  const name = deviceName(d) || id.slice(0, 8);

  if (mergeSource) {
    // Already in merge mode — clicking a target row
    return;
  }

  ctxMenu.innerHTML = `
    <button data-action="add-note" data-id="${id}">Add note…</button>
    <button data-action="add-addr" data-id="${id}">Add address…</button>
    <hr>
    <button data-action="refresh-bermuda" data-id="${id}">Refresh Bermuda</button>
    <button data-action="merge" data-id="${id}">Merge with…</button>
    <hr>
    <button data-action="delete" data-id="${id}" class="danger">Delete "${esc(name)}"</button>
  `;
  ctxMenu.hidden = false;
  ctxMenu.style.left = Math.min(e.clientX, window.innerWidth - 200) + 'px';
  ctxMenu.style.top  = Math.min(e.clientY, window.innerHeight - 100) + 'px';
});

ctxMenu.addEventListener('click', async e => {
  const btn = e.target.closest('button');
  if (!btn) return;
  hideCtx();
  const id = btn.dataset.id;

  if (btn.dataset.action === 'add-note') { openNoteModal(id); return; }
  if (btn.dataset.action === 'add-addr') { openAddrModal(id); return; }

  if (btn.dataset.action === 'refresh-bermuda') {
    const row = document.querySelector(`tr[data-id="${id}"]`);
    row?.classList.add('row-refreshing');
    try {
      const updated = await api('POST', `/api/devices/${id}/refresh-bermuda`);
      const idx = allDevices.findIndex(x => x.id === id);
      if (idx >= 0) allDevices[idx] = updated;
      render();
      row?.classList.add('row-flash-ok');
      setTimeout(() => row?.classList.remove('row-flash-ok'), 1200);
    } catch (ex) {
      console.error('refreshBermuda:', ex);
    } finally {
      row?.classList.remove('row-refreshing');
    }
    return;
  }

  if (btn.dataset.action === 'delete') {
    const d = allDevices.find(x => x.id === id);
    const name = d ? (deviceName(d) || id.slice(0, 8)) : id.slice(0, 8);
    if (!confirm(`Delete device "${name}"? This cannot be undone.`)) return;
    try {
      await api('DELETE', `/api/devices/${id}`);
      allDevices.splice(allDevices.findIndex(x => x.id === id), 1);
      buildCatBar();
      render();
      loadStats();
    } catch (ex) { console.error('deleteDevice:', ex); }
  }

  if (btn.dataset.action === 'merge') {
    startMerge(id);
  }
});

function startMerge(sourceId) {
  mergeSource = sourceId;
  const d = allDevices.find(x => x.id === sourceId);
  const name = deviceName(d) || sourceId.slice(0, 8);
  const banner = document.getElementById('merge-banner');
  document.getElementById('merge-msg').textContent =
    `Click the device to keep. "${name}" will be merged into it.`;
  banner.hidden = false;

  // Highlight source row
  document.querySelector(`tr[data-id="${sourceId}"]`)?.classList.add('merge-target');
}

document.getElementById('merge-cancel').addEventListener('click', cancelMerge);

function cancelMerge() {
  mergeSource = null;
  document.getElementById('merge-banner').hidden = true;
  document.querySelectorAll('.merge-target').forEach(r => r.classList.remove('merge-target'));
}

// ── Initialise ────────────────────────────────────────────────────────────────

const jsVerEl = document.getElementById('js-version');
if (jsVerEl) jsVerEl.textContent = 'v' + JS_VERSION;
buildHeader();
loadDevices();
pollScanStatus();
setInterval(loadDevices, 60_000);    // refresh table every 60 s
setInterval(pollScanStatus, 30_000); // keep status bar current when idle

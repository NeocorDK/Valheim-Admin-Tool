import { t, lang, setLang, onLangChange, applyStatic } from './i18n.js';

// ---------------------------------------------------------------- helpers

const $ = (sel, root = document) => root.querySelector(sel);

/** h('div', {class: 'x', onclick: fn}, child, [children], 'text') */
function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k === 'style') el.style.cssText = v;
    else if (k === 'dataset') Object.assign(el.dataset, v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }
  append(el, kids);
  return el;
}
function append(el, kids) {
  for (const kid of kids.flat(Infinity)) {
    if (kid == null || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
}
function clear(el) { el.replaceChildren(); return el; }
/** Replaces the children; accepts nested arrays and skips null, unlike Element.append. */
function fill(el, ...kids) { el.replaceChildren(); append(el, kids); return el; }

async function api(path, { method = 'GET', body } = {}) {
  const res = await fetch('/api' + path, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    credentials: 'same-origin',
  });
  if (res.status === 401 && path !== '/login') { onUnauthorized(); throw new Error(t('err.auth')); }
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) throw new Error((data && data.error) || res.statusText);
  return data;
}

function toast(message, kind = 'info') {
  const el = h('div', { class: 'toast ' + kind, text: message });
  $('#toasts').append(el);
  setTimeout(() => el.remove(), kind === 'err' ? 8000 : 4000);
}
const fail = e => toast(e.message || String(e), 'err');

const pad = n => String(n).padStart(2, '0');
function fmtTime(ms) { const d = new Date(ms); return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`; }
function fmtDate(ms) { const d = new Date(ms); return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`; }
function fmtShort(ms) {
  const d = new Date(ms), now = new Date();
  return d.toDateString() === now.toDateString() ? fmtTime(ms) : `${pad(d.getDate())}.${pad(d.getMonth() + 1)} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
function fmtDuration(sec) {
  if (sec == null) return '—';
  sec = Math.max(0, Math.round(sec));
  const d = Math.floor(sec / 86400), hh = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
  if (d) return `${d}${t('u.d')} ${hh}${t('u.h')}`;
  if (hh) return `${hh}${t('u.h')} ${m}${t('u.m')}`;
  return `${m}${t('u.m')} ${sec % 60}${t('u.s')}`;
}

/** Unity rich text (as in game tooltips) to safe DOM. */
function richText(text) {
  const frag = document.createDocumentFragment();
  const stack = [frag];
  const re = /<(\/?)(color|b|i|size|sprite|voffset|u|s)(=[^>]*)?>/gi;
  let last = 0, m;
  const top = () => stack[stack.length - 1];
  while ((m = re.exec(text ?? ''))) {
    if (m.index > last) top().append(text.slice(last, m.index));
    last = re.lastIndex;
    const [, close, tag, value] = m;
    const name = tag.toLowerCase();
    if (!['color', 'b', 'i', 'u', 's'].includes(name)) continue;
    if (close) { if (stack.length > 1) stack.pop(); continue; }
    const el = document.createElement(name === 'color' ? 'span' : name);
    if (name === 'color') {
      const c = (value || '').slice(1).replace(/"/g, '');
      if (/^#?[0-9a-f]{3,8}$/i.test(c) || /^[a-z]+$/i.test(c)) el.style.color = /^[0-9a-f]{6,8}$/i.test(c) ? '#' + c : c;
    }
    top().append(el);
    stack.push(el);
  }
  if (last < (text ?? '').length) top().append(text.slice(last));
  return frag;
}

const ICONS = {
  map: 'M9 4 3 6v14l6-2 6 2 6-2V4l-6 2zm0 0v14m6-12v14',
  overview: 'M3 13h8V3H3zm0 8h8v-6H3zm10 0h8V11h-8zm0-18v6h8V3z',
  console: 'M4 5h16v14H4zm2 3 4 4-4 4m6 0h6',
  events: 'M12 8v5l3 2m6-3a9 9 0 1 1-18 0 9 9 0 0 1 18 0',
  players: 'M16 11a4 4 0 1 0-8 0 4 4 0 0 0 8 0m-12 10c0-4 4-6 8-6s8 2 8 6',
  characters: 'M4 7h16v13H4zm4-4h8v4H8zm-1 8h4m-4 4h10',
  configs: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6m7.4-3a7.4 7.4 0 0 0-.1-1.2l2-1.6-2-3.4-2.4 1a7 7 0 0 0-2-1.2L14.5 3h-5l-.4 2.6a7 7 0 0 0-2 1.2l-2.4-1-2 3.4 2 1.6a7.4 7.4 0 0 0 0 2.4l-2 1.6 2 3.4 2.4-1a7 7 0 0 0 2 1.2l.4 2.6h5l.4-2.6a7 7 0 0 0 2-1.2l2.4 1 2-3.4-2-1.6c.1-.4.1-.8.1-1.2',
  maintenance: 'M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18l3 3 6.3-6.3a4 4 0 0 0 5.4-5.4l-2.6 2.6-2.4-.6-.6-2.4z',
  join: 'M15 3h4v18h-4M10 17l5-5-5-5m5 5H3',
  leave: 'M9 21H5V3h4m7 14 5-5-5-5m5 5H9',
  death: 'M12 3a7 7 0 0 0-7 7c0 3 2 4 2 6v3h10v-3c0-2 2-3 2-6a7 7 0 0 0-7-7M9 11h.01M15 11h.01M10 19v2m4-2v2',
  chat: 'M4 5h16v11H8l-4 4z',
  boss: 'M5 16 3 6l5 4 4-6 4 6 5-4-2 10zm0 3h14',
  raid: 'M12 3 2 20h20zm0 6v5m0 3h.01',
  save: 'M5 3h11l3 3v15H5zm3 0v5h7V3M8 21v-7h8v7',
  server: 'M4 4h16v6H4zm0 10h16v6H4zm3-7h.01M7 17h.01',
  crash: 'M12 3 2 20h20zm0 6v5m0 3h.01',
  restore: 'M3 12a9 9 0 1 0 3-6.7L3 8m0-5v5h5',
  update: 'M12 3v12m0 0 4-4m-4 4-4-4M4 17v3h16v-3',
  config: 'M4 4h10l6 6v10H4zm10 0v6h6',
  globalkey: 'M15 7a4 4 0 1 1-3.9 5H3v3h3v2h3v-2h2.1A4 4 0 0 1 15 7',
};
function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.8');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  const p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  p.setAttribute('d', ICONS[name] || ICONS.server);
  svg.append(p);
  return svg;
}

// ---------------------------------------------------------------- dialogs

function openDialog(build) {
  const dlg = $('#dialog');
  return new Promise(resolve => {
    const done = value => { dlg.close(); resolve(value); };
    fill(dlg, build(done));
    dlg.onclose = () => resolve(undefined);
    dlg.showModal();
  });
}

function confirmDialog(title, body, okText = t('btn.ok'), danger = false) {
  return openDialog(done => h('form', { method: 'dialog', onsubmit: e => { e.preventDefault(); done(true); } },
    h('h2', { text: title }),
    body ? (body instanceof Node ? body : h('p', { text: body })) : null,
    h('div', { class: 'actions' },
      h('button', { type: 'button', class: 'btn', text: t('btn.cancel'), onclick: () => done(false) }),
      h('button', { type: 'submit', class: 'btn ' + (danger ? 'danger solid' : 'primary'), text: okText }))));
}

/** fields: [{name, label, value, type, list, required}] -> values or undefined */
function formDialog(title, fields, okText = t('btn.ok')) {
  return openDialog(done => {
    const inputs = {};
    const form = h('form', {
      onsubmit: e => {
        e.preventDefault();
        const values = {};
        for (const f of fields) values[f.name] = f.type === 'checkbox' ? inputs[f.name].checked : inputs[f.name].value.trim();
        done(values);
      },
    }, h('h2', { text: title }));
    for (const f of fields) {
      const input = f.type === 'select'
        ? h('select', {}, f.options.map(([v, l]) => h('option', { value: v, text: l, selected: v === f.value })))
        : h('input', { type: f.type || 'text', value: f.value ?? '', list: f.list, required: f.required, min: f.min, placeholder: f.placeholder, checked: f.type === 'checkbox' && f.value });
      inputs[f.name] = input;
      form.append(f.type === 'checkbox' ? h('label', { class: 'check' }, input, f.label) : h('label', {}, f.label, input));
      if (f.note) form.append(h('p', { class: 'note', text: f.note }));
    }
    form.append(h('div', { class: 'actions' },
      h('button', { type: 'button', class: 'btn', text: t('btn.cancel'), onclick: () => done(undefined) }),
      h('button', { type: 'submit', class: 'btn primary', text: okText })));
    setTimeout(() => form.querySelector('input,select')?.focus());
    return form;
  });
}

// ---------------------------------------------------------------- shared state

const state = {
  status: null,
  items: null,
  logs: [],
  logSeq: 0,
  gameCommands: null,
  admin: false,
  info: null,
};
const liveHandlers = new Set();

async function loadItems() {
  if (state.items?.length) return state.items;
  try { state.items = await api('/items'); } catch { state.items = []; }
  let dl = $('#dl-items');
  if (!dl) { dl = h('datalist', { id: 'dl-items' }); document.body.append(dl); }
  fill(dl, ...state.items.map(i => h('option', { value: i.prefab, text: i.token })));
  return state.items;
}

function playersDatalist() {
  let dl = $('#dl-players');
  if (!dl) { dl = h('datalist', { id: 'dl-players' }); document.body.append(dl); }
  fill(dl, ...(state.status?.players || []).map(p => h('option', { value: p.player })));
}

/** Game console commands registered on the server (vanilla and mods); loaded once per page. */
async function loadGameCommands() {
  if (state.gameCommands?.length) return state.gameCommands;
  try { state.gameCommands = await api('/console/commands'); } catch { state.gameCommands = []; }
  return state.gameCommands;
}

/**
 * Sends a console line. A cheat on a player whose character is not marked as cheated comes back
 * as needsConfirm: the admin decides, and the line is sent again with confirmCheats.
 */
async function runConsole(line) {
  let res = await api('/console', { method: 'POST', body: { line } });
  if (res.result?.needsConfirm) {
    const ok = await confirmDialog(t('con.cheatTitle'), t('con.cheatConfirm', res.player || '?', res.result.command), t('con.cheatRun'), true);
    if (!ok) return { declined: true };
    res = await api('/console', { method: 'POST', body: { line, confirmCheats: true } });
  }
  return res;
}

/** Text lines for a console answer. */
function consoleLines(res) {
  if (res.declined) return [t('con.cheatDeclined')];
  const r = res.result, out = [];
  if (r?.ranOnServer) out.push(t('con.ranOnServer'));
  if (Array.isArray(r?.output)) out.push(...(r.output.some(o => o.trim()) ? r.output : [t('con.noOutput')]));
  else if (r?.saving) out.push(t('con.saving'));
  else if (r?.recipients != null) out.push(t('con.sentTo', r.recipients));
  else if (Array.isArray(r)) out.push(r.length ? r.map(x => typeof x === 'string' ? x : JSON.stringify(x)).join('\n') : t('con.empty'));
  else out.push(r == null ? 'OK' : JSON.stringify(r, null, 2));
  return out;
}

const CHEATS = ['god', 'ghost', 'debugmode', 'fly', 'freefly', 'nocost', 'heal', 'puke', 'tame', 'killall', 'killenemies', 'removedrops',
  'exploremap', 'resetmap', 'pos', 'goto ', 'spawn ', 'raiseskill ', 'resetskill ', 'resetcharacter', 'location ', 'event ', 'stopevent', 'skiptime ', 'wind '];

// ---------------------------------------------------------------- live connection

let socket, reconnectTimer;
const hasFeature = name => (state.info?.features || []).includes(name);

function dispatchLive(msg) {
  if (msg.type === 'status') { state.status = msg.payload; renderTopbar(); }
  if (msg.type === 'log') pushLog(msg.payload);
  for (const fn of liveHandlers) { try { fn(msg); } catch (err) { console.error(err); } }
}

/** Live updates: the agent pushes them over a WebSocket; the plugin's own server is polled. */
function connectLive() {
  clearTimeout(reconnectTimer);
  if (!hasFeature('ws')) { pollLive(); return; }
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  socket = new WebSocket(`${proto}://${location.host}/ws`);
  socket.onopen = () => $('#live-indicator').classList.add('on');
  socket.onclose = () => {
    $('#live-indicator').classList.remove('on');
    if (state.admin) reconnectTimer = setTimeout(connectLive, 3000);
  };
  socket.onmessage = e => dispatchLive(JSON.parse(e.data));
}

const poll = { players: null, eventId: null };
async function pollLive() {
  clearTimeout(reconnectTimer);
  if (!state.admin) return;
  try {
    const status = await api('/status');
    dispatchLive({ type: 'status', payload: status });
    const names = (status.players || []).map(p => p.player).sort().join(',');
    if (poll.players !== null && names !== poll.players) dispatchLive({ type: 'players', payload: {} });
    poll.players = names;
    for (const line of await api('/logs?limit=1000&after=' + state.logSeq)) dispatchLive({ type: 'log', payload: line });
    const events = await api('/events?limit=20');
    if (poll.eventId !== null)
      for (const ev of events.filter(e => e.id > poll.eventId).reverse()) dispatchLive({ type: 'event', payload: ev });
    if (events.length) poll.eventId = Math.max(poll.eventId ?? 0, ...events.map(e => e.id));
    $('#live-indicator').classList.add('on');
  } catch {
    $('#live-indicator').classList.remove('on');
  }
  if (state.admin) reconnectTimer = setTimeout(pollLive, 3000);
}

function pushLog(line) {
  if (line.seq <= state.logSeq) return;
  state.logSeq = line.seq;
  state.logs.push(line);
  if (state.logs.length > 5000) state.logs.splice(0, state.logs.length - 5000);
}

function stateLabel(s) { return t('state.' + (s || 'Stopped')); }

function renderTopbar() {
  const st = state.status;
  if (!st) return;
  const s = st.server;
  $('#server-pill').className = 'server-pill st-' + s.state;
  $('#server-state').textContent = stateLabel(s.state) + (s.operation ? ' · ' + t('op.' + s.operation) : '');
  const stats = s.stats || {};
  fill($('#topbar-stats'),
    s.pid ? h('span', {}, t('stat.uptime') + ' ', h('b', { text: fmtDuration(s.uptimeSec) })) : null,
    h('span', {}, t('stat.players') + ' ', h('b', { text: String((st.players || []).length) })),
    s.cpuPercent != null ? h('span', {}, 'CPU ', h('b', { text: s.cpuPercent + '%' })) : null,
    s.memoryMb != null ? h('span', {}, 'RAM ', h('b', { text: s.memoryMb + ' MB' })) : null,
    stats.fps != null ? h('span', {}, 'FPS ', h('b', { text: String(stats.fps) })) : null,
    !s.pluginConnected && s.pid ? h('span', { class: 'error', text: t('stat.noplugin') }) : null,
  );
}

// ---------------------------------------------------------------- views

const views = {};
let current = null;

// ---- overview
views.overview = {
  icon: 'overview',
  mount(root) {
    const statsEl = h('div', { class: 'stats' });
    const controls = h('div', { class: 'row' });
    const restartEl = h('div', { class: 'row' });
    const playersEl = h('div');
    const eventsEl = h('div', { class: 'ev-list' });

    const act = async (action, confirmText, body) => {
      if (confirmText && !await confirmDialog(confirmText, null, t('btn.yes'), action === 'kill')) return;
      try { await api('/server/' + action, { method: 'POST', body }); toast(t('toast.sent'), 'ok'); } catch (e) { fail(e); }
    };

    root.append(
      h('div', { class: 'card' }, h('h2', { text: t('ov.server') }), statsEl, h('div', { style: 'height:12px' }), controls,
        h('div', { style: 'height:12px' }), restartEl),
      h('div', { class: 'grid grid-2' },
        h('div', { class: 'card' }, h('h2', { text: t('ov.online') }), playersEl),
        h('div', { class: 'card' }, h('div', { class: 'row' }, h('h2', { text: t('ov.recent') }), h('span', { class: 'spacer' }),
          h('a', { href: '#events', class: 'small', text: t('ov.allEvents') })), eventsEl)));

    const render = () => {
      const st = state.status;
      if (!st) return;
      const s = st.server, x = s.stats || {};
      const stat = (label, value) => h('div', { class: 'stat' }, h('div', { class: 'label', text: label }), h('div', { class: 'value', text: value ?? '—' }));
      fill(statsEl,
        stat(t('ov.state'), stateLabel(s.state)),
        stat(t('stat.uptime'), s.pid ? fmtDuration(s.uptimeSec) : '—'),
        stat('CPU', s.cpuPercent != null ? s.cpuPercent + '%' : '—'),
        stat('RAM', s.memoryMb != null ? s.memoryMb + ' MB' : '—'),
        stat('FPS', x.fps),
        stat(t('ov.day'), x.day),
        stat(t('ov.objects'), x.zdos?.toLocaleString()),
        stat(t('ov.event'), x.event || '—'),
        stat(t('ov.plugin'), s.pluginConnected ? 'v' + s.pluginVersion : t('ov.disconnected')),
        stat(t('ov.build'), st.update?.buildId),
      );
      const running = !!s.pid, busy = !!s.operation, control = hasFeature('server-control');
      // Without the agent (standalone mode) the game cannot start or stop itself; the host's panel does that.
      fill(controls,
        control ? h('button', { class: 'btn primary', text: t('btn.start'), disabled: running || busy, onclick: () => act('start') }) : null,
        control ? h('button', { class: 'btn', text: t('btn.stop'), disabled: !running || busy, onclick: () => act('stop', t('confirm.stop')) }) : null,
        control ? h('button', { class: 'btn', text: t('btn.restart'), disabled: !running || busy, onclick: () => act('restart', t('confirm.restart')) }) : null,
        h('button', { class: 'btn', text: t('btn.save'), disabled: !s.pluginConnected, onclick: () => act('save') }),
        h('span', { class: 'spacer' }),
        control ? h('button', { class: 'btn danger', text: t('btn.kill'), onclick: () => act('kill', t('confirm.kill')) }) : h('span', { class: 'muted small', text: t('ov.standalone') }),
      );
      restartEl.hidden = !control;
      const nr = st.nextRestart;
      fill(restartEl,
        h('span', { class: 'muted', text: t('ov.nextRestart') + ': ' }),
        h('b', { text: nr ? `${fmtDate(Date.parse(nr.at))} (${nr.reason})` : '—' }),
        nr ? h('button', { class: 'btn small', text: t('btn.cancelRestart'), onclick: () => act('cancel-restart') }) : null,
        h('span', { class: 'spacer' }),
        h('button', {
          class: 'btn small', text: t('btn.restartIn'), disabled: !running, onclick: async () => {
            const v = await formDialog(t('btn.restartIn'), [{ name: 'minutes', label: t('f.minutes'), type: 'number', value: 10, min: 0 }]);
            if (v) act('restart-in', null, { minutes: Number(v.minutes) });
          },
        }),
      );
      const players = st.players || [];
      fill(playersEl, players.length === 0 ? h('p', { class: 'muted', text: t('ov.nobody') }) :
        h('table', {}, h('tbody', {}, players.map(p => h('tr', {},
          h('td', {}, h('b', { text: p.player })),
          h('td', { class: 'muted small', text: p.ping >= 0 ? p.ping + ' ms' : '' }),
          h('td', {}, p.mod ? h('span', { class: 'badge ok', text: 'mod ' + p.mod }) : h('span', { class: 'badge', text: t('pl.nomod') })))))));
    };

    const loadEvents = async () => {
      try {
        const list = await api('/events?limit=12');
        fill(eventsEl, list.map(eventRow));
      } catch (e) { fail(e); }
    };

    render();
    loadEvents();
    return msg => {
      if (msg.type === 'status') render();
      if (msg.type === 'event') eventsEl.prepend(eventRow(msg.payload));
      while (eventsEl.children.length > 12) eventsEl.lastChild.remove();
    };
  },
};

function eventRow(ev) {
  return h('div', { class: 'ev k-' + ev.kind },
    h('span', { class: 'time', text: fmtShort(ev.ts) }),
    icon(ev.kind),
    h('span', { text: ev.message }));
}

// ---- console
views.console = {
  icon: 'console',
  mount(root) {
    let filter = 'all', search = '', follow = true;
    const logEl = h('div', { class: 'log' });
    const input = h('input', { placeholder: t('con.placeholder'), autocomplete: 'off', list: 'dl-console', spellcheck: 'false' });
    const history = JSON.parse(localStorage.getItem('va_history') || '[]');
    let hIndex = history.length;

    const matches = l => (filter === 'all' || (filter === 'warn' ? l.level === 'warning' || l.level === 'error' : l.level === 'error') || l.level === 'cmd' || l.level === 'result')
      && (!search || l.text.toLowerCase().includes(search));
    const lineEl = l => h('div', { class: 'l-' + l.level }, h('span', { class: 'ts', text: fmtTime(Date.parse(l.ts)) }), l.text);
    const renderAll = () => {
      fill(logEl, ...state.logs.filter(matches).slice(-3000).map(lineEl));
      if (follow) logEl.scrollTop = logEl.scrollHeight;
    };
    const addLocal = (text, level) => {
      const l = { seq: 0, ts: new Date().toISOString(), level, text };
      state.logs.push(l);
      logEl.append(lineEl(l));
      logEl.scrollTop = logEl.scrollHeight;
    };
    logEl.addEventListener('scroll', () => { follow = logEl.scrollTop + logEl.clientHeight >= logEl.scrollHeight - 30; });

    const dl = h('datalist', { id: 'dl-console' });
    const refreshSuggestions = () => {
      const players = (state.status?.players || []).map(p => p.player);
      const panel = ['help', 'players', 'save', 'say ', 'kick ', 'ban ', 'unban ', 'give ', 'snapshot', 'keys', 'setkey ', 'removekey ', 'sleep', 'events', 'event ', 'stopevent', 'admins', 'bans', 'permits', 'admin add ', 'permit add ', 'plugins', 'status'];
      const game = (state.gameCommands || []).map(c => c.name).filter(n => !panel.includes(n) && !panel.includes(n + ' '));
      fill(dl,
        ...panel.map(c => h('option', { value: c })),
        ...game.map(c => h('option', { value: c })),
        ...players.flatMap(p => CHEATS.slice(0, 12).map(c => h('option', { value: `@${p} ${c}` }))));
    };

    const run = async () => {
      const line = input.value.trim();
      if (!line) return;
      if (history[history.length - 1] !== line) history.push(line);
      localStorage.setItem('va_history', JSON.stringify(history.slice(-100)));
      hIndex = history.length;
      input.value = '';
      addLocal('» ' + line, 'cmd');
      try {
        const res = await runConsole(line);
        if (res.help) {
          addLocal(t('con.help'), 'result');
          const game = await loadGameCommands();
          if (game.length) addLocal(t('con.gameCommands', game.length) + '\n' + game.map(c => `  ${c.name}${c.description ? ' — ' + c.description : ''}`).join('\n'), 'result');
          return;
        }
        consoleLines(res).forEach(o => addLocal(o, 'result'));
      } catch (e) {
        addLocal(e.message, 'error');
      }
    };
    input.addEventListener('keydown', e => {
      if (e.key === 'Enter') run();
      else if (e.key === 'ArrowUp' && hIndex > 0) { input.value = history[--hIndex]; e.preventDefault(); }
      else if (e.key === 'ArrowDown') { hIndex = Math.min(history.length, hIndex + 1); input.value = history[hIndex] || ''; e.preventDefault(); }
    });

    const chips = h('div', { class: 'chips' }, ['all', 'warn', 'error'].map(f =>
      h('button', { class: 'chip' + (f === filter ? ' on' : ''), text: t('con.f.' + f), onclick: e => {
        filter = f; chips.querySelectorAll('.chip').forEach(c => c.classList.toggle('on', c === e.target)); renderAll();
      } })));
    const searchEl = h('input', { placeholder: t('con.search'), oninput: e => { search = e.target.value.toLowerCase(); renderAll(); } });

    root.append(dl, h('div', { class: 'card' },
      h('div', { class: 'row', style: 'margin-bottom:10px' }, chips, h('span', { class: 'spacer' }), searchEl,
        h('button', { class: 'btn small', text: t('con.bottom'), onclick: () => { follow = true; logEl.scrollTop = logEl.scrollHeight; } })),
      logEl,
      h('div', { class: 'console-input', style: 'margin-top:10px' }, input, h('button', { class: 'btn primary', text: t('btn.run'), onclick: run })),
      h('p', { class: 'note', text: t('con.hint') })));

    (async () => {
      try {
        const lines = await api('/logs?limit=3000');
        lines.forEach(pushLog);
      } catch (e) { fail(e); }
      renderAll();
      refreshSuggestions();
      input.focus();
      await loadGameCommands();
      refreshSuggestions();
    })();

    return msg => {
      if (msg.type === 'log' && matches(msg.payload)) {
        logEl.append(lineEl(msg.payload));
        while (logEl.childElementCount > 3000) logEl.firstChild.remove();
        if (follow) logEl.scrollTop = logEl.scrollHeight;
      }
      if (msg.type === 'players') refreshSuggestions();
    };
  },
};

// ---- events
const EVENT_KINDS = ['join', 'leave', 'death', 'chat', 'boss', 'raid', 'save', 'server', 'crash', 'restore', 'globalkey', 'update', 'config'];
views.events = {
  icon: 'events',
  mount(root) {
    const kinds = new Set();
    let player = '', q = '', oldest = null;
    const listEl = h('div', { class: 'ev-list' });
    const more = h('button', { class: 'btn', text: t('ev.more'), onclick: () => load(false) });

    const query = () => {
      const p = new URLSearchParams({ limit: 200 });
      if (kinds.size) p.set('kind', [...kinds].join(','));
      if (player) p.set('player', player);
      if (q) p.set('q', q);
      return p;
    };
    const load = async reset => {
      const p = query();
      if (!reset && oldest) p.set('before', oldest);
      try {
        const list = await api('/events?' + p);
        if (reset) clear(listEl);
        listEl.append(...list.map(eventRow));
        oldest = list.length ? list[list.length - 1].id : oldest;
        more.hidden = list.length < 200;
      } catch (e) { fail(e); }
    };
    const matches = ev => (!kinds.size || kinds.has(ev.kind)) && (!player || (ev.player || '').toLowerCase() === player.toLowerCase())
      && (!q || ev.message.toLowerCase().includes(q.toLowerCase()));

    let debounce;
    const onInput = () => { clearTimeout(debounce); debounce = setTimeout(() => load(true), 300); };
    root.append(h('div', { class: 'card' },
      h('div', { class: 'chips', style: 'margin-bottom:10px' }, EVENT_KINDS.map(k => h('button', {
        class: 'chip', text: t('kind.' + k), onclick: e => {
          kinds.has(k) ? kinds.delete(k) : kinds.add(k);
          e.target.classList.toggle('on');
          load(true);
        },
      }))),
      h('div', { class: 'row', style: 'margin-bottom:10px' },
        h('input', { placeholder: t('ev.player'), list: 'dl-players', oninput: e => { player = e.target.value.trim(); onInput(); } }),
        h('input', { placeholder: t('ev.search'), style: 'flex:1', oninput: e => { q = e.target.value.trim(); onInput(); } })),
      listEl, h('div', { class: 'row', style: 'margin-top:10px' }, more)));
    playersDatalist();
    load(true);
    return msg => { if (msg.type === 'event' && matches(msg.payload)) listEl.prepend(eventRow(msg.payload)); };
  },
};

// ---- players
views.players = {
  icon: 'players',
  mount(root) {
    const onlineEl = h('div', { class: 'table-wrap' });
    const historyEl = h('div', { class: 'table-wrap' });
    const listsEl = h('div', { class: 'grid grid-3' });

    const action = async (a, player) => {
      if (!await confirmDialog(t('pl.confirm.' + a, player), null, t('btn.yes'), a !== 'unban')) return;
      try { await api('/players/action', { method: 'POST', body: { action: a, player } }); toast(t('toast.done'), 'ok'); loadOnline(); } catch (e) { fail(e); }
    };
    const give = async player => {
      await loadItems();
      const v = await formDialog(t('pl.give') + ': ' + player, [
        { name: 'prefab', label: t('f.item'), list: 'dl-items', required: true, placeholder: 'Wood' },
        { name: 'count', label: t('f.count'), type: 'number', value: 1, min: 1 },
        { name: 'quality', label: t('f.quality'), type: 'number', value: 1, min: 1 },
      ], t('pl.give'));
      if (!v) return;
      try {
        await api('/console', { method: 'POST', body: { line: `give "${player}" ${v.prefab} ${v.count || 1} ${v.quality || 1}` } });
        toast(t('toast.done'), 'ok');
      } catch (e) { fail(e); }
    };
    const cheat = async player => {
      let dl = $('#dl-cheats');
      if (!dl) { dl = h('datalist', { id: 'dl-cheats' }, CHEATS.map(c => h('option', { value: c }))); document.body.append(dl); }
      const v = await formDialog(t('pl.cheat') + ': ' + player, [
        { name: 'command', label: t('f.command'), list: 'dl-cheats', required: true, placeholder: 'god', note: t('pl.cheatNote') },
      ], t('btn.run'));
      if (!v) return;
      try {
        const res = await runConsole(`@"${player}" ${v.command}`);
        toast(consoleLines(res).join('\n'), res.declined ? 'err' : 'ok');
      } catch (e) { fail(e); }
    };
    const snapshot = async player => {
      try { await api('/snapshots/take', { method: 'POST', body: { player } }); toast(t('toast.snapshot'), 'ok'); } catch (e) { fail(e); }
    };

    const loadOnline = async () => {
      let list = [];
      try { list = await api('/players/online'); } catch (e) { fail(e); }
      fill(onlineEl, list.length === 0 ? h('p', { class: 'muted', text: t('ov.nobody') }) : h('table', {},
        h('thead', {}, h('tr', {}, [t('pl.name'), 'ID', t('pl.ping'), t('pl.pos'), t('pl.mod'), ''].map(x => h('th', { text: x })))),
        h('tbody', {}, list.map(p => h('tr', {},
          h('td', {}, h('b', { text: p.player }), p.admin ? h('span', { class: 'badge warn', style: 'margin-left:6px', text: 'admin' }) : null),
          h('td', { class: 'mono small', text: p.host }),
          h('td', { class: 'num', text: p.ping >= 0 ? p.ping + ' ms' : '—' }),
          h('td', { class: 'mono small', text: (p.pos || []).join(', ') }),
          h('td', {}, p.mod ? h('span', { class: 'badge ok', text: p.mod }) : h('span', { class: 'badge', text: t('pl.nomod') })),
          h('td', {}, h('div', { class: 'row' },
            h('button', { class: 'btn small', text: t('pl.give'), onclick: () => give(p.player) }),
            h('button', { class: 'btn small', text: t('pl.cheat'), disabled: !p.mod, onclick: () => cheat(p.player) }),
            h('button', { class: 'btn small', text: t('pl.snapshot'), disabled: !p.mod, onclick: () => snapshot(p.player) }),
            h('button', { class: 'btn small danger', text: t('pl.kick'), onclick: () => action('kick', p.player) }),
            h('button', { class: 'btn small danger', text: t('pl.ban'), onclick: () => action('ban', p.host || p.player) }))))))));
    };

    const loadHistory = async () => {
      try {
        const list = await api('/players/history');
        fill(historyEl, h('table', {},
          h('thead', {}, h('tr', {}, [t('pl.name'), 'ID', t('pl.sessions'), t('pl.played'), t('pl.lastSeen'), t('pl.deaths')].map(x => h('th', { text: x })))),
          h('tbody', {}, list.map(p => h('tr', { class: 'clickable', onclick: () => sessions(p.player) },
            h('td', {}, p.online ? h('span', { class: 'badge ok', style: 'margin-right:6px', text: '●' }) : null, p.player),
            h('td', { class: 'mono small', text: p.host || '' }),
            h('td', { class: 'num', text: p.sessions }),
            h('td', { class: 'num', text: fmtDuration(p.playedMs / 1000) }),
            h('td', { text: p.online ? t('pl.now') : fmtDate(p.lastSeen) }),
            h('td', { class: 'num', text: p.deaths }))))));
      } catch (e) { fail(e); }
    };
    const sessions = async name => {
      const list = await api('/players/' + encodeURIComponent(name) + '/sessions');
      openDialog(done => h('div', {},
        h('h2', { text: t('pl.sessions') + ': ' + name }),
        h('div', { class: 'table-wrap', style: 'max-height:60vh;overflow:auto' }, h('table', {}, h('tbody', {}, list.map(s => h('tr', {},
          h('td', { text: fmtDate(s.start) }), h('td', { text: s.end ? fmtDate(s.end) : t('pl.now') }),
          h('td', { class: 'num', text: fmtDuration(((s.end || Date.now()) - s.start) / 1000) })))))),
        h('div', { class: 'actions' }, h('button', { class: 'btn', text: t('btn.close'), onclick: () => done() }))));
    };

    const listCard = name => {
      const body = h('div');
      const load = async () => {
        try {
          const items = await api('/lists/' + name);
          fill(body, items.length === 0 ? h('p', { class: 'muted', text: t('pl.listEmpty') }) :
            h('table', {}, h('tbody', {}, items.map(v => h('tr', {}, h('td', { class: 'mono small', text: v }),
              h('td', {}, h('button', { class: 'btn small danger', text: '✕', title: t('btn.remove'), onclick: () => change('remove', v) })))))));
        } catch (e) { fail(e); }
      };
      const change = async (op, value) => {
        try { await api('/lists/' + name, { method: 'POST', body: { op, value } }); load(); } catch (e) { fail(e); }
      };
      const input = h('input', { placeholder: t('pl.idPlaceholder'), style: 'flex:1' });
      load();
      return h('div', { class: 'card' }, h('h3', { text: t('list.' + name) }), body,
        h('div', { class: 'row', style: 'margin-top:8px' }, input,
          h('button', { class: 'btn small', text: t('btn.add'), onclick: () => { if (input.value.trim()) { change('add', input.value.trim()); input.value = ''; } } })));
    };

    root.append(
      h('div', { class: 'card' }, h('h2', { text: t('ov.online') }), onlineEl),
      h('div', { class: 'card' }, h('h2', { text: t('pl.history') }), historyEl),
      listsEl);
    listsEl.append(listCard('admin'), listCard('banned'), listCard('permitted'));
    loadOnline();
    loadHistory();
    return msg => { if (msg.type === 'players') { loadOnline(); loadHistory(); } };
  },
};

// ---- characters & snapshots
views.characters = {
  icon: 'characters',
  mount(root) {
    let chars = [], charId = null, snaps = [], snapId = null, snap = null, diff = null, focus = null;
    const picked = new Map(); // item index -> stack or null
    const charsEl = h('div', { class: 'list' });
    const snapsEl = h('div', { class: 'list' });
    const detailEl = h('div', { class: 'card' }, h('p', { class: 'muted', text: t('ch.pick') }));

    const loadChars = async () => {
      try { chars = await api('/characters'); } catch (e) { fail(e); }
      fill(charsEl, chars.length === 0 ? h('p', { class: 'muted', text: t('ch.none') }) : chars.map(c => h('div', {
        class: 'list-item' + (c.characterId === charId ? ' active' : ''), onclick: () => selectChar(c.characterId),
      }, h('div', {}, c.online ? h('span', { class: 't-Legendary', text: '● ' }) : null, h('b', { text: c.player })),
        h('div', { class: 'muted small', text: `${c.count} · ${fmtShort(c.lastTs)}` }))));
    };
    const selectChar = async id => {
      charId = id;
      loadChars();
      try { snaps = await api(`/characters/${id}/snapshots`); } catch (e) { fail(e); return; }
      renderSnaps();
      if (snaps.length) selectSnap(snaps[0].id);
    };
    const renderSnaps = () => {
      fill(snapsEl, snaps.map(s => {
        const magic = s.summary?.magic || {};
        return h('div', { class: 'list-item' + (s.id === snapId ? ' active' : ''), onclick: () => selectSnap(s.id) },
          h('div', { class: 'row' }, h('b', { text: fmtDate(s.ts) }), h('span', { class: 'spacer' }),
            h('span', { class: 'badge', text: t('trig.' + s.trigger) }), s.unchanged ? h('span', { class: 'badge', text: '=' , title: t('ch.unchanged') }) : null),
          h('div', { class: 'small muted' }, `${t('ch.items')}: ${s.summary?.items ?? '?'} `,
            Object.entries(magic).map(([r, n]) => h('span', { class: 't-' + r, text: ` ◆${n}`, title: t('rarity.' + r) }))));
      }));
    };
    const selectSnap = async id => {
      snapId = id; diff = null; focus = null; picked.clear();
      renderSnaps();
      try { snap = await api('/snapshots/' + id); } catch (e) { fail(e); return; }
      renderDetail();
    };

    const itemsOf = () => snap?.content?.items || [];
    const diffOf = idx => diff?.items.find(d => d.index === idx);
    const missingOf = idx => diffOf(idx)?.missing;
    /** Localized skill name from the game; the panel dictionary or the raw name when the game had none. */
    const skillName = s => s.displayName && !/^[[$]/.test(s.displayName) ? s.displayName
      : t('skill.' + s.name) === 'skill.' + s.name ? s.name : t('skill.' + s.name);

    const renderDetail = () => {
      if (!snap) return;
      const c = snap.content, row = snap.row;
      const w = c.inventory?.w || 8, hgt = c.inventory?.h || 4;
      const items = itemsOf();
      // Items of inventories that mods add to the player (see ExtraInventories) get their own grids.
      const main = [], byContainer = new Map();
      items.forEach((it, idx) => {
        if (!it.container) { main.push(idx); return; }
        if (!byContainer.has(it.container)) byContainer.set(it.container, []);
        byContainer.get(it.container).push(idx);
      });

      const slot = idx => {
        if (idx == null) return h('div', { class: 'slot empty' });
        const it = items[idx];
        const d = diffOf(idx), miss = d?.missing;
        const rarity = it.magic?.rarityName;
        const cls = ['slot', rarity ? 'r-' + rarity : '', miss ? 'missing' : '', d?.similar ? 'similar' : '', picked.has(idx) ? 'picked' : '', focus === idx ? 'focus' : ''].join(' ');
        const cb = h('input', { type: 'checkbox', class: 'pick', checked: picked.has(idx), title: t('ch.select'), onclick: e => {
          e.stopPropagation();
          e.target.checked ? picked.set(idx, miss ?? null) : picked.delete(idx);
          renderDetail();
        } });
        return h('div', { class: cls, title: it.label + (d?.similar ? '\n' + t('ch.similar') : ''), onclick: () => { focus = idx; renderDetail(); } },
          it.quality > 1 ? h('span', { class: 'q', text: '★' + it.quality }) : null,
          it.cheated ? h('span', { class: 'cheat', text: 'C', title: t('ch.cheated') }) : null,
          it.equipped ? h('span', { class: 'eq', text: 'E' }) : null,
          // Icons are rendered by a player's game; until one is stored the name stands in.
          h('img', { class: 'ico', alt: '', loading: 'lazy', src: `/api/icons/${encodeURIComponent(it.prefab)}/${it.variant || 0}`,
            onload: e => e.target.parentElement?.classList.add('has-ico'), onerror: e => e.target.remove() }),
          h('span', { class: 'name ' + (rarity ? 't-' + rarity : ''), text: shortLabel(it.magic?.displayName || it.label || it.prefab) }),
          it.stack > 1 ? h('span', { class: 'stack', text: it.stack }) : null,
          miss ? h('span', { class: 'miss', text: '−' + miss }) : null,
          cb);
      };

      const gridFor = (indices, gw, gh) => {
        const byPos = new Map(), overflow = [];
        indices.forEach(idx => {
          const it = items[idx], key = it.y * gw + it.x;
          if (it.x < gw && it.y < gh && !byPos.has(key)) byPos.set(key, idx); else overflow.push(idx);
        });
        const g = h('div', { class: 'inv', style: `--w:${gw}` });
        for (let y = 0; y < gh; y++) for (let x = 0; x < gw; x++) g.append(slot(byPos.get(y * gw + x)));
        return [g, overflow.length ? h('div', { class: 'inv', style: `--w:${gw};margin-top:8px` }, overflow.map(slot)) : null];
      };
      const [grid, extra] = gridFor(main, w, hgt);
      const containerGrids = [...byContainer].map(([key, idxs]) => {
        const meta = (c.containers || []).find(x => x.key === key) || {};
        const cw = meta.w || w, chh = meta.h || Math.max(1, Math.ceil(idxs.length / cw));
        return [h('h4', { class: 'muted small', style: 'margin:12px 0 6px', text: t('ch.container', key.split('.').slice(-2).join('.')), title: key }), ...gridFor(idxs, cw, chh)];
      });

      const fi = focus != null ? items[focus] : null;
      const tip = h('div', { class: 'tooltip' });
      if (fi) {
        tip.append(h('b', { class: fi.magic ? 't-' + fi.magic.rarityName : '', text: fi.magic?.displayName || fi.label || fi.prefab }), '\n');
        tip.append(richText(fi.tooltip || ''));
        tip.append('\n\n', h('span', { class: 'muted', text: `${fi.prefab} · ${t('f.quality')} ${fi.quality} · ${t('ch.durability')} ${Math.round(fi.durability)}/${Math.round(fi.maxDurability)}` +
          (fi.crafterName ? ` · ${t('ch.crafter')} ${fi.crafterName}` : '') }));
        if (fi.container) tip.append('\n', h('span', { class: 'muted', text: t('ch.container', fi.container) }));
        if (fi.cheated) tip.append('\n', h('span', { class: 'error', text: t('ch.cheated') }));
        const keys = Object.keys(fi.data || {});
        if (keys.length) tip.append('\n', h('span', { class: 'muted', text: t('ch.dataKeys') + ': ' + keys.join(', ') }));
        if (fi.magic) {
          tip.append('\n', h('span', { class: 't-' + fi.magic.rarityName, text: t('rarity.' + fi.magic.rarityName) + (fi.magic.setId ? ' · set ' + fi.magic.setId : '') }));
          fi.magic.effects.forEach(e => tip.append('\n', h('span', { class: 'muted', text: `  ${e.type} ${Math.round(e.value * 100) / 100}` })));
        }
      } else tip.append(h('span', { class: 'muted', text: t('ch.clickItem') }));

      const liveSkills = new Map((diff?.skills || []).map(s => [s.type, s]));
      const skills = (c.skills || []).slice().sort((a, b) => b.level - a.level);
      const skillsTable = h('table', {}, h('tbody', {}, skills.map(s => {
        const d = liveSkills.get(s.type);
        return h('tr', {}, h('td', { text: skillName(s) }),
          h('td', { class: 'num', text: s.level.toFixed(1) }),
          h('td', { class: 'num error', text: d ? `${t('ch.now')} ${d.currentLevel.toFixed(1)}` : '' }));
      })));

      const modeSel = h('select', {}, h('option', { value: 'add', text: t('ch.mode.add') }), h('option', { value: 'replace', text: t('ch.mode.replace') }));
      const skillSel = h('select', {}, h('option', { value: 'none', text: t('ch.skills.none') }), h('option', { value: 'raise', text: t('ch.skills.raise') }), h('option', { value: 'set', text: t('ch.skills.set') }));
      const pickMissing = () => { picked.clear(); (diff?.items || []).forEach(d => picked.set(d.index, d.missing)); renderDetail(); };
      const pickAll = () => { picked.clear(); items.forEach((_, i) => picked.set(i, null)); renderDetail(); };

      const restore = async () => {
        const mode = modeSel.value, skillMode = skillSel.value;
        const sel = mode === 'replace' ? null : [...picked].map(([index, stack]) => ({ index, stack }));
        if (mode === 'add' && sel.length === 0 && skillMode === 'none') { toast(t('ch.nothing'), 'err'); return; }
        const text = mode === 'replace' ? t('ch.confirmReplace', row.player, items.length) : t('ch.confirmAdd', row.player, sel.length);
        if (!await confirmDialog(t('ch.restore'), text + (skillMode !== 'none' ? '\n' + t('ch.confirmSkills.' + skillMode) : '') + '\n\n' + t('ch.safety'), t('ch.restore'), mode === 'replace')) return;
        try {
          const r = await api(`/snapshots/${snapId}/restore`, { method: 'POST', body: { mode, items: sel, skillMode } });
          const failed = (r.failed || []).map(f => `${f.prefab || f.skill}: ${f.reason}`);
          toast(t('ch.restored', r.added, r.dropped, r.skills) + (failed.length ? '\n' + t('ch.failed', failed.length) + '\n' + failed.slice(0, 6).join('\n') : ''), failed.length ? 'err' : 'ok');
          selectChar(charId);
        } catch (e) { fail(e); }
      };
      const compare = async () => {
        try {
          diff = await api(`/snapshots/${snapId}/diff`);
          pickMissing();
          toast(diff.items.length || diff.skills.length ? t('ch.diffFound', diff.items.length, diff.skills.length) : t('ch.diffNone'), 'ok');
        } catch (e) { fail(e); }
      };

      fill(detailEl,
        h('div', { class: 'row' }, h('h2', { style: 'margin:0', text: `${row.player} · ${fmtDate(row.ts)}` }), h('span', { class: 'badge', text: t('trig.' + row.trigger) }),
          h('span', { class: 'spacer' }), h('button', { class: 'btn', text: t('ch.compare'), onclick: compare, title: t('ch.compareHint') })),
        h('div', { style: 'height:12px' }),
        h('div', { class: 'grid grid-2' },
          h('div', {}, h('h3', { text: t('ch.inventory') }), grid, extra, containerGrids,
            h('div', { class: 'row small', style: 'margin-top:8px' },
              h('button', { class: 'btn small', text: t('ch.pickAll'), onclick: pickAll }),
              h('button', { class: 'btn small', text: t('ch.pickMissing'), disabled: !diff, onclick: pickMissing }),
              h('button', { class: 'btn small', text: t('ch.pickNone'), onclick: () => { picked.clear(); renderDetail(); } }),
              h('span', { class: 'muted', text: t('ch.selected', picked.size) }))),
          h('div', {}, h('h3', { text: t('ch.item') }), tip)),
        h('div', { style: 'height:16px' }),
        h('div', { class: 'card', style: 'background:var(--surface-2);box-shadow:none' },
          h('div', { class: 'restore-bar' },
            h('label', {}, t('ch.mode'), modeSel), h('label', {}, t('ch.skills'), skillSel),
            h('button', { class: 'btn primary', text: t('ch.restore') + '…', onclick: restore })),
          h('p', { class: 'note', text: t('ch.restoreNote') })),
        h('div', { style: 'height:16px' }),
        h('h3', { text: t('ch.skillsTitle') }), h('div', { class: 'table-wrap' }, skillsTable));
    };

    root.append(h('div', { class: 'chars' },
      h('div', { class: 'card' }, h('div', { class: 'row' }, h('h2', { style: 'margin:0', text: t('ch.characters') }), h('span', { class: 'spacer' }),
        h('button', { class: 'btn small', text: t('ch.snapAll'), onclick: async () => {
          try { await api('/snapshots/take', { method: 'POST', body: {} }); toast(t('toast.snapshot'), 'ok'); } catch (e) { fail(e); }
        } })), h('div', { style: 'height:8px' }), charsEl),
      h('div', { class: 'card' }, h('h2', { text: t('ch.snapshots') }), snapsEl),
      detailEl));
    loadChars().then(() => { if (chars.length) selectChar(chars[0].characterId); });
    return msg => {
      if (msg.type === 'snapshot') {
        loadChars();
        if (charId != null) api(`/characters/${charId}/snapshots`).then(s => { snaps = s; renderSnaps(); }).catch(() => {});
      }
    };
  },
};

function shortLabel(s) { return s && s.length > 22 ? s.slice(0, 21) + '…' : s; }

// ---- configs & plugins
views.configs = {
  icon: 'configs',
  mount(root) {
    let files = [], currentPath = null, original = '';
    const listEl = h('div', { class: 'list' });
    const editor = h('textarea', { class: 'editor', spellcheck: 'false', disabled: true });
    const title = h('b', { text: t('cfg.pick') });
    const saveBtn = h('button', { class: 'btn primary', text: t('btn.save'), disabled: true });
    const pluginsEl = h('div', { class: 'table-wrap' });
    const filter = h('input', { placeholder: t('cfg.filter'), oninput: () => renderList() });

    const renderList = () => {
      const f = filter.value.toLowerCase();
      fill(listEl, files.filter(x => x.path.toLowerCase().includes(f)).map(x => h('div', {
        class: 'list-item' + (x.path === currentPath ? ' active' : ''), onclick: () => open(x.path),
      }, h('span', { text: x.path }), h('span', { class: 'small muted', text: `${(x.size / 1024).toFixed(1)} KB · ${fmtShort(Date.parse(x.modified))}` }))));
    };
    const dirty = () => currentPath && editor.value !== original;
    const open = async path => {
      if (dirty() && !await confirmDialog(t('cfg.discard'), null, t('btn.yes'))) return;
      try {
        const r = await api('/configs/file?path=' + encodeURIComponent(path));
        currentPath = path; original = r.content; editor.value = r.content; editor.disabled = false;
        title.textContent = path; saveBtn.disabled = true;
        renderList();
      } catch (e) { fail(e); }
    };
    editor.addEventListener('input', () => { saveBtn.disabled = !dirty(); });
    saveBtn.onclick = async () => {
      try {
        await api('/configs/file', { method: 'PUT', body: { path: currentPath, content: editor.value } });
        original = editor.value; saveBtn.disabled = true;
        toast(t('cfg.saved'), 'ok');
      } catch (e) { fail(e); }
    };

    root.append(h('div', { class: 'configs' },
      h('div', { class: 'card' }, h('h2', { text: t('cfg.files') }), filter, h('div', { style: 'height:8px' }), listEl),
      h('div', { class: 'card' }, h('div', { class: 'row', style: 'margin-bottom:10px' }, title, h('span', { class: 'spacer' }), saveBtn),
        editor, h('p', { class: 'note', text: t('cfg.note') }))),
      h('div', { class: 'card' }, h('h2', { text: t('cfg.plugins') }), pluginsEl));

    (async () => {
      try { files = await api('/configs'); renderList(); } catch (e) { fail(e); }
      try {
        const p = await api('/plugins');
        const loaded = p.loaded || [];
        fill(pluginsEl,
          loaded.length ? h('div', {}, h('h3', { text: t('cfg.loaded') }), h('table', {},
            h('thead', {}, h('tr', {}, ['GUID', t('pl.name'), t('cfg.version'), t('cfg.file')].map(x => h('th', { text: x })))),
            h('tbody', {}, loaded.map(x => h('tr', {}, h('td', { class: 'mono small', text: x.guid }), h('td', { text: x.name }),
              h('td', { text: x.version }), h('td', { class: 'mono small', text: x.file })))))) : h('p', { class: 'muted', text: t('cfg.notLoaded') }),
          h('h3', { style: 'margin-top:16px', text: t('cfg.dlls') }), h('table', {},
            h('thead', {}, h('tr', {}, [t('cfg.file'), t('cfg.version'), t('cfg.size'), t('cfg.modified')].map(x => h('th', { text: x })))),
            h('tbody', {}, p.files.map(x => h('tr', {}, h('td', { class: 'mono small', text: x.path }), h('td', { text: x.version || '' }),
              h('td', { class: 'num', text: (x.size / 1024).toFixed(0) + ' KB' }), h('td', { text: fmtDate(Date.parse(x.modified)) }))))));
      } catch (e) { fail(e); }
    })();
    return null;
  },
};

// ---- maintenance
views.maintenance = {
  icon: 'maintenance',
  mount(root) {
    const settingsEl = h('div', { class: 'card' });
    const updateEl = h('div', { class: 'card' });
    const auditEl = h('div', { class: 'table-wrap', style: 'max-height:400px;overflow:auto' });
    const archiveEl = h('div');

    const loadSettings = async () => {
      let s;
      try { s = await api('/settings'); } catch (e) { fail(e); return; }
      const f = {};
      const field = (key, label, value, type = 'text', note) => {
        const input = type === 'checkbox' ? h('input', { type, checked: !!value }) : h('input', { type, value: value ?? '' });
        f[key] = input;
        return type === 'checkbox' ? h('label', { class: 'check' }, input, label) : h('label', {}, label, input, note ? h('span', { class: 'note', text: note }) : null);
      };
      const langSel = h('select', {}, h('option', { value: 'en', text: 'English', selected: s.language === 'en' }), h('option', { value: 'ru', text: 'Русский', selected: s.language === 'ru' }));
      const num = k => Number(f[k].value);
      const list = k => f[k].value.split(',').map(x => x.trim()).filter(Boolean);
      const save = async () => {
        const body = {
          language: langSel.value,
          serverDir: f.serverDir.value,
          autoStart: f.autoStart.checked,
          steamCmdPath: f.steamCmd.value,
          server: {
            name: f.name.value, port: num('port'), world: f.world.value, password: f.password.value, public: f.public.checked,
            crossplay: f.crossplay.checked, saveDir: f.saveDir.value, saveInterval: num('saveInterval'), backups: num('backups'),
            backupShort: s.server.backupShort, backupLong: s.server.backupLong, extraArgs: f.extraArgs.value,
          },
          watchdog: { enabled: f.wdEnabled.checked, hangSeconds: num('hang'), maxRestartsPerHour: num('maxRestarts') },
          restarts: { enabled: f.rsEnabled.checked, times: list('times'), warnMinutes: list('warn').map(Number) },
          snapshots: { keepAllDays: num('keepAll'), keepDailyDays: num('keepDaily'), ignoreDataKeys: list('ignoreKeys') },
        };
        try {
          await api('/settings', { method: 'PUT', body });
          toast(t('mt.saved'), 'ok');
          if (!localStorage.getItem('va_lang')) setLang(body.language);
        } catch (e) { fail(e); }
      };
      fill(settingsEl,
        h('h2', { text: t('mt.settings') }),
        h('h3', { text: t('mt.server') }),
        h('div', { class: 'form' },
          field('name', t('mt.name'), s.server.name), field('world', t('mt.world'), s.server.world),
          field('password', t('mt.password'), s.server.password), field('port', t('mt.port'), s.server.port, 'number'),
          field('saveInterval', t('mt.saveInterval'), s.server.saveInterval, 'number'), field('backups', t('mt.backups'), s.server.backups, 'number'),
          field('serverDir', t('mt.serverDir'), s.serverDir), field('saveDir', t('mt.saveDir'), s.server.saveDir, 'text', s.saveDir),
          field('extraArgs', t('mt.extraArgs'), s.server.extraArgs)),
        h('div', { class: 'row', style: 'margin:10px 0' }, field('public', t('mt.public'), s.server.public, 'checkbox'),
          field('crossplay', t('mt.crossplay'), s.server.crossplay, 'checkbox'), field('autoStart', t('mt.autoStart'), s.autoStart, 'checkbox')),
        h('p', { class: 'note', text: t('mt.serverNote') }),
        h('h3', { style: 'margin-top:16px', text: t('mt.watchdog') }),
        h('div', { class: 'form' }, field('wdEnabled', t('mt.enabled'), s.watchdog.enabled, 'checkbox'),
          field('hang', t('mt.hang'), s.watchdog.hangSeconds, 'number'), field('maxRestarts', t('mt.maxRestarts'), s.watchdog.maxRestartsPerHour, 'number')),
        h('h3', { style: 'margin-top:16px', text: t('mt.restarts') }),
        h('div', { class: 'form' }, field('rsEnabled', t('mt.enabled'), s.restarts.enabled, 'checkbox'),
          field('times', t('mt.times'), s.restarts.times.join(', '), 'text', '06:00, 18:00'),
          field('warn', t('mt.warn'), s.restarts.warnMinutes.join(', '), 'text', '15, 5, 1')),
        h('h3', { style: 'margin-top:16px', text: t('mt.snapshots') }),
        h('div', { class: 'form' }, field('keepAll', t('mt.keepAll'), s.snapshots.keepAllDays, 'number'),
          field('keepDaily', t('mt.keepDaily'), s.snapshots.keepDailyDays, 'number'),
          field('ignoreKeys', t('mt.ignoreKeys'), (s.snapshots.ignoreDataKeys || []).join(', '), 'text', t('mt.ignoreKeysNote'))),
        h('h3', { style: 'margin-top:16px', text: t('mt.other') }),
        h('div', { class: 'form' }, field('steamCmd', t('mt.steamcmd'), s.steamCmdPath, 'text', 'C:\\steamcmd\\steamcmd.exe'),
          h('label', {}, t('mt.language'), langSel)),
        h('div', { class: 'row', style: 'margin-top:16px' }, h('button', { class: 'btn primary', text: t('btn.save'), onclick: save })));
    };

    const renderUpdate = () => {
      const u = state.status?.update || {};
      fill(updateEl, h('h2', { text: t('mt.update') }),
        h('p', {}, t('mt.build') + ': ', h('b', { text: u.buildId || '—' })),
        !u.available ? h('p', { class: 'note', text: t('mt.noSteamcmd') }) : null,
        h('button', { class: 'btn', text: u.running ? t('mt.updating') : t('mt.updateNow'), disabled: !u.available || u.running, onclick: async () => {
          if (!await confirmDialog(t('mt.update'), t('mt.updateConfirm'), t('btn.yes'))) return;
          try { await api('/update', { method: 'POST' }); toast(t('toast.sent'), 'ok'); } catch (e) { fail(e); }
        } }),
        h('h2', { style: 'margin-top:20px', text: t('mt.passwordTitle') }),
        h('button', { class: 'btn', text: t('mt.changePassword'), onclick: async () => {
          const v = await formDialog(t('mt.changePassword'), [
            { name: 'current', label: t('mt.currentPassword'), type: 'password', required: true },
            { name: 'new', label: t('mt.newPassword'), type: 'password', required: true },
          ]);
          if (!v) return;
          try { await api('/settings/password', { method: 'POST', body: v }); toast(t('toast.done'), 'ok'); } catch (e) { fail(e); }
        } }),
        h('h2', { style: 'margin-top:20px', text: t('mt.archive') }), archiveEl);
    };

    root.append(settingsEl, h('div', { class: 'grid grid-2' }, updateEl, h('div', { class: 'card' }, h('h2', { text: t('mt.audit') }), auditEl)));
    loadSettings();
    renderUpdate();
    (async () => {
      try {
        const a = await api('/audit?limit=300');
        fill(auditEl, h('table', {}, h('tbody', {}, a.map(x => h('tr', {},
          h('td', { class: 'small', text: fmtShort(x.ts) }), h('td', { class: 'mono small', text: x.who }),
          h('td', { text: x.action }), h('td', { class: 'small muted', text: x.details || '' }))))));
        const files = await api('/logs/archive');
        fill(archiveEl, files.length ? h('div', { class: 'list', style: 'max-height:200px' }, files.map(f =>
          h('a', { href: '/api/logs/archive/' + f.name, target: '_blank', class: 'list-item', text: `${f.name} · ${(f.size / 1024).toFixed(0)} KB` })))
          : h('p', { class: 'muted', text: '—' }));
      } catch (e) { fail(e); }
    })();
    return msg => { if (msg.type === 'status') { const running = state.status?.update?.running; if (running !== updateEl.dataset.running) { updateEl.dataset.running = running; renderUpdate(); } } };
  },
};

// ---- map (public landing page; admins see everything)
const LOCATION_NAMES = {
  StartTemple: 'Sacrificial stones', Eikthyrnir: 'Eikthyr', GDKing: 'The Elder', Bonemass: 'Bonemass', Dragonqueen: 'Moder',
  GoblinKing: 'Yagluth', Mistlands_DvergrBossEntrance1: 'The Queen', FaderLocation: 'Fader', Vendor_BlackForest: 'Haldor',
  Hildir_camp: 'Hildir', BogWitch_Camp: 'Bog Witch',
};
const BOSS_LOCATIONS = ['Eikthyrnir', 'GDKing', 'Bonemass', 'Dragonqueen', 'GoblinKing', 'Mistlands_DvergrBossEntrance1', 'FaderLocation'];
const locationName = n => LOCATION_NAMES[n] || n.replace(/_/g, ' ');
const locationKind = n => BOSS_LOCATIONS.includes(n) ? 'boss' : /vendor|hildir|witch|trader/i.test(n) ? 'trader' : n === 'StartTemple' ? 'start' : 'loc';
const MAP_GLYPHS = { portal: '◆', boss: '☠', trader: '¤', start: '✦', loc: '•', tomb: '✝', pin: '⚑' };

/** Fetch for the public endpoints: never opens the login dialog. */
async function publicGet(path) {
  const res = await fetch(path, { credentials: 'same-origin' });
  if (!res.ok) throw new Error(res.statusText);
  return res.json();
}

views.map = {
  icon: 'map',
  mount(root) {
    root.classList.add('view-map');
    const admin = !!state.admin;
    const el = h('div', { class: 'map' });
    const statusEl = h('div', { class: 'map-status', hidden: true });
    const coordsEl = h('div', { class: 'map-coords mono' });
    const layersEl = h('div', { class: 'map-layers' });
    root.append(h('div', { class: 'map-wrap' }, el, statusEl, coordsEl, layersEl));

    const map = L.map(el, { crs: L.CRS.Simple, minZoom: -6, maxZoom: 3, zoomSnap: 0.25, zoomDelta: 0.5, attributionControl: false });
    const ll = (x, z) => L.latLng(z, x);
    const groups = { players: L.layerGroup(), pins: L.layerGroup(), portals: L.layerGroup(), locations: L.layerGroup(), tombstones: L.layerGroup() };
    let hiddenLayers;
    try { hiddenLayers = new Set(JSON.parse(localStorage.getItem('va_map_hidden') || '[]')); } catch { hiddenLayers = new Set(); }
    for (const [k, g] of Object.entries(groups)) if (!hiddenLayers.has(k)) g.addTo(map);

    let overlay = null, fogOverlay = null, bounds = null, fitted = false, info = null, markers = {};
    const glyph = kind => L.divIcon({ className: 'mk mk-' + kind, html: MAP_GLYPHS[kind] || '•', iconSize: [20, 20], iconAnchor: [10, 10] });

    const readInfo = async () => {
      if (admin) {
        const i = await api('/map/info');
        return { ...i, hasMap: !!i.mapFile, version: i.mapVersion, url: '/api/map/full.png' };
      }
      const r = await publicGet('/api/public/info');
      return { ...(r.map || {}), url: '/api/public/map.png' };
    };

    const statusText = i => {
      if (!i) return t('map.offline');
      if (i.enabled === false) return t('map.disabled');
      if (i.state === 'generating') return t('map.drawing', Math.round((i.progress || 0) * 100));
      if (i.state === 'failed') return t('map.failed', i.error || '');
      if (!i.hasMap) return i.online === false || i.state === 'offline' || i.state === 'waiting' ? t('map.offline') : t('map.preparing');
      return i.online === false ? t('map.serverDown') : '';
    };

    const showImage = i => {
      const R = i.size * i.pixelSize / 2;
      bounds = L.latLngBounds(ll(-R, -R), ll(R, R));
      const url = `${i.url}?v=${i.version}`;
      if (!overlay) overlay = L.imageOverlay(url, bounds, { className: 'map-image' }).addTo(map).bringToBack();
      else if (overlay._url !== url) overlay.setUrl(url);
      if (!fitted) {
        fitted = true;
        let saved = null;
        try { saved = JSON.parse(localStorage.getItem('va_map_view') || 'null'); } catch { }
        if (saved) map.setView(saved.c, saved.z); else map.fitBounds(bounds.pad(-0.12));
        map.setMaxBounds(bounds.pad(0.15));
      }
    };

    const refreshInfo = async () => {
      try { info = await readInfo(); } catch { info = null; }
      const text = statusText(info);
      statusEl.textContent = text;
      statusEl.hidden = !text;
      if (info?.hasMap) showImage(info);
      renderLayers();
    };

    const drawMarkers = data => {
      markers = data || {};
      for (const g of Object.values(groups)) g.clearLayers();
      for (const p of markers.players || []) {
        L.circleMarker(ll(p.x, p.z), { radius: 6, className: 'mk-player' + (admin && p.public === false ? ' private' : ''), weight: 2 })
          .bindTooltip(p.name + (admin && p.public === false ? ' · ' + t('map.privatePlayer') : ''), { permanent: true, direction: 'right', offset: [8, 0], className: 'mk-label' })
          .addTo(groups.players);
      }
      for (const p of markers.portals || [])
        L.marker(ll(p.x, p.z), { icon: glyph('portal') }).bindTooltip(p.tag || t('map.portal')).addTo(groups.portals);
      for (const l of markers.locations || [])
        L.marker(ll(l.x, l.z), { icon: glyph(locationKind(l.name)) }).bindTooltip(locationName(l.name)).addTo(groups.locations);
      for (const s of markers.tombstones || [])
        L.marker(ll(s.x, s.z), { icon: glyph('tomb') }).bindTooltip(t('map.tombstone', s.owner || '?'))
          .on('click', () => { location.hash = '#characters'; }).addTo(groups.tombstones);
      for (const p of markers.pins || []) {
        const m = L.marker(ll(p.x, p.z), { icon: glyph('pin') });
        if (p.label) m.bindTooltip(p.label, { permanent: true, direction: 'right', offset: [8, 0], className: 'mk-label pin' });
        if (admin) m.on('click', () => removePin(p));
        m.addTo(groups.pins);
      }
      renderLayers();
    };

    const refreshMarkers = async () => {
      if (document.hidden) return;
      try { drawMarkers(admin ? await api('/map/markers') : await publicGet('/api/public/markers')); } catch { }
    };

    const setFog = on => {
      if (on && !fogOverlay && bounds && info) fogOverlay = L.imageOverlay(`/api/map/fog.png?v=${info.fogVersion}`, bounds, { opacity: 0.55 }).addTo(map);
      if (!on && fogOverlay) { fogOverlay.remove(); fogOverlay = null; }
    };

    const renderLayers = () => {
      const available = Object.keys(groups).filter(k => k === 'players' || k === 'pins' || markers[k] !== undefined);
      fill(layersEl,
        h('div', { class: 'map-layers-title', text: t('map.layers') }),
        available.map(k => h('label', { class: 'check' },
          h('input', { type: 'checkbox', checked: !hiddenLayers.has(k), onchange: e => {
            if (e.target.checked) { hiddenLayers.delete(k); groups[k].addTo(map); } else { hiddenLayers.add(k); groups[k].remove(); }
            try { localStorage.setItem('va_map_hidden', JSON.stringify([...hiddenLayers])); } catch { }
          } }),
          `${t('map.' + k)} (${(markers[k] || []).length})`)),
        admin ? [
          h('label', { class: 'check' }, h('input', { type: 'checkbox', checked: !!fogOverlay, onchange: e => setFog(e.target.checked) }), t('map.explored')),
          h('button', { class: 'btn small', text: t('map.regen'), onclick: regen }),
          h('p', { class: 'note', text: t('map.pinHint') }),
        ] : null);
    };

    const regen = async () => {
      if (!await confirmDialog(t('map.regen'), t('map.regenConfirm'), t('btn.yes'))) return;
      try { await api('/map/regen', { method: 'POST' }); fitted = true; refreshInfo(); } catch (e) { fail(e); }
    };

    const addPin = async latlng => {
      const v = await formDialog(t('map.pinAdd'), [
        { name: 'label', label: t('map.pinLabel'), placeholder: t('map.pinPlaceholder') },
        { name: 'public', label: t('map.pinPublic'), type: 'checkbox', value: true },
      ], t('btn.add'));
      if (!v) return;
      try {
        await api('/map/pins', { method: 'POST', body: { x: latlng.lng, z: latlng.lat, label: v.label, public: v.public } });
        refreshMarkers();
      } catch (e) { fail(e); }
    };

    const removePin = async p => {
      if (!await confirmDialog(t('map.pinDelete'), p.label || '', t('btn.remove'), true)) return;
      try { await api('/map/pins/' + encodeURIComponent(p.id), { method: 'DELETE' }); refreshMarkers(); } catch (e) { fail(e); }
    };

    map.on('mousemove', e => { coordsEl.textContent = `x ${Math.round(e.latlng.lng)} · z ${Math.round(e.latlng.lat)}`; });
    map.on('moveend', () => { try { localStorage.setItem('va_map_view', JSON.stringify({ c: map.getCenter(), z: map.getZoom() })); } catch { } });
    if (admin) map.on('contextmenu', e => addPin(e.latlng));

    refreshInfo();
    refreshMarkers();
    const markerTimer = setInterval(refreshMarkers, 3000);
    const infoTimer = setInterval(refreshInfo, 10000);
    return {
      dispose() {
        clearInterval(markerTimer);
        clearInterval(infoTimer);
        map.remove();
      },
    };
  },
};

// ---------------------------------------------------------------- shell

const ADMIN_ORDER = ['overview', 'console', 'events', 'players', 'characters', 'configs', 'maintenance'];

/** The map for everybody; the admin sections the backend offers once signed in. */
function tabs() {
  const features = state.info?.features || ['map'];
  return ['map', ...(state.admin ? ADMIN_ORDER.filter(n => features.includes(n)) : [])];
}

function renderNav() {
  const tab = currentTab();
  fill($('#nav'), tabs().map(name => h('a', { href: '#' + name, class: name === tab ? 'active' : '' },
    icon(views[name].icon), h('span', { text: t('nav.' + name) }))));
}

function currentTab() {
  const name = location.hash.slice(1);
  return tabs().includes(name) ? name : 'map';
}

let currentDispose = null;
function route() {
  if (current) liveHandlers.delete(current);
  current = null;
  if (currentDispose) { try { currentDispose(); } catch (e) { console.error(e); } currentDispose = null; }
  const name = currentTab();
  renderNav();
  const root = clear($('#view'));
  root.className = 'view';
  document.title = t('nav.' + name) + ' · ' + (state.info?.name || 'Valheim Admin');
  // A view returns its live-update handler, or {live, dispose}.
  const result = views[name].mount(root);
  const live = typeof result === 'function' ? result : result?.live;
  currentDispose = typeof result === 'object' && result ? result.dispose || null : null;
  if (live) { current = live; liveHandlers.add(live); }
}

function renderLangSwitches() {
  document.querySelectorAll('[data-lang-switch]').forEach(el => fill(el, ['en', 'ru'].map(l =>
    h('button', { type: 'button', class: l === lang() ? 'on' : '', text: l.toUpperCase(), onclick: () => { localStorage.setItem('va_lang', l); setLang(l); } }))));
}

function setAdmin(on) {
  state.admin = on;
  $('#login-btn').hidden = on;
  $('#logout').hidden = !on;
  $('#topbar').hidden = !on;
  if (!on) { clearTimeout(reconnectTimer); socket?.close(); poll.players = poll.eventId = null; }
}

/** Plain HTTP to a non-local, non-Tailscale address: the password would travel unencrypted. */
function insecureConnection() {
  return location.protocol !== 'https:' &&
    !/^(localhost|127\.|\[::1\]|100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.)/.test(location.hostname);
}

function showLogin() {
  const dlg = $('#login-dialog');
  $('#login-error').hidden = true;
  $('#login-insecure').hidden = !insecureConnection();
  if (!dlg.open) dlg.showModal();
  $('#login-password').focus();
}

/** A request came back 401: the session ended. Back to the public map, and offer to sign in. */
function onUnauthorized() {
  if (state.admin) {
    setAdmin(false);
    route();
  }
  showLogin();
}

async function loadInfo() {
  try { state.info = await publicGet('/api/public/info'); } catch { state.info = state.info || null; }
  if (state.info?.name) $('#brand-name').textContent = state.info.name;
  return state.info;
}

async function startAdmin() {
  setAdmin(true);
  await loadInfo();
  try { state.status = await api('/status'); } catch (e) { fail(e); }
  renderTopbar();
  playersDatalist();
  route();
  connectLive();
}

$('#login-form').addEventListener('submit', async e => {
  e.preventDefault();
  const err = $('#login-error');
  err.hidden = true;
  try {
    await api('/login', { method: 'POST', body: { password: $('#login-password').value } });
    $('#login-password').value = '';
    $('#login-dialog').close();
    startAdmin();
  } catch (ex) {
    err.textContent = ex.message;
    err.hidden = false;
  }
});
$('#login-cancel').addEventListener('click', () => $('#login-dialog').close());
$('#login-btn').addEventListener('click', showLogin);
$('#logout').addEventListener('click', async () => {
  await api('/logout', { method: 'POST' }).catch(() => {});
  setAdmin(false);
  location.hash = '#map';
  route();
});
window.addEventListener('hashchange', route);
liveHandlers.add(msg => { if (msg.type === 'players') playersDatalist(); });

onLangChange(() => {
  applyStatic();
  renderLangSwitches();
  if (state.admin) renderTopbar();
  route();
});

(async () => {
  applyStatic();
  renderLangSwitches();
  const info = await loadInfo();
  if (!localStorage.getItem('va_lang') && info?.language) setLang(info.language);
  if (info?.admin) startAdmin();
  else { setAdmin(false); route(); }
})();

// The stand's stand-in for Unity: it speaks the same protocol as HtmlUi.cs
// (hello → ready → strings → show/hide, then polls commands every frame),
// so a window that works here differs from the game only in its data.
import * as bridge from './ui/bridge.js';

const state = {
  lang: 'ru',
  platform: 'web', // web = shared server world (remote), desktop = local game
  size: [960, 600],
  dialog: 'titleServers', // the window picked in the bar; null once it is closed
  volumes: { voices: 80, agentVoices: 100, music: 55, environment: 90 },
  updateFailed: false,
  hud: { day: 12, minutes: 14 * 60 + 20, raining: false, link: 'live', paused: false, speed: 1 },
};
const strings = {};
let ready = false;
let shownDialog = null;

// ---- the title screen (LoadingScreen.cs): servers, loading, failure ----
const SERVERS = [
  { id: 'singapore', nameTerm: 'menu.servers.singapore', name: 'Singapore', ping: -1, state: 'pending' },
  { id: 'new-york', nameTerm: 'menu.servers.newyork', name: 'New York', ping: -1, state: 'disabled' },
];
// What LoadingScreen reports on the server path, step by step (§174.7).
const LOAD_PLAN = [
  [0.01, 'connect', { text: 'Получаем реестр контента' }],
  [0.15, 'connect', { key: 'loading.connecting' }],
  [0.3, 'connect', { key: 'loading.connecting' }],
  [0.45, 'sync', { key: 'loading.syncing' }],
  [0.6, 'island', { key: 'loading.island' }],
  [0.75, 'people', { key: 'loading.warmup' }],
  [0.82, 'people', { key: 'loading.content.wear' }],
  [0.9, 'people', { key: 'loading.content.hair' }],
  [0.97, 'people', { key: 'loading.content.done' }],
  [1, 'done', { key: 'loading.done' }],
];
const title = { phase: 'servers', connecting: false, error: null, loading: null, timer: 0 };

function titleModel() {
  return { phase: title.phase, servers: SERVERS, connecting: title.connecting, error: title.error, loading: title.loading };
}

function sendTitle() {
  if (ready) post({ t: 'show', screen: 'title', model: titleModel() });
}

// The ping is re-measured on its own every 10 s while the card is up; only
// a server never measured shows «measuring», later rounds keep the last value.
function probe() {
  for (const server of SERVERS) if (server.state !== 'disabled' && server.ping < 0) server.state = 'pending';
  sendTitle();
  setTimeout(() => {
    const sg = SERVERS[0];
    sg.state = 'ready';
    sg.ping = 180 + Math.round(Math.random() * 40); // Singapore from Europe: yellow
    sendTitle();
  }, 1400);
}
setInterval(() => { if (isTitle(state.dialog) && title.phase === 'servers' && !title.connecting) probe(); }, 10000);

function startTitle(kind) {
  clearInterval(title.timer);
  title.connecting = false;
  title.loading = null;
  title.error = kind === 'titleError' ? { key: 'loading.connect.failed', detail: 'wss://play.62-146-235-120.sslip.io/watch: handshake timed out after 15 s' } : null;
  title.phase = kind === 'titleLoading' ? 'loading' : 'servers';
  if (kind === 'titleLoading') runLoading(); else probe();
}

function runLoading() {
  title.phase = 'loading';
  let i = 0;
  const step = () => {
    const [progress, stepId, status] = LOAD_PLAN[i];
    title.loading = { progress, step: stepId, status };
    sendTitle();
    if (++i >= LOAD_PLAN.length) { clearInterval(title.timer); setTimeout(() => startTitle('titleLoading'), 2500); }
  };
  step();
  title.timer = setInterval(step, 1100);
}

const $ = (id) => document.getElementById(id);
const log = $('log');

function post(message) {
  window.HexUi.receive(JSON.stringify(message));
}

async function loadStrings(lang) {
  if (!strings[lang]) {
    const response = await fetch(`i18n/${lang}.json`);
    strings[lang] = response.ok ? await response.json() : {};
  }
  return strings[lang];
}

async function sendStrings() {
  post({ t: 'strings', lang: state.lang, terms: await loadStrings(state.lang) });
}

// ---- test data, shaped exactly like the C# models in §174.7 ----

const MOLLY = {
  name: 'Молли',
  attributes: [['strength', 4], ['agility', 7], ['endurance', 6], ['hardiness', 5], ['toughness', 3], ['wits', 8], ['perception', 6]],
  traits: ['trait.neat', 'trait.brave'],
};
const MARTA = {
  name: 'Марта',
  attributes: [['strength', 8], ['agility', 5], ['endurance', 7], ['hardiness', 7], ['toughness', 6], ['wits', 3], ['perception', 5]],
  traits: [],
};

function character(person, intro) {
  return { name: person.name, intro, attributes: person.attributes.map(([id, value]) => ({ id, value })), traits: person.traits };
}

const DIALOGS = {
  gameMenu: () => ({ canQuit: state.platform === 'desktop', volumes: { ...state.volumes } }),
  connectionLost: () => ({ detail: 'socket closed 1006 (no close frame) after 6 reconnect attempts · last frame tick 682913' }),
  protocolUpdate: () => ({ canQuit: state.platform === 'desktop', failed: state.updateFailed }),
  playerAssignment: () => ({ losses: [], characters: [character(MOLLY, 'introduction')], footer: 'persistence' }),
  playerReplacement: () => ({
    losses: [{ kind: 'died', name: 'Джолли' }],
    characters: [character(MARTA, 'replacement')],
    footer: 'persistence',
  }),
};
// The replacement scenario is the same C# window with other data.
const SCREEN_OF = { playerReplacement: 'playerAssignment' };
const isTitle = (id) => !!id && id.startsWith('title');

function hudModel() {
  const h = state.hud;
  const hh = Math.floor(h.minutes / 60) % 24;
  const phase = hh < 6 ? 'Night' : hh < 11 ? 'Morning' : hh < 18 ? 'Day' : hh < 22 ? 'Evening' : 'Night';
  const temperature = h.raining ? 22 : 28;
  const remote = state.platform === 'web';
  return {
    day: h.day,
    temperature,
    band: temperature < 22 ? 'cold' : temperature > 30 ? 'hot' : 'mild',
    raining: h.raining,
    clock: `${String(hh).padStart(2, '0')}:${String(h.minutes % 60).padStart(2, '0')}`,
    phase,
    link: remote ? { state: h.link, ping: 84 + Math.round(Math.random() * 12) } : null,
    paused: remote ? false : h.paused,
    speed: remote ? 1 : h.speed,
    remote,
  };
}

function sendHud() {
  // SimSpeedBar: the HUD is withdrawn while the loading screen owns the display.
  if (!ready) return;
  if (isTitle(state.dialog)) post({ t: 'hide', screen: 'hud' });
  else post({ t: 'show', screen: 'hud', model: hudModel() });
}

function showDialog(id) {
  const wasTitle = isTitle(shownDialog);
  if (shownDialog && ready) post({ t: 'hide', screen: isTitle(shownDialog) ? 'title' : SCREEN_OF[shownDialog] || shownDialog });
  if (!isTitle(id)) clearInterval(title.timer);
  shownDialog = id;
  state.dialog = id;
  if (isTitle(id) !== wasTitle) drawWorld();
  sendHud();
  if (isTitle(id)) {
    for (const button of document.querySelectorAll('[data-dialog]')) button.setAttribute('aria-pressed', String(button.dataset.dialog === id));
    $('reopen').hidden = true;
    if (ready) startTitle(id);
    return;
  }
  $('reopen').hidden = !!id || !ready;
  for (const button of document.querySelectorAll('[data-dialog]')) button.setAttribute('aria-pressed', String(button.dataset.dialog === id));
  if (id && ready) post({ t: 'show', screen: SCREEN_OF[id] || id, model: DIALOGS[id]() });
}

function refreshDialog() {
  if (isTitle(shownDialog)) { sendTitle(); return; }
  if (shownDialog && ready) post({ t: 'show', screen: SCREEN_OF[shownDialog] || shownDialog, model: DIALOGS[shownDialog]() });
}

function note(from, text) {
  const item = document.createElement('li');
  const who = document.createElement('span');
  who.className = 'from';
  who.textContent = from + ' ';
  item.append(who, text);
  log.prepend(item);
  while (log.children.length > 60) log.lastChild.remove();
  return item;
}

// What the C# side does with each command, in words.
const EFFECT = {
  'gameMenu.continue': 'закрыть меню, мир продолжает идти',
  'gameMenu.mainMenu': 'сохранить игру и вернуться в главное меню',
  'gameMenu.quit': 'выйти из игры (только десктоп)',
  'connectionLost.ok': 'вернуться в главное меню',
  'playerAssignment.ok': 'отметить уведомления прочитанными и выбрать персонажа',
  'hud.pause': 'пауза / пустить время',
  'hud.speed': 'сменить скорость и снять паузу',
};

function handle(message) {
  if (message.t === 'ready') {
    ready = true;
    note('страница', `ready v${message.v}`);
    sendStrings().then(() => { sendHud(); showDialog(state.dialog); });
    return;
  }
  if (message.t !== 'cmd') return;
  const key = `${message.screen}.${message.cmd}`;
  const args = message.args && Object.keys(message.args).length ? JSON.stringify(message.args) : '';
  if (key === 'gameMenu.volume') {
    state.volumes[message.args.category] = message.args.value;
    const last = log.firstElementChild; // a drag is one line per slider
    if (last && last.dataset.volume === message.args.category) last.remove();
    note(key, args).dataset.volume = message.args.category;
    return;
  }
  if (message.screen === 'title') {
    if (message.cmd === 'play') {
      note(key, `${args} → свой сервер страницы: создание персонажа, потом подключение`);
      title.connecting = true;
      title.error = null;
      sendTitle();
      setTimeout(() => { title.connecting = false; runLoading(); }, 900);
    }
    return;
  }
  if (key === 'protocolUpdate.update') {
    if (state.platform === 'web') {
      note(key, '→ перезагрузить страницу (на стенде не перезагружаем)');
    } else {
      state.updateFailed = true;
      note(key, '→ запустить обновлятор; на стенде показан отказ');
      refreshDialog();
    }
    return;
  }
  note(key, `${args} → ${EFFECT[key] || '?'}`);
  if (message.screen === 'hud') {
    if (message.cmd === 'pause') state.hud.paused = !state.hud.paused;
    if (message.cmd === 'speed') { state.hud.speed = message.args.value; state.hud.paused = false; }
    sendHud();
    return;
  }
  showDialog(null);
}

function pump() {
  for (let json = window.HexUi?.take(); json; json = window.HexUi.take()) handle(JSON.parse(json));
  requestAnimationFrame(pump);
}

// A living HUD: ten game minutes a second, so model updates are visible
// (and prove that a refreshed HUD never climbs over a dialog).
setInterval(() => {
  const h = state.hud;
  if (state.platform === 'desktop' && h.paused) return;
  h.minutes += 10;
  if (h.minutes >= 24 * 60) { h.minutes -= 24 * 60; h.day += 1; }
  sendHud();
}, 1000);

// ---- the world behind the windows: flat faceted hex tiles, like the game ----
function hash(x, y) {
  const s = Math.sin(x * 127.1 + y * 311.7) * 43758.5453;
  return s - Math.floor(s);
}

// LoadingScreen.cs draws the island art (cover) and the logo at left 36,
// top 20, 360 px wide on a 1920×1080 reference — Unity's layer, not the page's.
const art = { island: new Image(), logo: new Image() };
art.island.src = 'assets/loading_island.jpg';
art.logo.src = 'assets/logo.png';
art.island.onload = art.logo.onload = () => { if (isTitle(state.dialog)) drawWorld(); };

function drawTitleArt(g, w, h) {
  g.fillStyle = 'rgb(13 18 23)';
  g.fillRect(0, 0, w, h);
  const img = art.island;
  if (img.naturalWidth) {
    const k = Math.max(w / img.naturalWidth, h / img.naturalHeight);
    g.drawImage(img, (w - img.naturalWidth * k) / 2, (h - img.naturalHeight * k) / 2, img.naturalWidth * k, img.naturalHeight * k);
  }
  const s = h / 1080;
  if (art.logo.naturalWidth) {
    const lw = 360 * s;
    g.drawImage(art.logo, 36 * s, 20 * s, lw, lw * art.logo.naturalHeight / art.logo.naturalWidth);
  }
}

function drawWorld() {
  const canvas = $('world');
  const [w, h] = state.size;
  canvas.width = w;
  canvas.height = h;
  const g = canvas.getContext('2d');
  if (isTitle(state.dialog)) { drawTitleArt(g, w, h); return; }
  g.fillStyle = '#1f5f78';
  g.fillRect(0, 0, w, h);
  const r = h / 14;
  const cx = w * 0.5;
  const cy = h * 0.52;
  const palette = {
    deep: ['#1f5f78', '#22667f'],
    shallow: ['#2f8296', '#348a9d'],
    sand: ['#e2c88f', '#d8bc80'],
    grass: ['#7fae58', '#73a14e'],
    forest: ['#4f8442', '#467a3b'],
  };
  for (let q = -14; q <= 14; q++) {
    for (let s = -9; s <= 9; s++) {
      const x = cx + r * 1.5 * q;
      const y = cy + r * Math.sqrt(3) * (s + q / 2);
      if (x < -r * 2 || x > w + r * 2 || y < -r * 2 || y > h + r * 2) continue;
      const d = Math.hypot((x - cx) / (w * 0.36), (y - cy) / (h * 0.36)) + (hash(q, s) - 0.5) * 0.35;
      const kind = d < 0.45 ? 'forest' : d < 0.75 ? 'grass' : d < 0.92 ? 'sand' : d < 1.15 ? 'shallow' : 'deep';
      const [light, dark] = palette[kind];
      for (let i = 0; i < 6; i++) {
        const a0 = (Math.PI / 3) * i;
        const a1 = a0 + Math.PI / 3;
        g.beginPath();
        g.moveTo(x, y);
        g.lineTo(x + r * Math.cos(a0), y + r * Math.sin(a0));
        g.lineTo(x + r * Math.cos(a1), y + r * Math.sin(a1));
        g.closePath();
        g.fillStyle = i < 3 ? dark : light; // two facet tones = the low-poly look
        g.fill();
      }
      g.strokeStyle = 'rgb(0 0 0 / 0.12)';
      g.lineWidth = 1;
      g.beginPath();
      for (let i = 0; i <= 6; i++) {
        const a = (Math.PI / 3) * i;
        g[i ? 'lineTo' : 'moveTo'](x + r * Math.cos(a), y + r * Math.sin(a));
      }
      g.stroke();
    }
  }
}

// ---- sizing: the game area keeps its pixel size and is scaled to fit ----
function layout() {
  const viewport = $('viewport');
  const stage = $('hex-stage');
  const [w, h] = state.size;
  stage.style.width = `${w}px`;
  stage.style.height = `${h}px`;
  const scale = viewport.clientWidth / w;
  stage.style.transform = `scale(${scale})`;
  viewport.style.height = `${h * scale}px`;
  // Two frames: the rem scale is set by ui/main.js's ResizeObserver, which
  // runs after this frame's animation callbacks.
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const rem = parseFloat(document.documentElement.style.fontSize) || 16;
    $('frame-size').textContent = `${w}×${h} · 1rem = ${rem.toFixed(1)}px · показано в ${Math.round(scale * 100)}%`;
  }));
}

function bindSegment(attr, apply) {
  for (const button of document.querySelectorAll(`[data-${attr}]`)) {
    button.addEventListener('click', () => {
      for (const other of document.querySelectorAll(`[data-${attr}]`)) other.setAttribute('aria-pressed', String(other === button));
      apply(button.dataset[attr]);
    });
  }
}

bindSegment('lang', (lang) => { state.lang = lang; if (ready) sendStrings(); });
bindSegment('platform', (platform) => { state.platform = platform; state.updateFailed = false; sendHud(); refreshDialog(); });
bindSegment('size', (size) => { state.size = size.split('x').map(Number); drawWorld(); layout(); });
bindSegment('weather', (weather) => { state.hud.raining = weather === 'rain'; sendHud(); });
bindSegment('link', (link) => { state.hud.link = link; sendHud(); });
for (const button of document.querySelectorAll('[data-dialog]')) {
  button.addEventListener('click', () => { state.updateFailed = false; showDialog(button.dataset.dialog); });
}
$('reopen').addEventListener('click', () => showDialog('gameMenu'));
$('clear-log').addEventListener('click', () => { log.textContent = ''; });
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  // GameMenu.cs: Escape opens the menu and closes it; other dialogs ignore it.
  if (isTitle(shownDialog)) return; // GameMenu: Escape never opens over the loading screen
  if (shownDialog === 'gameMenu') handle({ t: 'cmd', screen: 'gameMenu', cmd: 'continue', args: {} });
  else if (!shownDialog) showDialog('gameMenu');
});
new ResizeObserver(layout).observe($('viewport'));

drawWorld();
layout();
requestAnimationFrame(pump);
post({ t: 'hello', v: bridge.PROTOCOL });

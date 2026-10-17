// The stand's stand-in for Unity: it speaks the same protocol as HtmlUi.cs
// (hello → ready → strings → show/hide, then polls commands every frame),
// so a window that works here differs from the game only in its data.
import * as bridge from './ui/bridge.js';

const state = {
  lang: 'ru',
  platform: 'web',
  size: [960, 600],
  menuOpen: true,
  volumes: { voices: 80, agentVoices: 100, music: 55, environment: 90 },
};
const strings = {};
let ready = false;

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

function menuModel() {
  return { canQuit: state.platform === 'desktop', volumes: { ...state.volumes } };
}

function setMenu(open) {
  state.menuOpen = open;
  $('reopen').hidden = open;
  if (!ready) return;
  post(open ? { t: 'show', screen: 'gameMenu', model: menuModel() } : { t: 'hide', screen: 'gameMenu' });
}

function note(from, text) {
  const item = document.createElement('li');
  const who = document.createElement('span');
  who.className = 'from';
  who.textContent = from + ' ';
  item.append(who, text);
  log.prepend(item);
  while (log.children.length > 60) log.lastChild.remove();
}

// What GameMenu.cs does with each command, in words.
const EFFECT = {
  continue: 'закрыть меню, мир продолжает идти',
  mainMenu: 'сохранить игру и вернуться в главное меню',
  quit: 'выйти из игры (только десктоп)',
};

function handle(message) {
  if (message.t === 'ready') {
    ready = true;
    note('страница', `ready v${message.v}`);
    sendStrings().then(() => setMenu(state.menuOpen));
    return;
  }
  if (message.t !== 'cmd') return;
  const args = message.args && Object.keys(message.args).length ? ' ' + JSON.stringify(message.args) : '';
  if (message.cmd === 'volume') {
    state.volumes[message.args.category] = message.args.value;
    // Collapse a drag into one line per slider.
    const last = log.firstElementChild;
    if (last && last.dataset.volume === message.args.category) last.remove();
    note(`${message.screen}.volume`, args.trim());
    log.firstElementChild.dataset.volume = message.args.category;
    return;
  }
  note(`${message.screen}.${message.cmd}`, `${args} → ${EFFECT[message.cmd] || '?'}`);
  if (message.cmd === 'continue' || message.cmd === 'mainMenu' || message.cmd === 'quit') setMenu(false);
}

function pump() {
  for (let json = window.HexUi?.take(); json; json = window.HexUi.take()) handle(JSON.parse(json));
  requestAnimationFrame(pump);
}

// ---- the world behind the menu: flat faceted hex tiles, like the game ----
function hash(x, y) {
  const s = Math.sin(x * 127.1 + y * 311.7) * 43758.5453;
  return s - Math.floor(s);
}

function drawWorld() {
  const canvas = $('world');
  const [w, h] = state.size;
  canvas.width = w;
  canvas.height = h;
  const g = canvas.getContext('2d');
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
bindSegment('platform', (platform) => { state.platform = platform; if (state.menuOpen) setMenu(true); });
bindSegment('size', (size) => { state.size = size.split('x').map(Number); drawWorld(); layout(); });
$('reopen').addEventListener('click', () => setMenu(true));
$('screen-gameMenu').addEventListener('click', () => setMenu(true));
$('clear-log').addEventListener('click', () => { log.textContent = ''; });
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  // GameMenu.cs: Escape toggles the menu (and «continue» on close).
  if (state.menuOpen) handle({ t: 'cmd', screen: 'gameMenu', cmd: 'continue', args: {} });
  else setMenu(true);
});
new ResizeObserver(layout).observe($('viewport'));

drawWorld();
layout();
requestAnimationFrame(pump);
post({ t: 'hello', v: bridge.PROTOCOL });

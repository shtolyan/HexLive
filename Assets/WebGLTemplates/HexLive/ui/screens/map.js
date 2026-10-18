// §174.7: the compact HUD map (§150 TacticalMapPanel/TacticalMapView) — a
// canvas at the top left. C# builds the same visibility-safe frame it draws in
// UI Toolkit (fog, remembered markers, the camera footprint) and the page only
// paints it; a click sends the world point and the camera flies there.
//
// Model: {
//   collapsed,
//   terrain: asset key → JSON { radius, tiles: [[x, z, water 0|1, elevation]] } (world units),
//            sent once per island through HtmlUi.Asset: a model is last-one-wins, an asset is kept
//   fog: string,                       // per tile: '0' unknown, '1' remembered, '2' in sight
//   markers: [{ kind: 'camp'|'palm'|'resource'|'item', x, z, live, lit, home?, icon? }],  // home: her own camp, never dimmed
//   people: [{ id, x, z, owned, hostile, selected, dead, controlled, face? }],
//   strangers: [{ x, z }],             // seen as someone, not yet known
//   mobs: [{ x, z, kind: 'wolf'|'crab'|'other' }],
//   footprint: [[x, z]] | null,        // what the camera sees, as a ground polygon
// }
// Commands: click { x, z }, collapse, expand.
//
// The map's height goes to --hx-left-stack on the page root, so the colony
// history below it in the same left column moves out of its way.
import { html, useEffect, useRef } from '../html.js';
import { t } from '../i18n.js';
import { asset } from '../bridge.js';

// TacticalMapView colours (MapColors).
const C = {
  ocean: 'rgb(14 41 54)', unknown: 'rgb(14 18 17)', remembered: 'rgb(64 74 64)', low: [87, 140, 79], high: [179, 168, 107],
  line: 'rgb(20 31 26 / 0.62)', palm: 'rgb(89 224 110)', resource: 'rgb(255 176 64)', campRing: 'rgb(71 46 26)',
  fire: 'rgb(255 107 31)', cold: 'rgb(184 138 92)', own: 'rgb(56 217 255)', friendly: 'rgb(222 232 219)', hostile: 'rgb(255 79 77)',
  border: 'rgb(240 250 255)', wolf: 'rgb(250 74 64)', crab: 'rgb(255 140 46)', item: 'rgb(184 120 209)',
};
const images = new Map();
const terrains = new Map(); // asset key → parsed terrain
function terrainOf(key) {
  if (!key) return null;
  if (!terrains.has(key)) {
    const raw = asset(key);
    if (!raw) return null;
    try { terrains.set(key, JSON.parse(raw)); } catch { return null; }
  }
  return terrains.get(key);
}
function image(url, redraw) {
  if (!url) return null;
  let img = images.get(url);
  if (!img) { img = new Image(); img.onload = redraw; img.src = url; images.set(url, img); }
  return img.complete && img.naturalWidth ? img : null;
}

function draw(canvas, model, redraw) {
  const g = canvas.getContext('2d');
  const dpr = Math.min(2, window.devicePixelRatio || 1);
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (canvas.width !== Math.round(w * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.fillStyle = C.ocean; g.fillRect(0, 0, w, h);
  const terrain = model.terrain;
  if (!terrain?.tiles?.length) return null;
  const R = terrain.radius || 1.5, half = R * Math.sqrt(3) / 2;
  let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
  for (const [x, z, water] of terrain.tiles) {
    if (water) continue;
    minX = Math.min(minX, x - half); maxX = Math.max(maxX, x + half); minZ = Math.min(minZ, z - R); maxZ = Math.max(maxZ, z + R);
  }
  if (!isFinite(minX)) return null;
  const pad = 6;
  const s = Math.min((w - pad * 2) / (maxX - minX), (h - pad * 2) / (maxZ - minZ));
  const ox = (w - (maxX - minX) * s) / 2, oy = (h - (maxZ - minZ) * s) / 2;
  const P = (x, z) => [ox + (x - minX) * s, oy + (maxZ - z) * s];
  const fog = model.fog || '';
  let maxElev = 1;
  for (const tile of terrain.tiles) maxElev = Math.max(maxElev, tile[3]);
  // Tiles: pointy-top hexagons of the world's own radius.
  terrain.tiles.forEach(([x, z, water, elev], i) => {
    if (water) return;
    const state = fog[i] || '0';
    const k = Math.min(1, elev / maxElev);
    g.fillStyle = state === '0' ? C.unknown : state === '1' ? C.remembered
      : `rgb(${C.low.map((v, j) => Math.round(v + (C.high[j] - v) * k)).join(' ')})`;
    g.beginPath();
    for (let a = 0; a < 6; a++) {
      const ang = Math.PI / 180 * (60 * a - 90);
      const [px, py] = P(x + R * Math.cos(ang), z - R * Math.sin(ang));
      a ? g.lineTo(px, py) : g.moveTo(px, py);
    }
    g.closePath(); g.fill();
    if (s * R > 5) { g.strokeStyle = C.line; g.lineWidth = 0.6; g.stroke(); }
  });
  const dot = (x, z, r, fill, stroke) => {
    const [px, py] = P(x, z);
    g.beginPath(); g.arc(px, py, r, 0, Math.PI * 2);
    if (fill) { g.fillStyle = fill; g.fill(); }
    if (stroke) { g.strokeStyle = stroke; g.lineWidth = 1.2; g.stroke(); }
  };
  for (const m of model.markers || []) {
    g.globalAlpha = m.live || m.home ? 1 : 0.55;
    if (m.kind === 'camp') { dot(m.x, m.z, 5, C.campRing); dot(m.x, m.z, 2.6, m.lit ? C.fire : C.cold); }
    else if (m.kind === 'palm') dot(m.x, m.z, 2, C.palm);
    else if (m.kind === 'resource') dot(m.x, m.z, 1.8, C.resource);
    else {
      const img = image(asset(m.icon), redraw);
      const [px, py] = P(m.x, m.z);
      if (img) { g.fillStyle = 'rgb(20 28 33 / 0.94)'; g.fillRect(px - 5, py - 5, 10, 10); g.drawImage(img, px - 4.5, py - 4.5, 9, 9); }
      else dot(m.x, m.z, 2.4, C.item);
    }
    g.globalAlpha = 1;
  }
  for (const m of model.mobs || []) dot(m.x, m.z, 2.6, m.kind === 'crab' ? C.crab : C.wolf, 'rgb(0 0 0 / 0.6)');
  for (const p of model.strangers || []) dot(p.x, p.z, 3, 'rgb(150 150 150)', C.border);
  // People: faces in a ring of their side; the one you control wears a star.
  for (const p of model.people || []) {
    const r = p.selected ? 6.5 : 5;
    const [px, py] = P(p.x, p.z);
    const ring = p.dead ? 'rgb(110 110 110)' : p.owned ? C.own : p.hostile ? C.hostile : C.friendly;
    g.beginPath(); g.arc(px, py, r + 1.6, 0, Math.PI * 2); g.fillStyle = ring; g.fill();
    const img = image(asset(p.face), redraw);
    g.save(); g.beginPath(); g.arc(px, py, r, 0, Math.PI * 2); g.clip();
    if (img) g.drawImage(img, px - r, py - r, r * 2, r * 2); else { g.fillStyle = 'rgb(40 48 54)'; g.fillRect(px - r, py - r, r * 2, r * 2); }
    g.restore();
    // The selected one wears a white outline (TacticalMapView); the dead lose the controller's star.
    if (p.selected) { g.beginPath(); g.arc(px, py, r + 3.2, 0, Math.PI * 2); g.strokeStyle = 'rgb(255 255 255 / 0.9)'; g.lineWidth = 1.4; g.stroke(); }
    if (p.controlled && !p.dead) { g.fillStyle = 'rgb(240 180 92)'; g.font = `bold ${Math.round(r * 1.5)}px system-ui`; g.fillText('★', px + r - 2, py - r + 3); }
  }
  if (model.footprint?.length > 2) {
    g.beginPath();
    model.footprint.forEach(([x, z], i) => { const [px, py] = P(x, z); i ? g.lineTo(px, py) : g.moveTo(px, py); });
    g.closePath(); g.strokeStyle = 'rgb(255 255 255 / 0.85)'; g.lineWidth = 1.2; g.stroke();
    g.fillStyle = 'rgb(255 255 255 / 0.07)'; g.fill();
  }
  return { toWorld: (px, py) => [minX + (px - ox) / s, maxZ - (py - oy) / s] };
}

export function MapScreen({ model, send }) {
  const card = useRef(null);
  const canvas = useRef(null);
  const view = useRef(null);
  const full = { ...model, terrain: terrainOf(model.terrain) };

  useEffect(() => {
    const root = document.getElementById('hex-ui');
    const report = () => root?.style.setProperty('--hx-left-stack', `${card.current ? card.current.offsetHeight : 0}px`);
    const observer = new ResizeObserver(report);
    if (card.current) observer.observe(card.current);
    report();
    return () => { observer.disconnect(); root?.style.setProperty('--hx-left-stack', '0px'); };
  }, [model.collapsed]);

  useEffect(() => {
    if (model.collapsed || !canvas.current) return;
    const redraw = () => { view.current = draw(canvas.current, full, redraw); };
    redraw();
    const observer = new ResizeObserver(redraw);
    observer.observe(canvas.current);
    return () => observer.disconnect();
  });

  if (model.collapsed) {
    return html`<button type="button" ref=${card} class="hx-map-tab hx-surface" title=${t('map.title')} aria-label=${t('map.title')}
      onClick=${() => send('expand')}>${'›'}</button>`;
  }
  return html`
    <section class="hx-map hx-surface" ref=${card} aria-label=${t('map.title')}>
      <canvas ref=${canvas} class="hx-map__canvas" onClick=${(e) => {
        const r = e.currentTarget.getBoundingClientRect();
        const k = r.width / e.currentTarget.clientWidth || 1;
        const world = view.current?.toWorld((e.clientX - r.left) / k, (e.clientY - r.top) / k);
        if (world) send('click', { x: Math.round(world[0] * 100) / 100, z: Math.round(world[1] * 100) / 100 });
      }}></canvas>
      <button type="button" class="hx-map__fold" title=${t('ui.close')} aria-label=${t('ui.close')} onClick=${() => send('collapse')}>‹</button>
    </section>`;
}

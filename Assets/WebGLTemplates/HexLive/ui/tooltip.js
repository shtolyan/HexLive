// §174.5: the one rich tooltip card of the page — what CharacterPanel's shared
// UI Toolkit card did (effect chips, need rows, sheet rows): an icon, a title
// tinted by its tone, a description and optional rows of effects with ↑/↓.
// It pops above what is pointed at, pinned to its left edge, and stays inside
// the game area; below it only when there is no room above.
//
//   <button ...${tip(() => ({ icon, title, tone, desc, rows }))}>
//   rows: [{ icon, title, up }]
import { html, useEffect, useRef, useState } from './html.js';

let current = null; // { anchor, content }
const listeners = new Set();
const emit = () => listeners.forEach((l) => l());

/** Spread onto any element: hover shows the card built by `content()`. */
export function tip(content) {
  return {
    onPointerEnter: (e) => { current = { anchor: e.currentTarget, content: content() }; emit(); },
    onPointerLeave: () => { current = null; emit(); },
  };
}

export function hideTip() {
  if (!current) return;
  current = null;
  emit();
}

/** Rendered once by main.js, after every screen. */
export function TooltipLayer() {
  const [, setVersion] = useState(0);
  const card = useRef(null);
  const [pos, setPos] = useState(null);
  useEffect(() => {
    const listener = () => setVersion((v) => v + 1);
    listeners.add(listener);
    return () => listeners.delete(listener);
  }, []);
  useEffect(() => {
    const ui = document.getElementById('hex-ui');
    if (!current || !card.current || !ui || !current.anchor.isConnected) { if (pos) setPos(null); return; }
    const s = ui.getBoundingClientRect();
    const k = s.width / ui.clientWidth || 1;
    const a = current.anchor.getBoundingClientRect();
    const w = card.current.offsetWidth, h = card.current.offsetHeight;
    const left = Math.max(6, Math.min((a.left - s.left) / k, ui.clientWidth - w - 6));
    const above = (a.top - s.top) / k - h - 8;
    const top = above >= 6 ? above : Math.min((a.bottom - s.top) / k + 8, ui.clientHeight - h - 6);
    // Only a real move re-renders; setting the same spot again would loop.
    if (!pos || Math.abs(pos.left - left) > 0.5 || Math.abs(pos.top - top) > 0.5) setPos({ left, top });
  });
  if (!current) return null;
  const c = current.content;
  return html`
    <div class="hx-tip" ref=${card} role="tooltip" data-tone=${c.tone || ''}
      style=${pos ? { left: `${pos.left}px`, top: `${pos.top}px` } : { visibility: 'hidden', left: 0, top: 0 }}>
      <header>${c.icon && html`<span class="hx-tip__icon" aria-hidden="true">${c.icon}</span>`}<b>${c.title}</b></header>
      ${c.desc && html`<p>${c.desc}</p>`}
      ${c.rows?.length > 0 && html`<ul>${c.rows.map((r, i) => html`
        <li key=${i} data-up=${r.up}><span aria-hidden="true">${r.icon}</span><span>${r.title}</span><b>${r.up ? '↑' : '↓'}</b></li>`)}</ul>`}
    </div>`;
}

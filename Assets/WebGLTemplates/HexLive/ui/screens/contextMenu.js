// §174.7: the action menu over what was clicked in the world (§121,
// ContextMenuPanel). C# builds the entries — what can be done, what cannot and
// why — and runs the picked one; the page draws the list where the click was.
//
// Model: {
//   at: { x, y },                           // the click, 0–1 of the game area from the top left
//   title,
//   entries: [{ label, enabled, hint, danger, children?: [same, one level] }],
// }
// Commands: pick { path: [i] | [i, j] }, close.
//
// A press anywhere outside closes the menu and goes no further — exactly what
// the UI Toolkit card did (TryDismissOnOutsidePress): the world never sees it.
import { html, useEffect, useRef, useState } from '../html.js';

const chevron = html`<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M6 4l4 4-4 4" stroke="currentColor"
  stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" fill="none" /></svg>`;

// Keep a box inside the game area; returns the shift to apply.
function fit(el, area, left, top) {
  const w = el.offsetWidth, h = el.offsetHeight;
  return {
    left: Math.max(4, Math.min(left, area.clientWidth - w - 4)),
    top: Math.max(4, Math.min(top, area.clientHeight - h - 4)),
  };
}

function Rows({ entries, active, setActive, onPick, onOpen, prefix, open }) {
  return entries.map((e, i) => {
    const parent = e.children?.length > 0;
    const cls = ['hx-ctx__row', !e.enabled && 'is-disabled', e.danger && 'is-danger', active === i && 'is-active', open === i && 'is-open']
      .filter(Boolean).join(' ');
    return html`<li key=${i} role="none">
      <button type="button" role="menuitem" class=${cls} aria-disabled=${!e.enabled} aria-haspopup=${parent ? 'menu' : undefined}
        aria-expanded=${parent ? open === i : undefined} id=${`ctx-${prefix}-${i}`}
        onPointerEnter=${() => { setActive(i); if (e.enabled) onOpen(parent ? i : null); }}
        onClick=${() => { if (!e.enabled) return; if (parent) onOpen(i); else onPick(i); }}>
        <span class="hx-ctx__label">${e.label}</span>
        ${parent && html`<span class="hx-ctx__chevron">${chevron}</span>`}
        ${!e.enabled && e.hint && html`<small class="hx-ctx__hint">${e.hint}</small>`}
      </button>
    </li>`;
  });
}

export function ContextMenu({ model, send }) {
  const entries = model.entries || [];
  const card = useRef(null);
  const sub = useRef(null);
  const [active, setActive] = useState(-1);
  const [open, setOpen] = useState(null);       // index of the parent whose list is shown
  const [subActive, setSubActive] = useState(-1);
  const [pos, setPos] = useState(null);
  const [subPos, setSubPos] = useState(null);

  // A new menu (another click) starts fresh.
  useEffect(() => { setActive(-1); setOpen(null); setSubActive(-1); setPos(null); }, [model.at?.x, model.at?.y, model.title]);

  // Place the card at the click, then pull it inside the game area.
  useEffect(() => {
    const area = document.getElementById('hex-ui');
    if (!card.current || !area) return;
    setPos(fit(card.current, area, (model.at?.x ?? 0.5) * area.clientWidth, (model.at?.y ?? 0.5) * area.clientHeight));
    card.current.focus({ preventScroll: true });
  }, [model.at?.x, model.at?.y, entries.length]);

  // The list opens to the right of its row, or to the left when there is no room.
  useEffect(() => {
    const area = document.getElementById('hex-ui');
    if (open == null || !sub.current || !card.current || !area) { setSubPos(null); return; }
    const row = card.current.querySelector(`#ctx-m-${open}`);
    const c = card.current.getBoundingClientRect(), r = row.getBoundingClientRect(), s = area.getBoundingClientRect();
    const k = s.width / area.clientWidth || 1;
    const w = sub.current.offsetWidth;
    let left = (c.right - s.left) / k - 4;
    if (left + w > area.clientWidth - 4) left = (c.left - s.left) / k - w + 4;
    setSubPos(fit(sub.current, area, left, (r.top - s.top) / k - 6));
  }, [open, pos]);

  const children = open != null ? entries[open]?.children || [] : [];
  const pick = (path) => send('pick', { path });
  const step = (list, from, dir) => {
    for (let n = 1; n <= list.length; n++) {
      const i = (from + dir * n + list.length * 2) % list.length;
      if (list[i].enabled) return i;
    }
    return from;
  };
  const onKey = (e) => {
    const inSub = open != null && subActive >= 0;
    const list = inSub ? children : entries;
    const cur = inSub ? subActive : active;
    const set = inSub ? setSubActive : setActive;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { set(step(list, cur, e.key === 'ArrowDown' ? 1 : -1)); e.preventDefault(); }
    else if (e.key === 'ArrowRight' && !inSub && entries[active]?.children?.length && entries[active].enabled) {
      setOpen(active); setSubActive(step(entries[active].children, -1, 1)); e.preventDefault();
    } else if (e.key === 'ArrowLeft' && inSub) { setSubActive(-1); setOpen(null); e.preventDefault(); }
    else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      if (inSub) { if (children[subActive]?.enabled) pick([open, subActive]); }
      else if (entries[active]?.enabled) {
        if (entries[active].children?.length) { setOpen(active); setSubActive(step(entries[active].children, -1, 1)); }
        else pick([active]);
      }
    } else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); if (inSub) { setSubActive(-1); setOpen(null); } else send('close'); }
  };

  return html`
    <div class="hx-ctx-layer">
      <div class="hx-ctx-catch hx-surface" onPointerDown=${() => send('close')} onContextMenu=${(e) => { e.preventDefault(); send('close'); }}></div>
      <div class="hx-ctx hx-surface" ref=${card} role="menu" aria-label=${model.title} tabindex="-1" onKeyDown=${onKey}
        onContextMenu=${(e) => e.preventDefault()}
        style=${pos ? { left: `${pos.left}px`, top: `${pos.top}px` } : { visibility: 'hidden' }}>
        <div class="hx-ctx__title">${model.title}</div>
        <ul class="hx-ctx__list" role="none">
          <${Rows} entries=${entries} active=${active} setActive=${setActive} open=${open} prefix="m"
            onOpen=${(i) => { setOpen(i); setSubActive(-1); }} onPick=${(i) => pick([i])} />
        </ul>
      </div>
      ${children.length > 0 && html`
        <div class="hx-ctx hx-ctx--sub hx-surface" ref=${sub} role="menu" aria-label=${entries[open].label}
          onContextMenu=${(e) => e.preventDefault()}
          style=${subPos ? { left: `${subPos.left}px`, top: `${subPos.top}px` } : { visibility: 'hidden' }}>
          <ul class="hx-ctx__list" role="none">
            <${Rows} entries=${children} active=${subActive} setActive=${setSubActive} prefix="s"
              onOpen=${() => {}} onPick=${(j) => pick([open, j])} />
          </ul>
        </div>`}
    </div>`;
}

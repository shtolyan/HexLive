// §174.5: the shared pieces every window is built from. A screen composes
// these; it does not restyle them.
import { html, useState, useEffect, useRef } from './html.js';
import { t } from './i18n.js';
import { icons } from './icons.js';

const chevronIcon = html`<svg class="hx-disclosure__chevron" viewBox="0 0 16 16" aria-hidden="true">
  <path d="M4 6l4 4 4-4" stroke="currentColor" stroke-width="1.75" stroke-linecap="round"
    stroke-linejoin="round" fill="none" />
</svg>`;

/**
 * A modal window over a dimmed world. With onClose it gets the close button
 * and closes on a click into the dim area; without it the player must answer.
 * size: 'wide' for content windows; tone: 'danger' for a broken state.
 */
export function ModalWindow({ id, title, onClose, size, tone, children }) {
  const titleId = `${id}-title`;
  const cls = ['hx-window', size && `hx-window--${size}`, tone && `hx-window--${tone}`].filter(Boolean).join(' ');
  return html`
    <div class="hx-backdrop hx-surface" onPointerDown=${(e) => e.target === e.currentTarget && onClose?.()}>
      <section class=${cls} role="dialog" aria-modal="true" aria-labelledby=${titleId} data-screen=${id}>
        <header class=${onClose ? 'hx-window__header' : 'hx-window__header hx-window__header--plain'}>
          <h2 class="hx-title" id=${titleId}>${title}</h2>
          ${onClose && html`
            <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')}
              onClick=${onClose}>${icons.close()}</button>`}
        </header>
        ${children}
      </section>
    </div>`;
}

export function Button({ variant, onClick, children, ...rest }) {
  const cls = variant ? `hx-btn hx-btn--${variant}` : 'hx-btn';
  return html`<button class=${cls} type="button" onClick=${onClick} ...${rest}>${children}</button>`;
}

/** The button row at the bottom of a dialog. */
export function Actions({ children }) {
  return html`<div class="hx-actions">${children}</div>`;
}

/** A foldout. Open state is the page's own (it is presentation, not game state). */
export function Disclosure({ label, hint, initiallyOpen = false, children }) {
  const [open, setOpen] = useState(initiallyOpen);
  return html`
    <div class="hx-disclosure" data-open=${open}>
      <button class="hx-disclosure__toggle" type="button" aria-expanded=${open} title=${hint}
        onClick=${() => setOpen(!open)}>
        <span>${label}</span>${chevronIcon}
      </button>
      ${open && html`<div class="hx-disclosure__body">${children}</div>`}
    </div>`;
}

/**
 * 0–100 slider. The value shown follows the hand at once; onChange is told
 * every step (Unity applies volume live, as the UI Toolkit slider did). The
 * hand's value stands until Unity sends a different one in the model.
 */
export function Slider({ label, value, onChange, name }) {
  const [local, setLocal] = useState(null);
  useEffect(() => setLocal(null), [value]);
  const shown = Math.round(local ?? value);
  return html`
    <label class="hx-slider">
      <span class="hx-slider__head">
        <span>${label}</span><span class="hx-slider__value">${shown}%</span>
      </span>
      <input type="range" min="0" max="100" step="1" name=${name} value=${shown}
        style=${{ '--fill': `${shown}%` }}
        onInput=${(e) => { const v = Number(e.currentTarget.value); setLocal(v); onChange(v); }} />
    </label>`;
}

/**
 * A 0–10 score as ten cells — one element for reading and for choosing.
 * Without onChange it only shows (the assignment card). With onChange it is a
 * control: a click on a cell sets the value to that cell, − and + step by one,
 * arrow keys too; cells past `limit` (what the budget still allows) are dim and
 * cannot be taken.
 */
export function Pips({ value, max = 10, onChange, limit = max, label }) {
  const filled = Math.max(0, Math.min(max, Math.round(value)));
  const [hover, setHover] = useState(-1);
  if (!onChange) {
    return html`<span class="hx-pips" aria-hidden="true">
      ${Array.from({ length: max }, (_, i) => html`<i class=${i < filled ? 'on' : ''}></i>`)}
    </span>`;
  }
  const cap = Math.max(filled, Math.min(max, limit));
  const set = (v) => { const next = Math.max(0, Math.min(cap, v)); if (next !== filled) onChange(next); };
  const key = (e) => {
    const step = { ArrowRight: 1, ArrowUp: 1, ArrowLeft: -1, ArrowDown: -1 }[e.key];
    if (step) { e.preventDefault(); set(filled + step); }
    if (e.key === 'Home') { e.preventDefault(); set(0); }
    if (e.key === 'End') { e.preventDefault(); set(cap); }
  };
  return html`
    <span class="hx-pips hx-pips--edit">
      <button type="button" class="hx-pips__step" tabindex="-1" aria-hidden="true" disabled=${filled <= 0}
        onClick=${() => set(filled - 1)}>−</button>
      <span class="hx-pips__cells" role="slider" tabindex="0" aria-label=${label} aria-valuemin="0"
        aria-valuemax=${max} aria-valuenow=${filled} onKeyDown=${key} onPointerLeave=${() => setHover(-1)}>
        ${Array.from({ length: max }, (_, i) => {
          const state = i < filled ? 'on' : hover >= 0 && i <= hover && i < cap ? 'hint' : i >= cap ? 'off' : '';
          return html`<i class=${state} onPointerEnter=${() => setHover(i)}
            onClick=${() => i < cap && set(i + 1 === filled ? i : i + 1)}></i>`;
        })}
      </span>
      <button type="button" class="hx-pips__step" tabindex="-1" aria-hidden="true" disabled=${filled >= cap}
        onClick=${() => set(filled + 1)}>+</button>
    </span>`;
}

/**
 * A ring meter (RingMeter in UI Toolkit): health around a face, relation
 * scores. value 0–1; children sit in the middle.
 */
export function Ring({ value, color, size = '3rem', stroke = '0.25rem', label, children }) {
  const v = Math.max(0, Math.min(1, value ?? 0));
  return html`
    <span class="hx-ring" role=${label ? 'img' : undefined} aria-label=${label}
      style=${{ width: size, height: size, '--ring': color || 'var(--hx-leaf)', '--ring-w': stroke, '--ring-v': `${v * 360}deg` }}>
      <span class="hx-ring__inner">${children}</span>
    </span>`;
}

/**
 * §174.3: a transparent hole Unity draws a camera into (camera.rect). It
 * reports its rectangle as `cmd` { x, y, w, h, vw, vh } in CSS px of the game
 * area, re-measures after the window's rise-in transform, sends an empty
 * rectangle when it goes away, and turns a horizontal drag into
 * `dollRotate` { dx }. The fill around it is its own wide shadow (CSS).
 */
export function Hole({ send, cmd, className = 'hx-inv__hole' }) {
  const hole = useRef(null);
  useEffect(() => {
    const report = () => {
      const ui = document.getElementById('hex-ui');
      const r = hole.current.getBoundingClientRect();
      const s = ui.getBoundingClientRect();
      const k = s.width / ui.clientWidth || 1;
      send(cmd, { x: (r.left - s.left) / k, y: (r.top - s.top) / k, w: r.width / k, h: r.height / k, vw: ui.clientWidth, vh: ui.clientHeight });
    };
    const observer = new ResizeObserver(report);
    observer.observe(hole.current);
    report();
    // The window rises in with a transform, which ResizeObserver does not see.
    const settle = setTimeout(report, 250);
    // Gone (craft tab, item card, window closed): Unity stops drawing there.
    return () => { observer.disconnect(); clearTimeout(settle); send(cmd, { x: 0, y: 0, w: 0, h: 0, vw: 0, vh: 0 }); };
  }, []);
  const drag = useRef(null);
  return html`<div class=${className} ref=${hole}
    onPointerDown=${(e) => { drag.current = e.clientX; e.currentTarget.setPointerCapture(e.pointerId); }}
    onPointerMove=${(e) => { if (drag.current == null) return; send('dollRotate', { dx: e.clientX - drag.current }); drag.current = e.clientX; }}
    onPointerUp=${() => { drag.current = null; }}></div>`;
}

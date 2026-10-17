// §174.5: the shared pieces every window is built from. A screen composes
// these; it does not restyle them.
import { html, useState, useEffect } from './html.js';
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

/** A 0–10 score as ten pips — character attributes (§76) read at a glance. */
export function Pips({ value, max = 10 }) {
  const filled = Math.max(0, Math.min(max, Math.round(value)));
  return html`<span class="hx-pips" aria-hidden="true">
    ${Array.from({ length: max }, (_, i) => html`<i class=${i < filled ? 'on' : ''}></i>`)}
  </span>`;
}

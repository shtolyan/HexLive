// Line icons of the HTML windows — the same set VectorIcon.cs draws in
// UI Toolkit, as inline SVG that takes the text colour (currentColor).
import { html } from './html.js';

const svg = (body, cls = 'hx-icon') => html`<svg class=${cls} viewBox="0 0 20 20" aria-hidden="true">${body}</svg>`;
const stroke = { stroke: 'currentColor', 'stroke-width': 2, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', fill: 'none' };

export const icons = {
  close: () => svg(html`<path d="M5 5l10 10M15 5L5 15" ...${stroke} />`),
  pause: () => svg(html`<path d="M7 4.5v11M13 4.5v11" ...${stroke} stroke-width="2.6" />`),
  play: () => svg(html`<path d="M6.5 4.2v11.6L16 10z" fill="currentColor" />`),
  sun: () => svg(html`<circle cx="10" cy="10" r="3.4" fill="currentColor" />
    <path d="M10 2.2v2.2M10 15.6v2.2M2.2 10h2.2M15.6 10h2.2M4.5 4.5l1.6 1.6M13.9 13.9l1.6 1.6M4.5 15.5l1.6-1.6M13.9 6.1l1.6-1.6"
      ...${stroke} stroke-width="1.6" />`),
  drop: () => svg(html`<path d="M10 2.8C7.6 6.6 5.4 9.2 5.4 12a4.6 4.6 0 009.2 0c0-2.8-2.2-5.4-4.6-9.2z" fill="currentColor" />`),
  alert: () => svg(html`<path d="M10 3.2L2.6 16.4h14.8z" ...${stroke} stroke-width="1.7" /><path d="M10 8.2v3.8" ...${stroke} />
    <circle cx="10" cy="14.2" r="1" fill="currentColor" />`),
  signal: () => svg(html`<circle cx="10" cy="10" r="4" fill="currentColor" />`, 'hx-icon hx-icon--dot'),
};

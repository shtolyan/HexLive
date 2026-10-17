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
  turnLeft: () => svg(html`<path d="M7.5 6.5H13a4 4 0 010 8H8" ...${stroke} /><path d="M10 3.5l-3 3 3 3" ...${stroke} />`),
  turnRight: () => svg(html`<path d="M12.5 6.5H7a4 4 0 000 8h5" ...${stroke} /><path d="M10 3.5l3 3-3 3" ...${stroke} />`),
  ai: () => svg(html`<rect x="4.5" y="6" width="11" height="9" rx="2.5" ...${stroke} stroke-width="1.7" />
    <path d="M10 3v3M2.5 10.5h2M15.5 10.5h2" ...${stroke} stroke-width="1.7" /><circle cx="8" cy="10.5" r="1" fill="currentColor" /><circle cx="12" cy="10.5" r="1" fill="currentColor" />`),
  hand: () => svg(html`<path d="M7 10V4.8a1.2 1.2 0 012.4 0V9m0-4.8a1.2 1.2 0 012.4 0V9m0-3.6a1.2 1.2 0 012.4 0V11c0 3.4-2 5.5-5 5.5-2.2 0-3.4-1.1-4.6-3.3L4 10.6a1.2 1.2 0 012-1.3L7 10.6" ...${stroke} stroke-width="1.6" />`),
  phone: () => svg(html`<path d="M5.2 3.5h2.3l1.2 3-1.6 1.1a8.6 8.6 0 004.3 4.3l1.1-1.6 3 1.2v2.3c0 .8-.7 1.5-1.5 1.5A12.1 12.1 0 013.7 5c0-.8.7-1.5 1.5-1.5z" ...${stroke} stroke-width="1.6" />`),
  bag: () => svg(html`<path d="M5 7.5h10l-.8 9H5.8z" ...${stroke} stroke-width="1.7" /><path d="M7.5 7.5V6a2.5 2.5 0 015 0v1.5M7.5 11h5" ...${stroke} stroke-width="1.7" />`),
  book: () => svg(html`<path d="M4 4.5c2.5-.8 4.5-.5 6 1 1.5-1.5 3.5-1.8 6-1v11c-2.5-.8-4.5-.5-6 1-1.5-1.5-3.5-1.8-6-1z" ...${stroke} stroke-width="1.6" /><path d="M10 5.5v11" ...${stroke} stroke-width="1.6" />`),
  walk: () => svg(html`<circle cx="10.5" cy="3.8" r="1.6" fill="currentColor" /><path d="M10 7l-1.5 4 2.5 2.5V17M8.5 11L6.5 17M10 7l2.5 2.5 2.5 1" ...${stroke} stroke-width="1.7" />`),
  run: () => svg(html`<circle cx="12.5" cy="3.6" r="1.6" fill="currentColor" /><path d="M11.5 6.8L8.5 10l3 2.5-1.5 4.5M8.5 10L5 9.5M11.5 6.8l2.5 3 3 .5M11.5 12.5l-4 2" ...${stroke} stroke-width="1.7" />`),
  signal: () => svg(html`<circle cx="10" cy="10" r="4" fill="currentColor" />`, 'hx-icon hx-icon--dot'),
};

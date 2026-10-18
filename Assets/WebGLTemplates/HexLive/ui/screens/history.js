// §174.7: the colony history (GameHistoryPanel, GameHistoryFormatter) — a
// drawer on the left under the HUD; folded, it is one pill. C# reads the log,
// formats each record (time, title, detail, tone) and pages it; the page only
// draws the timeline and filters what it already has by tone.
//
// Model: { collapsed, entries: [{ id, time, title, detail, tone }], more }   // newest first
//   tone: 'neutral'|'good'|'bad'|'danger'|'social'|'build'; more: older entries exist
// Commands: expand, collapse, more.
import { html, useState } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';

const FILTERS = { all: null, good: ['good'], bad: ['bad', 'danger'], social: ['social'], build: ['build'] };

export function History({ model, send }) {
  const [filter, setFilter] = useState('all');
  if (model.collapsed) {
    return html`<button type="button" class="hx-history-tab hx-surface" onClick=${() => send('expand')}>
      ${icons.book()}<span>${t('history.title')}</span></button>`;
  }
  const tones = FILTERS[filter];
  const entries = (model.entries || []).filter((e) => !tones || tones.includes(e.tone));
  return html`
    <section class="hx-history hx-surface" aria-labelledby="history-title">
      <header class="hx-history__head">
        <div>
          <h2 class="hx-history__title" id="history-title">${t('history.title')}</h2>
          <p class="hx-muted">${t('history.subtitle')}</p>
        </div>
        <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('collapse')}>${icons.close()}</button>
      </header>
      <div class="hx-chips">
        ${Object.keys(FILTERS).map((f) => html`
          <button type="button" key=${f} class="hx-chip hx-history__chip" data-tone=${f} aria-pressed=${filter === f} onClick=${() => setFilter(f)}>${t(`history.filter.${f}`)}</button>`)}
      </div>
      <ol class="hx-history__list">
        ${entries.length === 0 && html`<li class="hx-muted hx-history__empty">${t('history.empty')}</li>`}
        ${entries.map((e) => html`
          <li key=${e.id} class="hx-history__item" data-tone=${e.tone}>
            <time>${e.time}</time>
            <b>${e.title}</b>
            ${e.detail && html`<span>${e.detail}</span>`}
          </li>`)}
      </ol>
      ${model.more && html`<button type="button" class="hx-btn hx-history__more" onClick=${() => send('more')}>${t('history.show_older')}</button>`}
    </section>`;
}

// §174.7: the colonist's journal (§136) — a paper page in her handwriting.
// Deliberately NOT the dark glass of the other windows: it is the one place
// in the game where somebody writes by hand (CharacterPanel.Journal).
//
// C# formats every entry (JournalFormatter: time line, text, tone) and keeps
// the client-side «read up to» mark; entries arrive newest first.
//
// Model: { name, unread, entries: [{ id, time, text, danger, unread }] }
// Commands: close.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';

export function Journal({ model, send }) {
  const entries = model.entries || [];
  return html`
    <div class="hx-backdrop hx-surface" onPointerDown=${(e) => { if (e.target === e.currentTarget) send('close'); }}>
      <section class="hx-journal" role="dialog" aria-modal="true" aria-labelledby="journal-title">
        <header class="hx-journal__head">
          <div>
            <h2 class="hx-journal__title" id="journal-title">${model.name || t('panel.journal')}</h2>
            <p class="hx-journal__subtitle">${t('journal.subtitle', { 0: model.name })}
              ${model.unread > 0 && html` · <b>${model.unread} ${t('journal.unread')}</b>`}</p>
          </div>
          <button class="hx-journal__close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
        </header>
        <div class="hx-journal__page">
          ${entries.length === 0
            ? html`<p class="hx-journal__empty">${t('journal.empty')}</p>`
            : html`<ol class="hx-journal__entries">${entries.map((e) => html`
                <li key=${e.id} class=${`hx-journal__entry${e.danger ? ' is-danger' : ''}${e.unread ? ' is-unread' : ''}`}>
                  <time>${e.time}</time>
                  <p>${e.text}</p>
                </li>`)}</ol>`}
        </div>
      </section>
    </div>`;
}

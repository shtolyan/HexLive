// §174.7: the colony list on the right (CharacterPanel BuildRoster/RefreshRoster).
// Always on screen in play. Model: {
//   clan: { count, total },             // visible / all members of the player's camp
//   cards: [{ id, name, face, health 0–1, healthColor, status, statusTone, fighting, mine, selected }],
//   outsiders: [same shape],            // only perceived strangers
// }
// face: asset key (NpcPortraitCache), may be missing → the initial.
// Commands: select { id, add } (add = Shift: multi-select), selectClan.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { asset } from '../bridge.js';
import { Ring } from '../components.js';

function Face({ face, name, size }) {
  const url = asset(face);
  return url
    ? html`<img class="hx-face" src=${url} alt="" style=${{ width: size, height: size }} />`
    : html`<span class="hx-face hx-face--initial" style=${{ width: size, height: size }}>${(name || '?').slice(0, 1)}</span>`;
}

function Card({ card, send }) {
  return html`
    <li>
      <button type="button" class=${`hx-roster__card${card.selected ? ' is-selected' : ''}${card.fighting ? ' is-fighting' : ''}`}
        onClick=${(e) => send('select', { id: card.id, add: e.shiftKey })}>
        <${Ring} value=${card.health} color=${card.healthColor} size="2.75rem" stroke="0.1875rem">
          <${Face} face=${card.face} name=${card.name} size="2.125rem" />
        <//>
        <span class="hx-roster__text">
          <b>${card.mine ? html`<span class="hx-star" aria-hidden="true">★ </span>` : ''}${card.name}</b>
          <span class="hx-roster__status" data-tone=${card.fighting ? 'danger' : card.statusTone}>
            <i aria-hidden="true"></i>${t(card.fighting ? 'badge.fighting' : `state.${card.status}`)}
          </span>
        </span>
      </button>
    </li>`;
}

export { Face };

export function Roster({ model, send }) {
  const clan = model.clan || { count: 0, total: 0 };
  return html`
    <aside class="hx-roster hx-surface" aria-label=${t('roster.clan')}>
      <button type="button" class="hx-roster__head" onClick=${() => send('selectClan')}>
        ${t('roster.clan_count', { 0: clan.count, 1: clan.total })}
      </button>
      <ul class="hx-roster__list">${(model.cards || []).map((c) => html`<${Card} key=${c.id} card=${c} send=${send} />`)}</ul>
      ${model.outsiders?.length > 0 && html`
        <p class="hx-roster__head hx-roster__head--plain">${t('roster.outsiders')}</p>
        <ul class="hx-roster__list">${model.outsiders.map((c) => html`<${Card} key=${c.id} card=${c} send=${send} />`)}</ul>`}
    </aside>`;
}

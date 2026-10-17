// §174.7: who the player now controls (PlayerAssignmentDialog.cs, §171).
// Model: {
//   losses: [{ kind: 'died' | 'unavailable', name }],
//   characters: [{ name, intro: 'introduction' | 'replacement',
//                  attributes: [{ id, value }]  // value 0–10
//                  traits: [id] }],
//   footer: 'waiting' | 'persistence' }
// Command: ok — acknowledge; C# then selects the first character.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { ModalWindow, Button, Actions, Pips } from '../components.js';

function Character({ character }) {
  return html`
    <section class="hx-character">
      <h3 class="hx-character__name">${t(`assignment.${character.intro}`, { 0: character.name })}</h3>
      <div class="hx-section">
        <h4 class="hx-section__title">${t('assignment.attributes')}</h4>
        <ul class="hx-attributes">
          ${character.attributes.map((a) => html`
            <li key=${a.id} title=${t(`attr.${a.id}.desc`)}>
              <span>${t(`attr.${a.id}`)}</span><${Pips} value=${a.value} /><b>${a.value}</b>
            </li>`)}
        </ul>
      </div>
      <div class="hx-section">
        <h4 class="hx-section__title">${t('assignment.traits')}</h4>
        ${character.traits.length === 0
          ? html`<p class="hx-muted">${t('sheet.character.empty')}</p>`
          : html`<ul class="hx-traits">
              ${character.traits.map((id) => html`
                <li key=${id}><b>${t(`${id}.title`)}</b><span>${t(`${id}.desc`)}</span></li>`)}
            </ul>`}
      </div>
    </section>`;
}

export function PlayerAssignment({ model, send }) {
  const losses = model.losses || [];
  const characters = model.characters || [];
  return html`
    <${ModalWindow} id="player-assignment" size="wide" title=${t('assignment.title')}>
      <div class="hx-stack hx-scroll">
        ${losses.map((loss, i) => html`
          <p class="hx-notice" key=${`loss-${i}`}>${icons.alert()}<span>${t(`assignment.${loss.kind}`, { 0: loss.name })}</span></p>`)}
        ${characters.map((c) => html`<${Character} key=${c.name} character=${c} />`)}
        <p class="hx-muted">${t(`assignment.${model.footer || 'persistence'}`)}</p>
      </div>
      <${Actions}>
        <${Button} variant="primary" onClick=${() => send('ok')}>${t('assignment.ok')}<//>
      <//>
    <//>`;
}

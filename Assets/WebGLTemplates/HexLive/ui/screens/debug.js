// §174.7: the developer tools (DebugControlsPanel) — a drawer under the HUD,
// opened by its «DEBUG» button. The actions stay in C# (wounds, dirt, tan,
// fog switches, inspectors); the page lists them in groups and shows which
// switches are on. Bug reporting has its own HUD button; it is here too.
//
// Model: {
//   target: npc name | null,              // null: the tools act on everyone
//   on: { clothesHidden, fog, fogSelected, culling, trace, hexInspector },
//   shade: text,                          // memory shade style, cycled
//   ready: count of reports ready to test, canReport, canManage, console,
// }
// Commands: act { id }, close.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';

const GROUPS = [
  ['health', [['wound', 'danger'], ['bruise'], ['clearWounds']]],
  ['look', [['dirtPlus'], ['dirtMinus'], ['tanPlus'], ['tanMinus'], ['sweatPlus'], ['sweatMinus'], ['tear'], ['clothesHidden', null, true]]],
  ['view', [['fog', null, true], ['fogSelected', null, true], ['culling', null, true], ['shade'], ['trace', null, true], ['hexInspector', null, true]]],
  ['tools', [['adminVoice'], ['console']]],
];

export function Debug({ model, send }) {
  const on = model.on || {};
  return html`
    <section class="hx-debug hx-surface" aria-labelledby="debug-title">
      <header class="hx-debug__head">
        <h2 id="debug-title">DEBUG</h2>
        <span class="hx-muted">${model.target ? t('debug.target.one', { 0: model.target }) : t('debug.target.all')}</span>
        <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
      </header>
      <div class="hx-debug__groups">
        ${GROUPS.map(([group, items]) => html`
          <div key=${group} class="hx-debug__group">
            <h3 class="hx-section__title">${t(`debug.group.${group}`)}</h3>
            <div class="hx-debug__buttons">
              ${items.filter(([id]) => id !== 'console' || model.console).map(([id, tone, toggle]) => html`
                <button type="button" key=${id} class=${`hx-debug__btn${tone ? ` is-${tone}` : ''}`}
                  aria-pressed=${toggle ? !!on[id] : undefined} onClick=${() => send('act', { id })}>
                  ${toggle && html`<i class="hx-debug__check" aria-hidden="true">${on[id] ? '✓' : ''}</i>`}
                  <span>${id === 'shade' ? t('debug.shade', { 0: model.shade }) : t(`debug.${id}`)}</span>
                </button>`)}
            </div>
          </div>`)}
        <div class="hx-debug__group">
          <h3 class="hx-section__title">${t('debug.group.bugs')}</h3>
          <div class="hx-debug__buttons">
            <button type="button" class="hx-debug__btn is-accent" disabled=${!model.canReport} onClick=${() => send('act', { id: 'report' })}>
              <span>${model.ready > 0 ? t('bugs.debug.ready', { 0: model.ready }) : t('debug.report')}</span></button>
            <button type="button" class="hx-debug__btn" disabled=${!model.canManage} onClick=${() => send('act', { id: 'manager' })}>
              <span>${t('debug.manager')}</span></button>
          </div>
        </div>
      </div>
    </section>`;
}

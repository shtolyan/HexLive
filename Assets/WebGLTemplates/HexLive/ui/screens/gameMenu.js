// §174.7: the Escape menu (GameMenu.cs keeps when it opens and what each
// button does). Model: { canQuit, lang: 'ru' | 'en', volumes: { voices,
// agentVoices, music, environment } } with volumes 0–100. Commands: continue,
// mainMenu, quit, volume { category, value }, lang { code }. The language
// switch moved here from the screen corner (player, 18.10.2026).
import { html } from '../html.js';
import { t } from '../i18n.js';
import { ModalWindow, Button, Disclosure, Slider } from '../components.js';

const VOLUMES = [
  ['voices', 'audio.voices'],
  ['agentVoices', 'audio.agent_voices'],
  ['music', 'audio.music'],
  ['environment', 'audio.environment'],
];

export function GameMenu({ model, send }) {
  const volumes = model.volumes || {};
  return html`
    <${ModalWindow} id="game-menu" title=${t('menu.title')} onClose=${() => send('continue')}>
      <div class="hx-stack">
        <${Button} variant="primary" onClick=${() => send('continue')}>${t('menu.continue')}<//>
        <${Disclosure} label=${t('menu.settings')}>
          <div class="hx-lang-row" role="group" aria-labelledby="menu-lang-label">
            <span id="menu-lang-label">${t('menu.language')}</span>
            <div class="hx-segmented">
              ${['ru', 'en'].map((code) => html`<button key=${code} type="button" class="hx-seg" aria-pressed=${model.lang === code}
                onClick=${() => model.lang !== code && send('lang', { code })}>${code === 'ru' ? 'Русский' : 'English'}</button>`)}
            </div>
          </div>
          <p class="hx-hint">${t('audio.settings.hint')}</p>
          ${VOLUMES.map(([category, key]) => html`
            <${Slider} key=${category} name=${`volume-${category}`} label=${t(key)}
              value=${volumes[category] ?? 100}
              onChange=${(value) => send('volume', { category, value })} />`)}
        <//>
        <${Button} onClick=${() => send('mainMenu')}>${t('menu.mainmenu')}<//>
        ${model.canQuit && html`<${Button} variant="danger" onClick=${() => send('quit')}>${t('menu.quit')}<//>`}
      </div>
    <//>`;
}

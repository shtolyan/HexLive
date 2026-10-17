// §174.7: the server speaks a newer protocol (ProtocolUpdatePanel.cs).
// Model: { canQuit, failed } — failed after a desktop updater did not start.
// Commands: update (web: reload the page, §168.5; desktop: updater), quit.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { ModalWindow, Button, Actions } from '../components.js';

export function ProtocolUpdate({ model, send }) {
  return html`
    <${ModalWindow} id="protocol-update" size="medium" title=${t('update.required.title')}>
      <div class="hx-stack">
        <p class="hx-text">${t('update.required.message')}</p>
        ${model.failed && html`<p class="hx-error" role="alert">${t('update.start.failed')}</p>`}
        <${Actions}>
          ${model.canQuit && html`<${Button} variant="danger" onClick=${() => send('quit')}>${t('menu.quit')}<//>`}
          <${Button} variant="primary" onClick=${() => send('update')}>${t('update.action')}<//>
        <//>
      </div>
    <//>`;
}

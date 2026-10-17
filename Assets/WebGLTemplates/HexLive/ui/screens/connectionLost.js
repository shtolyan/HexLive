// §174.7: the server is gone for good (ConnectionLostDialog.cs, §145.3).
// Model: { detail } — the backend's last reason, may be empty. Command: ok.
// No close button: the only way on is back to the main menu.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { ModalWindow, Button, Actions } from '../components.js';

export function ConnectionLost({ model, send }) {
  return html`
    <${ModalWindow} id="connection-lost" size="medium" tone="danger" title=${html`${icons.alert()} ${t('net.lost.title')}`}>
      <div class="hx-stack">
        <p class="hx-text">${t('net.lost.body')}</p>
        ${model.detail && html`<p class="hx-detail">${model.detail}</p>`}
        <${Actions}>
          <${Button} variant="primary" onClick=${() => send('ok')}>${t('net.lost.ok')}<//>
        <//>
      </div>
    <//>`;
}

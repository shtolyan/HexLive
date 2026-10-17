// §174.7: the web client's title screen (LoadingScreen.cs + ServerSelectionPanel.cs).
// Unity keeps drawing the island art and the logo underneath; the page draws
// the server card and the loading strip. While the character creator (still
// UI Toolkit) is up, C# sends phase 'creator' and the page draws nothing.
//
// Model: {
//   phase: 'servers' | 'creator' | 'loading',
//   servers: [{ id, nameTerm, name, ping,            // ms, -1 unknown
//               state: 'pending' | 'offline' | 'ready' | 'disabled' | 'update' }],
//   connecting,                                      // a row was chosen
//   error: null | { key, detail },                   // the last failure, shown on the card
//   loading: { progress 0–1, step: 'connect' | 'sync' | 'island' | 'people' | 'done',
//              status: { key } | { text } },         // text: content service, already worded
// }
// Commands: play { id }, refresh.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';

const STEPS = ['connect', 'sync', 'island', 'people'];

// Same thresholds as ServerDirectory.PingClass and the term menu.servers.ping.description.
function quality(server) {
  if (server.state === 'disabled') return 'disabled';
  if (server.state === 'pending') return 'pending';
  if (server.state === 'offline' || server.ping < 0) return 'offline';
  return server.ping < 100 ? 'good' : server.ping < 250 ? 'fair' : 'poor';
}

function ServerRow({ server, connecting, send }) {
  const q = quality(server);
  const name = server.nameTerm ? t(server.nameTerm) : server.name;
  const ping = q === 'disabled' ? '' // not probed: it is switched off, not down
    : server.state === 'pending' ? t('menu.servers.checking')
    : q === 'offline' ? t('closedtest.unavailable')
    : t('menu.servers.ping', { 0: server.ping });
  const action = server.state === 'disabled' ? t('menu.servers.notready')
    : server.state === 'update' ? t('menu.servers.update')
    : t('menu.servers.play');
  const playable = server.state === 'ready' && !connecting;
  return html`
    <li class="hx-server" data-quality=${q} data-state=${server.state}>
      <span class="hx-server__dot" aria-hidden="true"></span>
      <span class="hx-server__name">${name}</span>
      ${ping && html`<span class="hx-server__ping" title=${t('menu.servers.ping.description')}>${ping}</span>`}
      <button class=${playable ? 'hx-btn hx-btn--primary hx-server__go' : 'hx-btn hx-server__go'} type="button"
        disabled=${!playable} onClick=${() => send('play', { id: server.id })}>${action}</button>
    </li>`;
}

function ServerCard({ model, send }) {
  const servers = model.servers || [];
  return html`
    <div class="hx-title-center">
      <section class="hx-window hx-window--medium hx-server-card hx-surface" aria-labelledby="servers-title">
        <header class="hx-server-card__head">
          <h2 class="hx-title" id="servers-title">${t('menu.servers.title')}</h2>
          <p class="hx-muted">${t('menu.servers.subtitle')}</p>
        </header>
        ${model.error && html`
          <div class="hx-notice" role="alert">${icons.alert()}
            <span><b>${t(model.error.key)}</b>${model.error.detail && html`<br /><span class="hx-detail-inline">${model.error.detail}</span>`}</span>
          </div>`}
        <ul class="hx-servers">
          ${servers.map((s) => html`<${ServerRow} key=${s.id} server=${s} connecting=${model.connecting} send=${send} />`)}
        </ul>
        <footer class="hx-server-card__foot">
          ${model.connecting
            ? html`<span class="hx-muted hx-spinner-text">${t('menu.servers.connecting')}</span>`
            : html`<button class="hx-link-btn" type="button" onClick=${() => send('refresh')}>${t('menu.servers.refresh')}</button>`}
        </footer>
      </section>
    </div>`;
}

function LoadingStrip({ loading }) {
  const progress = Math.max(0, Math.min(1, loading.progress || 0));
  const current = loading.step === 'done' ? STEPS.length : Math.max(0, STEPS.indexOf(loading.step));
  const status = loading.status?.key ? t(loading.status.key) : loading.status?.text || '';
  return html`
    <div class="hx-loading" role="status" aria-live="polite">
      <div class="hx-loading__row">
        <span class="hx-loading__status">${status}</span>
        <span class="hx-loading__percent">${Math.round(progress * 100)}%</span>
      </div>
      <div class="hx-loading__bar"><i style=${{ width: `${progress * 100}%` }}></i></div>
      <ol class="hx-steps">
        ${STEPS.map((step, i) => html`
          <li key=${step} data-state=${i < current ? 'done' : i === current ? 'now' : 'next'}>${t(`loading.step.${step}`)}</li>`)}
      </ol>
    </div>`;
}

export function Title({ model, send }) {
  if (model.phase === 'creator') return null;
  if (model.phase === 'loading') return html`<${LoadingStrip} loading=${model.loading || {}} />`;
  return html`<${ServerCard} model=${model} send=${send} />`;
}

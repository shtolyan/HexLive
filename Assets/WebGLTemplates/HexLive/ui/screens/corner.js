// §174.7: the account strip (CharacterPanel: language button, FPS/RAM line,
// account balance) — the last UI Toolkit bits the web page still showed, in
// their «Corner Info» document. The HUD row has no room left at 960×600, so
// it heads the right column: the roster moves down by its height
// (--hx-right-stack, as the map moves the history), clear of the page's
// fullscreen button above it.
//
// Model: {
//   lang: 'ru' | 'en',
//   stats: null | { version, fps, ms, ram, gc },     // twice a second
//   balance: null                                    // no account on this client
//     | { unavailable: true }
//     | { amount, spent, cents, approximate, pending, estimated,
//         opening, confirmed, estimate },            // USD as text, «0.00»
// }
// Commands: lang { code }.
import { html, useEffect, useRef } from '../html.js';
import { t } from '../i18n.js';
import { tip } from '../tooltip.js';

const LANGS = ['ru', 'en'];

function Balance({ b }) {
  if (b.unavailable) return html`<span class="hx-corner__balance is-off">${t('account.balance.unavailable')}</span>`;
  const sign = b.approximate ? '≈ ' : '';
  const details = () => ({
    icon: '💳', title: t('account.balance.amount', { 0: sign + b.amount }),
    desc: t('account.balance.details', { 0: b.opening, 1: b.confirmed, 2: b.estimate, 3: b.pending }),
  });
  return html`<span class="hx-corner__balance" tabindex="0" ...${tip(details)}>
    <b>${t('account.balance.amount', { 0: sign + b.amount })}</b>
    <span>${t('account.balance.spent', { 0: sign + b.spent, 1: b.cents })}</span>
    ${b.pending > 0 ? html`<span class="is-warn">${t('account.balance.pending', { 0: b.pending })}</span>`
      : b.estimated && html`<span>${t('account.balance.estimated')}</span>`}
  </span>`;
}

export function Corner({ model, send }) {
  const strip = useRef(null);
  useEffect(() => {
    const root = document.getElementById('hex-ui');
    const report = () => root?.style.setProperty('--hx-right-stack', `${strip.current ? strip.current.offsetHeight + 6 : 0}px`);
    const observer = new ResizeObserver(report);
    if (strip.current) observer.observe(strip.current);
    report();
    return () => { observer.disconnect(); root?.style.setProperty('--hx-right-stack', '0px'); };
  }, []);
  const s = model.stats;
  return html`
    <div class="hx-corner" ref=${strip}>
      <div class="hx-corner__row">
        ${model.balance && html`<${Balance} b=${model.balance} />`}
        <div class="hx-corner__lang hx-surface" role="group" aria-label="Language">
          ${LANGS.map((code) => html`<button key=${code} type="button" aria-pressed=${model.lang === code}
            onClick=${() => model.lang !== code && send('lang', { code })}>${code.toUpperCase()}</button>`)}
        </div>
      </div>
      ${s && html`<span class="hx-corner__stats" aria-hidden="true">v${s.version} · ${s.fps} FPS · ${s.ms} ms<br />RAM ${s.ram} MB · GC ${s.gc} MB</span>`}
    </div>`;
}

// §174.7: the end of the game (EndSummaryPanel) — the raft is done and the
// colony leaves the island. C# reads the completed snapshot; the page shows
// the outcome. Closing it lets the player look around the island.
//
// Model: {
//   survivors, total, fallen, day, time, raft: { done, target }, avgHealth 0–1 | null,
//   people: [{ id, name, face, health 0–1, blood 0–1, stamina 0–1, wounds }],
//   dead: [{ id, name, face, day, tile, cause }],          // cause: already a sentence part, C# localises
// }
// Commands: close.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { Ring } from '../components.js';
import { Face } from './roster.js';

const pct = (v) => `${Math.round(Math.max(0, Math.min(1, v || 0)) * 100)}%`;
const tone = (v) => (v >= 0.6 ? 'var(--hx-leaf)' : v >= 0.3 ? 'var(--hx-gold)' : 'rgb(232 104 92)');

function Metric({ label, value, color }) {
  return html`<div class="hx-end__metric"><b style=${{ color }}>${value}</b><span>${label}</span></div>`;
}

export function EndSummary({ model, send }) {
  const people = model.people || [];
  const dead = model.dead || [];
  return html`
    <div class="hx-backdrop hx-backdrop--deep hx-surface">
      <section class="hx-end" role="dialog" aria-modal="true" aria-labelledby="end-title">
        <button class="hx-close hx-end__close" type="button" aria-label=${t('end.close_tooltip')} title=${t('end.close_tooltip')}
          onClick=${() => send('close')}>${icons.close()}</button>
        <header class="hx-end__head">
          <span class="hx-end__eyebrow">${t('end.eyebrow')}</span>
          <h2 id="end-title">${t('end.title')}</h2>
          <p>${t('end.subtitle', { 0: model.survivors, 1: model.total })}</p>
        </header>
        <div class="hx-end__metrics">
          <${Metric} label=${t('end.survivors')} value=${`${model.survivors}/${model.total}`} color="var(--hx-leaf)" />
          <${Metric} label=${t('end.fallen')} value=${model.fallen} color="var(--hx-warm-red)" />
          <${Metric} label=${t('end.day')} value=${model.day} color="var(--hx-gold)" />
          <${Metric} label=${t('end.time')} value=${model.time} />
          <${Metric} label=${t('end.raft')} value=${`${model.raft?.done ?? 0}/${model.raft?.target ?? 0}`} color="var(--hx-gold)" />
          <${Metric} label=${t('end.avg_health')} value=${model.avgHealth == null ? '—' : pct(model.avgHealth)} color="var(--hx-leaf)" />
        </div>
        <div class="hx-end__cols">
          <section>
            <h3 class="hx-section__title">${t('end.survivors_title')}</h3>
            <ul class="hx-end__people">
              ${people.map((p) => html`
                <li key=${p.id}>
                  <${Ring} value=${p.health} color=${tone(p.health)} size="3rem" stroke="0.25rem" label=${t('end.hp')}>
                    <${Face} face=${p.face} name=${p.name} size="2.375rem" />
                  <//>
                  <div>
                    <b>${p.name}</b>
                    <span class="hx-end__stats">
                      <span>${t('end.hp')} ${pct(p.health)}</span><span>${t('end.blood')} ${pct(p.blood)}</span>
                      <span>${t('end.stamina')} ${pct(p.stamina)}</span><span>${t('end.wounds')} ${p.wounds}</span>
                    </span>
                  </div>
                </li>`)}
            </ul>
          </section>
          <section>
            <h3 class="hx-section__title">${t('end.fallen_title')}</h3>
            ${dead.length === 0
              ? html`<p class="hx-muted">${t('end.no_fallen')}</p>`
              : html`<ul class="hx-end__people hx-end__people--dead">
                  ${dead.map((d) => html`
                    <li key=${d.id}>
                      <span class="hx-end__ghost"><${Face} face=${d.face} name=${d.name} size="2.375rem" /></span>
                      <div>
                        <b>${d.name || t('end.unknown')}</b>
                        <span class="hx-end__stats"><span>${t('end.on_day')} ${d.day}</span><span>${t('end.tile')} ${d.tile}</span></span>
                        <span class="hx-end__cause">${d.cause}</span>
                      </div>
                    </li>`)}
                </ul>`}
          </section>
        </div>
      </section>
    </div>`;
}

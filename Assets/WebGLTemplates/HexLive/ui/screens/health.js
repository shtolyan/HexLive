// §174.7: the health window (§57) — the doll coloured by body zone and one
// row per zone. C# reads the body (hit points, trauma, wounds, bandages,
// splints, armour, prostheses) and sends numbers; the page only lays them out.
//
// The doll is CharacterDollStage in its health mode, drawn by Unity into the
// hole (dollRect), exactly as in the backpack.
//
// Model: {
//   name, dollAvailable,                    // false: no zone colouring, plain doll
//   zones: [{ id: 'Head'|'Torso'|'Pelvis'|'ArmL'|'ArmR'|'LegL'|'LegR',
//             hp 0–1, critical 0–1 (deep trauma, part of the lost hp), severed,
//             bleeding, bandaged, splint 0–1, armor 0–1, wounds (open), cut 0–1, blunt 0–1,
//             prosthetic: null | { name, hp, max, function 0–1 } }],
// }
// Commands: close, dollRect { x, y, w, h, vw, vh }, dollRotate { dx }.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { Hole } from '../components.js';

const pct = (v) => Math.round(Math.max(0, Math.min(1, v || 0)) * 100);

// CharacterDollStage.StatusColor: crit → warn below half, warn → good above.
const GOOD = [92, 184, 84], WARN = [232, 178, 60], CRIT = [232, 87, 79];
const mix = (a, b, k) => a.map((x, i) => Math.round(x + (b[i] - x) * k));
export function statusColor(hp, severed) {
  if (severed) return 'rgb(61 43 43)';
  const v = Math.max(0, Math.min(1, hp));
  const c = v >= 0.5 ? mix(WARN, GOOD, (v - 0.5) * 2) : mix(CRIT, WARN, v * 2);
  return `rgb(${c.join(' ')})`;
}

function Zone({ z }) {
  const name = t(`zone.${z.id}`);
  if (z.severed && !z.prosthetic) {
    return html`<li class="hx-zone is-severed">
      <i class="hx-zone__dot" style=${{ background: statusColor(0, true) }}></i>
      <b class="hx-zone__name">${name}</b>
      <span class="hx-zone__value">${t('health.severed')}</span>
    </li>`;
  }
  if (z.prosthetic) {
    const p = z.prosthetic;
    const cond = p.max > 0 ? p.hp / p.max : 0;
    return html`<li class="hx-zone">
      <i class="hx-zone__dot" style=${{ background: statusColor(cond, false) }}></i>
      <b class="hx-zone__name">${name}</b>
      <span class="hx-zone__value">${pct(cond * p.function)}%</span>
      <span class="hx-bar"><i style=${{ width: `${pct(cond)}%`, background: statusColor(cond, false) }}></i></span>
      <span class="hx-zone__chips"><span class="hx-zone__chip">🦾 ${p.name}</span>
        <span class="hx-zone__chip">${t('health.prosthetic_hp')} ${Math.round(p.hp * 100)}/${Math.round(p.max * 100)}</span>
        <span class="hx-zone__chip">${t('health.function', { 0: pct(cond * p.function) })}</span></span>
    </li>`;
  }
  const hp = Math.max(0, Math.min(1, z.hp));
  const critical = Math.min(hp, Math.max(0, z.critical || 0));
  const chips = [
    z.bleeding && html`<span class="hx-zone__chip is-alarm">🩸 ${t('health.bleeding')}</span>`,
    z.bandaged && html`<span class="hx-zone__chip">🩹 ${t('health.bandaged')}</span>`,
    z.splint > 0 && html`<span class="hx-zone__chip">🩼 ${t('health.splint', { 0: pct(z.splint) })}</span>`,
    z.wounds > 0 && html`<span class="hx-zone__chip">${t('health.wounds', { 0: z.wounds })}</span>`,
    z.armor > 0.005 && html`<span class="hx-zone__chip is-armor">🛡 ${pct(z.armor)}%</span>`,
  ].filter(Boolean);
  const damage = [z.cut > 0.005 && `${t('health.cut')} ${pct(z.cut)}%`, z.blunt > 0.005 && `${t('health.blunt')} ${pct(z.blunt)}%`].filter(Boolean);
  return html`<li class=${`hx-zone${critical > 0 ? ' is-critical' : ''}`}>
    <i class="hx-zone__dot" style=${{ background: statusColor(hp - critical, false) }}></i>
    <b class="hx-zone__name">${name}</b>
    <span class="hx-zone__value">${pct(hp)}%${critical > 0 && html`<small> −${pct(critical)}%</small>`}</span>
    <span class="hx-bar" title=${critical > 0 ? t('health.critical', { 0: pct(critical) }) : ''}>
      <i style=${{ width: `${pct(hp - critical)}%`, background: statusColor(hp - critical, false) }}></i>
      ${critical > 0 && html`<i class="hx-zone__trauma" style=${{ left: `${pct(hp - critical)}%`, width: `${pct(critical)}%` }}></i>`}
    </span>
    ${(chips.length > 0 || damage.length > 0) && html`<span class="hx-zone__chips">${chips}
      ${damage.length > 0 && html`<span class="hx-zone__damage">${damage.join(' · ')}</span>`}</span>`}
  </li>`;
}

Health.covers = true; // the doll hole must not show the card below (main.js)

export function Health({ model, send }) {
  const zones = model.zones || [];
  return html`
    <div class="hx-inv-layer">
      <div class="hx-inv-catch hx-surface" onPointerDown=${() => send('close')}></div>
      <section class="hx-inv hx-health has-hole hx-surface" role="dialog" aria-modal="true" aria-labelledby="health-title">
        <header class="hx-inv__head">
          <h2 class="hx-title" id="health-title">${t('panel.health')} · ${model.name}</h2>
          <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
        </header>
        <div class="hx-health__body">
          <div class="hx-health__doll">
            <${Hole} send=${send} cmd="dollRect" />
            ${model.dollAvailable === false
              ? html`<p class="hx-muted hx-health__note">${t('health.doll_unavailable')}</p>`
              : html`<div class="hx-health__legend" aria-hidden="true">
                  <span>${t('health.legend.good')}</span><i></i><span>${t('health.legend.bad')}</span>
                  <b style=${{ background: statusColor(0, true) }}></b><span>${t('health.severed')}</span>
                </div>`}
          </div>
          <ul class="hx-zones">${zones.map((z) => html`<${Zone} key=${z.id} z=${z} />`)}</ul>
        </div>
      </section>
    </div>`;
}

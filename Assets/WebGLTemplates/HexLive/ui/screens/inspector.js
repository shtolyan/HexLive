// §174.7: the hex inspector (HexInspectorPanel, DEBUG → «Инспектор гекса») —
// what lies on the clicked hex. It stands left of the roster, where the
// UI Toolkit card stood top right, and does not dim the world: the player
// keeps clicking hexes and the card follows.
//
// Model: {
//   q, r, empty,                           // empty: the snapshot has no such hex
//   objects: [{ icon, emoji, name, category, desc, stats: [row] }],
//   people: [{ name, id }],
//   hex: [row],
//   junctions: [text],                     // debug: junction flags of this hex, as the old panel printed them
// }
// row: [labelKey, value, tone] — the label is an I2 key (inspect.row.*), a
// boolean value reads yes/no; tone: 'good'|'warn'|'bad'|'water'|'dim'|null.
// Commands: close.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { Pic } from '../components.js';

const value = (v) => (v === true ? t('inspect.yes') : v === false ? t('inspect.no') : v);

function Stats({ rows, grid }) {
  if (!rows?.length) return null;
  return html`<dl class=${`hx-inspect__stats${grid ? ' is-grid' : ''}`}>
    ${rows.map(([label, v, tone], i) => html`<div key=${i}><dt>${t(`inspect.row.${label}`)}</dt><dd data-tone=${tone || ''}>${value(v)}</dd></div>`)}
  </dl>`;
}

export function Inspector({ model, send }) {
  const objects = model.objects || [];
  return html`
    <section class="hx-inspect hx-surface" aria-labelledby="inspect-title">
      <header class="hx-inspect__head">
        <div>
          <h2 id="inspect-title">${t('inspect.title')}</h2>
          <span class="hx-muted">Q ${model.q} · R ${model.r}</span>
        </div>
        <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
      </header>
      <div class="hx-inspect__body">
        ${model.empty ? html`<p class="hx-muted">${t('inspect.no_hex')}</p>` : html`
          <h3 class="hx-section__title">${t('inspect.game')}</h3>
          ${objects.length === 0 && html`<p class="hx-muted">${t('inspect.no_objects')}</p>`}
          ${objects.map((o, i) => html`
            <article key=${i} class="hx-inspect__object">
              <header>
                <span class="hx-inspect__pic"><${Pic} of=${o} /></span>
                <div><b>${o.name}</b><span>${o.category}</span></div>
              </header>
              ${o.desc && html`<p>${o.desc}</p>`}
              <${Stats} rows=${o.stats} />
            </article>`)}
          ${model.people?.length > 0 && html`
            <h3 class="hx-section__title">${t('inspect.people')}</h3>
            <ul class="hx-inspect__people">${model.people.map((p) => html`<li key=${p.id}><b>${p.name}</b><span>#${p.id}</span></li>`)}</ul>`}
          <h3 class="hx-section__title">${t('inspect.hex')}</h3>
          <${Stats} rows=${model.hex} grid />
          ${model.junctions?.length > 0 && html`
            <h3 class="hx-section__title">${t('inspect.junctions', { 0: model.junctions.length })}</h3>
            <ul class="hx-inspect__junctions">${model.junctions.map((j, i) => html`<li key=${i}>${j}</li>`)}</ul>`}`}
      </div>
    </section>`;
}

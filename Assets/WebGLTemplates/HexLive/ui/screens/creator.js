// §174.7: character creation in the web client (PlayerCharacterCreationFlow.cs,
// PlayerCharacterCreator.cs, §171.20). C# keeps the flow — account, options,
// draft, attempt, Web Lock, POST — and the 3D preview; the page draws the form.
// The preview is a transparent hole: Unity renders the stage camera straight
// into that rectangle, which the page reports with previewRect.
//
// Model (C# PlayerCharacterCreator.HtmlModel / the flow's gate): {
//   phase: 'signedOut' | 'loading' | 'failed' | 'editing',
//   messageText,                        // gate only: the flow's status, already worded (I2)
//   bannerText, busyText, pending,      // editing: flow message / submit in progress / frozen attempt
//   draft: { name, sex, skin, hair, hairColour, eyes, camp,
//            attributes: { id: 0–10 }, skills: { id: 0–10 }, traits: [id] },
//   options: { sexes, skins: [id], hair: [{ id, colours: [id] }], eyes: [id],
//              attributes: [id], skills: [id], traits: [id], pairs: [[a, b]],
//              attributeBudget, skillBudget,  // in the same 0–10 points as the cells
//              camps: [{ id, number, hasSpace, residents?, capacity? }] },  // counts: §171.10, 18.10.2026
//   labels: { 'skin:<id>' | 'hair:<id>' | 'colour:<id>': text },
//   thumbs: { 'skin:<id>' | 'hair:<id>' | 'colour:<id>': asset key },   // HtmlUi.Asset, rendered by Unity
//   tints: { '<colour id>': css colour }, eyeSwatches: { '<eye id>|default': '#rrggbb' },
//   preview: 'loading' | 'ready' | 'failed',
//   nameErrorText, blocked: { appearance, abilities, camp },  // why a step cannot go on, worded, or null
// }
// Commands: set { field, value }, look { tab } (which thumbnails to render),
// submit, back, signIn, retry, camera { action: 'left'|'right'|'body'|'face'|'orbit'|'zoom', dx, dy },
// previewRect { x, y, w, h, vw, vh } (CSS px within the game area of vw×vh).
import { html, useState, useEffect, useRef } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { Button, Pips } from '../components.js';
import { asset } from '../bridge.js';

const STEPS = ['appearance', 'abilities', 'camp'];
const LOOKS = ['skin', 'hair', 'colour'];

const sexIcon = {
  female: html`<svg class="hx-icon" viewBox="0 0 20 20" aria-hidden="true"><circle cx="10" cy="7.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M10 12v6M7 15.5h6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>`,
  male: html`<svg class="hx-icon" viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="11.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M12 8l5-5M13 3h4v4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" fill="none"/></svg>`,
};

function Tile({ selected, label, thumb, swatch, onClick, wide }) {
  return html`
    <button type="button" class=${`hx-tile${wide ? ' hx-tile--wide' : ''}`} aria-pressed=${selected} title=${label} onClick=${onClick}>
      ${swatch
        ? html`<span class="hx-tile__swatch" style=${{ background: swatch }}></span>`
        : html`<span class="hx-tile__img">${thumb ? html`<img src=${thumb} alt="" />` : html`<i class="hx-tile__wait"></i>`}</span>`}
      <span class="hx-tile__label">${label}</span>
    </button>`;
}

const label = (model, kind, id) => model.labels?.[`${kind}:${id}`] ?? t(`creator.${kind}.${id}`);

function Appearance({ model, set, send }) {
  const [look, setLookState] = useState('skin');
  const setLook = (id) => { setLookState(id); send('look', { tab: id }); };
  useEffect(() => send('look', { tab: look }), []);
  const { draft, options } = model;
  const hairIds = options.hair.map((h) => h.id);
  const hairIndex = Math.max(0, hairIds.indexOf(draft.hair));
  const hair = options.hair[hairIndex];
  const stepHair = (d) => set('hair', hairIds[(hairIndex + d + hairIds.length) % hairIds.length]);
  return html`
    <div class="hx-section">
      <div class="hx-seg-row" role="group" aria-label=${t('creator.sex')}>
        ${options.sexes.map((sex) => html`
          <button type="button" class="hx-choice" key=${sex} aria-pressed=${draft.sex === sex} onClick=${() => set('sex', sex)}>
            ${sexIcon[sex]}${t(`creator.${sex}`)}
          </button>`)}
      </div>
    </div>
    <div class="hx-subtabs" role="tablist">
      ${LOOKS.map((id) => html`
        <button type="button" role="tab" key=${id} aria-selected=${look === id} onClick=${() => setLook(id)}>${t(`creator.${id}`)}</button>`)}
    </div>
    ${look === 'skin' && html`
      <div class="hx-tiles">
        ${options.skins.map((id) => html`<${Tile} key=${id} selected=${draft.skin === id} label=${label(model, 'skin', id)}
          thumb=${asset(model.thumbs[`skin:${id}`])} onClick=${() => set('skin', id)} />`)}
      </div>
      <p class="hx-muted">${t('creator.starterClothes')}</p>`}
    ${look === 'hair' && html`
      <div class="hx-stepper">
        <button type="button" class="hx-seg" aria-label=${t('creator.previousHair')} onClick=${() => stepHair(-1)}>‹</button>
        <span>${label(model, 'hair', draft.hair)}</span>
        <button type="button" class="hx-seg" aria-label=${t('creator.nextHair')} onClick=${() => stepHair(1)}>›</button>
      </div>
      <div class="hx-tiles">
        ${options.hair.map((h) => html`<${Tile} key=${h.id} selected=${draft.hair === h.id} label=${label(model, 'hair', h.id)}
          thumb=${asset(model.thumbs[`hair:${h.id}`])} onClick=${() => set('hair', h.id)} />`)}
      </div>`}
    ${look === 'colour' && (draft.hair === 'none'
      ? html`<p class="hx-muted">${t('creator.chooseHairFirst')}</p>`
      : html`<div class="hx-swatches">
          ${hair.colours.map((id) => html`<${Tile} key=${id} wide selected=${draft.hairColour === id}
            label=${label(model, 'colour', id)} swatch=${model.tints[id]} thumb=${asset(model.thumbs[`colour:${id}`])}
            onClick=${() => set('hairColour', id)} />`)}
        </div>`)}`;
}

// The same ten cells as the assignment card, as a control (§174.5).
function PointSlider({ id, kind, value, left, onChange }) {
  const label = t(`${kind}.${id}`);
  return html`
    <div class="hx-points">
      <span title=${t(`${kind}.${id}.desc`)}>${label}</span>
      <${Pips} value=${value} limit=${value + Math.max(0, left)} label=${label} onChange=${onChange} />
      <b>${value}</b>
    </div>`;
}

function Budget({ used, total }) {
  const left = total - used;
  return html`<span class=${left < 0 ? 'hx-budget hx-budget--over' : 'hx-budget'}>${t('creator.budget', { 0: left, 1: total })}</span>`;
}

function Abilities({ model, set }) {
  const { draft, options } = model;
  const sum = (o) => Object.values(o).reduce((a, b) => a + b, 0);
  const paired = new Set(options.pairs.flat());
  const toggle = (id) => {
    const pair = options.pairs.find((p) => p.includes(id));
    let traits = draft.traits.includes(id) ? draft.traits.filter((x) => x !== id) : [...draft.traits, id];
    if (pair && traits.includes(id)) traits = traits.filter((x) => x === id || !pair.includes(x)); // opposites exclude
    set('traits', traits);
  };
  const chip = (id) => html`<button type="button" class="hx-chip" key=${id} aria-pressed=${draft.traits.includes(id)}
    title=${t(`trait.${id.toLowerCase()}.desc`)} onClick=${() => toggle(id)}>${t(`trait.${id.toLowerCase()}.title`)}</button>`;
  return html`
    <p class="hx-muted">${t('creator.abilitiesHint')}</p>
    <section class="hx-section">
      <header class="hx-section__head"><h4 class="hx-section__title">${t('creator.attributes')}</h4>
        <${Budget} used=${sum(draft.attributes)} total=${options.attributeBudget} /></header>
      ${options.attributes.map((id) => html`<${PointSlider} key=${id} id=${id.toLowerCase()} kind="attr" value=${draft.attributes[id] ?? 0}
        left=${options.attributeBudget - sum(draft.attributes)} onChange=${(v) => set('attributes', { ...draft.attributes, [id]: v })} />`)}
    </section>
    <section class="hx-section">
      <header class="hx-section__head"><h4 class="hx-section__title">${t('creator.skills')}</h4>
        <${Budget} used=${sum(draft.skills)} total=${options.skillBudget} /></header>
      ${options.skills.map((id) => html`<${PointSlider} key=${id} id=${id.toLowerCase()} kind="skill" value=${draft.skills[id] ?? 0}
        left=${options.skillBudget - sum(draft.skills)} onChange=${(v) => set('skills', { ...draft.skills, [id]: v })} />`)}
    </section>
    <section class="hx-section">
      <h4 class="hx-section__title">${t('creator.traits')}</h4>
      ${options.pairs.map(([a, b]) => html`<div class="hx-pair" key=${a}>${chip(a)}<span class="hx-pair__sep" aria-hidden="true"></span>${chip(b)}</div>`)}
      <div class="hx-chips">${options.traits.filter((id) => !paired.has(id)).map(chip)}</div>
    </section>`;
}

function Camp({ model, set }) {
  return html`
    <p class="hx-muted">${t('creator.campHint')}</p>
    <div class="hx-camps">
      ${model.options.camps.map((c) => html`
        <button type="button" class="hx-camp" key=${c.id} disabled=${!c.hasSpace} aria-pressed=${model.draft.camp === c.id}
          onClick=${() => set('camp', c.id)}>
          <b>${t('creator.campNumber', { 0: c.number })}</b>
          ${c.capacity > 0 && html`
            <span class="hx-camp__people">${t('creator.campResidents', { 0: c.residents, 1: c.capacity })}</span>
            <span class="hx-camp__bar" aria-hidden="true"><i style=${{ width: `${Math.min(100, (100 * c.residents) / c.capacity)}%` }}></i></span>`}
          <span>${t(c.hasSpace ? 'creator.campOpen' : 'creator.campFull')}</span>
        </button>`)}
    </div>
    <p class="hx-muted">${t('creator.confirmHint')}</p>`;
}

function Stage({ model, send, step }) {
  const hole = useRef(null);
  const drag = useRef(null);
  useEffect(() => {
    const report = () => {
      const r = hole.current.getBoundingClientRect();
      const stage = document.getElementById('hex-ui').getBoundingClientRect();
      const k = stage.width / document.getElementById('hex-ui').clientWidth || 1; // the stand scales the game area
      const ui = document.getElementById('hex-ui');
      send('previewRect', { x: (r.left - stage.left) / k, y: (r.top - stage.top) / k, w: r.width / k, h: r.height / k,
        vw: ui.clientWidth, vh: ui.clientHeight });
    };
    const observer = new ResizeObserver(report);
    observer.observe(hole.current);
    report();
    return () => observer.disconnect();
  }, []);
  const down = (e) => { drag.current = { x: e.clientX, y: e.clientY }; e.currentTarget.setPointerCapture(e.pointerId); };
  const move = (e) => {
    if (!drag.current) return;
    send('camera', { action: 'orbit', dx: e.clientX - drag.current.x, dy: e.clientY - drag.current.y });
    drag.current = { x: e.clientX, y: e.clientY };
  };
  const eyes = model.options.eyes;
  return html`
    <div class="hx-stage">
      <div class="hx-stage__hole hx-surface" ref=${hole} onPointerDown=${down} onPointerMove=${move}
        onPointerUp=${() => { drag.current = null; }} onWheel=${(e) => send('camera', { action: 'zoom', dy: Math.sign(e.deltaY) })}>
        ${model.preview !== 'ready' && html`<p class="hx-stage__state">${t(model.preview === 'failed' ? 'lobby.appearanceUnavailable' : 'lobby.loadingAppearance')}</p>`}
      </div>
      <div class="hx-stage__tools hx-surface">
        <button type="button" class="hx-seg" aria-label=${t('creator.rotateLeft')} title=${t('creator.rotateLeft')} onClick=${() => send('camera', { action: 'left' })}>${icons.turnLeft()}</button>
        <button type="button" class="hx-seg" onClick=${() => send('camera', { action: 'body' })}>${t('creator.fullBody')}</button>
        <button type="button" class="hx-seg" onClick=${() => send('camera', { action: 'face' })}>${t('creator.face')}</button>
        <button type="button" class="hx-seg" aria-label=${t('creator.rotateRight')} title=${t('creator.rotateRight')} onClick=${() => send('camera', { action: 'right' })}>${icons.turnRight()}</button>
      </div>
      ${step === 'appearance' && html`
        <div class="hx-eyes hx-surface" role="group" aria-label=${t('creator.eyes')}>
          <span class="hx-eyes__title">${t('creator.eyes')}</span>
          ${eyes.map((id) => html`
            <button type="button" class="hx-eye" key=${id || 'default'} aria-pressed=${model.draft.eyes === id}
              title=${t(`creator.eye.${id || 'default'}`)} aria-label=${t(`creator.eye.${id || 'default'}`)}
              onClick=${() => send('set', { field: 'eyes', value: id })}>
              <i style=${{ background: model.eyeSwatches[id || 'default'] }}></i>
            </button>`)}
        </div>`}
      <p class="hx-stage__hint">${t('creator.orbitHint')}</p>
    </div>`;
}

function NameField({ value, error, set }) {
  // The field is the page's own: typing never waits for Unity (the #473 trap).
  const [text, setText] = useState(value);
  const timer = useRef(0);
  return html`
    <label class="hx-field">
      <span class="hx-field__label">${t('creator.name')}</span>
      <input type="text" maxlength="64" autocomplete="off" spellcheck="false" value=${text} name="creator-name"
        aria-invalid=${!!error} onInput=${(e) => {
          const v = e.currentTarget.value;
          setText(v);
          clearTimeout(timer.current);
          timer.current = setTimeout(() => set('name', v), 150);
        }} />
      <span class=${error ? 'hx-field__error' : 'hx-field__hint'}>${error || t('creator.nameHint')}</span>
    </label>`;
}

function Editor({ model, send }) {
  const [step, setStep] = useState('appearance');
  const set = (field, value) => send('set', { field, value });
  const index = STEPS.indexOf(step);
  const blocked = model.blocked?.[step];
  const locked = model.pending || !!model.busyText;
  const last = step === 'camp';
  const primary = model.pending ? t('creator.retryCreation') : last ? t('creator.create') : t('creator.next');
  const go = () => (model.pending || last ? send('submit') : setStep(STEPS[index + 1]));
  return html`
    <div class="hx-creator hx-surface">
      <header class="hx-creator__head">
        <div><p class="hx-eyebrow">${t('creator.eyebrow')}</p><h2 class="hx-title">${t('creator.title')}</h2></div>
        <${Button} onClick=${() => send('back')}>${t('creator.back')}<//>
      </header>
      <div class="hx-creator__body">
        <${Stage} model=${model} send=${send} step=${step} />
        <section class="hx-panel" aria-label=${t('creator.title')}>
          ${model.bannerText && html`<p class="hx-notice" role="alert">${icons.alert()}<span>${model.bannerText}</span></p>`}
          <${NameField} value=${model.draft.name} error=${model.nameErrorText} set=${set} />
          <ol class="hx-steps-tabs" role="tablist">
            ${STEPS.map((id, i) => html`
              <li key=${id}><button type="button" role="tab" aria-selected=${step === id} disabled=${locked}
                onClick=${() => setStep(id)}><b>${i + 1}</b>${t(`creator.${id}`)}</button></li>`)}
          </ol>
          <div class="hx-panel__scroll" inert=${locked}>
            ${step === 'appearance' && html`<${Appearance} model=${model} set=${set} send=${send} />`}
            ${step === 'abilities' && html`<${Abilities} model=${model} set=${set} />`}
            ${step === 'camp' && html`<${Camp} model=${model} set=${set} />`}
          </div>
          <footer class="hx-panel__foot">
            <div class="hx-panel__summary">
              <b>${model.draft.name.trim() || t('creator.summary')}</b>
              ${(model.busyText || blocked) && html`<span class=${model.busyText ? 'hx-muted hx-spinner-text' : 'hx-warn'}>${model.busyText || blocked}</span>`}
            </div>
            ${(model.pending || model.preview === 'failed') && html`<${Button} onClick=${() => send('retry')}>${t('creator.retry')}<//>`}
            <${Button} variant="primary" disabled=${!!blocked || !!model.busyText} title=${blocked || undefined} onClick=${go}>${primary}<//>
          </footer>
        </section>
      </div>
    </div>`;
}

function Gate({ model, send }) {
  // Signed out, loading options, or failed before the editor could open.
  return html`
    <div class="hx-title-center">
      <section class="hx-window hx-window--medium hx-surface hx-stack">
        <p class="hx-eyebrow">${t('creator.eyebrow')}</p>
        <h2 class="hx-title hx-title--left">${t('creator.title')}</h2>
        ${model.phase === 'loading' && html`<p class="hx-muted hx-spinner-text">${model.messageText || t('creator.loading')}</p>`}
        ${model.phase === 'signedOut' && html`<p class="hx-text">${t('creator.signInHint')}</p>`}
        ${model.phase === 'failed' && html`<p class="hx-notice" role="alert">${icons.alert()}<span>${model.messageText || t('creator.error.network')}</span></p>`}
        <div class="hx-actions">
          <${Button} onClick=${() => send('back')}>${t('creator.back')}<//>
          ${model.phase === 'signedOut' && html`<${Button} variant="primary" onClick=${() => send('signIn')}>${t('creator.signIn')}<//>`}
          ${model.phase === 'failed' && html`<${Button} variant="primary" onClick=${() => send('retry')}>${t('creator.retry')}<//>`}
        </div>
      </section>
    </div>`;
}

export function Creator({ model, send }) {
  if (model.phase !== 'editing') return html`<${Gate} model=${model} send=${send} />`;
  return html`<${Editor} model=${model} send=${send} />`;
}

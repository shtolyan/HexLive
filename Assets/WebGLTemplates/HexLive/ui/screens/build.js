// §174.7: the build tray (§120.7 BuildModePanel) — a Sims-style Build/Buy tray
// at the bottom while build mode is on. The ghost, the green/red check, the
// grid dots and the placement click all stay in Unity and the world; the page
// is the catalogue and the card of what is chosen.
//
// The same tray is the house constructor (§120.8/§120.10 HutLayoutDesigner,
// screen «blueprint»): «Свой проект» and «Свободное строительство» draw
// floors, walls, openings and roofs right in the world. It differs only by
// `editor` — a header with the Select tool, Undo/Redo, Exit and Build — and by
// what the selection card can do.
//
// Model: {
//   mode: 'furniture'|'construction', tabs?: ['construction', 'furniture'],
//   categories: [{ id, label }], category,     // the open category (its label is the cards' title)
//   cards: [{ id, name, desc, icon?, glyph }], active,   // active: the card on the cursor, or ''
//   selection: { name, desc, availability: 'ready'|'blocked'|'floor'|null, canDelete, canEdit, canFinish },
//   status | statusText,                        // a term, or text C# already wrote (a validation message)
//   arrows: null | { x, y },                    // the ghost or chosen site on screen, 0–1 from the top left
//   editor?: { select, canUndo, canRedo, build: 'build'|'apply'|null },
// }
// Commands — tray: mode { value }, category { id }, card { id }, rotate { dir: -1|1 }, delete, edit, finish.
// Commands — blueprint: mode, category, card, select, undo, redo, delete, done (drop the selection), exit, build.
// The constructor's mouse/keyboard hint (blueprint.shortcuts) runs along its bottom edge.
// Q/E, Ctrl+Z and Escape stay Unity's keys: the tray never takes the keyboard.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { Button, Pic } from '../components.js';

const AVAIL = { ready: 'blueprint.availability.ready', blocked: 'build.mode.blocked', floor: 'blueprint.availability.floor' };

function Arrows({ at, send }) {
  if (!at) return null;
  return html`<div class="hx-build-arrows" style=${{ left: `${at.x * 100}%`, top: `${at.y * 100}%` }}>
    <button type="button" class="hx-build-arrow hx-surface" title="Q" aria-label=${t('build.rotate.left')} onClick=${() => send('rotate', { dir: -1 })}>${icons.rotateLeft()}</button>
    <button type="button" class="hx-build-arrow hx-surface" title="E" aria-label=${t('build.rotate.right')} onClick=${() => send('rotate', { dir: 1 })}>${icons.rotateRight()}</button>
  </div>`;
}

function Header({ model, send }) {
  const ed = model.editor;
  return html`<header class="hx-build__head">
    <div class="hx-toggle" role="tablist">
      ${(model.tabs || ['furniture', 'construction']).map((m) => html`
        <button type="button" role="tab" key=${m} aria-pressed=${model.mode === m} onClick=${() => send('mode', { value: m })}>${t(`blueprint.mode.${m}`)}</button>`)}
    </div>
    ${(() => { const text = model.statusText ?? t(model.status || 'build.mode.hint');
      return html`<p class="hx-build__status" title=${text}>${text}</p>`; })()}
    ${ed ? html`
      <div class="hx-build__tools">
        <button type="button" class="hx-seg hx-seg--wide" aria-pressed=${!!ed.select} onClick=${() => send('select')}>${t('blueprint.action.select')}</button>
        <button type="button" class="hx-seg hx-seg--wide" disabled=${!ed.canUndo} title="Ctrl+Z" onClick=${() => send('undo')}>${icons.turnLeft()}${t('blueprint.action.undo')}</button>
        <button type="button" class="hx-seg hx-seg--wide" disabled=${!ed.canRedo} title="Ctrl+Y" onClick=${() => send('redo')}>${icons.turnRight()}${t('blueprint.action.redo')}</button>
      </div>
      <${Button} onClick=${() => send('exit')}>${t('blueprint.action.exit')}<//>
      ${ed.build && html`<${Button} variant="primary" onClick=${() => send('build')}>${t(`blueprint.action.${ed.build}`)}<//>`}`
    : html`<${Button} variant="primary" onClick=${() => send('finish')}>${t('blueprint.action.finish')}<//>`}
  </header>`;
}

export function Build({ model, send }) {
  const sel = model.selection || {};
  const ed = model.editor;
  const title = (model.categories || []).find((c) => c.id === model.category)?.label;
  return html`
    <${Arrows} at=${model.arrows} send=${send} />
    <section class="hx-build hx-surface" aria-label=${t(ed ? 'blueprint.title' : 'build.mode.button')}>
      <${Header} model=${model} send=${send} />
      <div class="hx-build__body">
        <nav class="hx-build__cats" aria-label=${t('blueprint.mode.' + model.mode)}>
          ${(model.categories || []).map((c) => html`
            <button type="button" key=${c.id} class="hx-build__cat" aria-pressed=${c.id === model.category}
              onClick=${() => send('category', { id: c.id })}>${c.label}</button>`)}
        </nav>
        <div class="hx-build__catalog">
          <h3 class="hx-section__title">${title}</h3>
          <ul class="hx-build__cards">
            ${(model.cards || []).map((c) => html`
              <li key=${c.id}><button type="button" class=${`hx-build__card${c.id === model.active ? ' is-active' : ''}`}
                aria-pressed=${c.id === model.active} onClick=${() => send('card', { id: c.id })}>
                <span class="hx-build__thumb"><${Pic} of=${c} fallback=${html`<span class="hx-build__glyph">${c.glyph}</span>`} /></span>
                <b>${c.name}</b>
                <small>${c.desc}</small>
              </button></li>`)}
          </ul>
        </div>
        <aside class="hx-build__sel">
          <span class="hx-build__sel-title">${t('blueprint.selection.title')}</span>
          <b class="hx-build__sel-name">${sel.name || t('blueprint.selection.none')}</b>
          <p class="hx-build__sel-desc">${sel.desc || t('blueprint.catalog.help')}</p>
          ${sel.availability && html`<span class=${`hx-build__avail is-${sel.availability === 'ready' ? 'ready' : 'blocked'}`}>${t(AVAIL[sel.availability])}</span>`}
          <div class="hx-build__sel-actions">
            ${sel.canEdit && html`<${Button} onClick=${() => send('edit')}>${t('blueprint.action.edit_building')}<//>`}
            ${ed ? html`
              <${Button} variant="danger" disabled=${!sel.canDelete} onClick=${() => send('delete')}>${t('blueprint.action.delete')}<//>
              <${Button} disabled=${!sel.canFinish} onClick=${() => send('done')}>${t('blueprint.action.finish')}<//>`
            : sel.canDelete && html`<${Button} variant="danger" onClick=${() => send('delete')}>${t('blueprint.action.delete')}<//>`}
          </div>
        </aside>
      </div>
      ${ed && html`<p class="hx-build__hint" title=${t('blueprint.shortcuts')}>${t('blueprint.shortcuts')}</p>`}
    </section>`;
}

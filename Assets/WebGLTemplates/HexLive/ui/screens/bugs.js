// §174.7: the bug tracker in the game (§114, BugReportPanel + BugReportStore).
// Two screens, as in §114.1: the small centred quick form («bugReport») and
// the big manager («bugManager»): tabs, status filters, a list on the left
// and the chosen report's card on the right (the §114.3B card over the list).
// C# talks to Flashback; the page only shows and edits text.
//
// bugReport model: { context, sending, error }
//   Commands: submit { text }, manager, close.
// bugManager model: {
//   tab: 'open'|'fixed'|'archive', filter: status|null, counts: { open, fixed, archive },
//   reports: [{ id, status, archived, title, created, comments, build: 'included'|'not_included'|null }],
//   card: null | { id, status, archived, created, meta: [text], build, text, context, commits: [sha],
//                  comments: [{ author: 'user'|'codex'|'claude', text, editable }],
//                  can: { fix, rework, archive, restore } },
// }
//   Commands: close, new, tab { value }, filter { status|null }, open { id }, back,
//   editText { id, text }, comment { id, text }, editComment { id, index, text },
//   fixed { id }, rework { id, text }, archive { id, value }.
import { html, useState, useEffect, useRef } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { Button, ModalWindow } from '../components.js';

const STATUSES = ['created', 'in_progress', 'ready_for_test', 'rework', 'fixed'];

function Chip({ status, archived }) {
  return html`<span class="hx-bug-chip" data-status=${status}>${t(`bugs.status.${status}`)}${archived ? ' · 🗄' : ''}</span>`;
}

function Editor({ initial = '', placeholder, confirm, tone, onSave, onCancel, autoFocus = true }) {
  const [text, setText] = useState(initial);
  const area = useRef(null);
  useEffect(() => { if (autoFocus) area.current?.focus(); }, []);
  return html`<div class="hx-bug-editor">
    <textarea ref=${area} rows="3" value=${text} placeholder=${placeholder} onInput=${(e) => setText(e.currentTarget.value)}></textarea>
    <div class="hx-actions hx-actions--left">
      <${Button} variant=${tone || 'primary'} disabled=${!text.trim()} onClick=${() => onSave(text.trim())}>${confirm}<//>
      ${onCancel && html`<${Button} onClick=${onCancel}>${t('bugs.cancel')}<//>`}
    </div>
  </div>`;
}

export function BugReport({ model, send }) {
  const [text, setText] = useState('');
  const area = useRef(null);
  useEffect(() => { area.current?.focus(); }, []);
  return html`
    <${ModalWindow} id="bug-quick" title=${t('bugs.quick.title')} size="wide" onClose=${() => send('close')}>
      <p class="hx-muted">${t('bugs.quick.hint')}</p>
      <textarea ref=${area} class="hx-bug-quick__text" rows="6" value=${text} onInput=${(e) => setText(e.currentTarget.value)}
        onKeyDown=${(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && text.trim()) send('submit', { text: text.trim() }); }}></textarea>
      ${model.context && html`<code class="hx-bug-context">${model.context}</code>`}
      ${model.error && html`<p class="hx-warn">${model.error}</p>`}
      <div class="hx-actions">
        <${Button} onClick=${() => send('manager')}>${t('bugs.open_manager')}<//>
        <${Button} variant="primary" disabled=${!text.trim() || model.sending} title="Ctrl+Enter"
          onClick=${() => send('submit', { text: text.trim() })}>${t('bugs.submit')}<//>
      </div>
    <//>`;
}

function Card({ card, send }) {
  const [editing, setEditing] = useState(null); // 'text' | 'comment' | 'rework' | { index }
  useEffect(() => setEditing(null), [card.id]);
  const id = card.id;
  return html`
    <article class="hx-bug-card" aria-label=${`#${id}`}>
      <header class="hx-bug-card__head">
        <button type="button" class="hx-link-back" onClick=${() => send('back')}>‹ ${t('inv.back')}</button>
        <${Chip} status=${card.status} archived=${card.archived} />
        ${card.build && html`<span class="hx-bug-build" data-in=${card.build === 'included'}>${t(`bugs.build.${card.build}`)}</span>`}
      </header>
      <p class="hx-bug-card__meta">#${id} · ${card.created}${card.meta.map((m) => ` · ${m}`).join('')}</p>
      ${editing === 'text'
        ? html`<${Editor} initial=${card.text} confirm=${t('bugs.save')} onCancel=${() => setEditing(null)}
            onSave=${(text) => { send('editText', { id, text }); setEditing(null); }} />`
        : html`<p class="hx-bug-card__text">${card.text}</p>`}
      ${card.context && html`<code class="hx-bug-context">${card.context}</code>`}
      ${card.commits.length > 0 && html`<p class="hx-bug-commits">${t('bugs.fix_commits', { 0: '' })}${card.commits.map((c) => html`<code key=${c}>${c}</code>`)}</p>`}
      ${card.comments.length > 0 && html`
        <ol class="hx-bug-thread">
          ${card.comments.map((c, index) => html`
            <li key=${index} data-author=${c.author}>
              <b>${c.author === 'user' ? t('bugs.author.user') : c.author === 'codex' ? 'Codex' : 'Claude'}</b>
              ${editing?.index === index
                ? html`<${Editor} initial=${c.text} confirm=${t('bugs.save')} onCancel=${() => setEditing(null)}
                    onSave=${(text) => { send('editComment', { id, index, text }); setEditing(null); }} />`
                : html`<p>${c.text}</p>${c.editable && html`<button type="button" class="hx-link-back" onClick=${() => setEditing({ index })}>${t('bugs.edit')}</button>`}`}
            </li>`)}
        </ol>`}
      ${editing === 'comment' && html`<${Editor} confirm=${t('bugs.add')} onCancel=${() => setEditing(null)}
        onSave=${(text) => { send('comment', { id, text }); setEditing(null); }} />`}
      ${editing === 'rework' && html`<${Editor} confirm=${t('bugs.return_with_comment')} tone="danger" onCancel=${() => setEditing(null)}
        onSave=${(text) => { send('rework', { id, text }); setEditing(null); }} />`}
      ${editing == null && html`
        <div class="hx-actions hx-actions--left hx-bug-card__actions">
          ${card.can.fix && html`<${Button} variant="primary" onClick=${() => send('fixed', { id })}>${t('bugs.mark_fixed')}<//>`}
          ${card.can.rework && html`<${Button} variant="danger" onClick=${() => setEditing('rework')}>${t('bugs.rework')}<//>`}
          <${Button} onClick=${() => setEditing('comment')}>${t('bugs.comment')}<//>
          <${Button} onClick=${() => setEditing('text')}>${t('bugs.edit_report')}<//>
          ${(card.can.archive || card.can.restore) && html`<${Button} onClick=${() => send('archive', { id, value: !card.archived })}>${t(card.archived ? 'bugs.restore' : 'bugs.archive')}<//>`}
        </div>`}
    </article>`;
}

export function BugManager({ model, send }) {
  const counts = model.counts || {};
  return html`
    <div class="hx-backdrop hx-surface" onPointerDown=${(e) => { if (e.target === e.currentTarget) send('close'); }}>
      <section class="hx-bugs" role="dialog" aria-modal="true" aria-labelledby="bugs-title">
        <header class="hx-inv__head">
          <h2 class="hx-title" id="bugs-title">${t('bugs.manager.title')}</h2>
          <${Button} variant="primary" onClick=${() => send('new')}>${t('bugs.new')}<//>
          <button class="hx-close" type="button" aria-label=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
        </header>
        <div class="hx-subtabs" role="tablist">
          ${['open', 'fixed', 'archive'].map((tab) => html`
            <button type="button" role="tab" key=${tab} aria-selected=${model.tab === tab} onClick=${() => send('tab', { value: tab })}>
              ${t(`bugs.tab.${tab}`)}${counts[tab] != null && html` <span class="hx-count">${counts[tab]}</span>`}</button>`)}
        </div>
        <div class="hx-chips hx-bug-filters">
          <button type="button" class="hx-chip" aria-pressed=${!model.filter} onClick=${() => send('filter', { status: null })}>${t('bugs.filter.all')}</button>
          ${STATUSES.map((s) => html`<button type="button" key=${s} class="hx-chip hx-bug-chip" data-status=${s} aria-pressed=${model.filter === s}
            onClick=${() => send('filter', { status: model.filter === s ? null : s })}>${t(`bugs.status.${s}`)}</button>`)}
        </div>
        <div class=${`hx-bugs__body${model.card ? ' has-card' : ''}`}>
          <ul class="hx-bugs__list">
            ${(model.reports || []).length === 0 && html`<li class="hx-muted hx-bugs__empty">${t('bugs.empty')}</li>`}
            ${(model.reports || []).map((r) => html`
              <li key=${r.id}><button type="button" class=${`hx-bug-row${model.card?.id === r.id ? ' is-selected' : ''}`} onClick=${() => send('open', { id: r.id })}>
                <span class="hx-bug-row__top"><${Chip} status=${r.status} archived=${r.archived} /><span class="hx-muted">#${r.id} · ${r.created}</span>
                  ${r.comments > 0 && html`<span class="hx-bug-row__count" title=${t('bugs.comment')}>💬 ${r.comments}</span>`}</span>
                <span class="hx-bug-row__title">${r.title}</span>
                ${r.build && html`<span class="hx-bug-build" data-in=${r.build === 'included'}>${t(`bugs.build.${r.build}`)}</span>`}
              </button></li>`)}
          </ul>
          ${model.card && html`<${Card} card=${model.card} send=${send} />`}
        </div>
      </section>
    </div>`;
}

// §174.7: an unfinished craft project in the world (CraftProjectDetailsPanel,
// «Подробнее» in its action menu): what it is, who works on it, how far it got.
// Taking or resuming it stays in the action menu.
//
// Model: { name, icon, emoji, batch, status: 'ready'|'active'|'paused',
//          worker: null | { name, current }, progress 0–1, remaining, required }
// Commands: close.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { ModalWindow, Pic } from '../components.js';

export function CraftProject({ model, send }) {
  const pct = Math.round(Math.max(0, Math.min(1, model.progress || 0)) * 100);
  const worker = model.worker;
  return html`
    <${ModalWindow} id="craft-project" title=${t('craft.project.title')} size="medium" onClose=${() => send('close')}>
      <div class="hx-project">
        <span class="hx-project__pic"><${Pic} of=${model} /></span>
        <div class="hx-project__what">
          <b>${model.name}${model.batch > 1 ? ` × ${model.batch}` : ''}</b>
          <span class="hx-project__status" data-status=${model.status}>${t(`craft.project.${model.status}`)}</span>
        </div>
      </div>
      <p class="hx-project__worker">${worker
        ? t(worker.current ? 'craft.project.current_worker' : 'craft.project.last_worker', { 0: worker.name })
        : t('craft.project.last_worker', { 0: t('craft.project.unknown_worker') })}</p>
      <div class="hx-project__progress">
        <span class="hx-bar"><i style=${{ width: `${pct}%`, background: model.status === 'ready' ? 'var(--hx-leaf)' : 'var(--hx-gold)' }}></i></span>
        <b>${pct}%</b>
      </div>
      <p class="hx-muted">${t('craft.project.remaining', { 0: model.remaining, 1: model.required })}</p>
    <//>`;
}

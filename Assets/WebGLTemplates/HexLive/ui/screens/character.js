// §174.7: the selected colonist's card at the bottom (CharacterPanel BuildUi:
// identity column, needs/sheet column, relations column) and the pills above it
// (thought, dream, directive, effects). Opened windows (inventory, health,
// journal) are separate screens.
//
// The live portrait (PortraitStage) is a transparent hole like the creator's
// preview: Unity renders that camera into portraitRect. Model: {
//   mode: 'none' | 'single' | 'group', collapsed,
//   npc: { id, name, control: 'ai'|'manual'|'call', controlLocked (I2 key | null), canCall, call (null|'ringing'|…),
//          pace: 'walk'|'run', canStop, health 0–1, healthColor, status, statusTone, uv, shade, badge,
//          journalUnread, goal, dream, directive: { kind, from } | null,
//          effects: [{ slug, emoji, buff, intensity }],
//          needs: [{ id, value 0–1 (1 = good), thermal? −1…1, deficit? }],
//          nature: [{ id, value 0–10 }], skills: [{ id, value 0–10 }], traits: [key],
//          relations: [{ id, name, face, tag, affinity −1…1, familiarity 0–1, trust 0–1 }] },
//   group: { count, manual, total, blocked, fighting, mode, faces: [{ id, name, face }], feedback },
// }
// Commands: control { mode }, pace { value }, stop, open { window: 'inventory'|'journal'|'health' },
// collapse, expand, select { id }, groupMode, groupStop, portraitRect { x, y, w, h, vw, vh }.
import { html, useState, useEffect, useRef } from '../html.js';
import { t } from '../i18n.js';
import { asset } from '../bridge.js';
import { Pips, Ring } from '../components.js';
import { Face } from './roster.js';
import { icons } from '../icons.js';

const MODE_ICON = { ai: icons.ai, manual: icons.hand, call: icons.phone };

const good = (v) => (v >= 0.6 ? 'var(--hx-leaf)' : v >= 0.3 ? 'var(--hx-gold)' : 'rgb(232 104 92)');

function Pill({ tone, children }) {
  return html`<div class=${`hx-pill-note${tone ? ` hx-pill-note--${tone}` : ''}`}>${children}</div>`;
}

function Pills({ npc }) {
  const directive = npc.directive && (npc.directive.from
    ? t('panel.directive.from', { 0: npc.directive.from, 1: t(`directive.${npc.directive.kind}`) })
    : t('panel.directive.player', { 0: t(`directive.${npc.directive.kind}`) }));
  return html`
    <div class="hx-pills hx-surface">
      <div class="hx-pills__row">
        <${Pill}><span aria-hidden="true">💭</span>${npc.status === 'dead' ? t('state.dead') : t(`goal.${npc.goal}`)}<//>
        ${npc.dream && html`<${Pill} tone="dream"><span aria-hidden="true">✨</span>${t(`dream.${npc.dream}`)}<//>`}
        ${directive && html`<${Pill} tone="directive"><span aria-hidden="true">🤝</span>${directive}<//>`}
      </div>
      ${npc.effects?.length > 0 && html`
        <ul class="hx-effects">
          ${npc.effects.map((e) => html`
            <li key=${e.slug} class=${e.buff ? 'is-buff' : 'is-debuff'} title=${`${t(`effect.${e.slug}.title`)} — ${t(`effect.${e.slug}.desc`)}`}>
              <span aria-hidden="true">${e.emoji}</span><span class="hx-sr">${t(`effect.${e.slug}.title`)}</span>
            </li>`)}
        </ul>`}
    </div>`;
}

function Identity({ npc, send }) {
  const hole = useRef(null);
  useEffect(() => {
    const report = () => {
      const ui = document.getElementById('hex-ui');
      const r = hole.current.getBoundingClientRect();
      const s = ui.getBoundingClientRect();
      const k = s.width / ui.clientWidth || 1;
      send('portraitRect', { x: (r.left - s.left) / k, y: (r.top - s.top) / k, w: r.width / k, h: r.height / k,
        vw: ui.clientWidth, vh: ui.clientHeight });
    };
    const observer = new ResizeObserver(report);
    observer.observe(hole.current);
    report();
    return () => observer.disconnect();
  }, []);
  const modes = ['ai', 'manual', ...(npc.canCall ? ['call'] : [])];
  return html`
    <section class="hx-id" aria-label=${npc.name}>
      <div class="hx-id__portrait" ref=${hole}></div>
      <div class="hx-id__top">
        <div class="hx-toggle" role="group" aria-label=${t('panel.control.tooltip')} title=${npc.controlLocked ? t(npc.controlLocked) : t('panel.control.tooltip')}>
          ${modes.map((mode) => html`
            <button type="button" key=${mode} aria-pressed=${npc.control === mode} disabled=${!!npc.controlLocked}
              title=${mode === 'call' ? t('agent.call.button') : t(`panel.control.${mode}`)}
              onClick=${() => send('control', { mode })}>${MODE_ICON[mode]()}${mode !== 'call' && html`<span>${t(`panel.control.${mode}`)}</span>`}</button>`)}
        </div>
        <div class="hx-id__name"><b>${npc.name}</b><span>${t('panel.role')} · #${npc.id}</span></div>
      </div>
      ${npc.badge && html`<span class="hx-id__badge">${t(`badge.${npc.badge}`)}</span>`}
      <div class="hx-id__side">
        <button type="button" class="hx-id__btn" title=${t('inv.button')} onClick=${() => send('open', { window: 'inventory' })}>${icons.bag()}<span>${t('inv.button')}</span></button>
        <button type="button" class="hx-id__btn" title=${t('journal.button')} onClick=${() => send('open', { window: 'journal' })}>${icons.book()}<span>${t('journal.button')}</span>
          ${npc.journalUnread > 0 && html`<b class="hx-dot-count">${npc.journalUnread}</b>`}</button>
      </div>
      <div class="hx-id__bottom">
        <div class="hx-toggle hx-toggle--small" role="group" title=${t('panel.pace.tooltip')}>
          ${['walk', 'run'].map((p) => html`<button type="button" key=${p} aria-pressed=${npc.pace === p} title=${t(`panel.pace.${p}`)}
            aria-label=${t(`panel.pace.${p}`)} onClick=${() => send('pace', { value: p })}>${p === 'walk' ? icons.walk() : icons.run()}</button>`)}
        </div>
        ${npc.canStop && html`<button type="button" class="hx-btn hx-btn--tiny" title=${t('menu.stop.tooltip')} onClick=${() => send('stop')}>${t('menu.stop')}</button>`}
        <div class="hx-vitals">
          <div class="hx-vitals__text">
            <span class="hx-roster__status" data-tone=${npc.statusTone}><i aria-hidden="true"></i>${t(`state.${npc.status}`)}</span>
            <span class="hx-muted">${t(`uv.${npc.uv}`)}${npc.shade ? ` · ${t('uv.shade')}` : ''}</span>
          </div>
          <button type="button" class="hx-vitals__hp" title=${t('panel.health')} onClick=${() => send('open', { window: 'health' })}>
            <${Ring} value=${npc.health} color=${npc.healthColor} size="4rem" stroke="0.375rem" label=${t('panel.health')}>
              <b>${Math.round(npc.health * 100)}</b>
            <//>
          </button>
        </div>
      </div>
    </section>`;
}

function Needs({ npc }) {
  const [tab, setTab] = useState('needs');
  return html`
    <section class="hx-sheet">
      <div class="hx-subtabs" role="tablist">
        ${['needs', 'nature', 'skills', 'character'].map((id) => html`
          <button type="button" role="tab" key=${id} aria-selected=${tab === id} onClick=${() => setTab(id)}>${t(`panel.${id}`)}</button>`)}
      </div>
      ${tab === 'needs' && html`
        <ul class="hx-needs">
          ${npc.needs.map((n) => html`
            <li key=${n.id} title=${n.thermal != null ? undefined : `${Math.round(n.value * 100)}%`}>
              <span class="hx-needs__label">${t(`need.${n.id}`)}</span>
              ${n.thermal != null
                ? html`<span class="hx-bar hx-bar--bipolar"><i style=${{ left: `${50 + Math.min(0, n.thermal) * 50}%`, width: `${Math.abs(n.thermal) * 50}%`,
                    background: n.thermal < 0 ? 'rgb(71 182 230)' : 'rgb(232 116 107)' }}></i></span>`
                : html`<span class="hx-bar"><i style=${{ width: `${n.value * 100}%`, background: good(n.value) }}></i></span>`}
              ${n.deficit > 0 && html`<span class="hx-needs__deficit">−${Math.round(n.deficit * 100)}%</span>`}
            </li>`)}
        </ul>`}
      ${(tab === 'nature' || tab === 'skills') && html`
        <ul class="hx-stats">
          ${(tab === 'nature' ? npc.nature : npc.skills).map((s) => html`
            <li key=${s.id} title=${t(`${tab === 'nature' ? 'attr' : 'skill'}.${s.id}.desc`)}>
              <span>${t(`${tab === 'nature' ? 'attr' : 'skill'}.${s.id}`)}</span><${Pips} value=${s.value} /><b>${s.value}</b>
            </li>`)}
        </ul>`}
      ${tab === 'character' && (npc.traits.length === 0
        ? html`<p class="hx-muted">${t('sheet.character.empty')}</p>`
        : html`<ul class="hx-traits hx-traits--grid">${npc.traits.map((k) => html`<li key=${k}><b>${t(`${k}.title`)}</b><span>${t(`${k}.desc`)}</span></li>`)}</ul>`)}
    </section>`;
}

const TAG_TONE = { friend: 'good', close: 'good', acquaint: '', neutral: '', tense: 'warn', hostile: 'bad' };

function Relations({ npc }) {
  const list = npc.relations || [];
  const [focus, setFocus] = useState(list[0]?.id);
  const r = list.find((x) => x.id === focus) || list[0];
  return html`
    <section class="hx-rel">
      <h3 class="hx-section__title">${t('panel.relations')}</h3>
      ${list.length === 0 ? html`<p class="hx-muted">${t('panel.none')}</p>` : html`
        <div class="hx-rel__faces" role="tablist">
          ${list.map((x) => html`
            <button type="button" role="tab" key=${x.id} aria-selected=${x.id === r.id} title=${x.name} onClick=${() => setFocus(x.id)}>
              <${Face} face=${x.face} name=${x.name} size="2.25rem" /><span>${x.name}</span>
            </button>`)}
        </div>
        <div class="hx-rel__focus">
          <div class="hx-rel__who"><b>${r.name}</b><span data-tone=${TAG_TONE[r.tag] || ''}>${t(`rel.${r.tag}`)}</span></div>
          <div class="hx-rel__rings">
            ${[['affinity', (r.affinity + 1) / 2, r.affinity >= 0 ? 'var(--hx-leaf)' : 'rgb(232 104 92)'],
               ['familiarity', r.familiarity, 'rgb(120 170 230)'], ['trust', r.trust, 'var(--hx-gold)']].map(([id, v, c]) => html`
              <figure key=${id}><${Ring} value=${v} color=${c} size="3rem" stroke="0.3125rem" label=${t(`rel.${id}`)}><b>${Math.round(v * 100)}</b><//>
                <figcaption>${t(`rel.${id}`)}</figcaption></figure>`)}
          </div>
        </div>`}
    </section>`;
}

function Group({ group, send }) {
  return html`
    <div class="hx-card hx-card--group hx-surface">
      <div class="hx-group__sum">
        <b>${t('group.selected', { 0: group.count })}</b>
        <span class="hx-muted">${t('group.manual_summary', { 0: group.manual, 1: group.total })}</span>
        ${(group.blocked > 0 || group.fighting > 0) && html`<span class="hx-warn">${t('group.alerts', { 0: group.blocked, 1: group.fighting })}</span>`}
      </div>
      <div class="hx-group__faces">
        ${group.faces.map((f) => html`<button type="button" key=${f.id} title=${f.name} onClick=${() => send('select', { id: f.id })}>
          <${Face} face=${f.face} name=${f.name} size="3.25rem" /></button>`)}
      </div>
      <div class="hx-group__cmd">
        <button type="button" class="hx-btn" onClick=${() => send('groupMode')}>${t(`panel.control.${group.mode}`)}</button>
        <button type="button" class="hx-btn hx-btn--danger" onClick=${() => send('groupStop')}>${t('group.stop')}</button>
        <span class="hx-muted">${group.feedback || t('group.order_hint')}</span>
      </div>
    </div>`;
}

export function Character({ model, send }) {
  if (model.mode === 'group' && model.group) return html`<${Group} group=${model.group} send=${send} />`;
  if (model.mode !== 'single' || !model.npc) return null;
  if (model.collapsed) {
    return html`<button type="button" class="hx-expand hx-surface" onClick=${() => send('expand')}>▲ ${model.npc.name}</button>`;
  }
  const npc = model.npc;
  return html`
    <div class="hx-bottom">
      <${Pills} npc=${npc} />
      <div class="hx-card hx-surface">
        <${Identity} npc=${npc} send=${send} />
        <${Needs} npc=${npc} />
        <${Relations} key=${npc.id} npc=${npc} />
        <button type="button" class="hx-card__collapse" aria-label=${t('panel.close')} title=${t('panel.close')} onClick=${() => send('collapse')}>▼</button>
      </div>
    </div>`;
}

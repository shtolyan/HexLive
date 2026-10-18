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
import { icons, needIcons } from '../icons.js';
import { tip } from '../tooltip.js';

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
            <li key=${e.slug} class=${e.buff ? 'is-buff' : 'is-debuff'}
              ...${tip(() => ({ icon: e.emoji, title: t(`effect.${e.slug}.title`), tone: e.buff ? 'buff' : 'debuff', desc: t(e.desc || `effect.${e.slug}.desc`) }))}>
              <span aria-hidden="true">${e.emoji}</span><span class="hx-sr">${t(`effect.${e.slug}.title`)}</span>
            </li>`)}
        </ul>`}
    </div>`;
}

// The orbit camera keeps the followed colonist above the card
// (NpcSelection.BottomUiCoverage): tell C# how much of the height it covers.
function useCoverage(ref, send) {
  useEffect(() => {
    const ui = document.getElementById('hex-ui');
    const report = () => {
      if (!ref.current || !ui) return;
      const r = ref.current.getBoundingClientRect();
      const s = ui.getBoundingClientRect();
      send('coverage', { value: Math.max(0, Math.min(1, (s.bottom - r.top) / Math.max(1, s.height))) });
    };
    const observer = new ResizeObserver(report);
    observer.observe(ref.current);
    observer.observe(ui);
    report();
    const settle = setTimeout(report, 250); // after the rise-in transform
    return () => { observer.disconnect(); clearTimeout(settle); send('coverage', { value: 0 }); };
  }, []);
}

function CardBox({ className, send, children }) {
  const ref = useRef(null);
  useCoverage(ref, send);
  return html`<div class=${className} ref=${ref}>${children}</div>`;
}

// Just the name (player's request 18.10.2026); a click copies the id for bug reports.
function copyText(text) {
  const fallback = () => {
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.cssText = 'position:absolute;left:-9999px;top:0';
    document.body.appendChild(area);
    area.select();
    try { document.execCommand('copy'); } catch { /* nothing more to try */ }
    area.remove();
  };
  if (navigator.clipboard?.writeText) navigator.clipboard.writeText(text).catch(fallback);
  else fallback();
}

function NameCopy({ npc }) {
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return undefined;
    const timer = setTimeout(() => setCopied(false), 1400);
    return () => clearTimeout(timer);
  }, [copied]);
  return html`<button type="button" class="hx-id__name" title=${t('panel.copy_id')}
    onClick=${() => { copyText(String(npc.id)); setCopied(true); }}>
    <b>${npc.name}</b>${copied && html`<span class="hx-id__copied" role="status">${t('panel.id_copied')}</span>`}</button>`;
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
        <${NameCopy} npc=${npc} />
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
              <b class="hx-hp">${Math.round(npc.health * 100)}<span aria-hidden="true">❤️</span></b>
            <//>
          </button>
        </div>
      </div>
    </section>`;
}

// CharacterPanel.Needs: each need keeps the glyph and colour of the UI Toolkit card.
const NEED_LOOK = {
  hunger: ['hunger', 'rgb(232 145 60)'], thirst: ['thirst', 'rgb(71 182 230)'], energy: ['energy', 'rgb(95 196 106)'],
  comfort: ['comfort', 'rgb(125 148 234)'], social: ['social', 'rgb(181 139 226)'], stamina: ['energy', 'rgb(95 196 106)'],
  blood: ['health', 'rgb(232 86 112)'], hygiene: ['thirst', 'rgb(71 182 230)'], stress: ['think', 'rgb(181 139 226)'],
  compassion: ['heartFill', 'rgb(236 115 150)'], breath: ['energy', 'rgb(71 182 230)'],
};

// Every need shows its number (player's request 18.10.2026: blood alone used
// to, with its deficit, and stood out); warmth is signed — cold minus, hot plus.
function needValue(n) {
  if (n.thermal != null) {
    const v = Math.round(n.thermal * 100);
    return v > 0 ? `+${v}%` : v < 0 ? `−${-v}%` : '0%';
  }
  return `${Math.round(n.value * 100)}%`;
}

function NeedIcon({ need }) {
  const look = need.thermal != null
    ? ['thermal', need.thermal < -0.05 ? 'rgb(71 182 230)' : need.thermal > 0.05 ? 'rgb(232 116 107)' : 'rgb(154 166 173)']
    : NEED_LOOK[need.id];
  return look ? html`<i class="hx-needs__icon" style=${{ color: look[1] }}>${needIcons[look[0]]()}</i>` : null;
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
            <li key=${n.id} ...${tip(() => ({
              // CharacterPanel.ShowNeedTooltip: the effects acting on this need right now.
              icon: '↕', title: `${t(`need.${n.id}`)} · ${needValue(n)}${n.deficit > 0 ? ` (−${Math.round(n.deficit * 100)}%)` : ''}`,
              desc: t(n.impacts?.length ? 'effect.impacts.current' : 'effect.impacts.none'),
              rows: (n.impacts || []).map((i) => ({ icon: i.emoji, title: t(i.title), up: i.up })),
            }))}>
              <span class="hx-needs__label"><${NeedIcon} need=${n} />${t(`need.${n.id}`)}</span>
              <span class="hx-needs__value" data-tone=${n.thermal != null ? (n.thermal < -0.05 ? 'cold' : n.thermal > 0.05 ? 'hot' : '') : ''}>${needValue(n)}</span>
              ${n.thermal != null
                ? html`<span class="hx-bar hx-bar--bipolar"><i style=${{ left: `${50 + Math.min(0, n.thermal) * 50}%`, width: `${Math.abs(n.thermal) * 50}%`,
                    background: n.thermal < 0 ? 'rgb(71 182 230)' : 'rgb(232 116 107)' }}></i></span>`
                : html`<span class="hx-bar"><i style=${{ width: `${n.value * 100}%`, background: good(n.value) }}></i></span>`}
            </li>`)}
        </ul>`}
      ${(tab === 'nature' || tab === 'skills') && html`
        <ul class="hx-stats">
          ${(tab === 'nature' ? npc.nature : npc.skills).map((s) => html`
            <li key=${s.id} ...${tip(() => ({ icon: '?', title: t(`${tab === 'nature' ? 'attr' : 'skill'}.${s.id}`),
              desc: t(`${tab === 'nature' ? 'attr' : 'skill'}.${s.id}.desc`) }))}>
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
            ${[['affinity', (r.affinity + 1) / 2, r.affinity >= 0 ? 'var(--hx-leaf)' : 'rgb(232 104 92)', r.affinity >= 0 ? '💗' : '💔'],
               ['familiarity', r.familiarity, 'rgb(120 170 230)', '👥'], ['trust', r.trust, 'var(--hx-gold)', '🤝']].map(([id, v, c, glyph]) => html`
              <figure key=${id} ...${tip(() => ({ icon: glyph, title: `${t(`rel.${id}`)}: ${Math.round(v * 100)}`, desc: t(`rel.${id}.desc`) }))}>
                <${Ring} value=${v} color=${c} size="3rem" stroke="0.3125rem" label=${t(`rel.${id}`)}>
                <span class="hx-rel__value"><i aria-hidden="true">${glyph}</i><b>${Math.round(v * 100)}</b></span><//>
                <figcaption>${t(`rel.${id}`)}</figcaption></figure>`)}
          </div>
        </div>`}
    </section>`;
}

function Group({ group, send }) {
  return html`
    <${CardBox} className="hx-card hx-card--group hx-surface" send=${send}>
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
    <//>`;
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
      <${CardBox} className="hx-card hx-surface" send=${send}>
        <${Identity} npc=${npc} send=${send} />
        <${Needs} npc=${npc} />
        <${Relations} key=${npc.id} npc=${npc} />
        <button type="button" class="hx-card__collapse" aria-label=${t('panel.close')} title=${t('panel.close')} onClick=${() => send('collapse')}>▼</button>
      <//>
    </div>`;
}

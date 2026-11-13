// §174.7: the selected colonist's card at the bottom (CharacterPanel BuildUi:
// identity column, needs/sheet column, relations column) and the pills above it
// (thought, dream, directive, effects). Opened windows (inventory, health,
// journal) are separate screens.
//
// The live portrait (PortraitStage) is a transparent hole like the creator's
// preview: Unity renders that camera into portraitRect. Model: {
//   mode: 'none' | 'single' | 'group', collapsed,
//   npc: { id, name, race: { id: 'island'|'raider'|'modern', icon: asset key | null }, control: 'ai'|'manual'|'call', controlLocked (I2 key | null), canCall,
//          call: null | { state: 'ringing'|'listening'|'speaking'|'failed', reason (I2 key, when failed) },
//          toast: null | I2 key,              // §121: why her last order was refused or cut short (2.5 s)
//          pace: 'walk'|'run', canStop, health 0–1, healthColor, status, statusTone, uv, shade, badge,
//          journalUnread, goal, dream, directive: { kind, from } | null,
//          thought: null | { thinking, text },   // §160 an attached agent's own line instead of her goal
//          effects: [{ slug, emoji, buff, intensity 0–1 }],   // a debuff shades amber → red with intensity
//          needs: [{ id, value 0–1 (1 = good), direct? (bar fills with 1 − value), thermal? −1…1, deficit? }],
//          nature: [{ id, value 0–10 }], compassion: 0–100 | null, perks: [key], skills: [{ id, value 0–10 }], traits: [key],
//          relations: [{ id, name, face, tag, affinity −1…1, familiarity 0–1, trust 0–1 }] },
//          // her bond with the player's own voice (§171) is one of them, id −159, no face
//   group: { count, manual, total, blocked, fighting, mode, faces: [{ id, name, face, fighting }], feedback },
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

// §171 the call's state under the switch (CharacterPanel.ServerVoice RenderServerVoiceButton).
const CALL_TEXT = { ringing: 'agent.call.ringing', listening: 'agent.call.listening', speaking: 'agent.call.speaking' };
const callTitle = (call) => (!call ? t('agent.call.button')
  : call.state === 'failed' ? `${t(call.reason)}\n${t('agent.call.button')}` : t('agent.call.hangup'));

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
        <${Pill}><span aria-hidden="true">💭</span>${npc.status === 'dead' ? t('state.dead')
          : npc.thought ? (npc.thought.thinking ? t('agent.thinking') : npc.thought.text) : t(`goal.${npc.goal}`)}<//>
        ${npc.dream && html`<${Pill} tone="dream"><span aria-hidden="true">✨</span>${t(`dream.${npc.dream}`)}<//>`}
        ${directive && html`<${Pill} tone="directive"><span aria-hidden="true">🤝</span>${directive}<//>`}
      </div>
      ${npc.effects?.length > 0 && html`
        <ul class="hx-effects">
          ${npc.effects.map((e) => html`
            <li key=${e.slug} class=${e.buff ? 'is-buff' : 'is-debuff'} style=${e.buff ? undefined : { '--sev': Math.max(0, Math.min(1, e.intensity ?? 0)) }}
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

// Bug #733 (§67.16): her race right after the name — a picture, never an
// emoji; while the picture has not arrived the badge is simply absent.
function RaceBadge({ race }) {
  const url = race && asset(race.icon);
  if (!url) return null;
  const name = t(`race.${race.id}.name`);
  return html`<img class="hx-id__race" src=${url} alt=${name}
    ...${tip(() => ({ title: t('race.tooltip', { 0: name }), desc: t(`race.${race.id}.desc`) }))} />`;
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
  const call = npc.call;
  return html`
    <section class="hx-id" aria-label=${npc.name}>
      <div class="hx-id__portrait" ref=${hole}></div>
      <div class="hx-id__top">
        ${(npc.canCall || npc.speak) && html`<div class="hx-toggle">
          ${npc.canCall && html`<button type="button" aria-pressed=${npc.control === 'call'}
            class=${call ? `hx-call is-${call.state}` : undefined}
            title=${callTitle(call)} aria-label=${callTitle(call)}
            onClick=${() => send('control', { mode: 'call' })}>${icons.phone()}</button>`}
          ${npc.speak && html`<button type="button" aria-label=${t('agent.speak.button')}
            title=${t(npc.speak.state === 'sending' ? 'agent.speak.sending' : 'agent.speak.button')}
            disabled=${!npc.speak.enabled} aria-pressed=${npc.speak.state === 'recording'}
            onClick=${() => send('speak')}>${icons.mic()}</button>`}
        </div>`}
        ${npc.speak?.state === 'sending' && html`<span class="hx-call-state" role="status">${t('agent.speak.sending')}</span>`}
        ${call && html`<span class="hx-call-state" data-state=${call.state} role="status">${t(CALL_TEXT[call.state] || call.reason)}</span>`}
        <div class="hx-id__nameline"><${NameCopy} npc=${npc} /><${RaceBadge} race=${npc.race} /></div>
        ${npc.toast && html`<p class="hx-order-toast" role="status" key=${npc.toast}>${t(npc.toast)}</p>`}
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
            <span class="hx-muted hx-uv" data-uv=${npc.uv}>${t(`uv.${npc.uv}`)}${npc.shade ? ` · ${t('uv.shade')}` : ''}</span>
          </div>
          <button type="button" class="hx-vitals__hp" title=${t('panel.health')} onClick=${() => send('open', { window: 'health' })}>
            <${Ring} value=${npc.health} color=${npc.healthColor} size="4rem" stroke="0.375rem" label=${t('panel.health')}>
              <span class="hx-rel__value hx-hp"><i aria-hidden="true">❤️</i><b>${Math.round(npc.health * 100)}</b></span>
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
  return `${Math.round(needFill(n) * 100)}%`;
}

// Bug #751: a direct need (stress) fills with its pressure, like the UI
// Toolkit card — calm = empty, 100% = full; the colour still follows
// goodness (value), so a full stress bar is red and an empty one green.
function needFill(n) {
  return n.direct ? 1 - n.value : n.value;
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
              <span class="hx-needs__value"><span class="hx-needs__increase" data-active=${n.increasing === true} aria-hidden="true">↑</span><span data-tone=${n.thermal != null ? (n.thermal < -0.05 ? 'cold' : n.thermal > 0.05 ? 'hot' : '') : ''}>${needValue(n)}</span></span>
              ${n.thermal != null
                ? html`<span class="hx-bar hx-bar--bipolar"><i style=${{ left: `${50 + Math.min(0, n.thermal) * 50}%`, width: `${Math.abs(n.thermal) * 50}%`,
                    background: n.thermal < 0 ? 'rgb(71 182 230)' : 'rgb(232 116 107)' }}></i></span>`
                : html`<span class="hx-bar"><i style=${{ width: `${needFill(n) * 100}%`, background: good(n.value) }}></i></span>`}
            </li>`)}
        </ul>`}
      ${tab === 'nature' && npc.perks?.length > 0 && html`
        <ul class="hx-perks">${npc.perks.map((k) => html`<li key=${k} class=${k.endsWith('.high') ? 'is-gift' : ''}>${t(k)}</li>`)}</ul>`}
      ${(tab === 'nature' || tab === 'skills') && html`
        <ul class="hx-stats">
          ${(tab === 'nature' ? npc.nature : npc.skills).map((s) => html`
            <li key=${s.id} ...${tip(() => ({ icon: '?', title: t(`${tab === 'nature' ? 'attr' : 'skill'}.${s.id}`),
              desc: t(`${tab === 'nature' ? 'attr' : 'skill'}.${s.id}.desc`) }))}>
              <span>${t(`${tab === 'nature' ? 'attr' : 'skill'}.${s.id}`)}</span><${Pips} value=${s.value} /><b>${s.value}</b>
            </li>`)}
          ${tab === 'nature' && npc.compassion != null && html`
            <li class="hx-stats__share" ...${tip(() => ({ icon: '🤲', title: t('trait.compassion'), desc: t('trait.compassion.desc') }))}>
              <span>${t('trait.compassion')}</span><span class="hx-bar"><i style=${{ width: `${npc.compassion}%` }}></i></span><b>${npc.compassion}%</b>
            </li>`}
        </ul>`}
      ${tab === 'character' && (npc.traits.length === 0
        ? html`<p class="hx-muted">${t('sheet.character.empty')}</p>`
        : html`<ul class="hx-traits hx-traits--grid">${npc.traits.map((k) => html`<li key=${k}><b>${t(`${k}.title`)}</b><span>${t(`${k}.desc`)}</span></li>`)}</ul>`)}
    </section>`;
}

const TAG_TONE = { friend: 'good', close: 'good', acquaint: '', neutral: '', tense: 'warn', hostile: 'bad' };

// §121.13: one action glyph — a white PNG from Player Resources, tinted by the
// chip colour through a CSS mask; the vector «play» only while it has not arrived.
function OrderGlyph({ icon }) {
  const url = asset(icon);
  return url
    ? html`<i class="hx-order-glyph" aria-hidden="true" style=${{ '--glyph': `url(${url})` }}></i>`
    : icons.play();
}

// §121.13: the action list. Hover shows what each entry is («Обнять: Марта»),
// a click cancels it, and a waiting entry can be dragged to a new place. Only
// the waiting tail moves: the running entry is neither draggable nor a drop
// target in front of it. The page sends stable ids, never positions.
function PlayerOrders({ orders, send }) {
  const [drag, setDrag] = useState(null);      // id of the dragged order
  const [drop, setDrop] = useState(null);      // { before } of the gap under the pointer
  if (!(orders?.length > 0)) return null;
  const first = orders[0].active ? 1 : 0;
  const gapAt = (e, index) => {
    const r = e.currentTarget.getBoundingClientRect();
    const slot = e.clientX < r.left + r.width / 2 ? index : index + 1;
    return Math.max(first, slot);
  };
  const finish = (slot) => {
    const from = orders.findIndex((o) => o.id === drag);
    setDrag(null); setDrop(null);
    if (from < first || slot === from || slot === from + 1) return;
    send('moveOrder', { id: drag, before: slot < orders.length ? orders[slot].id : 0 });
  };
  return html`
      <div class="hx-order-row" role="group" aria-label=${t('panel.orders.action')}>
        ${orders.map((order, index) => html`<button type="button" key=${order.id} class="hx-order-chip"
          data-active=${order.active} data-dragging=${drag === order.id}
          data-drop=${drag != null && drop === index ? 'before' : drag != null && drop === orders.length && index === orders.length - 1 ? 'after' : ''}
          draggable=${!order.active}
          aria-label=${t('panel.orders.cancel', { 0: order.label })}
          ...${tip(() => ({ icon: html`<${OrderGlyph} icon=${order.icon} />`, title: order.label, desc: order.hint,
            tone: order.active ? 'buff' : '' }))}
          onDragStart=${(e) => { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(order.id)); setDrag(order.id); }}
          onDragOver=${(e) => { if (drag == null) return; e.preventDefault(); setDrop(gapAt(e, index)); }}
          onDrop=${(e) => { e.preventDefault(); if (drag != null) finish(gapAt(e, index)); }}
          onDragEnd=${() => { setDrag(null); setDrop(null); }}
          onClick=${() => send('cancelOrder', { id: order.id })}>
          <span class="hx-order-chip__action"><${OrderGlyph} icon=${order.icon} /></span>
          <span class="hx-order-chip__cancel">${icons.close()}</span>
        </button>`)}
      </div>`;
}

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
        ${(group.blocked > 0 || group.fighting > 0) && html`<span class="hx-warn">${t('group.alerts', { 0: group.blocked, 1: group.fighting })}</span>`}
      </div>
      <div class="hx-group__faces">
        ${group.faces.map((f) => html`<button type="button" key=${f.id} title=${f.name} class=${f.fighting ? 'is-fighting' : undefined} onClick=${() => send('select', { id: f.id })}>
          <${Face} face=${f.face} name=${f.name} size="3.25rem" /></button>`)}
      </div>
      <div class="hx-group__cmd">
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
      <div class="hx-character-overlays">
        <${Pills} npc=${npc} />
        <${PlayerOrders} orders=${npc.orders} send=${send} />
      </div>
      <${CardBox} className="hx-card hx-surface" send=${send}>
        <${Identity} npc=${npc} send=${send} />
        <${Needs} npc=${npc} />
        <${Relations} key=${npc.id} npc=${npc} />
        <button type="button" class="hx-card__collapse" aria-label=${t('panel.close')} title=${t('panel.close')} onClick=${() => send('collapse')}>▼</button>
      <//>
    </div>`;
}

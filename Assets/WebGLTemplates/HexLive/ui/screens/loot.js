// §174.7: the exchange window (§128 LootTransferPanel) — her things on the
// left, the other side on the right: a person, or a thing that holds things
// (a basket, a sack, §128.5). Also the gift (§153.1): the same window, one way.
//
// C# owns every rule: which side may take, where a thing fits, what a double
// click does. The page lets the player point at an item and a side.
//
// Model: {
//   mode: 'exchange'|'gift', pending,          // pending: an order is on its way
//   status: { key, tone: 'hint'|'busy'|'error' },
//   left, right: {
//     name, face?, kind: 'person'|'container',
//     count?: { used, cap },                    // a person's own inventory count (the groups also hold hands)
//     groups: [{ title, filled, cap, overflow, owner?: item,      // owner: the bag itself
//                slots: [{ item: item|null, hand?: 'left'|'right',
//                          accepts?: name }] }],  // hand: a hand, not a pocket; accepts: a slot kept for one thing
//     worn: [item],                             // clothes that hold nothing
//   },
//   info: null | { uid, detail: { category, desc, stats: [[label, value, color, fill|null]] } },  // the card asked for
// }
// item: { uid, def, icon, emoji, name, count, drop? }      // drop: her own item that can be thrown away (§128.6)
// Commands: move { uid, to: 'left'|'right', count }, drop { uid, count }, quick { uid }, info { uid|null }, close.
import { html, useState, useRef } from '../html.js';
import { t } from '../i18n.js';
import { asset } from '../bridge.js';
import { icons } from '../icons.js';
import { Button, Pic, Quantity, ItemStats } from '../components.js';

const sideOf = (model, uid) => {
  for (const side of ['left', 'right']) {
    const p = model[side];
    if (!p) continue;
    if ((p.worn || []).some((i) => i.uid === uid)) return side;
    for (const g of p.groups || []) {
      if (g.owner?.uid === uid || g.slots.some((s) => s.item?.uid === uid)) return side;
    }
  }
  return null;
};
const findItem = (model, uid) => {
  for (const side of ['left', 'right']) {
    const p = model[side];
    for (const i of p?.worn || []) if (i.uid === uid) return i;
    for (const g of p?.groups || []) {
      if (g.owner?.uid === uid) return g.owner;
      for (const s of g.slots) if (s.item?.uid === uid) return s.item;
    }
  }
  return null;
};

function Hand({ side }) {
  return side && html`<span class="hx-loot__badge" aria-hidden="true">${icons.hand()}${t(`inv.hand_${side}.short`)}</span>`;
}

function Cell({ slot, ctx }) {
  const it = slot.item;
  const hand = slot.hand ? t(`inv.hand_${slot.hand}`) : undefined;
  if (!it) {
    // A typed slot: only one kind of thing goes in — say which, or a drop there looks broken.
    const typed = slot.accepts ? `${t('inv.typed_slot')}: ${slot.accepts}` : null;
    return html`<li class=${`hx-slot hx-slot--empty${typed ? ' is-typed' : ''}`} title=${[hand, typed].filter(Boolean).join(' · ') || undefined}>
      <${Hand} side=${slot.hand} /></li>`;
  }
  return html`<li class=${`hx-slot${ctx.selected === it.uid ? ' is-selected' : ''}`} title=${hand ? `${hand}: ${it.name}` : undefined}>
    <button type="button" draggable=${!ctx.pending} title=${hand ? undefined : it.name} disabled=${ctx.pending}
      onClick=${() => ctx.select(it.uid)} onDblClick=${() => ctx.quick(it.uid)}
      onDragStart=${(e) => { e.dataTransfer.setData('text/plain', it.uid); e.dataTransfer.effectAllowed = 'move'; ctx.drag(it.uid); }}
      onDragEnd=${() => ctx.drag(null)}>
      <${Pic} of=${it} fallback=${html`<span class="hx-slot__name">${it.name}</span>`} />
      ${it.count > 1 && html`<b class="hx-slot__count">×${it.count}</b>`}
      <${Hand} side=${slot.hand} />
    </button>
  </li>`;
}

function Chip({ item, ctx, as = 'li' }) {
  const body = html`<button type="button" class=${`hx-loot__chip${ctx.selected === item.uid ? ' is-selected' : ''}`} draggable=${!ctx.pending}
    disabled=${ctx.pending} title=${item.name}
    onClick=${() => ctx.select(item.uid)} onDblClick=${() => ctx.quick(item.uid)}
    onDragStart=${(e) => { e.dataTransfer.setData('text/plain', item.uid); ctx.drag(item.uid); }} onDragEnd=${() => ctx.drag(null)}>
    <${Pic} of=${item} /><span>${item.name}</span></button>`;
  return as === 'li' ? html`<li>${body}</li>` : body;
}

function Info({ item, detail, onClose, onDrop }) {
  const d = detail || {};
  return html`<section class="hx-loot__info" aria-label=${item.name}>
    <header class="hx-inv__detail-head">
      <${Pic} of=${item} />
      <div><h3>${item.name}${item.count > 1 ? ` ×${item.count}` : ''}</h3><span class="hx-muted">${d.category}</span></div>
      <button class="hx-close" type="button" aria-label=${t('ui.close')} onClick=${onClose}>${icons.close()}</button>
    </header>
    ${d.desc && html`<p class="hx-text">${d.desc}</p>`}
    <${ItemStats} rows=${d.stats} />
    ${item.drop && html`<div class="hx-actions hx-actions--left">
      <${Button} variant="danger" onClick=${() => onDrop(item)}>${t('inv.action.drop')}<//></div>`}
  </section>`;
}

function Pane({ side, pane, ctx, accepts }) {
  const face = pane.face && asset(pane.face);
  const used = pane.count?.used ?? (pane.groups || []).reduce((n, g) => n + g.filled, 0);
  const cap = pane.count?.cap ?? (pane.groups || []).reduce((n, g) => n + g.cap, 0);
  const info = ctx.selected && sideOf(ctx.model, ctx.selected) === side ? findItem(ctx.model, ctx.selected) : null;
  const detail = info && ctx.model.info?.uid === info.uid ? ctx.model.info.detail : null;
  const target = ctx.dragging && sideOf(ctx.model, ctx.dragging) !== side;
  return html`<section class=${`hx-loot__pane${target ? (accepts ? ' is-target' : ' is-refused') : ''}`} aria-label=${pane.name}
    onDragOver=${(e) => { if (target && accepts) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; } }}
    onDrop=${(e) => { e.preventDefault(); ctx.drop(e.dataTransfer.getData('text/plain'), side); }}>
    <header class="hx-loot__head">
      ${pane.kind === 'person'
        ? html`<span class="hx-loot__face">${face ? html`<img src=${face} alt="" />` : pane.name.slice(0, 1)}</span>`
        : html`<span class="hx-loot__face hx-loot__face--thing">${icons.bag()}</span>`}
      <h3>${pane.name}</h3>
      ${!pane.privateInventory && html`<span class="hx-inv__cap">${used}/${cap}</span>`}
    </header>
    <div class="hx-loot__scroll">
      ${(pane.groups || []).map((g, i) => html`
        <div key=${i} class=${`hx-loot__group${g.overflow ? ' is-overflow' : ''}`}>
          <div class="hx-loot__group-head">
            ${g.owner ? html`<${Chip} item=${g.owner} ctx=${ctx} as="span" />` : html`<b>${g.title}</b>`}
            <span class="hx-inv__cap">${g.filled}/${g.cap}</span>
          </div>
          <ul class="hx-slots hx-loot__slots">${g.slots.map((s, j) => html`<${Cell} key=${s.item?.uid ?? `e${j}`} slot=${s} ctx=${ctx} />`)}</ul>
        </div>`)}
      ${pane.worn?.length > 0 && html`
        <div class="hx-loot__group">
          <div class="hx-loot__group-head"><b>${t('inv.worn')}</b></div>
          <ul class="hx-loot__chips">${pane.worn.map((it) => html`<${Chip} key=${it.uid} item=${it} ctx=${ctx} />`)}</ul>
        </div>`}
      ${!(pane.groups?.length || pane.worn?.length) && html`<p class="hx-muted hx-loot__empty">${pane.hint ?? t('loot.empty')}</p>`}
    </div>
    ${info && html`<${Info} item=${info} detail=${detail} onClose=${() => ctx.select(null)} onDrop=${ctx.throwAway} />`}
  </section>`;
}

export function Loot({ model, send }) {
  const [selected, setSelected] = useState(null);
  const [dragging, setDragging] = useState(null);
  const [asking, setAsking] = useState(null); // { item, to }
  const [dropping, setDropping] = useState(null); // her own stack: how many to throw away
  const gift = model.mode === 'gift';
  const ctx = {
    model, selected, dragging, pending: model.pending,
    // The card is C#'s (the same builder as in UI Toolkit): ask for it.
    select: (uid) => {
      if (gift && uid && sideOf(model, uid) === 'left') { send('move', { uid, to: 'right', count: 1 }); return; }
      const next = uid === selected ? null : uid; setSelected(next); send('info', { uid: next });
    },
    drag: setDragging,
    quick: (uid) => { setSelected(null); send('quick', { uid }); },
    // §128.6 / #734: «Выбросить» — her own things only (C# marks them), a stack asks how many.
    throwAway: (item) => {
      setDragging(null);
      if (selected) { setSelected(null); send('info', { uid: null }); }
      if (!item?.drop || model.pending) return;
      if (item.count > 1) setDropping(item);
      else send('drop', { uid: item.uid, count: 1 });
    },
    drop: (uid, to) => {
      setDragging(null);
      const item = findItem(model, uid);
      if (!item || sideOf(model, uid) === to) return;
      if (gift) send('move', { uid, to, count: 1 });
      else if (item.count > 1) setAsking({ item, to });
      else send('move', { uid, to, count: 1 });
    },
  };
  // A gift goes one way: dragging her things back says so before the drop.
  const takingBack = gift && dragging && sideOf(model, dragging) === 'right';
  const status = takingBack ? { key: 'gift.take_forbidden', tone: 'error' }
    : dragging ? { key: findItem(model, dragging)?.drop ? 'loot.drop_hint_own' : 'loot.drop_hint', tone: 'hint' }
    : model.status || { key: gift ? 'gift.drag_hint' : 'loot.drag_hint', tone: 'hint' };
  return html`
    <div class="hx-backdrop hx-surface" onPointerDown=${(e) => { if (e.target === e.currentTarget) send('close'); }}
      onDragOver=${(e) => { if (e.target === e.currentTarget && findItem(model, dragging)?.drop) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; } }}
      onDrop=${(e) => { if (e.target !== e.currentTarget) return; e.preventDefault(); ctx.throwAway(findItem(model, e.dataTransfer.getData('text/plain'))); }}>
      <section class=${`hx-loot${model.pending ? ' is-pending' : ''}`} role="dialog" aria-modal="true" aria-labelledby="loot-title">
        <header class="hx-inv__head">
          <h2 class="hx-title" id="loot-title">${t(gift ? 'gift.title' : 'loot.title')}</h2>
          <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
        </header>
        <div class="hx-loot__body">
          <${Pane} side="left" pane=${model.left} ctx=${ctx} accepts=${!gift} />
          <div class="hx-loot__gutter" aria-hidden="true">${gift ? '→' : '⇄'}</div>
          <${Pane} side="right" pane=${model.right} ctx=${ctx} accepts=${true} />
        </div>
        <p class=${`hx-loot__status is-${status.tone}`} role="status">
          ${status.tone === 'error' && icons.alert()}${status.tone === 'busy' && html`<i class="hx-loot__spin"></i>`}
          <span>${status.text ?? t(status.key)}</span>
        </p>
      </section>
      ${asking && html`<${Quantity} title=${t('loot.quantity_title', { 0: asking.item.name })} max=${asking.item.count}
        initial=${asking.item.count} confirm=${t('loot.quantity_confirm')} onClose=${() => setAsking(null)}
        onConfirm=${(count) => { send('move', { uid: asking.item.uid, to: asking.to, count }); setAsking(null); }} />`}
      ${dropping && html`<${Quantity} title=${t('inv.drop_quantity_title', { 0: dropping.name })} max=${dropping.count}
        initial=${dropping.count} confirm=${t('inv.drop_quantity_confirm')} danger onClose=${() => setDropping(null)}
        onConfirm=${(count) => { send('drop', { uid: dropping.uid, count }); setDropping(null); }} />`}
    </div>`;
}

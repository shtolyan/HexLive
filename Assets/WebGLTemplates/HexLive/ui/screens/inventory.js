// §174.7: the backpack window — inventory, clothes and crafting tabs
// (CharacterPanel inventory/craft, ~3k lines in UI Toolkit). C# keeps every
// rule (what can be worn, dropped, crafted, why not); the page lays it out.
//
// The doll (CharacterDollStage) is a transparent hole inside the window: Unity
// renders it into dollRect. The dim around the window is the window's own
// shadow, so nothing dims the doll.
//
// Model: {
//   name, readonly, outfitLocked, capacity: { used, cap },
//   items: [{ uid, def, icon, name, count, inUse, favorite, worn, overflow, layer?,
//             detail: { category, desc, stats: [[label, value]], actions: ['wear'|'stow'|'drop'|'fill'|'plant'], note } }],
//   slots,                                  // total cells, empty ones included
//   dollLayer: 'underwear'|'wear'|'outerwear'|'bags',
//   craft: { blocked (key|null), categories: [id], recipes: [{ id, icon, name, category, working, queued }],
//            detail: { recipe → { ingredients: [{ icon, name, have, need }], station, inPlace, progress, block, button,
//                                 queued, canQueue } },
//            stopped: null | { recipe, reason } },        // §138.7: why the queue stopped on its own
// }
// Commands: close, action { uid, kind, count }, outfitLock { value }, dollLayer { layer },
// dollRotate { dx }, dollRect { x, y, w, h, vw, vh }, craft { recipe } (one more into the queue),
// craftCancel { recipe } (drop what is queued; the item in the making is finished).
import { html, useState, useEffect, useRef } from '../html.js';
import { t } from '../i18n.js';
import { asset } from '../bridge.js';
import { icons } from '../icons.js';
import { Button } from '../components.js';

function Hole({ send, cmd }) {
  const hole = useRef(null);
  useEffect(() => {
    const report = () => {
      const ui = document.getElementById('hex-ui');
      const r = hole.current.getBoundingClientRect();
      const s = ui.getBoundingClientRect();
      const k = s.width / ui.clientWidth || 1;
      send(cmd, { x: (r.left - s.left) / k, y: (r.top - s.top) / k, w: r.width / k, h: r.height / k, vw: ui.clientWidth, vh: ui.clientHeight });
    };
    const observer = new ResizeObserver(report);
    observer.observe(hole.current);
    report();
    // The window rises in with a transform, which ResizeObserver does not see.
    const settle = setTimeout(report, 250);
    return () => { observer.disconnect(); clearTimeout(settle); };
  }, []);
  const drag = useRef(null);
  return html`<div class="hx-inv__hole" ref=${hole}
    onPointerDown=${(e) => { drag.current = e.clientX; e.currentTarget.setPointerCapture(e.pointerId); }}
    onPointerMove=${(e) => { if (drag.current == null) return; send('dollRotate', { dx: e.clientX - drag.current }); drag.current = e.clientX; }}
    onPointerUp=${() => { drag.current = null; }}></div>`;
}

function Slot({ item, selected, onSelect, onQuick, onDragItem }) {
  if (!item) return html`<li class="hx-slot hx-slot--empty" aria-hidden="true"></li>`;
  const icon = asset(item.icon);
  return html`
    <li class=${`hx-slot${selected ? ' is-selected' : ''}${item.overflow ? ' is-overflow' : ''}`}>
      <button type="button" draggable="true" title=${item.name}
        onClick=${() => onSelect(item.uid)} onDblClick=${() => onQuick(item)}
        onDragStart=${(e) => { e.dataTransfer.setData('text/plain', item.uid); onDragItem(item); }}>
        ${icon ? html`<img src=${icon} alt="" />` : html`<span class="hx-slot__name">${item.name}</span>`}
        ${item.count > 1 && html`<b class="hx-slot__count">×${item.count}</b>`}
        ${item.favorite && html`<span class="hx-slot__fav" aria-hidden="true">★</span>`}
        ${item.inUse && html`<span class="hx-slot__tag">${t('inv.in_use')}</span>`}
        ${item.worn && html`<span class="hx-slot__tag hx-slot__tag--worn">${t('inv.worn')}</span>`}
      </button>
    </li>`;
}

function Detail({ item, model, send, onBack, onDrop }) {
  const d = item.detail || {};
  return html`
    <section class="hx-inv__detail" aria-label=${item.name}>
      <button type="button" class="hx-link-back" onClick=${onBack}>‹ ${t('inv.back')}</button>
      <header class="hx-inv__detail-head">
        ${asset(item.icon) && html`<img src=${asset(item.icon)} alt="" />`}
        <div><h3>${item.name}${item.count > 1 ? ` ×${item.count}` : ''}</h3><span class="hx-muted">${d.category}</span></div>
      </header>
      ${d.desc && html`<p class="hx-text">${d.desc}</p>`}
      ${d.stats?.length > 0 && html`<dl class="hx-kv">${d.stats.map(([k, v]) => html`<dt key=${k}>${t(k)}</dt><dd>${v}</dd>`)}</dl>`}
      ${(model.readonly || model.outfitLocked) && html`<p class="hx-muted">${t(model.readonly ? 'inv.readonly' : 'inv.outfit_locked')}</p>`}
      <div class="hx-actions hx-actions--left">
        ${(model.readonly ? [] : d.actions || []).map((a) => {
          const locked = model.outfitLocked && (a === 'wear' || a === 'stow');
          return html`<${Button} key=${a} variant=${a === 'drop' ? 'danger' : a === 'wear' || a === 'stow' ? 'primary' : undefined}
            disabled=${locked} onClick=${() => (a === 'drop' && item.count > 1 ? onDrop(item) : send('action', { uid: item.uid, kind: a, count: 1 }))}>${t(`inv.action.${a}`)}<//>`;
        })}
      </div>
    </section>`;
}

function DropQuantity({ item, send, onClose }) {
  const [count, setCount] = useState(1);
  const clamp = (v) => Math.max(1, Math.min(item.count, v));
  return html`
    <div class="hx-backdrop hx-surface" onPointerDown=${(e) => e.target === e.currentTarget && onClose()}>
      <section class="hx-window hx-window--medium" role="dialog" aria-modal="true">
        <h2 class="hx-title hx-title--left">${t('inv.drop_quantity_title', { 0: item.name })}</h2>
        <div class="hx-qty">
          <button type="button" class="hx-pips__step" onClick=${() => setCount(clamp(count - 1))}>−</button>
          <input type="range" min="1" max=${item.count} value=${count} style=${{ '--fill': `${((count - 1) / Math.max(1, item.count - 1)) * 100}%` }}
            onInput=${(e) => setCount(Number(e.currentTarget.value))} />
          <button type="button" class="hx-pips__step" onClick=${() => setCount(clamp(count + 1))}>+</button>
          <b>${count}</b>
        </div>
        <div class="hx-actions">
          <${Button} onClick=${onClose}>${t('loot.quantity_cancel')}<//>
          <${Button} variant="danger" onClick=${() => { send('action', { uid: item.uid, kind: 'drop', count }); onClose(); }}>${t('inv.drop_quantity_confirm')}<//>
        </div>
      </section>
    </div>`;
}

function Craft({ model, send }) {
  const craft = model.craft || {};
  const [cat, setCat] = useState('all');
  const [pick, setPick] = useState(null);
  const recipes = (craft.recipes || []).filter((r) => cat === 'all' || r.category === cat);
  const chosen = recipes.find((r) => r.id === pick) || recipes[0];
  const detail = chosen && craft.detail?.[chosen.id];
  if (craft.blocked) return html`<p class="hx-notice">${icons.alert()}<span>${t(craft.blocked)}</span></p>`;
  return html`
    <div class="hx-inv__body">
      <div class="hx-inv__left">
        <div class="hx-chips">
          ${['all', ...(craft.categories || [])].map((c) => html`
            <button type="button" class="hx-chip" key=${c} aria-pressed=${cat === c} onClick=${() => setCat(c)}>${t(`craft.category.${c}`)}</button>`)}
        </div>
        <ul class="hx-recipes">
          ${recipes.map((r) => html`
            <li key=${r.id}><button type="button" class=${`hx-recipe${chosen?.id === r.id ? ' is-selected' : ''}`} onClick=${() => setPick(r.id)}>
              ${asset(r.icon) ? html`<img src=${asset(r.icon)} alt="" />` : html`<span class="hx-slot__name">${r.name}</span>`}
              <span>${r.name}</span>
              ${r.working && html`<b class="hx-slot__tag">${t('craft.working')}</b>`}
              ${r.queued > 0 && html`<b class="hx-recipe__queue">×${r.queued}</b>`}
            </button></li>`)}
        </ul>
      </div>
      ${chosen && detail && html`
        <section class="hx-inv__detail">
          <header class="hx-inv__detail-head">
            ${asset(chosen.icon) && html`<img src=${asset(chosen.icon)} alt="" />`}
            <div><h3>${chosen.name}</h3></div>
          </header>
          <h4 class="hx-section__title">${t('craft.resources')}</h4>
          <ul class="hx-ingredients">
            ${detail.ingredients.map((g) => html`
              <li key=${g.name} class=${g.have >= g.need ? 'is-ok' : 'is-missing'}>
                ${asset(g.icon) ? html`<img src=${asset(g.icon)} alt="" />` : html`<i></i>`}
                <span>${g.name}</span><b>${g.have}/${g.need}</b><span aria-hidden="true">${g.have >= g.need ? '✓' : '✕'}</span>
              </li>`)}
          </ul>
          <dl class="hx-kv">
            <dt>${t('craft.place')}</dt><dd>${detail.station ? t(`craft.station.${detail.station}`) : t('craft.place.in_place')}</dd>
            ${detail.progress > 0 && html`<dt>${t('craft.progress')}</dt><dd>${Math.round(detail.progress * 100)}%</dd>`}
          </dl>
          ${detail.progress > 0 && html`<span class="hx-bar"><i style=${{ width: `${detail.progress * 100}%`, background: 'var(--hx-gold)' }}></i></span>`}
          ${detail.block && !detail.canQueue && html`<p class="hx-warn">${t(`craft.block.${detail.block}`)}</p>`}
          ${craft.stopped?.recipe === chosen.id && html`<p class="hx-notice">${icons.alert()}
            <span>${t('craft.queue_stopped', { 0: t(`craft.block.${craft.stopped.reason}`) })}</span></p>`}
          <div class="hx-actions hx-actions--left hx-craft-go">
            <${Button} variant="primary" disabled=${!detail.canQueue}
              onClick=${() => send('craft', { recipe: chosen.id })}>${t(detail.button === 'working' ? 'craft.create_more' : `craft.${detail.button || 'create'}`)}<//>
            ${detail.queued > 0 && html`
              <span class="hx-queue" aria-live="polite">${t('craft.queue', { 0: detail.queued })}</span>
              <button type="button" class="hx-link-back" onClick=${() => send('craftCancel', { recipe: chosen.id })}>${t('craft.queue_clear')}</button>`}
          </div>
        </section>`}
    </div>`;
}

Inventory.covers = true; // hides the screens below while open (main.js)

export function Inventory({ model, send }) {
  const [tab, setTab] = useState('backpack');
  const [selected, setSelected] = useState(null);
  const [dropping, setDropping] = useState(null);
  const dragged = useRef(null);
  const items = model.items || [];
  const carried = items.filter((i) => !i.worn);
  const worn = items.filter((i) => i.worn);
  const shown = tab === 'clothes' ? worn : carried;
  const cells = tab === 'clothes' ? shown.length : Math.max(model.slots || 0, shown.length);
  const item = items.find((i) => i.uid === selected);
  const quick = (it) => !model.readonly && !model.outfitLocked && it.detail?.actions?.some((a) => a === 'wear' || a === 'stow') &&
    send('action', { uid: it.uid, kind: it.worn ? 'stow' : 'wear', count: 1 });
  return html`
    <div class="hx-inv-layer">
      <div class="hx-inv-catch hx-surface" onPointerDown=${() => send('close')}></div>
      <section class=${`hx-inv hx-surface${tab !== 'craft' && !item ? ' has-hole' : ''}`} role="dialog" aria-modal="true" aria-labelledby="inv-title">
        <header class="hx-inv__head">
          <h2 class="hx-title" id="inv-title">${t('panel.inventory')} · ${model.name}</h2>
          <span class="hx-inv__cap">${model.capacity.used}/${model.capacity.cap} ${t('inv.slots')}</span>
          <label class="hx-switch" title=${t('inv.outfit_lock.tooltip')}>
            <input type="checkbox" checked=${model.outfitLocked} disabled=${model.readonly}
              onChange=${(e) => send('outfitLock', { value: e.currentTarget.checked })} />
            <span>${t('inv.outfit_lock')}</span>
          </label>
          <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
        </header>
        <div class="hx-subtabs" role="tablist">
          ${[['backpack', 'craft.tab.backpack'], ['clothes', 'inv.tab.clothes'], ['craft', 'craft.tab.craft']].map(([id, key]) => html`
            <button type="button" role="tab" key=${id} aria-selected=${tab === id} onClick=${() => { setTab(id); setSelected(null); }}>${t(key)}</button>`)}
        </div>
        ${tab === 'craft' ? html`<${Craft} model=${model} send=${send} />` : html`
          <div class="hx-inv__body">
            <ul class="hx-slots" onDragOver=${(e) => e.preventDefault()}
              onDrop=${(e) => { e.preventDefault(); const it = dragged.current; if (it?.worn && !model.outfitLocked) send('action', { uid: it.uid, kind: 'stow', count: 1 }); }}>
              ${Array.from({ length: cells }, (_, i) => html`<${Slot} key=${shown[i]?.uid ?? `e${i}`} item=${shown[i]} selected=${shown[i]?.uid === selected}
                onSelect=${setSelected} onQuick=${quick} onDragItem=${(it) => { dragged.current = it; }} />`)}
              ${shown.length === 0 && tab === 'clothes' && html`<li class="hx-muted">${t('inv.empty')}</li>`}
            </ul>
            <div class="hx-inv__right">
              ${item ? html`<${Detail} item=${item} model=${model} send=${send} onBack=${() => setSelected(null)} onDrop=${setDropping} />` : html`
                <div class="hx-inv__doll" onDragOver=${(e) => e.preventDefault()}
                  onDrop=${(e) => { e.preventDefault(); const it = dragged.current; if (it && !it.worn && !model.outfitLocked) send('action', { uid: it.uid, kind: 'wear', count: 1 }); }}>
                  <div class="hx-toggle hx-inv__layers" role="group">
                    ${['underwear', 'wear', 'outerwear', 'bags'].map((l) => html`
                      <button type="button" key=${l} aria-pressed=${model.dollLayer === l} onClick=${() => send('dollLayer', { layer: l })}>${t(`layer.${l}`)}</button>`)}
                  </div>
                  <${Hole} send=${send} cmd="dollRect" />
                </div>`}
            </div>
          </div>`}
      </section>
      ${dropping && html`<${DropQuantity} item=${dropping} send=${send} onClose=${() => setDropping(null)} />`}
    </div>`;
}

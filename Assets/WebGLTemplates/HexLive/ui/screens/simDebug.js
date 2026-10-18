// §174.7: the simulation debug overlay (UnityDebug SimulationDebugPanel),
// toggled by Tab — two columns at the top left: the selected colonist as the
// simulation sees her, and the engine's state, clock controls, goal scores
// and her trace tail. Developer tool: the values are the simulation's own
// names (goals, statuses, event types), only the captions are I2.
//
// Model: {
//   ready, tick, paused, speed, remote,      // remote: the server owns the clock (§83), controls are off
//   npc: null | { id, name, tile: [q, r], goal, interaction, inventory: [id], capacity, worn: [id], body,
//                 starving, fighting, needs: { health, hunger, thirst, energy, comfort, social, thermal } (0–1),
//                 relations: [text] },
//   sim: null | { plan, goalLock (tick | null), cooldowns: [goal], known, target: [q, r] | null, movement,
//                 execution, path: 'junction ids' | null, clock, phase, uv, raining, temperature, reserved, occupied },
//   scores: [{ goal, score }],
//   trace: { recorder (bool: the per-NPC flight recorder, else the colony ring), rows: [[tick, type, message]] },
// }
// Commands: pause, step, speed { value }, close.
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';

const SPEEDS = [0.25, 1, 4, 16, 64];
// SimulationDebugPanel's colours: what kind of goal, how the step is going.
const GOAL_TONE = { Eat: 'hunger', GetFood: 'hunger', Socialize: 'social', Sleep: 'energy', Sit: 'comfort', Dress: 'thermal', Idle: 'dim' };
const MOVE_TONE = { Moving: 'energy', Rotating: 'blue', Arrived: 'teal', Blocked: 'thermal' };
const EXEC_TONE = { InProgress: 'blue', Completed: 'energy', Failed: 'thermal', Starting: 'hunger' };
const NEEDS = ['health', 'hunger', 'thirst', 'energy', 'comfort', 'social', 'thermal'];

const tile = (c) => (c ? `(${c[0]}, ${c[1]})` : '—');
const list = (items) => (items?.length ? items.join(', ') : '—');

function Rows({ rows }) {
  return html`<dl class="hx-sdbg__kv">${rows.map(([label, value, tone, mono]) => html`
    <div key=${label}><dt>${label}</dt><dd data-tone=${tone || ''} class=${mono ? 'is-mono' : ''}>${value}</dd></div>`)}</dl>`;
}

function Npc({ npc }) {
  if (!npc) return html`<p class="hx-muted">—</p>`;
  return html`
    <div class="hx-sdbg__who"><b>${npc.name}</b><span>#${npc.id}</span>
      ${npc.starving && html`<i class="hx-sdbg__badge">${t('badge.starving')}</i>`}
      ${npc.fighting && html`<i class="hx-sdbg__badge">${t('simdebug.fighting')}</i>`}</div>
    <${Rows} rows=${[
      [t('simdebug.tile'), tile(npc.tile), null, true],
      [t('simdebug.goal'), npc.goal || '—', GOAL_TONE[npc.goal]],
      [t('simdebug.interaction'), npc.interaction || '—'],
      [t('simdebug.inventory'), `${list(npc.inventory)} (${npc.inventory.length}/${npc.capacity})`],
      [t('simdebug.worn'), list(npc.worn)],
      [t('simdebug.body'), npc.body || '—'],
    ]} />
    <h3 class="hx-section__title">${t('simdebug.needs')}</h3>
    <div class="hx-sdbg__needs">
      ${NEEDS.map((id) => {
        const v = Math.max(0, Math.min(1, npc.needs[id] ?? 0));
        return html`<div key=${id} class="hx-sdbg__need" data-need=${id} title=${t(`simdebug.${id}_tip`)}>
          <span>${t(`simdebug.${id}`)}</span><b>${v.toFixed(2)}</b><i style=${{ '--v': v }}></i></div>`;
      })}
    </div>
    <h3 class="hx-section__title">${t('simdebug.relations')}</h3>
    ${npc.relations.length ? html`<ul class="hx-sdbg__lines">${npc.relations.map((r, i) => html`<li key=${i}>${r}</li>`)}</ul>`
      : html`<p class="hx-muted">—</p>`}`;
}

export function SimDebug({ model, send }) {
  const s = model.sim;
  const trace = model.trace || { rows: [] };
  return html`
    <section class="hx-sdbg hx-surface" aria-label=${t('simdebug.tools')}>
      <div class="hx-sdbg__col">
        <header class="hx-sdbg__head"><h2>${t('simdebug.npc_hud')}</h2></header>
        <${Npc} npc=${model.npc} />
      </div>
      <div class="hx-sdbg__col hx-sdbg__col--wide">
        <header class="hx-sdbg__head">
          <h2>${t('simdebug.tools')}</h2><span class="hx-muted">Tab</span>
          <button class="hx-close" type="button" aria-label=${t('ui.close')} title=${t('ui.close')} onClick=${() => send('close')}>${icons.close()}</button>
        </header>
        <${Rows} rows=${[
          [t('simdebug.ready'), t(model.ready ? 'inspect.yes' : 'inspect.no'), model.ready ? 'energy' : 'thermal'],
          [t('simdebug.tick'), model.tick, null, true],
          [t('simdebug.paused'), t(model.paused ? 'inspect.yes' : 'inspect.no'), model.paused ? 'hunger' : 'energy'],
          [t('simdebug.speed'), `${model.speed}x`, null, true],
        ]} />
        <h3 class="hx-section__title">${t('simdebug.controls')}</h3>
        <div class="hx-sdbg__controls">
          <button type="button" class="hx-sdbg__btn" aria-pressed=${!!model.paused} disabled=${model.remote}
            title=${model.remote ? t('hud.speed.locked') : undefined} onClick=${() => send('pause')}>
            ${model.paused ? icons.play() : icons.pause()}<span>${t('simdebug.pause_resume')}</span></button>
          <button type="button" class="hx-sdbg__btn" disabled=${model.remote} onClick=${() => send('step')}>${t('simdebug.step_tick')}</button>
          <span class="hx-sdbg__speeds" role="group" aria-label=${t('simdebug.speed_presets')} title=${t('simdebug.speed_presets')}>
            ${SPEEDS.map((v) => html`<button type="button" key=${v} class="hx-sdbg__btn" aria-pressed=${model.speed === v} disabled=${model.remote}
              onClick=${() => send('speed', { value: v })}>${v}x</button>`)}
          </span>
        </div>
        ${s && html`
          <h3 class="hx-section__title">${t('simdebug.simulation')}</h3>
          <${Rows} rows=${[
            [t('simdebug.plan'), s.plan || '—'],
            [t('simdebug.goal_lock'), s.goalLock != null ? t('simdebug.until', { 0: s.goalLock }) : '—', null, true],
            [t('simdebug.cooldowns'), list(s.cooldowns)],
            [t('simdebug.memory'), t('simdebug.known', { 0: s.known })],
            [t('simdebug.target_tile'), tile(s.target), null, true],
            [t('simdebug.movement'), s.movement || '—', MOVE_TONE[s.movement]],
            [t('simdebug.execution'), s.execution || '—', EXEC_TONE[s.execution]],
            [t('simdebug.path'), s.path || '—', null, true],
            [t('simdebug.time'), `${s.clock} (${s.phase}) UV ${s.uv.toFixed(2)}${s.raining ? ` · ${t('weather.rain')}` : ''}`, null, true],
            [t('simdebug.temperature'), `${s.temperature.toFixed(1)} °C`, s.temperature < 12 ? 'thermal' : 'energy', true],
            [t('simdebug.reserved_pts'), s.reserved, null, true],
            [t('simdebug.occupied_pts'), s.occupied, null, true],
          ]} />`}
        <h3 class="hx-section__title">${t('simdebug.goal_scores')}</h3>
        ${model.scores?.length ? html`<div class="hx-sdbg__scores">${model.scores.map((g) => html`
          <div key=${g.goal} class="hx-sdbg__score" data-tone=${GOAL_TONE[g.goal] || ''}>
            <span>${g.goal}</span><b>${g.score.toFixed(3)}</b><i style=${{ '--v': Math.max(0, Math.min(1, g.score)) }}></i></div>`)}</div>`
          : html`<p class="hx-muted">—</p>`}
        <h3 class="hx-section__title">${t('simdebug.trace_log')}</h3>
        ${trace.rows.length ? html`
          <p class="hx-sdbg__note">${t('simdebug.trace_count', { 0: trace.rows.length, 1: model.npc?.id ?? 0,
            2: t(trace.recorder ? 'simdebug.trace_recorder' : 'simdebug.trace_ring') })}</p>
          <ol class="hx-sdbg__trace">${trace.rows.map(([tick, type, message], i) => html`
            <li key=${i}><span>[${tick}]</span><b>${type}</b><p>${message}</p></li>`)}</ol>`
          : html`<p class="hx-muted">${t(model.npc ? 'simdebug.trace_none_npc' : 'simdebug.trace_none')}</p>`}
      </div>
    </section>`;
}

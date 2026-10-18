// §174.7: the always-on HUD (SimSpeedBar.cs): survival day and weather at
// the top left, time controls at the top centre. Not modal — only the pills
// themselves take the pointer.
// Model: {
//   day, temperature (°C as shown), band: 'cold' | 'mild' | 'hot', raining,
//   clock: '14:20', phase: 'Day' | 'Morning' | 'Evening' | 'Night',
//   link: null | { state: 'live' | 'stalled' | 'reconnecting' | 'failed' | 'connecting', ping },
//   paused, speed: 1 | 2 | 4 | 50 | 'max' (nearest preset), remote,
//   build: null | { open },                  // §120.7 build mode, when it can be used
//   bugs: null | { ready, canReport },       // §114: report a bug; ready = waiting for your test
//   debug: null | { open },                  // the developer tools drawer
//   admin: null | { recording } }            // §161 admin voice: only on a server that has it
// Commands: pause (toggle), speed { value }, build (toggle the build tray), bug (the quick
// bug report), debug (toggle the drawer), admin (the admin microphone:
// opens the window until access is granted, then records / stops). On a shared world (remote) the
// server owns the clock: pause and >1× stay visible but disabled (§83).
import { html } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';

const SPEEDS = [1, 2, 4, 50, 'max'];

function Weather({ model }) {
  if (model.day == null) return null;
  const link = model.link;
  return html`
    <div class="hx-pill hx-weather hx-surface">
      <span class="hx-weather__day">${t('weather.day')} ${model.day}</span>
      <span class="hx-pill__divider"></span>
      <span class="hx-weather__temp" data-band=${model.band}>${model.temperature}°C</span>
      <span class="hx-weather__sky" data-rain=${!!model.raining}>
        ${model.raining ? icons.drop() : icons.sun()}${t(model.raining ? 'weather.rain' : 'weather.clear')}
      </span>
      <span class="hx-weather__clock">${model.clock} · ${t(`phase.${model.phase}`)}</span>
      ${link && html`
        <span class="hx-link" data-state=${link.state}>
          ${icons.signal()}${t(`link.${link.state}`)}${link.state === 'live' && link.ping >= 0 ? ` ${link.ping} ms` : ''}
        </span>`}
    </div>`;
}

function Speed({ model, send }) {
  const remote = !!model.remote;
  const locked = remote ? t('hud.speed.locked') : undefined;
  return html`
    <div class="hx-pill hx-speed hx-surface" role="toolbar">
      <button class="hx-seg" type="button" disabled=${remote} title=${locked || t(model.paused ? 'hud.resume' : 'hud.pause')}
        aria-label=${t(model.paused ? 'hud.resume' : 'hud.pause')} aria-pressed=${!!model.paused}
        onClick=${() => send('pause')}>
        ${model.paused ? icons.play() : icons.pause()}
      </button>
      <span class="hx-pill__divider"></span>
      ${SPEEDS.map((value) => {
        const disabled = remote && value !== 1;
        const active = !disabled && !model.paused && model.speed === value;
        return html`
          <button class="hx-seg" type="button" key=${value} disabled=${disabled} aria-pressed=${active}
            title=${disabled ? locked : value === 'max' ? t('hud.speed.max') : undefined}
            onClick=${() => send('speed', { value })}>${value === 'max' ? '∞' : `${value}x`}</button>`;
      })}
    </div>`;
}

// The build tray's door: next to the clock, because building is about the
// world, not about the colonist on the card (BuildModePanel).
function Build({ model, send }) {
  if (!model.build) return null;
  return html`<div class="hx-pill hx-surface">
    <button class="hx-seg hx-seg--wide" type="button" aria-pressed=${!!model.build.open} title=${`${t('build.mode.button')} (B)`}
      onClick=${() => send('build')}>${icons.hammer()}<span>${t('build.mode.button')}</span></button>
  </div>`;
}

// The bug report is the player's tool in every test round: its own button,
// with the number of fixes waiting for their check. DEBUG opens the dev tools.
function Tools({ model, send }) {
  if (!model.bugs && !model.debug && !model.admin) return null;
  return html`<div class="hx-pill hx-surface">
    ${model.bugs && html`<button class="hx-seg hx-seg--wide" type="button" disabled=${!model.bugs.canReport}
      title=${t('debug.report')} onClick=${() => send('bug')}>${icons.bug()}
      ${model.bugs.ready > 0 && html`<b class="hx-hud__badge" title=${t('bugs.debug.ready', { 0: model.bugs.ready })}>${model.bugs.ready}</b>`}</button>`}
    ${model.admin && html`<button class=${`hx-seg${model.admin.recording ? ' is-recording' : ''}`} type="button" aria-pressed=${!!model.admin.recording}
      title=${t('admin.voice.title')} aria-label=${t('admin.voice.title')} onClick=${() => send('admin')}>${icons.mic()}</button>`}
    ${model.debug && html`<button class="hx-seg" type="button" aria-pressed=${!!model.debug.open} title="DEBUG" onClick=${() => send('debug')}>DEBUG</button>`}
  </div>`;
}

export function Hud({ model, send }) {
  return html`<div class="hx-hud-top"><${Weather} model=${model} />
    <div class="hx-hud-centre"><${Speed} model=${model} send=${send} /><${Build} model=${model} send=${send} /><${Tools} model=${model} send=${send} /></div></div>`;
}

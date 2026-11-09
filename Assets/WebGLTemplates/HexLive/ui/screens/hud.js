// §174.7: the always-on HUD (SimSpeedBar.cs): survival day and weather at
// the top left, time controls at the top centre. Not modal — only the pills
// themselves take the pointer.
// Model: {
//   day, temperature (°C as shown), band: 'cold' | 'mild' | 'hot', raining,
//   clock: '14:20', phase: 'Day' | 'Morning' | 'Evening' | 'Night',
//   link: null | { state: 'live' | 'stalled' | 'reconnecting' | 'failed' | 'connecting', ping },
//   paused, speed: 1 | 2 | 4 | 50 | 'max' (nearest preset), remote,
//   build: null | { open },                  // §120.7 build mode, when it can be used
//   bugs: { canReport, recording, level, pending, failed, completed, toast },
//   debug: null | { open },                  // the developer tools drawer
//   balance: null | { amount: '10.07' | null (unavailable), details } } // the account, as «10.07 🪙»
// Commands: pause (toggle), speed { value }, build (toggle the build tray), bug (the quick
// recording), bugErrors (retry a failed delivery), debug (toggle the drawer).
// The administrator microphone is inside DEBUG. On a shared world (remote) the
// server owns the clock: pause and >1× stay visible but disabled (§83).
import { html, useEffect, useState } from '../html.js';
import { tip } from '../tooltip.js';
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
        <span class="hx-link" data-state=${link.state} title=${t(`link.${link.state}`)}>
          ${icons.signal()}${link.state === 'live' && link.ping >= 0
            // Live is said by the green signal; the words would cost the row the coins (18.10.2026).
            ? `${link.ping} ms` : t(`link.${link.state}`)}
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

// The player's microphone and delivery status stay under the clock.
function Tools({ model, send }) {
  if (!model.bugs && !model.debug) return null;
  return html`<div class="hx-pill hx-surface hx-hud-tools">
    ${model.bugs && html`<button class=${`hx-seg hx-bug-mic${model.bugs.recording ? ' is-recording' : ''}`} type="button" disabled=${!model.bugs.canReport}
      title=${t('bugs.voice.record')} aria-label=${t('bugs.voice.record')} style=${{ '--voice-level': model.bugs.level || 0 }} onClick=${() => send('bug')}>${icons.mic()}</button>`}
    ${model.bugs && (model.bugs.pending > 0 || model.bugs.completed > 0) && html`<span class="hx-bug-delivery" role="status"
      title=${t('bugs.queue.progress', { 0: model.bugs.completed || 0, 1: model.bugs.pending || 0 })}>
      ${model.bugs.pending > 0 && html`<span class="hx-bug-delivery__pending"><i class="hx-bug-spin" aria-hidden="true">↻</i> ${model.bugs.pending}</span>`}
      ${model.bugs.completed > 0 && html`<span class="hx-bug-delivery__sent">✓ ${model.bugs.completed}</span>`}
    </span>`}
    ${model.bugs && (model.bugs.failed > 0 || model.bugs.warning) && html`<button class="hx-seg hx-bug-retry" type="button"
      title=${model.bugs.error || t('bugs.queue.failed')} aria-label=${t('bugs.queue.error_short')} onClick=${() => send('bugErrors')}>
      ! ${t('bugs.queue.error_short')}${model.bugs.failed > 1 ? ` (${model.bugs.failed})` : ''}</button>`}
    ${model.bugs?.toast && !model.bugs.warning && html`<span class=${`hx-hud__report-toast${model.bugs.toastSuccess ? ' is-sent' : ''}`} role="status">${model.bugs.toast}</span>`}
    ${model.debug && html`<button class="hx-seg" type="button" aria-pressed=${!!model.debug.open} title="DEBUG" onClick=${() => send('debug')}>DEBUG</button>`}
  </div>`;
}

// The right end of the row: the account's coins and the fullscreen button.
// The page's own button (index.html #unity-footer) stood exactly here and
// left no room, so while the HUD is up the HUD carries it (body.hx-hud-on).
function isFull() { return !!(document.fullscreenElement || document.webkitFullscreenElement); }

function Corner({ model }) {
  const [full, setFull] = useState(isFull());
  useEffect(() => {
    const update = () => setFull(isFull());
    document.addEventListener('fullscreenchange', update);
    document.addEventListener('webkitfullscreenchange', update);
    document.body.classList.add('hx-hud-on');
    return () => {
      document.removeEventListener('fullscreenchange', update);
      document.removeEventListener('webkitfullscreenchange', update);
      document.body.classList.remove('hx-hud-on');
    };
  }, []);
  const b = model.balance;
  const canFull = typeof window.hexToggleFullscreen === 'function';
  return html`<div class="hx-hud-corner">
    ${b && html`<span class="hx-pill hx-surface hx-coins" tabindex="0" data-off=${b.amount == null}
      ...${tip(() => ({ icon: '🪙', title: b.amount == null ? t('account.balance.unavailable') : t('account.balance.amount', { 0: b.amount }), desc: b.amount == null ? null : b.details }))}>
      <b>${b.amount ?? '—'}</b><span aria-hidden="true">🪙</span></span>`}
    ${canFull && html`<button type="button" class="hx-pill hx-surface hx-fullscreen" data-hx-fullscreen
      title=${t(full ? 'ui.fullscreen.exit' : 'ui.fullscreen')} aria-label=${t(full ? 'ui.fullscreen.exit' : 'ui.fullscreen')}
      onClick=${() => window.hexToggleFullscreen()}>${full ? icons.fullscreenExit() : icons.fullscreen()}</button>`}
  </div>`;
}

export function Hud({ model, send }) {
  return html`<div class="hx-hud-top"><${Weather} model=${model} />
    <div class="hx-hud-centre"><div class="hx-hud-primary"><${Speed} model=${model} send=${send} /><${Build} model=${model} send=${send} /></div><${Tools} model=${model} send=${send} /></div>
    <${Corner} model=${model} /></div>`;
}

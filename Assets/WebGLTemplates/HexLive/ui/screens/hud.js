// §174.7: the always-on HUD (SimSpeedBar.cs): survival day and weather at
// the top left, time controls at the top centre. Not modal — only the pills
// themselves take the pointer.
// Model: {
//   day, temperature (°C as shown), band: 'cold' | 'mild' | 'hot', raining,
//   clock: '14:20', phase: 'Day' | 'Morning' | 'Evening' | 'Night',
//   link: null | { state: 'live' | 'stalled' | 'reconnecting' | 'failed' | 'connecting', ping },
//   paused, speed: 1 | 2 | 4 | 50 | 'max' (nearest preset), remote }
// Commands: pause (toggle), speed { value }. On a shared world (remote) the
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

export function Hud({ model, send }) {
  return html`<div class="hx-hud-top"><${Weather} model=${model} /><${Speed} model=${model} send=${send} /></div>`;
}

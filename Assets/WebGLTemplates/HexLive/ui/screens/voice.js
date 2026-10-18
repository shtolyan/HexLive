// §174.7: the voice windows (§160/§161) — three small screens that used to be
// UI Toolkit pieces of AdminVoicePanel and CharacterPanel.AgentVoice:
//
// adminVoice — the administrator's window: access to the server's admin
//   rights, then a request in plain words and the typed JSON command.
//   Model: {
//     access: 'unrequested'|'pending'|'approved'|'rejected'|'expired'|'revoked'|'invalid'|'queue_full',
//     expires: text | null,                  // when the granted rights end
//     showRequest, canRequest,               // the request button (hidden in a closed test)
//     busy,                                  // a request is on its way
//     confirm: text | null,                  // the server asks to confirm this action
//     mic: null | { recording },             // speech: only while STT and the agent are up
//   }
//   Commands: request, send { text }, typed { json }, confirm { accept }, mic, close.
//
// voiceCapture — the recording card both microphones share (the admin one
//   and talking to a character's agent). Model: { title, level 0…1 }.
//   Commands: done, cancel.
//
// voiceSubtitle — one line of a reply over the world, queued in C# so lines
//   never overwrite each other. Model: { text, tone: 'success'|'error'|null }.
import { html, useState } from '../html.js';
import { t } from '../i18n.js';
import { icons } from '../icons.js';
import { ModalWindow, Button } from '../components.js';

export function AdminVoice({ model, send }) {
  const [text, setText] = useState('');
  const [json, setJson] = useState('');
  const approved = model.access === 'approved';
  const idle = approved && !model.busy;
  return html`
    <${ModalWindow} id="admin-voice" title=${t('admin.voice.title')} size="medium" onClose=${() => send('close')}>
      <div class="hx-admin" onKeyDown=${(e) => {
        // A focused field keeps the keys from Unity, so Escape is answered here.
        if (e.key === 'Escape') { e.preventDefault(); send('close'); }
      }}>
        <p class=${`hx-admin__status is-${model.access}`}>
          ${t(`admin.voice.access.${model.access}`)}
          ${approved && model.expires && html`<span>${model.expires}</span>`}
        </p>
        ${model.showRequest && !approved && html`
          <${Button} variant="primary" disabled=${!model.canRequest || model.access === 'pending'} onClick=${() => send('request')}>
            ${t('admin.voice.request_access')}</${Button}>`}
        ${approved && html`
          ${model.confirm && html`
            <div class="hx-admin__confirm" role="alert">
              <b>${t('admin.voice.confirm_required')}</b>
              <p>${model.confirm}</p>
              <div class="hx-admin__row">
                <${Button} variant="danger" disabled=${model.busy} onClick=${() => send('confirm', { accept: true })}>${t('admin.voice.yes')}</${Button}>
                <${Button} disabled=${model.busy} onClick=${() => send('confirm', { accept: false })}>${t('admin.voice.no')}</${Button}>
              </div>
            </div>`}
          <label class="hx-admin__field">
            <span>${t('admin.voice.command')}</span>
            <textarea rows="3" maxlength="2000" value=${text} onInput=${(e) => setText(e.currentTarget.value)}
              onKeyDown=${(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && idle && text.trim()) { send('send', { text: text.trim() }); setText(''); } }}></textarea>
          </label>
          <p class="hx-admin__hint">${t('admin.voice.examples')}</p>
          <div class="hx-admin__row">
            ${model.mic && html`
              <button type="button" class=${`hx-admin__mic${model.mic.recording ? ' is-on' : ''}`} disabled=${!idle && !model.mic.recording}
                title=${t('admin.voice.microphone')} aria-label=${t('admin.voice.microphone')} onClick=${() => send('mic')}>${icons.mic()}</button>`}
            <${Button} variant="primary" disabled=${!idle || !text.trim() || !!model.confirm}
              onClick=${() => { send('send', { text: text.trim() }); setText(''); }}>
              ${model.busy ? t('admin.voice.thinking') : t('admin.voice.send')}</${Button}>
          </div>
          <details class="hx-admin__typed">
            <summary>${t('admin.voice.typed_command')}</summary>
            <textarea rows="4" maxlength="8000" spellcheck="false" value=${json} placeholder='{"kind":"heal","npcId":901,"target":"all"}'
              onInput=${(e) => setJson(e.currentTarget.value)}></textarea>
            <p class="hx-admin__hint">${t('admin.voice.typed_help')}</p>
            <${Button} disabled=${!idle || !json.trim()} onClick=${() => send('typed', { json })}>${t('admin.voice.send_typed')}</${Button}>
          </details>`}
      </div>
    </${ModalWindow}>`;
}

const BARS = 18;

export function VoiceCapture({ model, send }) {
  const lit = Math.round(Math.max(0, Math.min(1, model.level || 0)) * BARS);
  return html`
    <div class="hx-backdrop hx-surface hx-capture">
      <section class="hx-capture__card" role="dialog" aria-modal="true" aria-label=${model.title}>
        <span class="hx-capture__icon">${icons.mic()}</span>
        <h2>${model.title}</h2>
        <div class="hx-capture__meter" aria-hidden="true">
          ${Array.from({ length: BARS }, (_, i) => html`<i key=${i} class=${i < lit ? 'is-lit' : ''}></i>`)}
        </div>
        <button type="button" class="hx-capture__done" onClick=${() => send('done')}>✓ ${t('agent.voice.capture_done')}</button>
        <button type="button" class="hx-capture__cancel" onClick=${() => send('cancel')}>${t('loot.quantity_cancel')}</button>
      </section>
    </div>`;
}

export function VoiceSubtitle({ model }) {
  return html`<p class="hx-subtitle" data-tone=${model.tone || ''} role="status">${model.text}</p>`;
}

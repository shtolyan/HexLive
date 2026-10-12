// §171.11: the browser half of the character voice. ElevenLabs Agents hears and
// speaks over WebRTC; the game server hands out the session (only for the
// player's own character) and its brain decides. One module, served by the game
// server, used by the WebGL build (through HexVoiceAgent.jslib) and by test pages.
//
// Events delivered to onEvent({type, ...}):
//   state   {state: "connecting"|"listening"|"speaking"|"ended"|"error", detail?}
//   user    {text}                       final transcript of the player
//   agent   {text}                       what the character said
//   align   {chars, starts, durations}   per-character timing of the audio (lip-sync)
//   usage   {seconds, providerCredits}   after the session is closed and billed
const SDK = "https://cdn.jsdelivr.net/npm/@elevenlabs/client@1.25.0/+esm";

let active = null;

/** Start talking to npcId. server: "https://host" of the game server. */
export async function startVoice({ server, bearer, clientId, npcId, onEvent }) {
  if (active) await stopVoice();
  const emit = (e) => { try { onEvent && onEvent(e); } catch (_) { /* never let UI code kill audio */ } };
  const headers = { "Content-Type": "application/json", "Authorization": "Bearer " + bearer };
  if (clientId) headers["X-HexLive-Client-Id"] = clientId;
  const base = server.replace(/\/+$/, "");
  emit({ type: "state", state: "connecting" });
  const me = { conversation: null, ticket: null, base, headers, emit, stopped: false };
  active = me;
  try {
    const r = await fetch(base + "/api/voice/v1/session", { method: "POST", headers, body: JSON.stringify({ npcId }) });
    if (!r.ok) throw new Error("session:" + r.status);
    const session = await r.json();
    me.ticket = session.ticket;
    const { Conversation } = await import(SDK);
    if (me.stopped) return;
    me.conversation = await Conversation.startSession({
      conversationToken: session.conversationToken,
      connectionType: "webrtc",
      customLlmExtraBody: { ticket: session.ticket },
      overrides: session.voiceId ? { tts: { voiceId: session.voiceId } } : undefined,
      onConnect: () => emit({ type: "state", state: "listening" }),
      onDisconnect: () => { emit({ type: "state", state: "ended" }); if (active === me) active = null; },
      onError: (e) => emit({ type: "state", state: "error", detail: String((e && e.message) || e) }),
      onModeChange: ({ mode }) => emit({ type: "state", state: mode === "speaking" ? "speaking" : "listening" }),
      onMessage: ({ source, message }) => emit({ type: source === "user" ? "user" : "agent", text: message }),
      onAudioAlignment: (a) => emit({ type: "align", chars: a.chars, starts: a.char_start_times_ms, durations: a.char_durations_ms }),
    });
  } catch (e) {
    emit({ type: "state", state: "error", detail: String((e && e.message) || e) });
    if (active === me) active = null;
    throw e;
  }
}

/** Hang up and bill the conversation (§171.4). Safe to call twice. */
export async function stopVoice() {
  const me = active;
  if (!me) return;
  active = null;
  me.stopped = true;
  const id = me.conversation && me.conversation.getId ? me.conversation.getId() : null;
  try { if (me.conversation) await me.conversation.endSession(); } catch (_) { }
  if (id && me.ticket) {
    try {
      const r = await fetch(me.base + "/api/voice/v1/session/end", { method: "POST", headers: me.headers,
        body: JSON.stringify({ conversationId: id, ticket: me.ticket }) });
      if (r.ok) { const j = await r.json(); me.emit({ type: "usage", seconds: j.seconds, providerCredits: j.providerCredits }); }
    } catch (_) { }
  }
}

export function isActive() { return active !== null; }

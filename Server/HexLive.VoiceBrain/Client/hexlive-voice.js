// §171.11/§171.15: WebRTC lifecycle and user silence; audio stays in the SDK.
const SDK = "https://cdn.jsdelivr.net/npm/@elevenlabs/client@1.25.0/+esm";
export const USER_SILENCE_MS = 15000;
export function createVoiceClient({ request = (...a) => fetch(...a), sdk = () => import(SDK), later = setTimeout, cancel = clearTimeout } = {}) {
  let active = null;
  function arm(me) {
    cancel(me.timer); me.timer = null;
    if (me.stopped || !me.connected || me.mode === "speaking") return;
    me.timer = later(() => void finish(me, "user_silence"), USER_SILENCE_MS);
  }
  async function bill(me) {
    if (me.billed || !me.conversation || !me.ticket) return;
    const id = me.conversation.getId?.(); if (!id) return;
    me.billed = true;
    try {
      const r = await request(me.base + "/api/voice/v1/session/end", { method: "POST", headers: me.headers, keepalive: true,
        body: JSON.stringify({ conversationId: id, ticket: me.ticket }) });
      if (!r.ok) throw new Error("billing:" + r.status);
      const j = await r.json(); me.emit({ type: "usage", seconds: j.seconds, providerCredits: j.providerCredits });
    } catch (_) { me.emit({ type: "billing_pending" }); }
  }
  function finish(me, reason) {
    if (me.finishing) return me.finishing;
    me.stopped = true; cancel(me.timer);
    if (active === me) active = null;
    me.emit({ type: "state", state: "ended", detail: reason });
    // Assign before endSession can synchronously fire onDisconnect.
    me.finishing = Promise.resolve().then(async () => {
      try { await me.conversation?.endSession(); } catch (_) { }
      await bill(me);
    });
    return me.finishing;
  }
  async function startVoice({ server, bearer, clientId, npcId, onEvent }) {
    if (active) await finish(active, "replaced");
    const emit = e => { try { onEvent?.(e); } catch (_) { } };
    const headers = { "Content-Type": "application/json", Authorization: "Bearer " + bearer };
    if (clientId) headers["X-HexLive-Client-Id"] = clientId;
    const me = { conversation: null, ticket: null, base: server.replace(/\/+$/, ""), headers, emit,
      stopped: false, connected: false, mode: "listening", timer: null, billed: false, finishing: null };
    active = me; emit({ type: "state", state: "connecting" });
    try {
      const r = await request(me.base + "/api/voice/v1/session", { method: "POST", headers, body: JSON.stringify({ npcId }) });
      if (!r.ok) throw new Error("session:" + r.status);
      const session = await r.json(); me.ticket = session.ticket;
      if (me.stopped) return;
      const { Conversation } = await sdk(); if (me.stopped) return;
      me.conversation = await Conversation.startSession({
        conversationToken: session.conversationToken, connectionType: "webrtc", customLlmExtraBody: { ticket: session.ticket },
        overrides: session.voiceId ? { tts: { voiceId: session.voiceId } } : undefined,
        onConnect: () => { if (me.stopped) return; me.connected = true; arm(me); emit({ type: "state", state: "listening" }); },
        onDisconnect: () => { void finish(me, "disconnected"); },
        onError: e => { if (me.stopped) return; emit({ type: "state", state: "error", detail: String(e?.message || e) }); void finish(me, "error"); },
        onModeChange: ({ mode }) => {
          if (me.stopped) return;
          const next = mode === "speaking" ? "speaking" : "listening";
          if (next !== me.mode) { me.mode = next; arm(me); }
          emit({ type: "state", state: next });
        },
        onVadScore: ({ vadScore }) => { if (vadScore >= 0.5) arm(me); },
        onMessage: ({ source, message }) => {
          if (me.stopped) return; if (source === "user" && message?.trim()) arm(me);
          emit({ type: source === "user" ? "user" : "agent", text: message });
        },
        onAudioAlignment: a => { if (!me.stopped) emit({ type: "align", chars: a.chars,
          starts: a.char_start_times_ms, durations: a.char_durations_ms }); },
      });
      // Disconnect can happen before the SDK promise gives us the conversation.
      if (me.stopped) { try { await me.conversation.endSession(); } catch (_) { } await bill(me); }
    } catch (e) {
      if (!me.stopped) { emit({ type: "state", state: "error", detail: String(e?.message || e) }); await finish(me, "error"); }
      throw e;
    }
  }
  return { startVoice, stopVoice: () => active ? finish(active, "hangup") : Promise.resolve(), isActive: () => active !== null };
}
const client = createVoiceClient();
export const startVoice = client.startVoice;
export const stopVoice = client.stopVoice;
export const isActive = client.isActive;

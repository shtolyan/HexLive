// §171.11/§171.15: WebRTC lifecycle and user silence; audio stays in the SDK.
const SDK = "https://cdn.jsdelivr.net/npm/@elevenlabs/client@1.25.0/+esm";
export const USER_SILENCE_MS = 15000;
export const CONNECTION_TIMEOUT_MS = 30000;
const INPUT_ACTIVITY_THRESHOLD = 0.04;
export function createVoiceClient({ request = (...a) => fetch(...a), sdk = () => import(SDK), later = setTimeout, cancel = clearTimeout, clock = () => performance.now() } = {}) {
  let active = null;
  function arm(me, userActivity = false) {
    cancel(me.timer); me.timer = null;
    if (me.listeningSince !== null) me.remaining = Math.max(0, me.remaining - (clock() - me.listeningSince));
    me.listeningSince = null;
    if (userActivity) me.remaining = USER_SILENCE_MS;
    if (me.stopped || !me.connected || me.mode === "speaking") return;
    me.listeningSince = clock();
    me.timer = later(() => void finish(me, "user_silence"), me.remaining);
  }
  function pollInput(me) {
    if (me.stopped) return;
    // VAD is authoritative. Without it, only a new mic burst above the observed
    // background can extend listening: a continuous fan must not renew forever.
    try {
      const volume = me.conversation?.getInputVolume?.() || 0;
      if (!me.vadAvailable && me.mode !== "speaking" && Number.isFinite(volume)) {
        if (me.micBaseline === null) me.micBaseline = volume;
        const hot = volume >= Math.max(INPUT_ACTIVITY_THRESHOLD, me.micBaseline * 1.8 + 0.02);
        if (hot && !me.micHot) arm(me, true);
        if (!hot) me.micBaseline = Math.min(volume, me.micBaseline * 0.95 + volume * 0.05);
        me.micHot = hot;
      }
    } catch (_) { }
    me.activityTimer = later(() => pollInput(me), 200);
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
  async function releaseControl(me) {
    if (!me.ticket || me.released) return;
    me.released = true;
    try {
      const r = await request(me.base + "/api/voice/v1/session/cancel", { method: "POST", headers: me.headers, keepalive: true,
        body: JSON.stringify({ ticket: me.ticket }) });
      if (!r.ok) throw new Error("control_release:" + r.status);
    } catch (_) { me.emit({ type: "control_release_pending" }); }
  }
  function finish(me, reason) {
    if (me.finishing) return me.finishing;
    me.stopped = true; cancel(me.timer); cancel(me.activityTimer); cancel(me.connectTimer);
    me.abort.abort();
    if (active === me) active = null;
    // Leave error visible to the game until it acknowledges it; otherwise the
    // ended event in the same batch masks session/402 and microphone errors.
    if (reason !== "error") me.emit({ type: "state", state: "ended", detail: reason });
    // Assign before endSession can synchronously fire onDisconnect.
    me.finishing = Promise.resolve().then(async () => {
      void releaseControl(me); // Independent of WebRTC teardown and final billing.
      try { await me.conversation?.endSession(); } catch (_) { }
      await bill(me);
    });
    return me.finishing;
  }
  async function startVoice({ server, bearer, clientId, npcId, controlEpoch, onEvent }) {
    if (active) await finish(active, "replaced");
    const emit = e => { try { onEvent?.(e); } catch (_) { } };
    const headers = { "Content-Type": "application/json", Authorization: "Bearer " + bearer };
    if (clientId) headers["X-HexLive-Client-Id"] = clientId;
    const me = { conversation: null, ticket: null, base: server.replace(/\/+$/, ""), headers, emit,
      stopped: false, connected: false, mode: "listening", timer: null, activityTimer: null, connectTimer: null,
      remaining: USER_SILENCE_MS, listeningSince: null, micBaseline: null, micHot: false, vadAvailable: false,
      abort: new AbortController(), stage: "session", billed: false, released: false, finishing: null };
    active = me; emit({ type: "state", state: "connecting" });
    me.connectTimer = later(() => {
      if (me.stopped || me.connected) return;
      emit({ type: "state", state: "error", detail: "connect_timeout:" + me.stage });
      void finish(me, "error");
    }, CONNECTION_TIMEOUT_MS);
    try {
      const r = await request(me.base + "/api/voice/v1/session", { method: "POST", headers, body: JSON.stringify({ npcId, controlEpoch }), signal: me.abort.signal });
      if (!r.ok) throw new Error("session:" + r.status);
      const session = await r.json(); me.ticket = session.ticket;
      if (me.stopped) { await releaseControl(me); return; }
      me.stage = "sdk";
      const { Conversation } = await sdk(); if (me.stopped) return;
      me.stage = "webrtc";
      me.conversation = await Conversation.startSession({
        conversationToken: session.conversationToken, connectionType: "webrtc", userId: session.sessionId,
        customLlmExtraBody: { ticket: session.ticket },
        overrides: session.voiceId ? { tts: { voiceId: session.voiceId } } : undefined,
        onConnect: () => { if (me.stopped) return; me.connected = true; cancel(me.connectTimer); arm(me); emit({ type: "state", state: "listening" }); },
        onDisconnect: () => { void finish(me, "disconnected"); },
        onError: e => { if (me.stopped) return; emit({ type: "state", state: "error", detail: String(e?.message || e) }); void finish(me, "error"); },
        onModeChange: ({ mode }) => {
          if (me.stopped) return;
          const next = mode === "speaking" ? "speaking" : "listening";
          if (next !== me.mode) { arm(me); me.mode = next; arm(me); }
          emit({ type: "state", state: next });
        },
        onVadScore: ({ vadScore }) => {
          if (me.stopped || !Number.isFinite(vadScore)) return;
          me.vadAvailable = true;
          if (vadScore >= 0.5) arm(me, true);
        },
        onMessage: ({ source, message }) => {
          if (me.stopped) return; if (source === "user" && message?.trim()) arm(me, true);
          emit({ type: source === "user" ? "user" : "agent", text: message });
        },
        onAudioAlignment: a => { if (!me.stopped) emit({ type: "align", chars: a.chars,
          starts: a.char_start_times_ms, durations: a.char_durations_ms }); },
      });
      if (!me.stopped) pollInput(me);
      // Disconnect can happen before the SDK promise gives us the conversation.
      if (me.stopped) { try { await me.conversation.endSession(); } catch (_) { } await bill(me); }
    } catch (e) {
      if (me.stopped) return;
      emit({ type: "state", state: "error", detail: String(e?.message || e) }); await finish(me, "error");
      throw e;
    }
  }
  return { startVoice, stopVoice: () => active ? finish(active, "hangup") : Promise.resolve(), isActive: () => active !== null };
}
const client = createVoiceClient();
export const startVoice = client.startVoice;
export const stopVoice = client.stopVoice;
export const isActive = client.isActive;

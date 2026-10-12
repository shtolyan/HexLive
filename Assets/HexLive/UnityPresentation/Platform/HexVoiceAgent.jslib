// §171.11: bridge to the game server's browser voice module (/api/voice/v1/client.js).
// Same shape as HexWireSocket.jslib: JS queues events, C# drains them once a frame
// (size, then take into a buffer) — nothing in here calls back into C#.
// The module is loaded with an injected <script type="module">, not import():
// the WebGL build's minifier does not have to understand dynamic import syntax.
mergeInto(LibraryManager.library, {
  HexVoiceStart: function (serverPtr, bearerPtr, clientPtr, npcId) {
    var server = UTF8ToString(serverPtr).replace(/\/+$/, '');
    var bearer = UTF8ToString(bearerPtr);
    var clientId = UTF8ToString(clientPtr);
    var state = window.__hexVoice || (window.__hexVoice = { queue: [], module: null, pending: null });
    function push(e) { state.queue.push(e); }
    function begin(m) {
      m.startVoice({ server: server, bearer: bearer, clientId: clientId, npcId: npcId, onEvent: push })
        .catch(function (e) { push({ type: 'state', state: 'error', detail: String((e && e.message) || e) }); });
    }
    if (state.module) { begin(state.module); return; }
    state.pending = begin;
    if (state.loading) return;
    state.loading = true;
    window.addEventListener('hexvoice-ready', function () {
      state.module = window.__hexVoiceModule;
      if (state.pending) { var go = state.pending; state.pending = null; go(state.module); }
    }, { once: true });
    var script = document.createElement('script');
    script.type = 'module';
    script.textContent = 'import * as m from ' + JSON.stringify(server + '/api/voice/v1/client.js') +
      '; window.__hexVoiceModule = m; window.dispatchEvent(new Event("hexvoice-ready"));';
    script.onerror = function () { state.loading = false; push({ type: 'state', state: 'error', detail: 'module' }); };
    document.head.appendChild(script);
  },

  HexVoiceStop: function () {
    var state = window.__hexVoice;
    if (state) state.pending = null;
    if (state && state.module) state.module.stopVoice();
  },

  // Byte length of the pending event batch as UTF-8 JSON, 0 when empty. The
  // batch is frozen here so HexVoiceTake copies exactly what was measured.
  HexVoiceNextSize: function () {
    var state = window.__hexVoice;
    if (!state) return 0;
    if (!state.batch && state.queue.length) {
      state.batch = JSON.stringify(state.queue.splice(0, state.queue.length));
    }
    return state.batch ? lengthBytesUTF8(state.batch) : 0;
  },

  // bufferPtr holds size + 1 bytes (UTF-8 plus the terminator stringToUTF8 writes).
  HexVoiceTake: function (bufferPtr, capacity) {
    var state = window.__hexVoice;
    if (!state || !state.batch) return 0;
    var text = state.batch;
    var size = lengthBytesUTF8(text);
    if (size + 1 > capacity) return -size; // caller re-measures; batch kept
    state.batch = null;
    stringToUTF8(text, bufferPtr, capacity);
    return size;
  }
});

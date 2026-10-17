// §168.5: page-level helpers for WebPage.cs.
mergeInto(LibraryManager.library, {
  HexCreatorStoreGet: function (keyPtr) {
    try {
      var key = UTF8ToString(keyPtr);
      if (!/^(hexlive\.creator\.|hexlive\.characterAttempt\.)/.test(key)) return null;
      var value = localStorage.getItem(key);
      if (!value || value.length > 32768) return null;
      var buffer = _malloc(lengthBytesUTF8(value) + 1);
      stringToUTF8(value, buffer, lengthBytesUTF8(value) + 1); return buffer;
    } catch (e) { return null; }
  },
  HexCreatorStoreSet: function (keyPtr, valuePtr) {
    try {
      var key = UTF8ToString(keyPtr), value = UTF8ToString(valuePtr);
      if (!/^(hexlive\.creator\.|hexlive\.characterAttempt\.)/.test(key) || value.length > 32768) return 0;
      if (value) localStorage.setItem(key, value); else localStorage.removeItem(key);
      return 1;
    } catch (e) { return 0; }
  },
  HexCreatorLockStart: function (keyPtr) {
    var key = UTF8ToString(keyPtr);
    if (!key.startsWith('hexlive.character.')) return 2;
    var locks = window.__hexCreatorLocks || (window.__hexCreatorLocks = new Map());
    if (locks.has(key)) return 2;
    var entry = {state: 0, cancelled: false}; locks.set(key, entry);
    if (!navigator.locks) { entry.state = 1; return 1; }
    navigator.locks.request(key, {ifAvailable: true}, function (lock) {
      if (entry.cancelled) return;
      if (!lock) { entry.state = 2; return; }
      entry.state = 1;
      return new Promise(function (resolve) { entry.release = resolve; });
    }).catch(function () { entry.state = 2; });
    return 0;
  },
  HexCreatorLockStatus: function (keyPtr) {
    var entry = window.__hexCreatorLocks && window.__hexCreatorLocks.get(UTF8ToString(keyPtr));
    return entry ? entry.state : 2;
  },
  HexCreatorLockRelease: function (keyPtr) {
    var key = UTF8ToString(keyPtr), locks = window.__hexCreatorLocks;
    if (!locks) return;
    var entry = locks.get(key);
    if (entry) { entry.cancelled = true; if (entry.release) entry.release(); }
    locks.delete(key);
  },
  // §171.12: the account session the /login page stored. Returns null when absent,
  // malformed or expired; Unity frees the returned buffer.
  HexAccountSession: function () {
    try {
      var raw = window.localStorage.getItem('hexlive.session');
      if (!raw) return null;
      var s = JSON.parse(raw);
      if (!s || typeof s.sessionToken !== 'string' || !s.sessionToken) return null;
      if (s.expiresAt && Date.parse(s.expiresAt) <= Date.now()) return null;
      var size = lengthBytesUTF8(s.sessionToken) + 1;
      var buffer = _malloc(size);
      stringToUTF8(s.sessionToken, buffer, size);
      return buffer;
    } catch (e) {
      return null;
    }
  },

  HexPageReload: function () {
    window.location.reload();
  },

  // §168.6: the output rate the browser will really mix at. FMOD's driver info
  // before init reports a default (44100) even on a 48 kHz device, so the
  // tuning callback asks a throwaway AudioContext instead (allowed without a
  // gesture: it is created suspended and closed at once).
  HexAudioSampleRate: function () {
    try {
      var Ctx = window.AudioContext || window.webkitAudioContext;
      if (!Ctx) return 0;
      var ctx = new Ctx();
      var rate = ctx.sampleRate | 0;
      if (ctx.close) ctx.close();
      return rate;
    } catch (e) {
      return 0;
    }
  }
});

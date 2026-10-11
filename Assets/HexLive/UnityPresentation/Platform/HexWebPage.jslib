// §168.5: page-level helpers for WebPage.cs.
mergeInto(LibraryManager.library, {
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

// §174.2: the page's HTML interface (WebGLTemplates/HexLive/ui) for HtmlUi.cs.
//
// Same rule as HexWireSocket.jslib: nothing here calls back into C#. Unity
// posts JSON to the page (HexUiPost) and once per frame polls what the page
// queued — player commands (HexUiTake) and whether the pointer is over the
// HTML surface (HexUiPointer). A browser event therefore never re-enters
// presentation code mid-frame, and no GameObject has to answer SendMessage.
mergeInto(LibraryManager.library, {
  HexUiPost: function (jsonPtr) {
    var json = UTF8ToString(jsonPtr);
    var ui = window.HexUi;
    if (ui && ui.receive) {
      ui.receive(json);
    } else {
      // The module script of the page may still be loading — it drains this.
      (window.__hexUiInbox = window.__hexUiInbox || []).push(json);
    }
  },

  HexUiTake: function () {
    var ui = window.HexUi;
    var json = ui && ui.take ? ui.take() : null;
    if (!json) {
      return null;
    }
    var size = lengthBytesUTF8(json) + 1;
    var buffer = _malloc(size);
    stringToUTF8(json, buffer, size);
    return buffer;
  },

  // Bit 0 — pointer over an interactive HTML surface; bit 1 — a press that
  // started on HTML is still held (the world must not get its release);
  // bit 2 — a text field has focus (keyboard belongs to the page).
  HexUiPointer: function () {
    var ui = window.HexUi;
    return ui && ui.pointerBits ? ui.pointerBits() : 0;
  },
});

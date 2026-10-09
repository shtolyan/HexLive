// §168.2: the browser's WebSocket for BrowserWireSocket (WireSocket.cs).
//
// The C# side polls; nothing here calls back into C#. Whole binary messages
// are queued as they arrive and handed over one at a time by size-then-take,
// so a browser event never re-enters the remote backend mid-frame.
// Auth travels as subprotocols (the browser cannot set upgrade headers);
// permessage-deflate, if the server agrees, is negotiated by the browser
// itself and is invisible here.
mergeInto(LibraryManager.library, {
  $HexWire: { sockets: {}, next: 1 },

  HexWireOpen__deps: ['$HexWire'],
  HexWireOpen: function (urlPtr, protocolsPtr) {
    var id = HexWire.next++;
    var entry = { ws: null, queue: [], closeCode: 0 };
    HexWire.sockets[id] = entry;
    try {
      entry.ws = new WebSocket(UTF8ToString(urlPtr), JSON.parse(UTF8ToString(protocolsPtr)));
    } catch (e) {
      console.warn('[HexWire] open failed: ' + e);
      entry.closeCode = 1006;
      return id;
    }
    entry.ws.binaryType = 'arraybuffer';
    entry.ws.onmessage = function (ev) {
      // Text frames are not part of the HexLive wire; only binary is queued.
      if (ev.data instanceof ArrayBuffer) {
        entry.queue.push(new Uint8Array(ev.data));
      }
    };
    entry.ws.onclose = function (ev) {
      entry.closeCode = ev.code || 1006;
    };
    return id;
  },

  HexWireState__deps: ['$HexWire'],
  HexWireState: function (id) {
    var entry = HexWire.sockets[id];
    if (!entry || !entry.ws) {
      return 3;
    }
    return entry.ws.readyState;
  },

  HexWireNextSize__deps: ['$HexWire'],
  HexWireNextSize: function (id) {
    var entry = HexWire.sockets[id];
    if (!entry || entry.queue.length === 0) {
      return -1;
    }
    return entry.queue[0].length;
  },

  HexWireTake__deps: ['$HexWire'],
  HexWireTake: function (id, bufferPtr, length) {
    var entry = HexWire.sockets[id];
    if (!entry || entry.queue.length === 0) {
      return 0;
    }
    var message = entry.queue.shift();
    var count = Math.min(length, message.length);
    // HEAPU8 is re-read on every call: a heap growth replaces the view.
    HEAPU8.set(message.subarray(0, count), bufferPtr);
    return count;
  },

  HexWireSend__deps: ['$HexWire'],
  HexWireSend: function (id, bufferPtr, length) {
    var entry = HexWire.sockets[id];
    if (!entry || !entry.ws || entry.ws.readyState !== 1) {
      return 0;
    }
    // slice, not subarray: the heap may move before the browser sends it.
    entry.ws.send(HEAPU8.slice(bufferPtr, bufferPtr + length));
    return 1;
  },

  HexWireCloseCode__deps: ['$HexWire'],
  HexWireCloseCode: function (id) {
    var entry = HexWire.sockets[id];
    return entry ? entry.closeCode : 1006;
  },

  HexWireClose__deps: ['$HexWire'],
  HexWireClose: function (id) {
    var entry = HexWire.sockets[id];
    if (!entry) {
      return;
    }
    try {
      if (entry.ws) {
        entry.ws.onmessage = null;
        entry.ws.onclose = null;
        entry.ws.close();
      }
    } catch (e) {
      // Already closing; nothing to do.
    }
    delete HexWire.sockets[id];
  }
});

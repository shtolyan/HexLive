"""Talk to the MCPForUnity bridge inside a running Unity editor, directly.

The editor package (`com.coplaydev.unity-mcp`) listens on TCP 6400 whether or
not a Claude session happens to have the matching MCP server registered — and
registering one only takes effect at session start. So this is the same escape
hatch the wardrobe uses for DAZ: speak the wire protocol and skip the wrapper.

Protocol: connect, read the `WELCOME UNITY-MCP 1 FRAMING=1` banner line, then
exchange 8-byte big-endian length-prefixed JSON.

    python Tools/unity_bridge.py ping
    python Tools/unity_bridge.py menu "HexLive/Wear/Extract New Wear (Force Re-Extract)"

Unity runs this on its main thread, so a long import blocks the reply rather
than failing — give it a generous timeout.
"""
from __future__ import annotations

import json
import socket
import struct
import sys

HOST, PORT = "127.0.0.1", 6400


def call(command: str, params: dict | None = None, timeout: float = 900.0) -> dict:
    sock = socket.create_connection((HOST, PORT), timeout=timeout)
    sock.settimeout(timeout)
    try:
        stream = sock.makefile("rb")
        stream.readline()                                    # WELCOME banner
        payload = json.dumps({"type": command, "params": params or {}}).encode()
        sock.sendall(struct.pack(">Q", len(payload)) + payload)
        head = stream.read(8)
        if not head or len(head) < 8:
            raise RuntimeError("мост закрыл соединение, не ответив")
        body = stream.read(struct.unpack(">Q", head)[0])
        return json.loads(body.decode("utf-8"))
    finally:
        sock.close()


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    verb = sys.argv[1]
    if verb == "ping":
        result = call("ping")
    elif verb == "menu":
        result = call("execute_menu_item",
                      {"action": "execute", "menu_path": sys.argv[2]})
    else:
        result = call(verb, json.loads(sys.argv[2]) if len(sys.argv) > 2 else {})
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0 if result.get("status") == "success" else 1


if __name__ == "__main__":
    sys.exit(main())

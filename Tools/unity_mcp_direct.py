#!/usr/bin/env python3
"""Small local HTTP MCP client for the already-running Unity MCP server."""
import json
import sys
import urllib.request

URL = "http://127.0.0.1:8080/mcp"

def rpc(method, params=None, session=None, ident=1):
    body = json.dumps({"jsonrpc":"2.0", "id":ident, "method":method,
                       "params":params or {}}).encode()
    headers = {"Content-Type":"application/json", "Accept":"application/json, text/event-stream"}
    if session: headers["mcp-session-id"] = session
    req = urllib.request.Request(URL, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=60) as response:
        sid = response.headers.get("mcp-session-id") or session
        raw = response.read().decode()
    payload = None
    for line in raw.splitlines():
        if line.startswith("data: "):
            payload = json.loads(line[6:])
    return sid, payload

if __name__ == "__main__":
    sid, init = rpc("initialize", {"protocolVersion":"2025-03-26", "capabilities":{},
        "clientInfo":{"name":"codex-direct", "version":"1.0"}})
    method = sys.argv[1]
    params = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    sid, result = rpc(method, params, sid, 2)
    print(json.dumps(result, ensure_ascii=False))

#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Local static server for a HexLive WebGL build (§168.10).

    python3 Tools/webgl_serve.py <build-dir> [--port 8090]

Unity's Brotli build is served the way production Caddy serves it: the
`.br` files go out as-is with `Content-Encoding: br` and the real MIME type
(`application/wasm` for the wasm, so the browser can stream-compile it).
Python's http.server knows neither, and a build opened without them fails
with "Unable to parse Build/*.framework.js.br".

Localhost is a secure context, so the page behaves as it will under HTTPS.
"""

from __future__ import annotations

import argparse
import functools
import http.server
import pathlib

TYPES = {
    ".wasm": "application/wasm",
    ".js": "application/javascript",
    ".data": "application/octet-stream",
    ".json": "application/json",
    ".symbols.json": "application/json",
}


class Handler(http.server.SimpleHTTPRequestHandler):
    def guess_type(self, path):  # noqa: D401 - http.server API
        name = str(path)
        for compressed in (".br", ".gz"):
            if name.endswith(compressed):
                name = name[: -len(compressed)]
                break
        for suffix, mime in TYPES.items():
            if name.endswith(suffix):
                return mime
        return super().guess_type(name)

    def end_headers(self):
        path = self.path.split("?", 1)[0]
        if path.endswith(".br"):
            self.send_header("Content-Encoding", "br")
        elif path.endswith(".gz"):
            self.send_header("Content-Encoding", "gzip")
        self.send_header("Cache-Control", "no-cache")
        super().end_headers()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("root", type=pathlib.Path)
    parser.add_argument("--port", type=int, default=8090)
    args = parser.parse_args()
    root = args.root.expanduser().resolve()
    if not (root / "index.html").is_file():
        raise SystemExit(f"{root} has no index.html — not a WebGL build")
    handler = functools.partial(Handler, directory=str(root))
    with http.server.ThreadingHTTPServer(("127.0.0.1", args.port), handler) as server:
        print(f"[webgl] serving {root} at http://localhost:{args.port}/")
        server.serve_forever()


if __name__ == "__main__":
    main()

"""Minimal client for the Daz Script Server plugin (127.0.0.1:18811).

The plugin exposes `POST /execute` taking {"script", "args"} and authenticates
with an `X-API-Token` header whose value the plugin writes to
`~/.daz3d/dazscriptserver_token.txt` on startup. That is the whole contract we
need, so this is stdlib-only rather than a dependency on `dazpy`.

Gotchas baked in here the hard way:
  * the plugin is only loaded when DAZ Studio STARTS, and the pane's
    "Start Server" has to have been clicked — a silent connection refusal
    almost always means one of those two, not a bug in the script;
  * every script must be an IIFE returning a value, or the result is empty;
  * Action classes (DzNewCameraAction & co.) pop modal dialogs and hang the
    server. Use direct constructors instead;
  * the server accepts an "args" field but NEVER binds it — scripts using it
    die with `ReferenceError: Can't find variable: args` (measured, both via
    this client and via the MCP wrapper). So `params` is inlined into the
    script source as a `var args = {...}` prelude instead;
  * the server refuses work with HTTP 503 STUDIO_BUSY whenever DAZ's main
    thread is occupied, and a girl's scene takes MINUTES to open the first time
    (every garment in it is read off disk) against ~4 s once DAZ has it cached.
    So the first load of each scene reliably trips the guard even though nothing
    is wrong — hence the retry below. Without it a four-girl run dies on girl
    one, which reads as a broken pipeline.
"""
from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request

from . import config

# How long to keep re-offering work that DAZ refused as STUDIO_BUSY. Generous:
# the losing case is a cold scene load, which is minutes of real disk reading.
BUSY_RETRY_SECONDS = float(os.environ.get("DAZ_BUSY_RETRY_SECONDS", "900"))
BUSY_POLL_SECONDS = 10.0


class DazError(RuntimeError):
    """A script reached DAZ but failed there (or DAZ could not be reached)."""


def _token() -> str:
    try:
        return config.DAZ_TOKEN_FILE.read_text(encoding="utf-8").strip()
    except OSError:
        return ""


def execute(script: str, params: object = None, timeout: float = 600.0,
            retry_busy: bool = True) -> object:
    """Run DazScript and return its result value.

    `params` is exposed to the script as the global `args` — inlined as source,
    because the server's own args field is inert (see the module docstring).

    A STUDIO_BUSY refusal is retried until `BUSY_RETRY_SECONDS` runs out: it
    means DAZ is mid-operation, not that the script is wrong.

    Raises DazError with the DAZ-side message on failure, so callers can just
    let it propagate and the stage report records something actionable.
    """
    if params is not None:
        script = f"var args = {json.dumps(params, ensure_ascii=False)};\n{script}"
    payload = json.dumps({"script": script}).encode("utf-8")

    deadline = time.monotonic() + (BUSY_RETRY_SECONDS if retry_busy else 0.0)
    while True:
        request = urllib.request.Request(
            f"http://{config.DAZ_HOST}:{config.DAZ_PORT}/execute",
            data=payload,
            headers={"Content-Type": "application/json", "X-API-Token": _token()},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                body = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:400]
            if "STUDIO_BUSY" in detail and time.monotonic() < deadline:
                time.sleep(BUSY_POLL_SECONDS)
                continue
            raise DazError(f"DAZ вернул HTTP {e.code}: {detail}")
        except urllib.error.URLError as e:
            raise DazError(
                f"DAZ Studio недоступна на {config.DAZ_HOST}:{config.DAZ_PORT} ({e.reason}). "
                "Проверьте: Window → Panes → Daz Script Server → Start Server, "
                "и что студию перезапускали после установки плагина.")

        if not body.get("success", False):
            raise DazError(str(body.get("error") or body))
        return body.get("result")


def alive() -> bool:
    """Is DAZ reachable AND free right now — a liveness probe, so it never waits
    out a STUDIO_BUSY the way real work does."""
    try:
        return execute("(function(){ return 'ok'; })()", timeout=10.0,
                       retry_busy=False) == "ok"
    except DazError:
        return False


# --- DazScript fragments the stages share -----------------------------------

# DAZ names a fitted garment node "<name>_<vertexCount>" and the count does not
# depend on the body, so one key matches the same garment on every girl.
LIST_FITTED = """
(function(){
  var fig = Scene.findNodeByLabel(args.figure);
  if (!fig) return null;
  var out = [];
  for (var i = 0; i < Scene.getNumNodes(); i++) {
    var n = Scene.getNode(i);
    if (n == fig) continue;
    if (n.inherits("DzFigure") && n.getFollowTarget && n.getFollowTarget()) {
      out.push({ label: n.getLabel(), name: n.getName() });
    }
  }
  return JSON.stringify(out);
})()
"""

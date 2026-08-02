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
import sys
import time
import urllib.error
import urllib.request

from . import config, watchdog

# How long to keep re-offering work that DAZ refused as STUDIO_BUSY. Generous:
# the losing case is a cold scene load, which is minutes of real disk reading.
BUSY_RETRY_SECONDS = float(os.environ.get("DAZ_BUSY_RETRY_SECONDS", "900"))
BUSY_POLL_SECONDS = 10.0

# How long to wait for a script DAZ has ACCEPTED to finish. Not the same budget
# as the busy retry above, and it has to be far larger: measured, the first open
# of `Jana naked.duf` in a session took 29m35s (her scene references a character
# product that is not installed, so DAZ rescans the whole library per missing
# asset; the second open of the same scene is ~3 s). At the old 10 minutes the
# client hung up while DAZ was still working perfectly — the run died with a
# bare "timed out" and DAZ finished the job twenty minutes later, unread.
SCRIPT_TIMEOUT_SECONDS = float(os.environ.get("DAZ_SCRIPT_TIMEOUT_SECONDS", "2700"))


class DazError(RuntimeError):
    """A script reached DAZ but failed there (or DAZ could not be reached)."""


def _token() -> str:
    try:
        return config.DAZ_TOKEN_FILE.read_text(encoding="utf-8").strip()
    except OSError:
        return ""


def execute(script: str, params: object = None,
            timeout: float | None = None, retry_busy: bool = True) -> object:
    """Run DazScript and return its result value.

    `params` is exposed to the script as the global `args` — inlined as source,
    because the server's own args field is inert (see the module docstring).

    A STUDIO_BUSY refusal is retried until `BUSY_RETRY_SECONDS` runs out: it
    means DAZ is mid-operation, not that the script is wrong.

    Raises DazError with the DAZ-side message on failure, so callers can just
    let it propagate and the stage report records something actionable.
    """
    # A modal box owns DAZ's main thread, which is the thread this script is
    # about to run on, so the guard has to be up BEFORE the call — and on every
    # call, because any of them can be the one that opens content. Wiring it
    # into a single stage was the earlier mistake: a run stalled on a "Missing
    # Files" box that appeared in a stage nobody had guarded.
    watchdog.ensure_running()

    if timeout is None:
        timeout = SCRIPT_TIMEOUT_SECONDS
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
            if "STUDIO_BUSY" in detail:
                raise DazError(_busy_explanation())
            raise DazError(f"DAZ вернул HTTP {e.code}: {detail}")
        except urllib.error.URLError as e:
            raise DazError(
                f"DAZ Studio недоступна на {config.DAZ_HOST}:{config.DAZ_PORT} ({e.reason}). "
                "Проверьте: Window → Panes → Daz Script Server → Start Server, "
                "и что студию перезапускали после установки плагина.")
        except OSError as e:
            # A read that times out (or a connection DAZ drops while its main
            # thread is blocked) arrives RAW — urllib only wraps failures from
            # the connect phase in URLError. Uncaught, it flew straight past
            # `except daz.DazError` in every stage: one slow girl killed the
            # whole run with a bare "timed out" instead of being one line in
            # the report. Everything that reaches DAZ leaves here as DazError.
            dialog = pending_dialog()
            if dialog:
                raise DazError(
                    f"связь с DAZ Studio оборвалась, и на экране висит окно "
                    f"«{dialog}» — оно держит главный поток, через который идут "
                    "скрипты. Ответьте в окне и запустите стадию заново.")
            raise DazError(
                f"связь с DAZ Studio оборвалась ({type(e).__name__}: {e}) — "
                f"скрипт не уложился в {int(timeout / 60)} мин или студия "
                "закрыла соединение. Задать другой предел: "
                "DAZ_SCRIPT_TIMEOUT_SECONDS.")

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


# --- seeing a dialog we cannot ask about ------------------------------------
#
# Scripts run on DAZ's Qt main thread. A modal dialog owns that thread, so the
# server refuses everything with STUDIO_BUSY for as long as it is up, and from
# the outside a wait for a click is indistinguishable from a hang. DAZ has no
# scripting API to suppress prompts, so they cannot be prevented either.
#
# What still works is looking at the process from OUTSIDE: its windows stay
# enumerable through Win32 while the main thread is stuck. So instead of
# guessing, ask Windows what DAZ is showing and name it.

# Matched case-insensitively: the real title is "Daz Studio 4.24 — <scene>",
# not "DAZ Studio", and comparing case-sensitively mistook the main window for
# a dialog — reporting a prompt on a perfectly idle studio.
_MAIN_WINDOW_HINTS = ("daz studio",)


def _daz_window_titles() -> list[str]:
    """Titles of DAZ Studio's visible top-level windows, via Win32."""
    if sys.platform != "win32":
        return []
    import ctypes
    from ctypes import wintypes

    user32, kernel32 = ctypes.windll.user32, ctypes.windll.kernel32
    titles: list[str] = []

    def image_name(pid: int) -> str:
        # PROCESS_QUERY_LIMITED_INFORMATION is enough for the exe name and is
        # granted even while the process is not responding.
        handle = kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return ""
        try:
            size = wintypes.DWORD(512)
            buffer = ctypes.create_unicode_buffer(size.value)
            if kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
                return buffer.value
            return ""
        finally:
            kernel32.CloseHandle(handle)

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def visit(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if "dazstudio" not in image_name(pid.value).lower():
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length:
            buffer = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buffer, length + 1)
            if buffer.value.strip():
                titles.append(buffer.value.strip())
        return True

    try:
        user32.EnumWindows(visit, 0)
    except OSError:
        return []
    return titles


def pending_dialog() -> str | None:
    """Title of the dialog DAZ is waiting on, if any.

    Anything that is not the main window counts: DAZ opens real modal boxes for
    a missing texture, an AutoFit question, an overwrite confirmation. Naming it
    turns "DAZ зависла" into something the operator can go and click.
    """
    others = [t for t in _daz_window_titles()
              if not any(hint in t.lower() for hint in _MAIN_WINDOW_HINTS)]
    return others[0] if others else None


def _busy_explanation() -> str:
    waited = int(BUSY_RETRY_SECONDS / 60)
    dialog = pending_dialog()
    if dialog:
        return (f"DAZ Studio {waited} мин занята — на экране окно «{dialog}». "
                "Скрипты идут через её главный поток, и модальное окно держит "
                "его целиком. Ответьте в окне, и прогон продолжится.")
    return (f"DAZ Studio не освободилась за {waited} мин. Открытого окна не "
            "видно — возможно, идёт долгая операция (симуляция, рендер) или "
            "студия действительно зависла.")


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

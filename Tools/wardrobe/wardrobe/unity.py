"""Stage: drive the RUNNING Unity editor over the unity-mcp bridge.

Batch mode would need the project lock, i.e. closing the editor for the length
of a run. The `com.coplaydev.unity-mcp` package already listens on TCP 6400
from inside the live editor, so we talk to that instead and the user keeps
working.

Wire format, read out of `StdioBridgeHost.cs`:
  * on connect the server sends the ASCII line `WELCOME UNITY-MCP 1 FRAMING=1`;
  * every message after that is an 8-byte big-endian length followed by UTF-8
    JSON;
  * the literal text `ping` is answered specially and is the cheapest liveness
    check.

The bridge drops on domain reload — which is precisely what recompiling after
a new drop causes — so every call retries across a short reconnect window
instead of failing the run.
"""
from __future__ import annotations

import json
import socket
import struct
import time
from pathlib import Path

from . import config

HOST = "127.0.0.1"


def _port() -> int:
    """Which port THIS project's editor is listening on.

    Hard-coding 6400 was wrong and cost hours: the bridge publishes its real
    port in `~/.unity-mcp/unity-mcp-port.json`, and when a second Unity is
    already holding 6400 our editor quietly takes 6401. Every command then went
    to the other instance or nowhere, and the failure looked exactly like "the
    editor is busy" — which is why it was chased as a timeout for so long.

    The file also names the project it belongs to, so a stale entry from another
    checkout is ignored rather than trusted.
    """
    marker = Path.home() / ".unity-mcp" / "unity-mcp-port.json"
    try:
        data = json.loads(marker.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return 6400
    published = str(data.get("project_path", "")).replace("\\", "/").rstrip("/").lower()
    ours = str(config.ASSETS).replace("\\", "/").rstrip("/").lower()
    if published and published != ours:
        # The marker belongs to a different checkout — better the old default
        # than confidently talking to somebody else's editor.
        return 6400
    return int(data.get("unity_port", 6400))


PORT = _port()
_HANDSHAKE_PREFIX = b"WELCOME UNITY-MCP"


class UnityError(RuntimeError):
    """Unity refused the command, or the bridge could not be reached."""


def _read_exactly(sock: socket.socket, count: int) -> bytes:
    chunks = []
    remaining = count
    while remaining:
        chunk = sock.recv(remaining)
        if not chunk:
            raise UnityError("мост закрыл соединение на середине ответа")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def _read_handshake(sock: socket.socket) -> str:
    line = b""
    while not line.endswith(b"\n"):
        byte = sock.recv(1)
        if not byte:
            raise UnityError("мост не поздоровался — соединение закрыто")
        line += byte
        if len(line) > 200:
            raise UnityError(f"неожиданное приветствие: {line[:80]!r}")
    if not line.startswith(_HANDSHAKE_PREFIX):
        raise UnityError(f"это не unity-mcp: {line!r}")
    return line.decode("ascii").strip()


def _round_trip(payload: bytes, timeout: float) -> bytes:
    with socket.create_connection((HOST, PORT), timeout=timeout) as sock:
        sock.settimeout(timeout)
        _read_handshake(sock)
        sock.sendall(struct.pack(">Q", len(payload)) + payload)
        length = struct.unpack(">Q", _read_exactly(sock, 8))[0]
        if length > 64 * 1024 * 1024:
            raise UnityError(f"подозрительно длинный ответ: {length} байт")
        return _read_exactly(sock, length)


def send(command: str, params: dict | None = None,
         timeout: float = 300.0, retries: int = 3) -> dict:
    """Run one bridge command, retrying across a domain reload."""
    payload = json.dumps({"type": command, "params": params or {}}).encode("utf-8")
    last: Exception | None = None
    for attempt in range(retries):
        try:
            raw = _round_trip(payload, timeout)
            break
        except (OSError, UnityError) as e:
            last = e
            # A domain reload takes the listener down entirely and can run for
            # the better part of a minute in a project this size, so back off
            # generously rather than giving up in a few seconds.
            time.sleep(min(2.0 * (attempt + 1), 10.0))
    else:
        raise UnityError(
            f"мост Unity не ответил за {retries} попытк(и): {last}. "
            "Редактор запущен? Пакет com.coplaydev.unity-mcp слушает 127.0.0.1:6400?")

    try:
        response = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as e:
        raise UnityError(f"мост вернул не-JSON: {raw[:200]!r} ({e})")

    # The bridge answers {"status": "success", "result": {...}} even when the
    # command itself failed — the real verdict is the NESTED success flag.
    if not isinstance(response, dict):
        return {"result": response}
    if response.get("status") == "error":
        raise UnityError(str(response.get("error") or response))
    result = response.get("result")
    if isinstance(result, dict) and result.get("success") is False:
        raise UnityError(str(result.get("error") or result.get("code") or result))
    return response


MENU_COMPILE = "HexLive/Compile/Compile Now %#b"
MENU_EXTRACT = "HexLive/Wear/Extract New Wear (Temp FBX)"
MENU_EXTRACT_FORCE = "HexLive/Wear/Extract New Wear (Force Re-Extract)"
MENU_CATALOG = "HexLive/Garments/Rebuild Catalog From Defaults"
MENU_SIMDATA = "HexLive/Export Sim Data (JSON)"
MENU_TUNING = "HexLive/Validate Tuning Coverage"


def menu(path: str, timeout: float = 300.0) -> dict:
    return send("execute_menu_item", {"action": "execute", "menu_path": path},
                timeout=timeout)


def wait_ready(timeout: float = 240.0) -> bool:
    """Block until the editor answers again.

    A menu item that recompiles (or triggers a domain reload) leaves the bridge
    accepting connections but unable to run anything — it answers "Command
    processing timed out" instead. Polling something cheap is the only honest
    readiness signal.
    """
    deadline = time.monotonic() + timeout
    streak = 0
    while time.monotonic() < deadline:
        try:
            send("read_console", {"action": "get", "count": 1}, timeout=20, retries=1)
            # One good answer is not enough: the reload often starts a moment
            # AFTER the compile menu returns, so a single probe can slip through
            # the gap and the next real call then dies. Demand two in a row.
            streak += 1
            if streak >= 2:
                return True
            time.sleep(2.0)
        except UnityError:
            streak = 0
            time.sleep(3.0)
    return False


def _console_safe(count: int = 80, errors_only: bool = False) -> list[str]:
    """Console reading must never be the thing that fails a stage."""
    try:
        return console(count, errors_only)
    except UnityError:
        return []


def console(count: int = 60, errors_only: bool = False) -> list[str]:
    """Recent editor log lines — how a stage learns what Unity actually did.

    The bridge returns plain strings for some log shapes and dicts for others,
    so everything is flattened to text here.
    """
    params = {"action": "get", "count": count, "include_stacktrace": False}
    if errors_only:
        params["types"] = ["error", "exception"]
    response = send("read_console", params, timeout=60)
    data = (response.get("result") or {}).get("data") or []
    if not isinstance(data, list):
        return []
    return [entry if isinstance(entry, str) else str(entry.get("message", entry))
            for entry in data]


# The extractor reads this to learn which drop a run is about. One line, one
# name. See NewWearExtractor.ActiveDropFile.
ACTIVE_DROP = config.DROP_MANIFESTS / "_active.txt"


def set_active_drop(drop: str | None) -> None:
    """Scope the next extraction to one drop — or to everything, with `None`.

    Without this the extractor takes every manifest it can find: one new pair of
    knickers re-stamps all 23 garments, re-imports every girl's export and
    rewrites materials someone tuned by hand. A drop is the unit of work, so the
    pipeline names it.
    """
    ACTIVE_DROP.parent.mkdir(parents=True, exist_ok=True)
    if drop is None:
        ACTIVE_DROP.unlink(missing_ok=True)
    else:
        ACTIVE_DROP.write_text(drop.strip(), encoding="utf-8")


def build_wear(force: bool = False, drop: str | None = None) -> dict:
    """Compile, extract prefabs from ONE drop, refresh catalog + SimData.

    Each step's console output is captured, because Unity reports failure by
    logging rather than by returning it — the SimData export in particular
    writes nothing and logs `[ExportSimData] FAILED` when the tuning-coverage
    gate rejects the run.

    `drop` scopes the extraction. Pass it: a run that rebuilds the whole
    wardrobe is how hand-tuned materials get reverted and how the FBX of every
    past drop stays alive in the project.
    """
    set_active_drop(drop)

    steps = [
        ("компиляция", MENU_COMPILE),
        ("извлечение префабов", MENU_EXTRACT_FORCE if force else MENU_EXTRACT),
        ("пересборка каталога", MENU_CATALOG),
        ("экспорт SimData", MENU_SIMDATA),
    ]

    report: dict = {"steps": [], "errors": []}
    for label, path in steps:
        entry: dict = {"step": label, "menu": path}
        try:
            menu(path)
            entry["ok"] = True
        except UnityError as e:
            # The bridge gives every command a hard 30 s budget and reports a
            # timeout when it runs over — but the menu item keeps going on the
            # main thread and usually finishes. `Export Sim Data` is always
            # over budget. So a timeout is INCONCLUSIVE, not a failure: judge
            # it by the console afterwards instead of by the return value.
            if "timed out" in str(e).lower():
                entry["ok"] = None
                entry["note"] = "мост оборвал ожидание на 30 с; шаг продолжился в редакторе"
            else:
                entry["ok"] = False
                entry["error"] = str(e)
                report["errors"].append(f"{label}: {e}")

        if not wait_ready():
            report["errors"].append(f"{label}: редактор не ответил после шага")
            entry["log"] = []
            report["steps"].append(entry)
            break

        marks = ("[NewWear]", "[ExportSimData]", "GarmentCatalog", "FAILED", "Coverage")
        entry["log"] = [line for line in _console_safe(80) if any(m in line for m in marks)][-6:]
        report["steps"].append(entry)

    # Unity reports these failures by logging, not by returning them.
    log = "\n".join(line for step in report["steps"] for line in step["log"])
    for marker, why in (("[ExportSimData] FAILED", "экспорт SimData отклонён"),
                        ("Coverage FAILED", "покрытие тюнинга неполное")):
        if marker in log:
            report["errors"].append(why)

    report["console_errors"] = _console_safe(120, errors_only=True)[-10:]
    report["ok"] = not report["errors"]
    return report


def alive(timeout: float = 5.0) -> bool:
    try:
        with socket.create_connection((HOST, PORT), timeout=timeout) as sock:
            sock.settimeout(timeout)
            _read_handshake(sock)
            sock.sendall(struct.pack(">Q", 4) + b"ping")
            length = struct.unpack(">Q", _read_exactly(sock, 8))[0]
            return b"pong" in _read_exactly(sock, length).lower()
    except (OSError, UnityError, struct.error):
        return False

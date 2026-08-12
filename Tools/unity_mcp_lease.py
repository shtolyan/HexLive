#!/usr/bin/env python3
"""Atomic single-owner lease for HexLive's Unity MCP bridge.

The source of truth is the top-level ``unityMcpLease`` object in BUGS.json.
All agents must acquire it before making any Unity MCP call and release it
after the last call. A small sibling lock file serializes competing agents;
the game store uses the same lock while preserving the lease on its writes.
"""

from __future__ import annotations

import argparse
import contextlib
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
from typing import Any, Iterator

# Блокировка одного байта файла-компаньона — единственное, что здесь нужно от
# ОС, и она есть в обеих: POSIX даёт fcntl.lockf, Windows — msvcrt.locking.
# Импорт разведён, потому что fcntl на Windows нет вовсе, и без этой развилки
# ЛЮБАЯ работа с мостом на Windows падала на `ModuleNotFoundError: fcntl` —
# то есть требование CLAUDE.md «сначала возьми лизу» было невыполнимо.
try:  # POSIX
    import fcntl

    def _lock(handle) -> None:
        fcntl.lockf(handle.fileno(), fcntl.LOCK_EX, 1, 0, os.SEEK_SET)

    def _unlock(handle) -> None:
        fcntl.lockf(handle.fileno(), fcntl.LOCK_UN, 1, 0, os.SEEK_SET)

except ModuleNotFoundError:  # Windows
    import msvcrt

    def _lock(handle) -> None:
        handle.seek(0)
        # LK_LOCK ждёт освобождения (10 попыток по секунде), а не падает
        # сразу, — это и есть сериализация конкурирующих агентов.
        msvcrt.locking(handle.fileno(), msvcrt.LK_LOCK, 1)

    def _unlock(handle) -> None:
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


FREE = "free"
BUSY = "busy"
LEASE_KEY = "unityMcpLease"


def now_utc() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")


def default_lease() -> dict[str, str]:
    return {
        "status": FREE,
        "ownerAgent": "",
        "task": "",
        "acquiredUtc": "",
        "heartbeatUtc": "",
    }


def normalize_lease(raw: Any) -> dict[str, str]:
    lease = default_lease()
    if isinstance(raw, dict):
        for key in lease:
            value = raw.get(key)
            if isinstance(value, str):
                lease[key] = value

    if lease["status"] != BUSY or not lease["ownerAgent"].strip():
        return default_lease()

    lease["status"] = BUSY
    return lease


def read_store(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)
    if not isinstance(data, dict):
        raise ValueError(f"{path} must contain a JSON object")
    if "nextId" not in data or "reports" not in data:
        raise ValueError(f"{path} is not a HexLive BUGS.json store")
    return data


def write_store(path: Path, data: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    existing_mode = stat.S_IMODE(path.stat().st_mode) if path.exists() else None
    descriptor, temp_name = tempfile.mkstemp(
        prefix=path.name + ".tmp-", dir=path.parent
    )
    temp_path = Path(temp_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(data, handle, ensure_ascii=False, indent=4)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        if existing_mode is not None:
            os.chmod(temp_path, existing_mode)
        os.replace(temp_path, path)
    finally:
        temp_path.unlink(missing_ok=True)


@contextlib.contextmanager
def locked_store(path: Path) -> Iterator[dict[str, Any]]:
    lock_path = path.with_name(path.name + ".unity-mcp.lock")
    with lock_path.open("a+b") as lock_handle:
        _lock(lock_handle)
        try:
            yield read_store(path)
        finally:
            _unlock(lock_handle)


def set_lease(data: dict[str, Any], lease: dict[str, str]) -> None:
    if LEASE_KEY in data:
        data[LEASE_KEY] = lease
        return

    # Keep the canonical top-level order: nextId, lease, reports.
    reports = data.pop("reports")
    data[LEASE_KEY] = lease
    data["reports"] = reports


def print_status(lease: dict[str, str]) -> None:
    if lease["status"] == FREE:
        print("FREE")
        return

    print(
        "BUSY "
        f"owner={lease['ownerAgent']} "
        f"task={lease['task'] or '-'} "
        f"acquired={lease['acquiredUtc'] or '-'} "
        f"heartbeat={lease['heartbeatUtc'] or '-'}"
    )


def command_status(path: Path) -> int:
    with locked_store(path) as data:
        print_status(normalize_lease(data.get(LEASE_KEY)))
    return 0


def command_acquire(path: Path, agent: str, task: str) -> int:
    agent = agent.strip()
    task = task.strip()
    if not agent or not task:
        raise ValueError("--agent and --task must be non-empty")

    with locked_store(path) as data:
        current = normalize_lease(data.get(LEASE_KEY))
        if current["status"] == BUSY and current["ownerAgent"] != agent:
            print_status(current)
            print("DENIED: Unity MCP is owned by another agent", file=sys.stderr)
            return 2

        timestamp = now_utc()
        if current["status"] == BUSY:
            current["task"] = task
            current["heartbeatUtc"] = timestamp
        else:
            current = {
                "status": BUSY,
                "ownerAgent": agent,
                "task": task,
                "acquiredUtc": timestamp,
                "heartbeatUtc": timestamp,
            }
        set_lease(data, current)
        write_store(path, data)
        print_status(current)
    return 0


def command_heartbeat(path: Path, agent: str) -> int:
    agent = agent.strip()
    with locked_store(path) as data:
        current = normalize_lease(data.get(LEASE_KEY))
        if current["status"] != BUSY or current["ownerAgent"] != agent:
            print_status(current)
            print("DENIED: only the current owner may heartbeat", file=sys.stderr)
            return 3
        current["heartbeatUtc"] = now_utc()
        set_lease(data, current)
        write_store(path, data)
        print_status(current)
    return 0


def command_release(path: Path, agent: str) -> int:
    agent = agent.strip()
    with locked_store(path) as data:
        current = normalize_lease(data.get(LEASE_KEY))
        if current["status"] == FREE:
            print_status(current)
            return 0
        if current["ownerAgent"] != agent:
            print_status(current)
            print("DENIED: only the current owner may release", file=sys.stderr)
            return 3
        released = default_lease()
        set_lease(data, released)
        write_store(path, data)
        print_status(released)
    return 0


def parser() -> argparse.ArgumentParser:
    repo_root = Path(__file__).resolve().parents[1]
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument(
        "--bugs",
        type=Path,
        default=repo_root / "BUGS.json",
        help="BUGS.json path (default: repository root)",
    )
    sub = result.add_subparsers(dest="command", required=True)
    sub.add_parser("status", help="show the current file-backed lease")

    acquire = sub.add_parser("acquire", help="atomically acquire or refresh ownership")
    acquire.add_argument("--agent", required=True, help="stable agent task name")
    acquire.add_argument("--task", required=True, help="short description of Unity work")

    heartbeat = sub.add_parser("heartbeat", help="refresh the owner's heartbeat")
    heartbeat.add_argument("--agent", required=True, help="stable agent task name")

    release = sub.add_parser("release", help="release ownership")
    release.add_argument("--agent", required=True, help="stable agent task name")
    return result


def main() -> int:
    args = parser().parse_args()
    path = args.bugs.resolve()
    try:
        if args.command == "status":
            return command_status(path)
        if args.command == "acquire":
            return command_acquire(path, args.agent, args.task)
        if args.command == "heartbeat":
            return command_heartbeat(path, args.agent)
        if args.command == "release":
            return command_release(path, args.agent)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    raise AssertionError(args.command)


if __name__ == "__main__":
    raise SystemExit(main())

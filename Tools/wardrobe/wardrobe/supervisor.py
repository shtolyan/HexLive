"""Run a pipeline job under a Claude Code session that watches it.

The bot on its own is a fixed script: it runs stages and relays their reports,
but nothing looks at a result and decides. That is exactly why a real failure —
"надето 0 из 35" — ran to completion instead of stopping: every stage returned
what it was asked for, and no one asked whether the answer made sense.

This puts an agent in that seat. Each job spawns a FRESH `claude -p` session in
the repository with a brief: run the stages, stay out of the way while they
pass, and when one fails, investigate and fix the script rather than reporting
a dead end. Its narration is streamed back out so the bot can relay it.

Auth is the CLI's own — the operator runs `claude` once and does `/login`, and
runs go against that subscription. No API key is involved, and none is stored.

The bundled CLI is the same binary the desktop app uses, so there is nothing
extra to install.
"""
from __future__ import annotations

import json
import os
import subprocess
import uuid
from collections.abc import Callable, Iterator
from pathlib import Path

from . import config

Progress = Callable[[str], None]

# Scoped rather than blanket. An unattended agent with `bypassPermissions` can
# run anything in the repository; this list is what the wardrobe pipeline
# actually needs — the stages, git reads, and editing its own scripts.
DEFAULT_ALLOWED_TOOLS = [
    "Read", "Edit", "Write", "Glob", "Grep",
    "Bash(python*)", "Bash(*wardrobe*)", "Bash(git status*)",
    "Bash(git diff*)", "Bash(git log*)",
]

_BRIEF = """\
Ты ведёшь один прогон конвейера одежды в репозитории HexLive. Работай молча,
пока всё идёт хорошо, и вмешивайся только когда что-то сломалось.

Задача: {task}

Как работать:

1. Выполняй стадии по одной. Каждая печатает JSON-отчёт — читай его, а не
   только код возврата. Стадия может завершиться успешно и при этом вернуть
   бессмыслицу: пустой список там, где ожидались вещи, нулевой счётчик,
   поколение "None" у всех предметов подряд. Это и есть поломка.

2. Пока отчёты осмысленные — НИЧЕГО НЕ ТРОГАЙ. Не улучшай код, не
   рефакторь, не добавляй проверки «на будущее». Просто иди дальше.

3. Если стадия упала или вернула бессмыслицу — вот тут твоя работа.
   Разберись в причине по фактам: посмотри отчёт, загляни в данные, при
   необходимости опроси DAZ Studio через `wardrobe.daz.execute`. Не гадай.
   Когда причина понятна — почини скрипт и перезапусти стадию.

4. Если причина в самих данных, а не в коде (например, у продукта просто нет
   версии под Genesis 3 Female), это не поломка скрипта. Скажи об этом прямо
   и не правь код под несуществующую проблему.

5. Останавливайся и зови человека, если: нужно решение о том, как вещь должна
   называться или в какие слоты попадать; правка выходит за пределы
   `Tools/wardrobe`; ты дважды починил одно и то же место и оно снова упало.

В конце дай короткий отчёт: что сделано, что сломалось и как починено, что
осталось на человеке. Без пересказа каждого шага.

Команды конвейера (запускать из корня репозитория):
  {python} -m wardrobe <стадия> ...
Стадии: check, fetch, install, dress, build, show, register, unity, preview.
"""


def cli_path() -> Path | None:
    """The bundled Claude Code binary, or None when it cannot be found.

    The pinned version goes stale on every desktop-app update, so the newest
    sibling directory wins when it is gone. Both the physical package path and
    the virtualised `%APPDATA%` view are searched — which of the two resolves
    depends on whether this process was spawned by the app or from a plain
    terminal, and only one of them exists in each case.
    """
    if config.CLAUDE_CLI.exists():
        return config.CLAUDE_CLI

    roots = [config.CLAUDE_PACKAGE, Path(config.CLAUDE_CLI).parent.parent,
             Path(os.environ.get("APPDATA", "")) / "Claude" / "claude-code"]
    for root in roots:
        if not root.is_dir():
            continue
        found = sorted(root.glob("*/claude.exe")) + sorted(root.glob("*/claude"))
        if found:
            return found[-1]
    return None


def build_prompt(task: str) -> str:
    python = config.WARDROBE_PYTHON
    return _BRIEF.format(task=task.strip(), python=python)


def _render(event: dict) -> Iterator[str]:
    """Turn one stream-json event into human-readable progress lines.

    Written defensively: the event schema is the CLI's, not ours, so an
    unexpected shape must not take the run down.
    """
    kind = event.get("type")
    if kind == "system":
        if event.get("subtype") == "init":
            yield "🤖 сессия начата"
        return

    if kind == "assistant":
        for block in (event.get("message") or {}).get("content") or []:
            if not isinstance(block, dict):
                continue
            if block.get("type") == "text" and block.get("text", "").strip():
                yield block["text"].strip()
            elif block.get("type") == "tool_use":
                name = block.get("name", "?")
                cmd = (block.get("input") or {}).get("command")
                yield f"▸ {name}" + (f": {str(cmd)[:120]}" if cmd else "")
        return

    if kind == "result":
        if event.get("is_error"):
            yield f"❌ сессия завершилась ошибкой: {str(event.get('result'))[:400]}"
        elif event.get("result"):
            yield str(event["result"]).strip()


def run(task: str, progress: Progress = lambda _: None,
        timeout: float = 3600.0,
        permission_mode: str = "acceptEdits",
        allowed_tools: list[str] | None = None) -> dict:
    """Run one supervised job. Returns a report dict; never raises on failure."""
    binary = cli_path()
    if binary is None:
        return {"ok": False, "errors": [
            f"не найден Claude Code CLI (искал {config.CLAUDE_CLI})"]}

    session = str(uuid.uuid4())
    command = [
        str(binary), "-p", build_prompt(task),
        "--output-format", "stream-json", "--verbose",
        "--session-id", session,
        "--permission-mode", permission_mode,
        "--allowedTools", *(allowed_tools or DEFAULT_ALLOWED_TOOLS),
    ]

    lines: list[str] = []
    report: dict = {"session": session, "lines": lines, "errors": []}
    try:
        process = subprocess.Popen(
            command, cwd=str(config.PROJECT), stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True, encoding="utf-8",
            errors="replace", bufsize=1)
    except OSError as e:
        return {"ok": False, "errors": [f"не удалось запустить CLI: {e}"]}

    try:
        for raw in process.stdout:
            raw = raw.strip()
            if not raw:
                continue
            if not raw.startswith("{"):
                # Plain text on stdout is how the CLI reports setup problems —
                # "Not logged in" being the one that matters most.
                lines.append(raw)
                progress(raw)
                continue
            try:
                event = json.loads(raw)
            except json.JSONDecodeError:
                continue
            for line in _render(event):
                lines.append(line)
                progress(line)
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        process.kill()
        report["errors"].append(f"сессия не уложилась в {timeout / 60:.0f} мин")
    finally:
        if process.poll() is None:
            process.kill()

    report["exit_code"] = process.returncode
    if any("Not logged in" in line for line in lines):
        report["errors"].append(
            "CLI не авторизован. Запустите его один раз вручную и выполните /login: "
            f"{binary}")
    elif process.returncode not in (0, None):
        report["errors"].append(f"CLI вернул код {process.returncode}")

    report["ok"] = not report["errors"]
    return report

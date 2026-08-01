"""Keep DAZ's harmless "Missing Files" boxes from stalling a run.

DazScript executes on DAZ's Qt MAIN thread, so a modal dialog owns that thread
for as long as it is up: the script server answers STUDIO_BUSY, and from outside
a wait for a human click looks exactly like a hang. DAZ offers no way to
suppress the prompt, so the only lever is Win32 from outside the process.

`daz_dialog_watchdog.ps1` does the work — it polls DAZ's windows and closes the
ones it positively recognises as "content is missing" reports, leaving anything
else alone and logging it. This module just runs it alongside a stage and
collects what it did, so the run's report says which dialogs appeared instead of
quietly swallowing them.

⚠️ It removes the STALL, not the CAUSE. The scene still loaded without that
texture, and unnoticed that ships as white shoes — the Flair pumps did exactly
that, their missing map showing up on the character, not just the icon. Read the
report; install the content it names.
"""
from __future__ import annotations

import os
import subprocess
from pathlib import Path

from . import config

SCRIPT = Path(__file__).resolve().parent.parent / "daz_dialog_watchdog.ps1"
LOG = Path(os.environ.get("LOCALAPPDATA", ".")) / "Temp/wardrobe/daz-dialogs.log"


class Watchdog:
    """Runs the dismisser for as long as the `with` block lasts."""

    def __init__(self, watch_only: bool = False):
        self.watch_only = watch_only
        self._process: subprocess.Popen | None = None
        self._mark = 0
        self.lines: list[str] = []

    def __enter__(self) -> "Watchdog":
        if not SCRIPT.exists():
            return self
        # Remember where the log ended, so we report THIS stage's dialogs and
        # not every one since the machine was booted.
        self._mark = LOG.stat().st_size if LOG.exists() else 0
        command = [
            "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(SCRIPT), "-LogPath", str(LOG),
        ]
        if self.watch_only:
            command.append("-WatchOnly")
        try:
            self._process = subprocess.Popen(
                command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError:
            self._process = None    # no PowerShell: the run simply goes unguarded
        return self

    def __exit__(self, *_) -> None:
        if self._process is not None:
            self._process.terminate()
            try:
                self._process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self._process.kill()
        self.lines = self._since_mark()

    def _since_mark(self) -> list[str]:
        if not LOG.exists():
            return []
        try:
            with LOG.open(encoding="utf-8", errors="replace") as handle:
                handle.seek(self._mark)
                return [l.strip() for l in handle if l.strip()]
        except OSError:
            return []

    def report(self) -> dict:
        """What the watchdog saw, for the stage report."""
        closed = [l for l in self.lines if "ЗАКРЫТО" in l]
        left = [l for l in self.lines if "НЕЗНАКОМО" in l or "ОСТАВЛЕНО" in l]
        return {
            "running": self._process is not None,
            "dismissed": len(closed),
            "dialogs": closed,
            # Anything the watchdog would not answer is a question for a human,
            # and must reach the report rather than sit in a log nobody opens.
            "needs_attention": left,
        }

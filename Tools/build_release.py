#!/usr/bin/env python3
"""Build a versioned HexLive macOS player and publish its Git/bug report."""

from __future__ import annotations

import argparse
import csv
from contextlib import contextmanager
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import subprocess
import sys
import urllib.request
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DISTRIBUTION = Path.home() / "hex-girls"
DEFAULT_RELEASES = DEFAULT_DISTRIBUTION / "Releases"
PROJECT_SETTINGS = ROOT / "ProjectSettings" / "ProjectSettings.asset"
PENDING_VERSION = ROOT / "Library" / "HexLivePendingBuildVersion.txt"
BUG_API = os.environ.get("HEXLIVE_BUG_API", "https://163-245-204-96.sslip.io/api/bugs/v1").rstrip("/")
UNITY_LOCK = ROOT / "Temp" / "UnityLockfile"
UNITY_METHOD = "HexLive.UnityDebug.Editor.HexLiveReleaseBuilder.BuildMacOS"
UNITY_PREFS_DOMAIN = "com.unity3d.UnityEditor5.x"
UNITY_DEFAULTS = Path("/usr/bin/defaults")
CODE_SIGN = Path("/usr/bin/codesign")
AUTO_REFRESH_PREFS = ("kAutoRefreshMode", "kAutoRefresh")
AUTO_REFRESH_BACKUP = ROOT / "Library" / "HexLiveBuildAutoRefreshBackup.json"
MICROPHONE_TERM = "agent.voice.microphone_usage"
MICROPHONE_TERMS = ROOT / "_ArtSource" / "Voice" / "native_permissions.tsv"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Build HexLive for macOS, increment its build version, and place the "
            "player, Unity log, Git changes, and server bug report together."
        )
    )
    parser.add_argument(
        "--release",
        action="store_true",
        help="make a non-development player (development is the testing default)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print the planned version, commits, bugs, and paths without writing or building",
    )
    parser.add_argument(
        "--unity",
        type=Path,
        help="path to the Unity executable (normally discovered from ProjectVersion.txt)",
    )
    parser.add_argument(
        "--output-root",
        type=Path,
        default=DEFAULT_RELEASES,
        help=f"release directory (default: {DEFAULT_RELEASES})",
    )
    return parser.parse_args()


def git(*args: str, check: bool = True) -> str:
    result = subprocess.run(
        ["git", *args],
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if check and result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or f"git {' '.join(args)} failed")
    return result.stdout.rstrip("\n")


def git_succeeds(*args: str) -> bool:
    return subprocess.run(
        ["git", *args],
        cwd=ROOT,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    ).returncode == 0


def read_current_version() -> str:
    text = PROJECT_SETTINGS.read_text(encoding="utf-8")
    match = re.search(r"^\s*bundleVersion:\s*(\S+)\s*$", text, re.MULTILINE)
    if not match:
        raise RuntimeError(f"bundleVersion not found in {PROJECT_SETTINGS}")
    return match.group(1).lstrip("vV")


def next_patch(version: str) -> str:
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", version.strip().lstrip("vV"))
    if not match:
        raise RuntimeError(f"Unsupported HexLive version {version!r}; expected MAJOR.MINOR.PATCH")
    major, minor, patch = (int(part) for part in match.groups())
    return f"{major}.{minor}.{patch + 1}"


def planned_version() -> tuple[str, str]:
    if PENDING_VERSION.is_file():
        pending = PENDING_VERSION.read_text(encoding="utf-8").strip()
        if pending:
            return pending, "повтор ранее зарезервированной версии"
    current = read_current_version()
    return next_patch(current), f"следующая после {current}"


def read_json(path: Path) -> dict[str, Any]:
    with path.open(encoding="utf-8") as source:
        value = json.load(source)
    if not isinstance(value, dict):
        raise RuntimeError(f"Expected a JSON object in {path}")
    return value


def read_bug_tracker() -> dict[str, Any]:
    try:
        with urllib.request.urlopen(BUG_API + "/reports", timeout=15) as response:
            reports = json.loads(response.read().decode("utf-8"))
    except Exception as error:
        raise RuntimeError(f"Could not read bug API {BUG_API}: {error}") from error
    if not isinstance(reports, list):
        raise RuntimeError(f"Expected reports array from {BUG_API}")
    return {"reports": reports}


def active_reports(tracker: dict[str, Any], statuses: set[str]) -> list[dict[str, Any]]:
    return [
        report
        for report in tracker.get("reports", [])
        if isinstance(report, dict)
        and not report.get("archived", False)
        and report.get("status") in statuses
    ]


def short_text(value: Any, limit: int = 180) -> str:
    compact = " ".join(str(value or "").split())
    return compact if len(compact) <= limit else compact[: limit - 1].rstrip() + "…"


def load_last_success(releases: Path) -> dict[str, Any] | None:
    state_path = releases / ".last-success.json"
    if state_path.is_file():
        try:
            return read_json(state_path)
        except (OSError, ValueError, RuntimeError) as error:
            print(f"ПРЕДУПРЕЖДЕНИЕ: не читается {state_path}: {error}", file=sys.stderr)

    manifests = sorted(releases.glob("v*/build-manifest.json"), reverse=True)
    for manifest in manifests:
        try:
            return read_json(manifest)
        except (OSError, ValueError, RuntimeError):
            continue
    return None


def collect_git(last_success: dict[str, Any] | None) -> dict[str, Any]:
    head = git("rev-parse", "HEAD")
    branch = git("branch", "--show-current") or "(detached HEAD)"
    status = git("status", "--short", "--untracked-files=all").splitlines()
    base = str((last_success or {}).get("commit") or "").strip()
    commits: list[dict[str, str]] = []
    comparison = "Первый билд под управлением скрипта: предыдущий Git-срез неизвестен."

    if base and git_succeeds("cat-file", "-e", f"{base}^{{commit}}"):
        if git_succeeds("merge-base", "--is-ancestor", base, head):
            comparison = f"Сравнение с билдом {last_success.get('version', '?')} ({base[:8]})."
        else:
            comparison = (
                f"История разошлась с билдом {last_success.get('version', '?')} "
                f"({base[:8]}); показаны только коммиты текущей истории, которых не было там."
            )
        log_range = f"{base}..{head}"
        raw = git(
            "log",
            "--reverse",
            "--format=%H%x1f%h%x1f%ad%x1f%s",
            "--date=short",
            log_range,
        )
    else:
        raw = git("show", "-s", "--format=%H%x1f%h%x1f%ad%x1f%s", "--date=short", head)

    for line in raw.splitlines():
        parts = line.split("\x1f", 3)
        if len(parts) == 4:
            commits.append(
                {"commit": parts[0], "short": parts[1], "date": parts[2], "subject": parts[3]}
            )

    fingerprint_source = "\n".join([head, *status]).encode("utf-8")
    return {
        "commit": head,
        "branch": branch,
        "comparisonBaseCommit": base or None,
        "comparison": comparison,
        "commits": commits,
        "workingTree": status,
        "sourceFingerprint": hashlib.sha256(fingerprint_source).hexdigest(),
    }


def unity_version() -> str:
    path = ROOT / "ProjectSettings" / "ProjectVersion.txt"
    match = re.search(r"^m_EditorVersion:\s*(\S+)", path.read_text(encoding="utf-8"), re.MULTILINE)
    if not match:
        raise RuntimeError(f"m_EditorVersion not found in {path}")
    return match.group(1)


def find_unity(explicit: Path | None) -> Path:
    candidates: list[Path] = []
    if explicit:
        candidates.append(explicit.expanduser())
    configured = os.environ.get("UNITY_EDITOR_PATH")
    if configured:
        candidates.append(Path(configured).expanduser())
    candidates.append(
        Path("/Applications/Unity/Hub/Editor")
        / unity_version()
        / "Unity.app/Contents/MacOS/Unity"
    )
    for candidate in candidates:
        resolved = candidate.resolve()
        if resolved.is_file() and os.access(resolved, os.X_OK):
            return resolved
    raise RuntimeError("Unity executable not found; pass it with --unity /path/to/Unity")


def read_unity_pref(key: str) -> dict[str, Any]:
    result = subprocess.run(
        [str(UNITY_DEFAULTS), "read", UNITY_PREFS_DOMAIN, key],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        return {"exists": False, "value": None}
    try:
        value = int(result.stdout.strip())
    except ValueError as error:
        raise RuntimeError(f"Unity preference {key} is not an integer: {result.stdout!r}") from error
    return {"exists": True, "value": value}


def write_unity_pref(key: str, value: int) -> None:
    result = subprocess.run(
        [str(UNITY_DEFAULTS), "write", UNITY_PREFS_DOMAIN, key, "-int", str(value)],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or f"Could not write Unity preference {key}")


def delete_unity_pref(key: str) -> None:
    subprocess.run(
        [str(UNITY_DEFAULTS), "delete", UNITY_PREFS_DOMAIN, key],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )


def restore_unity_auto_refresh(backup: dict[str, Any]) -> None:
    prefs = backup.get("prefs", {})
    for key in AUTO_REFRESH_PREFS:
        saved = prefs.get(key, {"exists": False, "value": None})
        if saved.get("exists"):
            write_unity_pref(key, int(saved["value"]))
        else:
            delete_unity_pref(key)


def recover_interrupted_auto_refresh_override() -> None:
    if not AUTO_REFRESH_BACKUP.is_file():
        return
    backup = read_json(AUTO_REFRESH_BACKUP)
    restore_unity_auto_refresh(backup)
    AUTO_REFRESH_BACKUP.unlink()
    print("Восстановлены Unity Auto Refresh prefs после прерванного прошлого запуска.")


@contextmanager
def temporary_unity_auto_refresh():
    recover_interrupted_auto_refresh_override()
    backup = {
        "createdAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "prefs": {key: read_unity_pref(key) for key in AUTO_REFRESH_PREFS},
    }
    temporary = AUTO_REFRESH_BACKUP.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(backup, ensure_ascii=False, indent=4) + "\n", encoding="utf-8")
    os.replace(temporary, AUTO_REFRESH_BACKUP)
    try:
        for key in AUTO_REFRESH_PREFS:
            write_unity_pref(key, 1)
        enabled = {key: read_unity_pref(key).get("value") for key in AUTO_REFRESH_PREFS}
        if any(value != 1 for value in enabled.values()):
            raise RuntimeError(f"Could not enable Unity Auto Refresh for batch build: {enabled}")
        print("Auto Refresh временно включён для обязательной компиляции batchmode.")
        yield
    finally:
        restore_unity_auto_refresh(backup)
        AUTO_REFRESH_BACKUP.unlink(missing_ok=True)
        print("Исходные Unity Auto Refresh prefs восстановлены.")


def verify_code_signature(app_path: Path) -> tuple[bool, str]:
    result = subprocess.run(
        [str(CODE_SIGN), "--verify", "--deep", "--strict", "--verbose=2", str(app_path)],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    details = "\n".join(part for part in (result.stdout.strip(), result.stderr.strip()) if part)
    return result.returncode == 0, details


def configure_microphone_permission(app_path: Path, terms_path: Path = MICROPHONE_TERMS) -> None:
    """Export the §58 I2 authoring term to the native macOS privacy prompt.

    FMOD uses Core recording instead of Unity Microphone, so Unity's managed
    API scanner does not add NSMicrophoneUsageDescription automatically.
    This is packaging metadata; configure it before sealing the application.
    """
    with terms_path.open(encoding="utf-8", newline="") as source:
        rows = [row for row in csv.reader(source, delimiter="\t")
                if row and row[0] == MICROPHONE_TERM]
    if len(rows) != 1 or len(rows[0]) != 3 or not all(rows[0][1:]):
        raise RuntimeError("Native microphone permission requires one complete EN/RU I2 term")
    translations = dict(zip(("en", "ru"), rows[0][1:]))
    info_path = app_path / "Contents" / "Info.plist"
    with info_path.open("rb") as source:
        info = plistlib.load(source)
    info["NSMicrophoneUsageDescription"] = translations["en"]
    languages = info.setdefault("CFBundleLocalizations", [])
    if not isinstance(languages, list):
        raise RuntimeError("Invalid CFBundleLocalizations in built app")
    for language in translations:
        if language not in languages:
            languages.append(language)
    localized_files = []
    for language, description in translations.items():
        path = app_path / "Contents" / "Resources" / f"{language}.lproj" / "InfoPlist.strings"
        values = {}
        if path.exists():
            converted = subprocess.run(
                ["/usr/bin/plutil", "-convert", "json", "-o", "-", str(path)],
                check=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            values = json.loads(converted.stdout)
        values["NSMicrophoneUsageDescription"] = description
        localized_files.append((path, values))
    with info_path.open("wb") as output:
        plistlib.dump(info, output, sort_keys=False)
    for path, values in localized_files:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("wb") as output:
            plistlib.dump(values, output, sort_keys=False)
    print("Microphone privacy: NSMicrophoneUsageDescription установлен, EN/RU из I2 term.")


def remove_macos_metadata(app_path: Path) -> int:
    """Remove Finder sidecar files before sealing the local macOS player.

    Unity copies StreamingAssets verbatim, including Finder's `.DS_Store`.
    If that file is present when Unity signs the bundle and then disappears
    or changes while the build is staged, `codesign --verify --strict` reports
    a missing sealed resource. Keeping those files out of the final bundle
    makes the local release deterministic.
    """

    removed = 0
    for path in app_path.rglob("*"):
        if path.name == ".DS_Store" or path.name.startswith("._"):
            path.unlink(missing_ok=True)
            removed += 1
    return removed


def ensure_development_signature(app_path: Path, release: bool) -> str:
    removed_metadata = remove_macos_metadata(app_path)
    if removed_metadata:
        print(f"Удалены macOS metadata files из .app перед подписью: {removed_metadata}")

    valid, details = verify_code_signature(app_path)
    if valid:
        return "unity"

    if release:
        print("Unity оставил некорректную release-подпись; пересоздаю локальную ad-hoc подпись.")
    else:
        print("Unity оставил некорректную вложенную подпись; пересоздаю локальную ad-hoc подпись.")

    result = subprocess.run(
        [str(CODE_SIGN), "--force", "--deep", "--sign", "-", "--timestamp=none", str(app_path)],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or "Could not apply ad-hoc code signature")
    valid, details = verify_code_signature(app_path)
    if not valid:
        raise RuntimeError(f"Ad-hoc code signature is still invalid:\n{details}")
    return "ad-hoc-resigned"


def require_successful_version_finalize(version: str, included_bug_ids: set[int]) -> dict[str, Any]:
    if PENDING_VERSION.exists():
        raise RuntimeError(
            f"Successful Unity build did not clear {PENDING_VERSION}; version was not finalized"
        )
    tracker = read_bug_tracker()
    by_id = {
        report.get("id"): report
        for report in tracker.get("reports", [])
        if isinstance(report, dict)
    }
    missing = [
        report_id
        for report_id in sorted(included_bug_ids)
        if by_id.get(report_id, {}).get("readyForTestInVersion") != version
    ]
    if missing:
        raise RuntimeError(
            f"Successful Unity build did not stamp server bug reports {missing} with version {version}"
        )
    return tracker


def print_plan(
    version: str,
    version_reason: str,
    variant: str,
    final_dir: Path,
    git_info: dict[str, Any],
    tracker: dict[str, Any],
) -> None:
    ready = active_reports(tracker, {"ready_for_test"})
    open_reports = active_reports(tracker, {"created", "rework", "in_progress"})
    commits = git_info["commits"]

    print(f"HexLive {version} ({variant}; {version_reason})")
    print(f"Git: {git_info['branch']} @ {git_info['commit'][:8]}")
    print(git_info["comparison"])
    print("Коммиты, которые попадут в билд:")
    if commits:
        for commit in commits:
            print(f"  {commit['short']}  {commit['date']}  {commit['subject']}")
    else:
        print("  новых коммитов нет")

    dirty = git_info["workingTree"]
    print(f"Незакоммиченные пути, которые попадут в билд: {len(dirty)}")
    for line in dirty[:20]:
        print(f"  {line}")
    if len(dirty) > 20:
        print(f"  … и ещё {len(dirty) - 20}; полный список будет в BUILD_REPORT.md")

    print(f"Баги, готовые к тестированию в этом билде: {len(ready)}")
    for report in ready:
        print(f"  #{report.get('id')}  {short_text(report.get('text'), 120)}")
    print(f"Открытые/в работе и ещё не готовые: {len(open_reports)}")
    print(f"Папка билда: {final_dir}")


def bug_record(report: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": report.get("id"),
        "status": report.get("status"),
        "text": report.get("text", ""),
        "context": report.get("context", ""),
        "readyForTestInVersion": report.get("readyForTestInVersion", ""),
    }


def markdown_bug_list(reports: list[dict[str, Any]], include_status: bool = False) -> list[str]:
    if not reports:
        return ["Нет."]
    lines: list[str] = []
    for report in reports:
        status = f" · `{report.get('status', '?')}`" if include_status else ""
        context = f" — {report.get('context')}" if report.get("context") else ""
        lines.append(
            f"- **#{report.get('id')}**{status}: {short_text(report.get('text'))}{context}"
        )
    return lines


def write_reports(
    staging: Path,
    final_dir: Path,
    version: str,
    variant: str,
    git_info: dict[str, Any],
    bugs_at_start: dict[str, Any],
    bugs_at_end: dict[str, Any],
    unity_summary: dict[str, Any],
    signature: str,
) -> dict[str, Any]:
    included = active_reports(bugs_at_start, {"ready_for_test"})
    included_ids = {report.get("id") for report in included}
    late_ready = [
        report
        for report in active_reports(bugs_at_end, {"ready_for_test"})
        if report.get("id") not in included_ids
    ]
    not_ready = active_reports(bugs_at_end, {"created", "rework", "in_progress"})
    built_at = unity_summary.get("builtAtUtc") or dt.datetime.now(dt.timezone.utc).isoformat()
    artifact = final_dir / "HexLive.app"
    report_path = final_dir / "BUILD_REPORT.md"
    manifest: dict[str, Any] = {
        "version": version,
        "builtAtUtc": built_at,
        "variant": variant,
        "platform": "StandaloneOSX",
        "artifact": str(artifact),
        **git_info,
        "unity": unity_summary,
        "codeSignature": signature,
        "bugs": {
            "readyForTestInThisBuild": [bug_record(report) for report in included],
            "readyAfterBuildStartedNotIncluded": [bug_record(report) for report in late_ready],
            "notReady": [bug_record(report) for report in not_ready],
        },
        "assetService": {
            "source": "api",
            "endpoint": "-hexlive-assets or derived from ws/wss server address",
            "rebuiltByPlayerBuild": False,
            "catalogInPlayer": False,
            "bundlesBesidePlayer": False,
        },
    }
    (staging / "build-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=4) + "\n",
        encoding="utf-8",
    )

    commits = git_info["commits"]
    commit_lines = (
        [f"- `{item['short']}` · {item['date']} · {item['subject']}" for item in commits]
        if commits
        else ["Новых коммитов нет."]
    )
    dirty = git_info["workingTree"]
    dirty_lines = [f"- `{line[:2]}` `{line[3:]}`" for line in dirty] if dirty else ["Нет."]
    content_note = (
        "Player не собирает и не содержит игровые bundles или каталог. "
        "Актуальные атомарные объекты клиент получает через Asset API; endpoint "
        "задаётся `-hexlive-assets` либо выводится из адреса ws/wss-сервера."
    )

    markdown = [
        f"# HexLive {version}",
        "",
        f"- Собрано: `{built_at}`",
        f"- Вариант: **{variant}**",
        f"- Платформа: `StandaloneOSX`",
        f"- Артефакт: [HexLive.app]({artifact.as_uri()})",
        f"- Git: `{git_info['branch']}` @ `{git_info['commit']}`",
        f"- Unity: `{unity_summary.get('unityVersion', '?')}`",
        "",
        "## Коммиты, вошедшие в билд",
        "",
        git_info["comparison"],
        "",
        *commit_lines,
        "",
        "## Незакоммиченные изменения, вошедшие в билд",
        "",
        *dirty_lines,
        "",
        "## Баг-трекер",
        "",
        "### Готовы к тестированию в этой сборке",
        "",
        *markdown_bug_list(included),
        "",
        "Этот список — срез серверного баг-трекера на старте сборки; успешный post-build "
        f"ставит этим отчётам `readyForTestInVersion: {version}`.",
        "",
        "### Стали готовы после старта и не вошли",
        "",
        *markdown_bug_list(late_ready),
        "",
        "### Открыты или в работе — ещё не готовы",
        "",
        *markdown_bug_list(not_ready, include_status=True),
        "",
        "## Результат сборки",
        "",
        f"- Результат Unity: `{unity_summary.get('result', '?')}`",
        f"- Длительность: `{float(unity_summary.get('durationSeconds', 0.0)):.1f} с`",
        f"- Размер Player: `{int(unity_summary.get('playerBytes', 0))} байт`",
        f"- data.unity3d + StreamingAssets: `"
        f"{int(unity_summary.get('dataUnity3dBytes', 0)) + int(unity_summary.get('streamingAssetsBytes', 0))} байт`",
        f"- Предупреждения / ошибки: `{unity_summary.get('warnings', '?')} / "
        f"{unity_summary.get('errors', '?')}`",
        f"- Подпись приложения: `{signature}`",
        f"- Лог: [unity-build.log]({(final_dir / 'unity-build.log').as_uri()})",
        "",
        "## Атомарный контент",
        "",
        content_note,
        "",
    ]
    (staging / "BUILD_REPORT.md").write_text("\n".join(markdown), encoding="utf-8")
    return manifest


def write_last_success(releases: Path, manifest: dict[str, Any]) -> None:
    state = {
        "version": manifest["version"],
        "builtAtUtc": manifest["builtAtUtc"],
        "commit": manifest["commit"],
        "sourceFingerprint": manifest["sourceFingerprint"],
        "manifest": str(releases / f"v{manifest['version']}" / "build-manifest.json"),
    }
    path = releases / ".last-success.json"
    temporary = releases / ".last-success.json.tmp"
    temporary.write_text(json.dumps(state, ensure_ascii=False, indent=4) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def update_latest_player_link(releases: Path, artifact: Path) -> Path:
    latest = releases.parent / "HexLive.app"
    if latest.exists() and not latest.is_symlink():
        raise RuntimeError(f"Refusing to overwrite non-symlink latest Player: {latest}")

    temporary = releases.parent / f".HexLive.app.tmp-{os.getpid()}"
    temporary.unlink(missing_ok=True)
    relative_artifact = Path(os.path.relpath(artifact, start=releases.parent))
    temporary.symlink_to(relative_artifact, target_is_directory=True)
    os.replace(temporary, latest)
    return latest


def main() -> int:
    args = parse_args()
    local_bug_token = Path.home() / ".config" / "hexlive" / "bug-token"
    if not os.environ.get("HEXLIVE_BUG_TOKEN") and local_bug_token.is_file():
        os.environ["HEXLIVE_BUG_TOKEN"] = local_bug_token.read_text(encoding="utf-8").strip()
    releases = args.output_root.expanduser().resolve()
    version, version_reason = planned_version()
    variant = "release" if args.release else "development"
    final_dir = releases / f"v{version}"
    previous = load_last_success(releases)
    git_info = collect_git(previous)
    bugs_at_start = read_bug_tracker()

    print_plan(version, version_reason, variant, final_dir, git_info, bugs_at_start)
    print("Контент: только через Asset API; Player не собирает и не копирует bundles.")
    sys.stdout.flush()
    if args.dry_run:
        print("DRY RUN: Unity не запускалась, версия и файлы не изменены.")
        return 0
    if not os.environ.get("HEXLIVE_BUG_TOKEN", "").strip():
        raise RuntimeError("HEXLIVE_BUG_TOKEN is required so the successful build can stamp bug versions")

    if UNITY_LOCK.exists():
        raise RuntimeError(
            f"Unity Editor держит {UNITY_LOCK}. Полностью закрой редактор и повтори сборку; "
            "скрипт не запускает второй Unity поверх открытого проекта."
        )
    if final_dir.exists():
        raise RuntimeError(f"Release directory already exists; refusing to overwrite: {final_dir}")

    unity = find_unity(args.unity)
    # §160: keep the native authorization bridge synchronized with its source.
    subprocess.run([sys.executable, str(ROOT / "Tools" / "build_macos_permissions.py")], check=True)
    releases.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    staging = releases / f"staging-v{version}-{stamp}-{os.getpid()}"
    staging.mkdir()
    app_path = staging / "HexLive.app"
    unity_summary_path = staging / "unity-summary.json"
    unity_log_path = staging / "unity-build.log"

    command = [
        str(unity),
        "-batchmode",
        "-nographics",
        "-quit",
        "-accept-apiupdate",
        "-projectPath",
        str(ROOT),
        "-buildTarget",
        "StandaloneOSX",
        "-executeMethod",
        UNITY_METHOD,
        "-hexlive-build-output",
        str(app_path),
        "-hexlive-build-summary",
        str(unity_summary_path),
        "-hexlive-bugs-api",
        BUG_API,
        "-logFile",
        str(unity_log_path),
    ]
    if args.release:
        command.append("-hexlive-release")

    print(f"Запускаю Unity {unity_version()}… лог: {unity_log_path}", flush=True)
    with temporary_unity_auto_refresh():
        completed = subprocess.run(command, cwd=ROOT, check=False)
    if completed.returncode != 0 or not app_path.is_dir() or not unity_summary_path.is_file():
        print(f"Сборка не опубликована. Диагностика осталась в {staging}", file=sys.stderr)
        if unity_log_path.is_file():
            tail = unity_log_path.read_text(encoding="utf-8", errors="replace").splitlines()[-40:]
            print("\n".join(tail), file=sys.stderr)
        return completed.returncode or 1

    unity_summary = read_json(unity_summary_path)
    if unity_summary.get("result") != "Succeeded" or unity_summary.get("version") != version:
        raise RuntimeError(
            f"Unity summary does not match the planned build: {unity_summary_path}"
        )

    included_bug_ids = {
        int(report["id"])
        for report in active_reports(bugs_at_start, {"ready_for_test"})
        if isinstance(report.get("id"), int)
    }
    bugs_at_end = require_successful_version_finalize(version, included_bug_ids)
    configure_microphone_permission(app_path)
    signature = ensure_development_signature(app_path, args.release)

    manifest = write_reports(
        staging,
        final_dir,
        version,
        variant,
        git_info,
        bugs_at_start,
        bugs_at_end,
        unity_summary,
        signature,
    )
    staging.rename(final_dir)
    write_last_success(releases, manifest)

    artifact = final_dir / "HexLive.app"
    latest = update_latest_player_link(releases, artifact)
    report = final_dir / "BUILD_REPORT.md"
    print(f"Готово: HexLive {version}")
    print(f"Билд:  {artifact.as_uri()}")
    print(f"Последний билд: {latest.as_uri()}")
    print(f"Отчёт: {report.as_uri()}")
    print(
        "Баги к тесту: "
        f"{len(manifest['bugs']['readyForTestInThisBuild'])} в билде, "
        f"{len(manifest['bugs']['readyAfterBuildStartedNotIncluded'])} не вошли."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as error:
        print(f"ОШИБКА: {error}", file=sys.stderr)
        raise SystemExit(1)

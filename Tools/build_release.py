#!/usr/bin/env python3
"""Build a versioned HexLive macOS player and publish its Git/bug report."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DISTRIBUTION = Path.home() / "hex-girls"
DEFAULT_RELEASES = DEFAULT_DISTRIBUTION / "Releases"
DEFAULT_CONTENT = DEFAULT_DISTRIBUTION / "HexLiveContent"
PROJECT_SETTINGS = ROOT / "ProjectSettings" / "ProjectSettings.asset"
PENDING_VERSION = ROOT / "Library" / "HexLivePendingBuildVersion.txt"
BUG_TRACKER = ROOT / "BUGS.json"
LEGACY_BUG_TRACKER = (
    Path.home() / "Library" / "Application Support" / "DefaultCompany" / "HexLive" / "BUGS.json"
)
UNITY_LOCK = ROOT / "Temp" / "UnityLockfile"
UNITY_METHOD = "HexLive.UnityDebug.Editor.HexLiveReleaseBuilder.BuildMacOS"
UNITY_PREFS_DOMAIN = "com.unity3d.UnityEditor5.x"
UNITY_DEFAULTS = Path("/usr/bin/defaults")
CODE_SIGN = Path("/usr/bin/codesign")
AUTO_REFRESH_PREFS = ("kAutoRefreshMode", "kAutoRefresh")
AUTO_REFRESH_BACKUP = ROOT / "Library" / "HexLiveBuildAutoRefreshBackup.json"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Build HexLive for macOS, increment its build version, and place the "
            "player, Unity log, Git changes, and BUGS.json report together."
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
    parser.add_argument(
        "--content-root",
        type=Path,
        default=DEFAULT_CONTENT,
        help=f"existing external HexLiveContent directory (default: {DEFAULT_CONTENT})",
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
    tracker = read_json(BUG_TRACKER)
    reports = tracker.get("reports")
    if not isinstance(reports, list):
        raise RuntimeError(f"Expected reports array in {BUG_TRACKER}")
    return tracker


def require_empty_legacy_bug_tracker() -> None:
    if not LEGACY_BUG_TRACKER.is_file():
        return
    legacy = read_json(LEGACY_BUG_TRACKER)
    reports = legacy.get("reports", [])
    if reports:
        raise RuntimeError(
            f"Legacy bug store contains {len(reports)} report(s): {LEGACY_BUG_TRACKER}. "
            "Merge them into the repository BUGS.json and empty the legacy reports array before building."
        )


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


def ensure_development_signature(app_path: Path, release: bool) -> str:
    valid, details = verify_code_signature(app_path)
    if valid:
        return "unity"
    if release:
        raise RuntimeError(f"Release code signature is invalid:\n{details}")

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
            f"Successful Unity build did not stamp BUGS.json reports {missing} with version {version}"
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


def select_external_catalog(content_source: Path) -> dict[str, Any]:
    platform_dir = content_source / "StandaloneOSX"
    if not platform_dir.is_dir():
        raise RuntimeError(f"External Addressables directory is missing: {platform_dir}")

    bundle_count = sum(1 for path in platform_dir.glob("*.bundle") if path.is_file())
    prosthetic_bundle_count = sum(
        1
        for path in platform_dir.glob("*.bundle")
        if path.is_file() and "prosthetic" in path.name.lower()
    )
    metadata_count = sum(1 for path in platform_dir.glob("*.json") if path.is_file())
    if bundle_count == 0:
        raise RuntimeError(f"No Addressables bundles found in {platform_dir}")

    candidates: list[tuple[float, Path, Path]] = []
    for catalog in platform_dir.glob("catalog_*.bin"):
        if not catalog.is_file():
            continue
        catalog_bytes = catalog.read_bytes()
        # An icon-only patch is not a valid bootstrap for a Player. The full
        # catalog must expose all four runtime content doors. A stale full
        # wardrobe catalog is not enough after fitted prostheses moved out of
        # Resources: the executable would have the loader but no model bundle.
        if not all(
            marker in catalog_bytes
            for marker in (b"wear/", b"hair/", b"hexlive.icons", b"prosthetic/")
        ):
            continue
        catalog_hash = catalog.with_suffix(".hash")
        if not catalog_hash.is_file() or not catalog_hash.read_text(encoding="utf-8").strip():
            continue
        candidates.append((catalog.stat().st_mtime, catalog, catalog_hash))

    if not candidates:
        raise RuntimeError(
            f"No full wear/hair/icons/prosthetics catalog with a matching hash found in {platform_dir}"
        )
    if prosthetic_bundle_count == 0:
        raise RuntimeError(f"No prosthetic Addressables bundle found in {platform_dir}")

    _, catalog, catalog_hash = max(candidates, key=lambda item: item[0])
    return {
        "catalog": catalog,
        "hash": catalog_hash,
        "bundleCount": bundle_count,
        "prostheticBundleCount": prosthetic_bundle_count,
        "metadataCount": metadata_count,
    }


def install_external_content_bootstrap(
    app_path: Path,
    content_source: Path,
    content_catalog: dict[str, Any],
) -> None:
    runtime_dir = app_path / "Contents" / "Resources" / "Data" / "StreamingAssets" / "aa"
    settings_path = runtime_dir / "settings.json"
    if not settings_path.is_file():
        raise RuntimeError(f"Addressables runtime settings are missing from Player: {settings_path}")

    catalog = Path(content_catalog["catalog"])
    catalog_hash = Path(content_catalog["hash"])
    settings = read_json(settings_path)
    locations = settings.get("m_CatalogLocations")
    if not isinstance(locations, list):
        raise RuntimeError(f"Addressables catalog locations are malformed: {settings_path}")

    remote_updated = False
    cache_updated = False
    for location in locations:
        if not isinstance(location, dict):
            continue
        keys = location.get("m_Keys")
        key = keys[0] if isinstance(keys, list) and keys else ""
        if key == "AddressablesMainContentCatalogRemoteHash":
            location["m_InternalId"] = (
                "{UnityEngine.Application.dataPath}/../HexLiveContent/StandaloneOSX/"
                + catalog_hash.name
            )
            remote_updated = True
        elif key == "AddressablesMainContentCatalogCacheHash":
            location["m_InternalId"] = (
                "{UnityEngine.Application.persistentDataPath}/com.unity.addressables/"
                + catalog_hash.name
            )
            cache_updated = True

    if not remote_updated or not cache_updated:
        raise RuntimeError(f"Could not retarget Addressables catalog locations in {settings_path}")

    shutil.copy2(catalog, runtime_dir / "catalog.bin")
    shutil.copy2(catalog_hash, runtime_dir / "catalog.hash")
    settings_path.write_text(
        json.dumps(settings, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )


def write_reports(
    staging: Path,
    final_dir: Path,
    version: str,
    variant: str,
    git_info: dict[str, Any],
    bugs_at_start: dict[str, Any],
    bugs_at_end: dict[str, Any],
    unity_summary: dict[str, Any],
    content_source: Path,
    content_catalog: dict[str, Any],
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
    content_available = (content_source / "StandaloneOSX").is_dir()

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
        "externalContent": {
            "source": str(content_source),
            "available": content_available,
            "rebuiltByPlayerBuild": False,
            "catalog": str(content_catalog["catalog"]),
            "catalogHash": str(content_catalog["hash"]),
            "bundleCount": content_catalog["bundleCount"],
            "prostheticBundleCount": content_catalog["prostheticBundleCount"],
            "metadataCount": content_catalog["metadataCount"],
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
        f"Переиспользован внешний каталог `{content_catalog['catalog']}` из "
        f"`{content_source}` через `HexLiveContent`: "
        f"{content_catalog['bundleCount']} bundle, "
        f"из них prosthetic — {content_catalog['prostheticBundleCount']}, "
        f"{content_catalog['metadataCount']} meta JSON. Player Build контент не пересобирал."
        if content_available
        else f"ВНИМАНИЕ: внешний каталог `{content_source / 'StandaloneOSX'}` не найден."
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
        "Этот список — срез `BUGS.json` на старте сборки; успешный post-build "
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
        f"- Размер Player: `{int(unity_summary.get('totalBytes', 0))} байт`",
        f"- Предупреждения / ошибки: `{unity_summary.get('warnings', '?')} / "
        f"{unity_summary.get('errors', '?')}`",
        f"- Подпись приложения: `{signature}`",
        f"- Лог: [unity-build.log]({(final_dir / 'unity-build.log').as_uri()})",
        "",
        "## Внешний контент",
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
    releases = args.output_root.expanduser().resolve()
    content_source = args.content_root.expanduser().resolve()
    content_catalog = select_external_catalog(content_source)
    version, version_reason = planned_version()
    variant = "release" if args.release else "development"
    final_dir = releases / f"v{version}"
    previous = load_last_success(releases)
    git_info = collect_git(previous)
    require_empty_legacy_bug_tracker()
    bugs_at_start = read_bug_tracker()

    print_plan(version, version_reason, variant, final_dir, git_info, bugs_at_start)
    print(
        "Внешний контент: "
        f"{content_catalog['catalog']} "
        f"({content_catalog['bundleCount']} bundle, {content_catalog['metadataCount']} meta JSON)"
    )
    sys.stdout.flush()
    if args.dry_run:
        print("DRY RUN: Unity не запускалась, версия и файлы не изменены.")
        return 0

    if UNITY_LOCK.exists():
        raise RuntimeError(
            f"Unity Editor держит {UNITY_LOCK}. Полностью закрой редактор и повтори сборку; "
            "скрипт не запускает второй Unity поверх открытого проекта."
        )
    if final_dir.exists():
        raise RuntimeError(f"Release directory already exists; refusing to overwrite: {final_dir}")

    unity = find_unity(args.unity)
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
        "-hexlive-bugs",
        str(BUG_TRACKER),
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
    install_external_content_bootstrap(app_path, content_source, content_catalog)
    content_link = staging / "HexLiveContent"
    content_link.symlink_to(content_source, target_is_directory=True)
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
        content_source,
        content_catalog,
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

#!/usr/bin/env python3
"""Build a versioned HexLive Windows player with its Addressables content.

Windows sibling of Tools/build_release.py (which stays macOS-only). Differences
that are platform reality, not policy drift:

  * The Addressables content IS rebuilt here (first Unity invocation) — this
    machine has no prebuilt shared HexLiveContent to reuse, and a catalog
    without icons/prosthetics must be refused, same as on macOS.
  * Unity Auto Refresh prefs live in the registry (hashed value names), not in
    `defaults`; they are forced to 1 for batchmode and restored afterwards.
  * No codesign. The "latest player" pointer is an NTFS junction (no admin
    rights needed), not a symlink.

Layout mirrors macOS:
  ~/hex-girls/Releases/v<version>/HexLive/HexLive.exe       the player
  ~/hex-girls/Releases/v<version>/HexLive/HexLiveContent    junction -> shared
  ~/hex-girls/HexLiveContent/StandaloneWindows64/           shared bundles
  ~/hex-girls/HexLive/                                      junction -> latest player
"""

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
import time
from typing import Any

# Отчёты и план печатаются по-русски, а консоль на этой машине бывает в cp1251
# (Git Bash) — тогда обычный print падает на UnicodeEncodeError ещё до запуска
# Unity. Сборка не должна зависеть от кодовой страницы терминала.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = Path(__file__).resolve().parents[1]
PLATFORM = "StandaloneWindows64"
DEFAULT_DISTRIBUTION = Path.home() / "hex-girls"
DEFAULT_RELEASES = DEFAULT_DISTRIBUTION / "Releases"
DEFAULT_CONTENT = DEFAULT_DISTRIBUTION / "HexLiveContent"
BUILT_CONTENT = ROOT / "Build" / "AddressableContent" / PLATFORM
PROJECT_SETTINGS = ROOT / "ProjectSettings" / "ProjectSettings.asset"
PENDING_VERSION = ROOT / "Library" / "HexLivePendingBuildVersion.txt"
BUG_TRACKER = ROOT / "BUGS.json"
# persistentDataPath on Windows; a player run without the repo writes here.
LEGACY_BUG_TRACKER = (
    Path.home() / "AppData" / "LocalLow" / "DefaultCompany" / "HexLive" / "BUGS.json"
)
UNITY_LOCK = ROOT / "Temp" / "UnityLockfile"
PLAYER_METHOD = "HexLive.UnityDebug.Editor.HexLiveReleaseBuilder.BuildWindows"
CONTENT_METHOD = "HexLiveContentBatchBuild.Build"
AUTO_REFRESH_BACKUP = ROOT / "Library" / "HexLiveBuildAutoRefreshBackup-Windows.json"
UNITY_PREFS_KEY = r"Software\Unity Technologies\Unity Editor 5.x"
AUTO_REFRESH_PREFIXES = ("kAutoRefreshMode", "kAutoRefresh")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Build HexLive for Windows: Addressables content first, then the "
            "player, then publish them together with the Git/bug report."
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
        help="print the planned version, commits, bugs, and paths without building",
    )
    parser.add_argument(
        "--skip-content",
        action="store_true",
        help=(
            "reuse the existing Build/AddressableContent instead of rebuilding "
            "it (retry after a player-side failure only; the catalog is still "
            "validated for completeness)"
        ),
    )
    parser.add_argument(
        "--unity",
        type=Path,
        help="path to Unity.exe (normally discovered from ProjectVersion.txt)",
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
        help=f"shared external HexLiveContent directory (default: {DEFAULT_CONTENT})",
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
        raw = git(
            "log",
            "--reverse",
            "--format=%H%x1f%h%x1f%ad%x1f%s",
            "--date=short",
            f"{base}..{head}",
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


def unity_processes() -> list[str]:
    """Names of running Unity editor processes, empty when none."""
    result = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Unity.exe", "/NH"],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    return [
        line.split()[0]
        for line in result.stdout.splitlines()
        if line.strip().lower().startswith("unity.exe")
    ]


def require_free_unity_lock() -> None:
    """Refuse while an editor is open; clear the lock a crashed batch run left.

    A batchmode Unity that exits non-zero leaves Temp/UnityLockfile behind, so
    the file alone does not prove a live editor. The process table does.
    """
    if not UNITY_LOCK.exists():
        return
    running = unity_processes()
    if running:
        raise RuntimeError(
            f"Unity Editor держит {UNITY_LOCK} (процессов: {len(running)}). "
            "Полностью закрой редактор и повтори сборку; "
            "скрипт не запускает второй Unity поверх открытого проекта."
        )
    UNITY_LOCK.unlink()
    print(f"Убран протухший {UNITY_LOCK.name}: процессов Unity нет.")


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
    version = unity_version()
    for hub_root in (
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Unity" / "Hub" / "Editor",
        Path.home() / "AppData" / "Local" / "Programs" / "Unity" / "Hub" / "Editor",
    ):
        candidates.append(hub_root / version / "Editor" / "Unity.exe")
    for candidate in candidates:
        resolved = candidate.resolve()
        if resolved.is_file():
            return resolved
    raise RuntimeError(r"Unity.exe not found; pass it with --unity C:\path\to\Unity.exe")


# --- Unity Auto Refresh (registry) -----------------------------------------
# CompileControl deliberately keeps Auto Refresh at 0 for interactive work, so
# a batchmode launch could run -executeMethod against STALE assemblies. Force
# both prefs to 1 for the build and restore the exact previous values after.
# Value names are hashed by Unity (e.g. kAutoRefreshMode_h2874646975), so the
# prefs are matched by prefix, not by exact name.


def read_auto_refresh_values() -> dict[str, int]:
    import winreg

    values: dict[str, int] = {}
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, UNITY_PREFS_KEY) as key:
            index = 0
            while True:
                try:
                    name, value, kind = winreg.EnumValue(key, index)
                except OSError:
                    break
                index += 1
                if kind == winreg.REG_DWORD and name.startswith(AUTO_REFRESH_PREFIXES):
                    values[name] = int(value)
    except FileNotFoundError:
        pass
    return values


def write_auto_refresh_values(values: dict[str, int]) -> None:
    import winreg

    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, UNITY_PREFS_KEY) as key:
        for name, value in values.items():
            winreg.SetValueEx(key, name, 0, winreg.REG_DWORD, int(value))


def recover_interrupted_auto_refresh_override() -> None:
    if not AUTO_REFRESH_BACKUP.is_file():
        return
    backup = read_json(AUTO_REFRESH_BACKUP)
    write_auto_refresh_values({str(k): int(v) for k, v in backup.get("prefs", {}).items()})
    AUTO_REFRESH_BACKUP.unlink()
    print("Восстановлены Unity Auto Refresh prefs после прерванного прошлого запуска.")


@contextmanager
def temporary_unity_auto_refresh():
    recover_interrupted_auto_refresh_override()
    original = read_auto_refresh_values()
    backup = {
        "createdAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "prefs": original,
    }
    temporary = AUTO_REFRESH_BACKUP.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(backup, ensure_ascii=False, indent=4) + "\n", encoding="utf-8")
    os.replace(temporary, AUTO_REFRESH_BACKUP)
    try:
        if original:
            write_auto_refresh_values({name: 1 for name in original})
            print("Auto Refresh временно включён для обязательной компиляции batchmode.")
        else:
            print("Auto Refresh prefs отсутствуют в реестре — Unity по умолчанию включает его сам.")
        yield
    finally:
        write_auto_refresh_values(original)
        AUTO_REFRESH_BACKUP.unlink(missing_ok=True)
        if original:
            print("Исходные Unity Auto Refresh prefs восстановлены.")


# --- Addressables content ---------------------------------------------------


def select_external_catalog(platform_dir: Path) -> dict[str, Any]:
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
        # catalog must expose all four runtime content doors; a pre-prosthetics
        # catalog is stale for current code and must also be refused.
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


def sync_shared_content(content_source: Path) -> None:
    """Mirror the freshly built bundles into the shared distribution folder."""
    target = content_source / PLATFORM
    target.parent.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [
            "robocopy",
            str(BUILT_CONTENT),
            str(target),
            "/MIR",
            "/MT:8",
            "/NFL",
            "/NDL",
            "/NJH",
            "/NP",
        ],
        check=False,
    )
    # Robocopy: 0-7 are success grades, 8+ are failures.
    if result.returncode >= 8:
        raise RuntimeError(f"robocopy failed with code {result.returncode}")


def install_external_content_bootstrap(
    player_dir: Path,
    content_catalog: dict[str, Any],
) -> None:
    runtime_dir = player_dir / "HexLive_Data" / "StreamingAssets" / "aa"
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
                "{UnityEngine.Application.dataPath}/../HexLiveContent/" + PLATFORM + "/"
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


# --- Junctions (no admin rights required, unlike symlinks) ------------------


def make_junction(link: Path, target: Path) -> None:
    if link.exists() or link.is_symlink():
        # rmdir removes a junction/empty dir but refuses a real dir with
        # content, so a mistaken path cannot delete anything valuable.
        try:
            os.rmdir(link)
        except OSError as error:
            raise RuntimeError(
                f"Refusing to replace non-junction directory: {link} ({error})"
            ) from error
    result = subprocess.run(
        ["cmd", "/c", "mklink", "/J", str(link), str(target)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or f"mklink /J failed for {link}")


# --- Reporting (same shape as the macOS script) -----------------------------


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
    content_plan: str,
) -> None:
    ready = active_reports(tracker, {"ready_for_test"})
    open_reports = active_reports(tracker, {"created", "rework", "in_progress"})
    commits = git_info["commits"]

    print(f"HexLive {version} ({variant}; {version_reason}; платформа {PLATFORM})")
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
    print(f"Контент: {content_plan}")
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
    content_source: Path,
    content_catalog: dict[str, Any],
    content_rebuilt: bool,
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
    artifact = final_dir / "HexLive" / "HexLive.exe"
    content_available = (content_source / PLATFORM).is_dir()

    manifest: dict[str, Any] = {
        "version": version,
        "builtAtUtc": built_at,
        "variant": variant,
        "platform": PLATFORM,
        "artifact": str(artifact),
        **git_info,
        "unity": unity_summary,
        "codeSignature": "none (Windows)",
        "bugs": {
            "readyForTestInThisBuild": [bug_record(report) for report in included],
            "readyAfterBuildStartedNotIncluded": [bug_record(report) for report in late_ready],
            "notReady": [bug_record(report) for report in not_ready],
        },
        "externalContent": {
            "source": str(content_source),
            "available": content_available,
            "rebuiltByPlayerBuild": False,
            "rebuiltByThisScript": content_rebuilt,
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
    rebuilt_note = "пересобран этим скриптом" if content_rebuilt else "переиспользован без пересборки"
    content_note = (
        f"Каталог `{content_catalog['catalog']}` ({rebuilt_note}), разложен в "
        f"`{content_source}`: {content_catalog['bundleCount']} bundle, "
        f"из них prosthetic — {content_catalog['prostheticBundleCount']}, "
        f"{content_catalog['metadataCount']} meta JSON."
        if content_available
        else f"ВНИМАНИЕ: внешний каталог `{content_source / PLATFORM}` не найден."
    )

    markdown = [
        f"# HexLive {version} (Windows)",
        "",
        f"- Собрано: `{built_at}`",
        f"- Вариант: **{variant}**",
        f"- Платформа: `{PLATFORM}`",
        f"- Артефакт: `{artifact}`",
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
        f"- Лог плеера: `unity-build.log`; лог контента: `unity-content-build.log`",
        "",
        "## Внешний контент",
        "",
        content_note,
        "",
    ]
    (staging / "BUILD_REPORT.md").write_text("\n".join(markdown), encoding="utf-8")
    return manifest


def publish_staging(staging: Path, final_dir: Path, attempts: int = 12) -> None:
    """Rename staging → v<version>, waiting out whoever holds a file inside.

    Windows refuses to rename a directory while ANY file under it is open, and
    a build directory is exactly what a log tail, an editor, or the antivirus
    scanning a fresh .exe likes to hold. Failing here throws away eight minutes
    of finished work over a handle that is usually gone a second later, so the
    rename is retried before giving up — and the error then names the cause
    instead of leaving a bare «Access is denied».
    """
    delay = 0.5
    for attempt in range(1, attempts + 1):
        try:
            staging.rename(final_dir)
            if attempt > 1:
                print(f"Публикация удалась с попытки {attempt}.")
            return
        except OSError as error:
            if attempt == attempts:
                raise RuntimeError(
                    f"Не удалось опубликовать {staging} как {final_dir} за {attempts} попыток: "
                    f"{error}. Обычно это открытый файл ВНУТРИ папки — «tail -f» на "
                    "unity-build.log, открытый проводник или антивирус, сканирующий свежий "
                    "HexLive.exe. Сам билд при этом цел: закрой держателя и переименуй папку."
                ) from error
            time.sleep(delay)
            delay = min(delay * 1.6, 5.0)


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


def run_unity(unity: Path, arguments: list[str], log_path: Path, label: str) -> int:
    command = [
        str(unity),
        "-batchmode",
        "-nographics",
        "-quit",
        "-accept-apiupdate",
        "-projectPath",
        str(ROOT),
        "-buildTarget",
        "Win64",
        *arguments,
        "-logFile",
        str(log_path),
    ]
    print(f"Запускаю Unity {unity_version()} ({label})… лог: {log_path}", flush=True)
    completed = subprocess.run(command, cwd=ROOT, check=False)
    return completed.returncode


def tail_log(log_path: Path) -> None:
    if log_path.is_file():
        tail = log_path.read_text(encoding="utf-8", errors="replace").splitlines()[-40:]
        print("\n".join(tail), file=sys.stderr)


def main() -> int:
    args = parse_args()
    releases = args.output_root.expanduser().resolve()
    content_source = args.content_root.expanduser().resolve()
    version, version_reason = planned_version()
    variant = "release" if args.release else "development"
    final_dir = releases / f"v{version}"
    previous = load_last_success(releases)
    git_info = collect_git(previous)
    require_empty_legacy_bug_tracker()
    bugs_at_start = read_bug_tracker()

    content_plan = (
        f"переиспользую {BUILT_CONTENT} (--skip-content)"
        if args.skip_content
        else f"пересоберу в {BUILT_CONTENT}, затем разложу в {content_source / PLATFORM}"
    )
    print_plan(version, version_reason, variant, final_dir, git_info, bugs_at_start, content_plan)
    sys.stdout.flush()
    if args.dry_run:
        print("DRY RUN: Unity не запускалась, версия и файлы не изменены.")
        return 0

    require_free_unity_lock()
    if final_dir.exists():
        raise RuntimeError(f"Release directory already exists; refusing to overwrite: {final_dir}")

    unity = find_unity(args.unity)
    releases.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    staging = releases / f"staging-v{version}-{stamp}-{os.getpid()}"
    staging.mkdir()
    player_dir = staging / "HexLive"
    exe_path = player_dir / "HexLive.exe"
    unity_summary_path = staging / "unity-summary.json"
    player_log_path = staging / "unity-build.log"
    content_log_path = staging / "unity-content-build.log"

    with temporary_unity_auto_refresh():
        if not args.skip_content:
            # Build artefact only; wipe so a stale pre-prosthetics catalog can
            # never win the newest-valid-catalog selection below.
            if BUILT_CONTENT.exists():
                shutil.rmtree(BUILT_CONTENT)
            code = run_unity(
                unity,
                ["-executeMethod", CONTENT_METHOD],
                content_log_path,
                "Addressables-контент",
            )
            if code != 0:
                print(f"Сборка контента не удалась. Диагностика в {staging}", file=sys.stderr)
                tail_log(content_log_path)
                return code or 1

        content_catalog = select_external_catalog(BUILT_CONTENT)
        print(
            "Контент собран: "
            f"{content_catalog['catalog'].name} "
            f"({content_catalog['bundleCount']} bundle, prosthetic — "
            f"{content_catalog['prostheticBundleCount']}, "
            f"{content_catalog['metadataCount']} meta JSON)"
        )
        sync_shared_content(content_source)

        player_arguments = [
            "-executeMethod",
            PLAYER_METHOD,
            "-hexlive-build-output",
            str(exe_path),
            "-hexlive-build-summary",
            str(unity_summary_path),
            "-hexlive-bugs",
            str(BUG_TRACKER),
        ]
        if args.release:
            player_arguments.append("-hexlive-release")
        code = run_unity(unity, player_arguments, player_log_path, "Player")

    if code != 0 or not exe_path.is_file() or not unity_summary_path.is_file():
        print(f"Сборка не опубликована. Диагностика осталась в {staging}", file=sys.stderr)
        tail_log(player_log_path)
        return code or 1

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

    # Point the shipped catalog references at the shared folder, then link it
    # beside the exe: dataPath/.. resolves to the player folder on Windows.
    install_external_content_bootstrap(player_dir, content_catalog)
    make_junction(player_dir / "HexLiveContent", content_source)

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
        not args.skip_content,
    )
    publish_staging(staging, final_dir)
    write_last_success(releases, manifest)

    artifact = final_dir / "HexLive" / "HexLive.exe"
    latest = releases.parent / "HexLive"
    make_junction(latest, final_dir / "HexLive")
    report = final_dir / "BUILD_REPORT.md"
    print(f"Готово: HexLive {version} (Windows, {variant})")
    print(f"Билд:  {artifact}")
    print(f"Последний билд: {latest}\\HexLive.exe")
    print(f"Отчёт: {report}")
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

#!/usr/bin/env python3
"""Install the real «FMOD for Unity» integration on a machine that only has the stub.

Why this exists. `Assets/Plugins/FMOD/` is 297 MB of third-party binaries and is
gitignored, so a fresh clone cannot compile `FmodSfx.cs` (CS0246 on the FMOD /
FMODUnity namespaces). Until the integration is imported, the machine carries
`Assets/HexLive/UnityPresentation/Audio/FmodStub.local.cs`, which satisfies the
compiler and plays nothing.

The swap is a TRANSACTION, not two steps: the stub declares the same namespaces
the real integration does, so leaving it in place after the import gives CS0101
(«type already defined») on every FMOD type. This script removes the stub and
brings the integration in within one Unity run, then verifies by CONTENT — the
wrapper's own version constant and the presence of the Windows native libs —
rather than by «the command exited 0».

Two sources, because both happen in practice:

    python3 Tools/setup_fmod.py --package  D:/dl/fmodstudio20314.unitypackage
    python3 Tools/setup_fmod.py --from-folder  //mac/HexLive/Assets/Plugins/FMOD

The version must match the banks. `Assets/HexLiveContent/AudioSource/FMODBanks/Master.bank`
carries its format version in the FMT chunk (146 = FMOD 2.03.x); this script
reads it and refuses an integration from a different major.minor line, since a
2.01 runtime answers ERR_HEADER_MISMATCH on a 2.03 bank and the game goes silent
with no obvious cause.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
STUB = ROOT / "Assets" / "HexLive" / "UnityPresentation" / "Audio" / "FmodStub.local.cs"
FMOD_PLUGIN = ROOT / "Assets" / "Plugins" / "FMOD"
MASTER_BANK = (ROOT / "Assets" / "HexLiveContent" / "AudioSource" /
               "FMODBanks" / "Master.bank")
UNITY_LOCK = ROOT / "Temp" / "UnityLockfile"

# Bank FMT-chunk format version -> the FMOD line that writes it. Only the lines
# this project has actually shipped are listed; an unknown version is reported,
# not guessed.
BANK_VERSION_TO_FMOD = {
    141: "2.02",
    142: "2.02",
    146: "2.03",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Import the real FMOD for Unity integration and remove the local stub."
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument(
        "--package",
        type=Path,
        help="path to the fmodstudio<version>.unitypackage downloaded from fmod.com",
    )
    source.add_argument(
        "--from-folder",
        type=Path,
        help="path to an existing Assets/Plugins/FMOD folder (e.g. from the Mac checkout)",
    )
    parser.add_argument(
        "--unity",
        type=Path,
        help="path to Unity.exe / Unity (normally discovered from ProjectVersion.txt)",
    )
    parser.add_argument(
        "--keep-stub",
        action="store_true",
        help="do NOT delete FmodStub.local.cs (diagnostics only; the project will not compile)",
    )
    return parser.parse_args()


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

    A batchmode Unity that exits non-zero — which is exactly what a compile
    error does — leaves Temp/UnityLockfile behind. Treating the file itself as
    proof of a live editor turns every failed run into a manual cleanup, so the
    process table is asked instead: the file only blocks when Unity.exe is
    actually running.
    """
    if not UNITY_LOCK.exists():
        return
    running = unity_processes()
    if running:
        raise RuntimeError(
            f"Unity Editor держит {UNITY_LOCK} (процессов: {len(running)}). "
            "Полностью закрой редактор и повтори."
        )
    UNITY_LOCK.unlink()
    print(f"Убран протухший {UNITY_LOCK.name}: процессов Unity нет.")


def bank_format_version(path: Path) -> int | None:
    """Read the FEV FMT chunk version. RIFF | size | 'FEV ' | 'FMT ' | size | version."""
    try:
        head = path.read_bytes()[:32]
    except OSError:
        return None
    if len(head) < 28 or head[0:4] != b"RIFF" or head[8:12] != b"FEV " or head[12:16] != b"FMT ":
        return None
    return struct.unpack_from("<I", head, 20)[0]


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
    candidates += [
        Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
        / "Unity" / "Hub" / "Editor" / version / "Editor" / "Unity.exe",
        Path.home() / "AppData" / "Local" / "Programs" / "Unity" / "Hub" / "Editor"
        / version / "Editor" / "Unity.exe",
        Path("/Applications/Unity/Hub/Editor") / version / "Unity.app/Contents/MacOS/Unity",
    ]
    for candidate in candidates:
        if candidate.expanduser().is_file():
            return candidate.expanduser().resolve()
    raise RuntimeError("Unity executable not found; pass it with --unity <path>")


def integration_version(plugin_root: Path) -> str | None:
    """FMOD's own wrapper states its version.

    The constant is decimal-in-hex: 0x00020314 reads as 2.03.14, NOT as
    2.3.20. Each pair of hex DIGITS is the decimal number it looks like, so the
    value is formatted from the digits, never from the integer.
    """
    for wrapper in plugin_root.rglob("fmod.cs"):
        match = re.search(
            r"public\s+const\s+int\s+number\s*=\s*0x([0-9a-fA-F]{8})",
            wrapper.read_text(encoding="utf-8", errors="replace"),
        )
        if match:
            digits = match.group(1)
            return f"{int(digits[0:4])}.{digits[4:6]}.{digits[6:8]}"
    return None


def windows_natives_present(plugin_root: Path) -> list[Path]:
    return [
        dll
        for dll in plugin_root.rglob("fmodstudio*.dll")
        if "x86_64" in str(dll).replace("\\", "/")
    ]


def remove_stub() -> None:
    for path in (STUB, STUB.with_suffix(".cs.meta")):
        if path.exists():
            path.unlink()
            print(f"Удалена заглушка: {path.relative_to(ROOT)}")


def extract_package(package: Path) -> int:
    """Unpack a .unitypackage into Assets/ without Unity.

    ⭐ Unity's own `-importPackage` CANNOT be used here, and the reason is a
    deadlock, not a preference: the editor compiles the existing scripts BEFORE
    it processes the argument, and the project does not compile until FMOD is
    present — removing the stub is what makes room for it. Unity then exits 1
    («Scripts have compiler errors») having imported nothing at all.

    A .unitypackage is a gzipped tar of one directory per asset:
        <guid>/pathname   destination, e.g. «Assets/Plugins/FMOD/fmod.cs»
        <guid>/asset      the file bytes (absent for a folder entry)
        <guid>/asset.meta the .meta, which CARRIES THE GUID — writing it is what
                          keeps FMOD's own prefab/asset references intact.
    """
    import tarfile

    written = 0
    with tarfile.open(package, "r:gz") as archive:
        members = {member.name: member for member in archive.getmembers()}
        for name, member in members.items():
            if not name.endswith("/pathname"):
                continue
            guid = name[: -len("/pathname")]
            source = archive.extractfile(member)
            if source is None:
                continue
            destination = source.read().decode("utf-8").splitlines()[0].strip()
            if (
                not destination
                or not destination.startswith("Assets/")
                or ".." in destination.split("/")
            ):
                print(f"ПРОПУСК подозрительного пути: {destination!r}", file=sys.stderr)
                continue

            target = ROOT / destination
            asset = members.get(f"{guid}/asset")
            meta = members.get(f"{guid}/asset.meta")

            if asset is None:
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                payload = archive.extractfile(asset)
                if payload is None:
                    continue
                target.write_bytes(payload.read())
                written += 1

            if meta is not None:
                payload = archive.extractfile(meta)
                if payload is not None:
                    Path(str(target) + ".meta").write_bytes(payload.read())

    return written


def compile_check(unity: Path, log: Path) -> int:
    """Let Unity import the new files and compile; fail loudly if it cannot."""
    command = [
        str(unity),
        "-batchmode",
        "-nographics",
        "-quit",
        "-accept-apiupdate",
        "-projectPath",
        str(ROOT),
        "-logFile",
        str(log),
    ]
    print(f"Компилирую проект в Unity {unity_version()}… лог: {log}", flush=True)
    return subprocess.run(command, cwd=ROOT, check=False).returncode


def copy_folder(source: Path, log: Path) -> None:
    print(f"Копирую интеграцию из {source} …", flush=True)
    if FMOD_PLUGIN.exists():
        shutil.rmtree(FMOD_PLUGIN)
    shutil.copytree(source, FMOD_PLUGIN)
    log.write_text(f"copied from {source}\n", encoding="utf-8")


def main() -> int:
    args = parse_args()
    require_free_unity_lock()

    bank_version = bank_format_version(MASTER_BANK)
    expected_line = BANK_VERSION_TO_FMOD.get(bank_version or -1)
    if bank_version is None:
        print(f"ПРЕДУПРЕЖДЕНИЕ: не удалось прочитать версию банка {MASTER_BANK}", file=sys.stderr)
    else:
        print(
            f"Банки: формат {bank_version} → FMOD "
            f"{expected_line or '(неизвестная линия)'} ({MASTER_BANK.name})"
        )

    unity = find_unity(args.unity)
    log = ROOT / "Library" / "HexLiveFmodImport.log"

    if not args.keep_stub:
        remove_stub()

    if args.package:
        package = args.package.expanduser()
        if not package.is_file():
            raise RuntimeError(f"Пакет не найден: {package}")
        size_mb = package.stat().st_size / (1024 * 1024)
        print(f"Пакет: {package} ({size_mb:.1f} МБ)")
        written = extract_package(package)
        print(f"Распаковано файлов: {written}")
        if written == 0:
            raise RuntimeError(f"Из пакета не извлечено ни одного файла: {package}")
    else:
        source = args.from_folder.expanduser()
        if not (source / "Resources").exists() and not any(source.rglob("fmod.cs")):
            raise RuntimeError(f"Не похоже на папку интеграции FMOD: {source}")
        copy_folder(source, log)

    # --- verify by content, never by exit code -----------------------------
    if not FMOD_PLUGIN.is_dir():
        raise RuntimeError(f"После установки нет {FMOD_PLUGIN} — интеграция не встала.")

    version = integration_version(FMOD_PLUGIN)
    natives = windows_natives_present(FMOD_PLUGIN)
    print(f"Интеграция: версия {version or '(не определена)'}")
    print(f"Нативные библиотеки Windows x86_64: {len(natives)}")
    for dll in natives[:6]:
        print(f"  {dll.relative_to(ROOT)}")

    problems: list[str] = []
    if version is None:
        problems.append("не удалось прочитать версию из fmod.cs")
    elif expected_line and not version.startswith(expected_line + "."):
        problems.append(
            f"интеграция {version} не совпадает с линией банков {expected_line}.x — "
            "рантайм ответит ERR_HEADER_MISMATCH и звука не будет"
        )
    if not natives:
        problems.append(
            "нет нативных .dll под Windows x86_64 — с такой копией плеер соберётся, но молча"
        )
    if STUB.exists() and not args.keep_stub:
        problems.append(f"заглушка всё ещё на месте: {STUB}")

    if problems:
        print("\nПРОБЛЕМЫ:", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    code = compile_check(unity, log)
    if code != 0:
        print(f"\nUnity не смогла скомпилировать проект (код {code}); лог: {log}", file=sys.stderr)
        errors = [
            line
            for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
            if ": error CS" in line
        ]
        for line in errors[:20]:
            print(f"  {line}", file=sys.stderr)
        return code

    print(
        "\nГотово: интеграция на месте и проект компилируется. Осталось настроить "
        "FMODStudioSettings (authoring source → Assets/HexLiveContent/AudioSource/FMODBanks, "
        "ImportType = AssetBundle, BankLoadType = None): Player не копирует банки, "
        "а raw audio records загружаются через ContentAssetService."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError) as error:
        print(f"ОШИБКА: {error}", file=sys.stderr)
        raise SystemExit(1)

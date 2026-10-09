#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Компилирует сборки HexLive под defines ПЛЕЕРА (WebGL или Standalone) без Unity (§168.9).

    python3 Tools/webgl_compile_check.py                 # WebGL: ошибки + запрещённые API
    python3 Tools/webgl_compile_check.py --target standalone
    python3 Tools/webgl_compile_check.py --report-only   # запрещённые API — предупреждения

Зачем: Unity в редакторе компилирует презентацию с UNITY_EDITOR и платформой
macOS, поэтому ветки `#if UNITY_WEBGL` не видит никто, пока не запущен
WebGL-билд (а он идёт десятки минут и требует закрытого редактора). Здесь те
же исходники собираются `dotnet build` за секунды:

* defines — от сгенерированного Unity csproj, но без UNITY_EDITOR и с
  платформой цели (UNITY_WEBGL / UNITY_STANDALONE_OSX);
* движок — managed-сборки ПЛЕЕРА цели из PlaybackEngines, не редактора;
  UnityEditor*.dll не подключаются вовсе, так что неогороженный
  редакторный вызов — ошибка компиляции, как в настоящем билде;
* под WebGL нет MagicaClothV2 (§168.7: в вебе ткань не живёт), значит
  любое неогороженное обращение к ней тоже ошибка;
* `Tools/webgl/BannedSymbols.txt` через BannedApiAnalyzers: HttpClient,
  ClientWebSocket, Task.Run, блокирующие ожидания и т.п. под UNITY_WEBGL —
  ошибки RS0030. Анализатор видит код ПОСЛЕ препроцессора, поэтому ветка
  `#if !UNITY_WEBGL` с HttpClient законна, а неогороженная — нет.

Источник ссылок — сгенерированные csproj и Library/ScriptAssemblies РАБОЧЕГО
проекта Unity (`--unity-project`, по умолчанию /Volumes/ORICO/HexLive):
сторонние пакеты (FMOD, I2, URP, InputSystem) берутся оттуда готовыми.
Исходники HexLive — из `--checkout` (по умолчанию этот репозиторий), так что
worktree проверяет СВОИ файлы, а не файлы основного дерева.
"""

from __future__ import annotations

import argparse
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

REPO = pathlib.Path(__file__).resolve().parent.parent
UNITY_VERSION = "6000.4.5f1"
UNITY_ROOT = pathlib.Path(f"/Applications/Unity/Hub/Editor/{UNITY_VERSION}")
PLAYER_MANAGED = {
    "webgl": UNITY_ROOT / "PlaybackEngines/WebGLSupport/Variations/nondevelopment/Data/Managed",
    "standalone": UNITY_ROOT / "Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/Variations/il2cpp/Managed",
}
BANNED_API_PACKAGE = ("Microsoft.CodeAnalysis.BannedApiAnalyzers", "3.3.4")

# Наши asmdef в порядке зависимостей: (имя, каталог asmdef, свои ссылки).
ASSEMBLIES = [
    ("HexLive.Simulation", "Assets/HexLive/Simulation", []),
    ("HexLive.UnityPresentation", "Assets/HexLive/UnityPresentation", ["HexLive.Simulation"]),
    ("HexLive.UnityDebug", "Assets/HexLive/UnityDebug", ["HexLive.Simulation", "HexLive.UnityPresentation"]),
]
OWN = {name for name, _, _ in ASSEMBLIES}
# Сторонние asmdef, которые цель не собирает вовсе.
EXCLUDED_THIRD_PARTY = {"webgl": {"MagicaClothV2"}, "standalone": set()}
# Banned-API проверяется только там, где код реально исполняется в клиенте.
# Simulation.LlmControl (Task.Run) живёт на сервере; её долг — отдельным отчётом.
BANNED_ENFORCED = {"HexLive.UnityPresentation", "HexLive.UnityDebug"}

EDITOR_DEFINE = re.compile(
    r"(EDITOR|STANDALONE|^ENABLE_MONO$|^PLATFORM_SUPPORTS_MONO$|^UNITY_INCLUDE_TESTS$"
    r"|^ENABLE_GAMECENTER$|^UNITY_TEAM_LICENSE$|^ENABLE_MARSHALLING_TESTS$"
    r"|^ENABLE_UNITY_COLLECTIONS_CHECKS$|^ENABLE_CLOUD_LICENSE$|^ENABLE_CLUSTER|^ENABLE_SPATIALTRACKING$"
    r"|^PLATFORM_HAS_CUSTOM_MUTEX$|^PLATFORM_SUPPORTS_SPLIT_GRAPHICS_JOBS$|^MAGICACLOTH2$"
    r"|^DEBUG$|^UNITY_ASSERTIONS$|^ENABLE_PROFILER)")
PLATFORM_DEFINES = {
    "webgl": ["UNITY_WEBGL", "PLATFORM_WEBGL", "ENABLE_IL2CPP", "UNITY_WEBGL_API"],
    "standalone": ["UNITY_STANDALONE_OSX", "UNITY_STANDALONE", "PLATFORM_STANDALONE_OSX",
                   "PLATFORM_STANDALONE", "ENABLE_IL2CPP", "MAGICACLOTH2"],
}
MSBUILD_NS = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--target", choices=sorted(PLAYER_MANAGED), default="webgl")
    p.add_argument("--unity-project", type=pathlib.Path, default=pathlib.Path("/Volumes/ORICO/HexLive"),
                   help="проект, где Unity сгенерировал *.csproj и Library/ScriptAssemblies")
    p.add_argument("--checkout", type=pathlib.Path, default=REPO, help="откуда брать исходники HexLive")
    p.add_argument("--out", type=pathlib.Path, default=None, help="каталог для проектов и obj (временный)")
    p.add_argument("--report-only", action="store_true",
                   help="запрещённые API — предупреждениями, не ошибками (список долга)")
    p.add_argument("--keep", action="store_true", help="не удалять сгенерированный каталог")
    return p.parse_args()


def read_generated(unity_project: pathlib.Path, name: str) -> tuple[list[str], list[tuple[str, str]], list[str]]:
    """defines, [(reference, hintpath)], [project-reference names] из csproj, сгенерированного Unity."""
    path = unity_project / f"{name}.csproj"
    if not path.exists():
        sys.exit(f"нет {path}: откройте проект в Unity хотя бы раз (он генерирует csproj)")
    root = ET.parse(path).getroot()
    defines: list[str] = []
    for node in root.iterfind(".//m:DefineConstants", MSBUILD_NS):
        defines = [d for d in (node.text or "").split(";") if d]
        break
    refs = []
    for node in root.iterfind(".//m:Reference", MSBUILD_NS):
        hint = node.find("m:HintPath", MSBUILD_NS)
        if hint is not None and hint.text:
            refs.append((node.get("Include") or "", hint.text))
    projects = [pathlib.Path(n.get("Include") or "").stem for n in root.iterfind(".//m:ProjectReference", MSBUILD_NS)]
    return defines, refs, projects


def player_reference(target: str, hint: str) -> str | None:
    """Ссылка для ПЛЕЕРА: редакторные — выкинуть, модули движка — из PlaybackEngines цели."""
    name = pathlib.Path(hint).name
    # Сам путь установки содержит «/Hub/Editor/» — судить только по хвосту.
    lowered = hint.replace("\\", "/").replace(str(UNITY_ROOT), "").lower()
    if name.startswith("UnityEditor") or "/editor/" in lowered or "nunit" in lowered \
            or "mono-cecil" in lowered or "unity xcode" in lowered or "/tests/" in lowered:
        return None
    if "/Scripting/Managed/UnityEngine/" in hint or name in ("UnityEngine.dll", "Unity.Scripting.dll"):
        candidate = PLAYER_MANAGED[target] / name
        if name == "UnityEngine.dll":
            # Фасад редактора тянет UnityEditor; модули плеера покрывают всё.
            return None
        return str(candidate) if candidate.exists() else None
    return hint


def sources(checkout: pathlib.Path, asm_dir: str) -> list[pathlib.Path]:
    """Все .cs каталога asmdef, кроме поддеревьев со СВОИМ asmdef (как решает Unity)."""
    root = checkout / asm_dir
    nested = {p.parent for p in root.rglob("*.asmdef") if p.parent != root}
    out = []
    for cs in sorted(root.rglob("*.cs")):
        if any(parent in nested for parent in cs.parents):
            continue
        out.append(cs)
    return out


def write_project(out: pathlib.Path, target: str, args: argparse.Namespace, name: str, asm_dir: str,
                  own_refs: list[str]) -> pathlib.Path:
    defines, refs, projects = read_generated(args.unity_project, name)
    defines = [d for d in defines if not EDITOR_DEFINE.search(d)] + PLATFORM_DEFINES[target]
    script_assemblies = args.unity_project / "Library" / "ScriptAssemblies"

    reference_items = []
    for include, hint in refs:
        mapped = player_reference(target, hint)
        if mapped:
            reference_items.append((include, mapped))
    # Модули движка, которых нет в списке редактора, но есть у плеера, не нужны;
    # сторонние asmdef из csproj — готовыми DLL из ScriptAssemblies.
    for project in projects:
        if project in OWN or project in EXCLUDED_THIRD_PARTY[target]:
            continue
        dll = script_assemblies / f"{project}.dll"
        if not dll.exists():
            sys.exit(f"нет {dll}: Unity ещё не компилировал {project}")
        reference_items.append((project, str(dll)))

    enforce = name in BANNED_ENFORCED and target == "webgl" and not args.report_only
    banned = REPO / "Tools" / "webgl" / "BannedSymbols.txt"
    lines = [
        '<Project Sdk="Microsoft.NET.Sdk">',
        "  <PropertyGroup>",
        f"    <AssemblyName>{name}</AssemblyName>",
        "    <TargetFramework>netstandard2.1</TargetFramework>",
        "    <LangVersion>9.0</LangVersion>",
        "    <Nullable>disable</Nullable>",
        "    <NoStdLib>true</NoStdLib>",
        "    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>",
        "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
        "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>",
        "    <NoWarn>0169;0649;0414;0618;0067;0219;0162;0168;8632;8669;CS1701;CS1702</NoWarn>",
        f"    <DefineConstants>{';'.join(defines)}</DefineConstants>",
        f"    <WarningsAsErrors>{'RS0030' if enforce else ''}</WarningsAsErrors>",
        "  </PropertyGroup>",
        "  <ItemGroup>",
    ]
    for cs in sources(args.checkout, asm_dir):
        lines.append(f'    <Compile Include="{cs}" />')
    lines.append("  </ItemGroup>")
    lines.append("  <ItemGroup>")
    for include, hint in reference_items:
        lines.append(f'    <Reference Include="{include}"><HintPath>{hint}</HintPath><Private>false</Private></Reference>')
    for ref in own_refs:
        lines.append(f'    <ProjectReference Include="../{ref}/{ref}.csproj" />')
    lines.append("  </ItemGroup>")
    if target == "webgl":
        lines += [
            "  <ItemGroup>",
            f'    <PackageReference Include="{BANNED_API_PACKAGE[0]}" Version="{BANNED_API_PACKAGE[1]}" '
            'PrivateAssets="all" />',
            f'    <AdditionalFiles Include="{banned}" />',
            "  </ItemGroup>",
        ]
    lines.append("</Project>")
    project_dir = out / name
    project_dir.mkdir(parents=True, exist_ok=True)
    path = project_dir / f"{name}.csproj"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


DIAG = re.compile(r"^(?P<file>/[^(]+)\((?P<line>\d+),(?P<col>\d+)\): (?P<sev>error|warning) (?P<code>\w+): "
                  r"(?P<msg>.*?) \[(?P<proj>[^\]]+)\]$")


def main() -> int:
    args = parse_args()
    args.checkout = args.checkout.resolve()
    if not PLAYER_MANAGED[args.target].exists():
        sys.exit(f"нет модуля плеера: {PLAYER_MANAGED[args.target]}")
    out = args.out or pathlib.Path(tempfile.mkdtemp(prefix=f"hexlive-compile-{args.target}-"))
    try:
        top = None
        for name, asm_dir, own_refs in ASSEMBLIES:
            top = write_project(out, args.target, args, name, asm_dir, own_refs)
        assert top is not None
        result = subprocess.run(
            ["dotnet", "build", str(top), "-nologo", "-v:q", "-clp:NoSummary", "--disable-build-servers"],
            capture_output=True, text=True, cwd=out)
        seen = set()
        errors = warnings = 0
        banned_by_file: dict[str, int] = {}
        for line in (result.stdout + result.stderr).splitlines():
            m = DIAG.match(line.strip())
            if not m:
                continue
            key = (m["file"], m["line"], m["code"], m["msg"])
            if key in seen:
                continue
            seen.add(key)
            rel = os.path.relpath(m["file"], args.checkout)
            if m["code"] == "RS0030":
                banned_by_file[rel] = banned_by_file.get(rel, 0) + 1
            if m["sev"] == "error":
                errors += 1
                print(f"{rel}:{m['line']}: {m['code']}: {m['msg']}")
            elif m["code"] == "RS0030":
                warnings += 1
                print(f"{rel}:{m['line']}: {m['code']} (долг): {m['msg']}")
        if result.returncode != 0 and errors == 0:
            print(result.stdout[-4000:], result.stderr[-4000:], sep="\n")
        print(f"\n[{args.target}] ошибок: {errors}, запрещённых API (предупреждения): {warnings}")
        if banned_by_file:
            print("запрещённые API по файлам:")
            for rel, count in sorted(banned_by_file.items(), key=lambda kv: -kv[1]):
                print(f"  {count:4d}  {rel}")
        return 0 if result.returncode == 0 and errors == 0 else 1
    finally:
        if not args.keep and args.out is None:
            shutil.rmtree(out, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Windows Unity CLI adapter for the canonical, validated WebGL content package.

Builds in the existing checkout, preserves dirty inputs and never publishes.
The same inventory, serialized-payload and package gates as macOS are mandatory.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

import build_release_windows as windows
import build_webgl_content as shared
import content


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--package', type=Path, required=True)
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--unity', type=Path)
    args = parser.parse_args()
    if sys.platform != 'win32':
        raise RuntimeError('Use the macOS builder on macOS')
    project = Path(__file__).resolve().parents[1]
    output, package = args.output.resolve(), args.package.resolve()
    if windows.unity_processes():
        raise RuntimeError('Unity is already running; do not interrupt its owner')
    unity = windows.find_unity(args.unity)
    module = unity.parent/'Data/PlaybackEngines/WebGLSupport'
    if not (module/'UnityEditor.WebGL.Extensions.dll').is_file() or not (module/'BuildTools').is_dir():
        raise RuntimeError('WebGL Build Support is missing')
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=project, text=True).strip()
    if sha != args.source_sha:
        raise RuntimeError(f'HEAD {sha} differs from requested {args.source_sha}')
    for path in (output, package):
        if path.exists() and any(path.iterdir()):
            raise RuntimeError(f'Refuse to overwrite existing output: {path}')
    if shutil.disk_usage(project).free < 8*1024**3:
        raise RuntimeError('Less than 8 GiB free for build and package')
    owner = project.parent/'UNITY_OWNER'
    owner_text = 'codex-msi-visual-bugs-20261011 WebGL content release 20261011-1704\n'
    # Exclusive creation prevents accidental replacement of another editor lease.
    with owner.open('x', encoding='utf-8') as stream:
        stream.write(owner_text)
    receipt = None
    try:
        output.mkdir(parents=True, exist_ok=True)
        content.REPO = project
        content.validate_lfs_payloads()
        dirty = subprocess.check_output(['git','-c','core.safecrlf=false','diff','--name-only','-z'],cwd=project).decode().split('\0')
        inputs = []
        for relative in filter(None, dirty):
            path = project/relative
            inputs.append({'path':relative,'sha256':hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None})
        receipt = {'sourceCommit':sha,'peopleCatalog':'primal-v1','platform':'WebGL',
            'runtimeProfile':content.RUNTIME_PROFILE,'startedUtc':datetime.now(timezone.utc).isoformat(),
            'publicationPerformed':False,'buildAuthorized':True,'builderPlatform':'Windows',
            'builderSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            'dirtyTrackedInputs':inputs,'unityExecutable':str(unity)}
        shared.write_json(output/'build-receipt.json',receipt)
        command = [str(unity),'-batchmode','-quit','-nographics','-projectPath',str(project),
            '-buildTarget','WebGL','-executeMethod','AtomicContentBatchBuild.BuildPreparedWebGL',
            '-content-platform','WebGL','-content-people-catalog','primal-v1',
            '-content-output',str(output),'-logFile',str(output/'unity.log')]
        with windows.temporary_unity_auto_refresh():
            process = subprocess.Popen(command,cwd=project,creationflags=subprocess.CREATE_NO_WINDOW)
            shared.write_json(output/'process.json',{'pid':process.pid,'startedUtc':receipt['startedUtc'],'project':str(project)})
            print(f'WebGL content Unity PID {process.pid}; log {output / "unity.log"}',flush=True)
            receipt['editorExitCode'] = process.wait()
        receipt['finishedUtc'] = datetime.now(timezone.utc).isoformat()
        shared.write_json(output/'build-receipt.json',receipt)
        if receipt['editorExitCode']:
            raise RuntimeError(f'Unity failed with exit {receipt["editorExitCode"]}; inspect unity.log')
        content.add_raw_candidates(output,'WebGL',content.RUNTIME_PROFILE)
        shared.package(output,package,project/'Assets/HexLiveContent/People/catalog.json')
    finally:
        # Never release the editor lease while a Unity process is still alive.
        if not windows.unity_processes() and owner.is_file() and owner.read_text()==owner_text:
            owner.unlink()


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        print(f'[webgl-content-windows] {error}',file=sys.stderr)
        raise SystemExit(1)

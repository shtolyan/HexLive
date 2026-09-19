"""Package the updater and immutable Windows archive for the release API."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile


def archive_player(staging: Path, manifest: dict, protocol: int) -> None:
    player = staging / "HexLive"
    if not (player / "HexLive.exe").is_file() or not (player / "HexLiveUpdater.exe").is_file():
        raise RuntimeError("Windows update requires both Player and bundled updater")
    archive = staging / "HexLive-Windows.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        for path in sorted(player.rglob("*")):
            if path.is_symlink():
                raise RuntimeError("Player archive must not contain links")
            if path.is_file():
                output.write(path, path.relative_to(staging).as_posix())
    digest = hashlib.sha256()
    with archive.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    manifest["playerRelease"] = {
        "protocolVersion": protocol,
        "archiveSha256": digest.hexdigest(), "archiveSize": archive.stat().st_size,
        "archiveFileName": archive.name, "executable": "HexLive/HexLive.exe",
        "runtimeProfile": "unity6000-content1",
        "assetApi": "https://vmi3529459.contaboserver.net/api/assets/v1",
        "gameServer": "wss://vmi3529459.contaboserver.net/watch",
    }
    (staging / "build-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def package_update(repo: Path, staging: Path, manifest: dict) -> None:
    updater = staging / "updater-build"
    subprocess.run(["dotnet", "publish", str(repo / "Launcher/HexLive.Launcher/HexLive.Launcher.csproj"),
                    "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", str(updater)],
                   cwd=repo, check=True)
    shutil.copy2(updater / "HexLiveInstaller.exe", staging / "HexLive/HexLiveUpdater.exe")
    wire = (repo / "Assets/HexLive/Simulation/Wire/WireProtocol.cs").read_text(encoding="utf-8")
    protocol = int(re.search(r"public const int ProtocolVersion\s*=\s*(\d+)", wire)[1])
    archive_player(staging, manifest, protocol)

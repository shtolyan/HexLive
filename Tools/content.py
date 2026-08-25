#!/usr/bin/env python3
"""Build and publish one §152 content object without an Addressables catalog."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import uuid


REPO = Path(__file__).resolve().parents[1]
RUNTIME_PROFILE = "unity6000-content1"
PLATFORMS = ("StandaloneOSX", "StandaloneWindows64")
TYPES = {
    "wear", "actor", "hair", "prosthetic", "object", "building",
    "mob", "ui", "vfx", "audio", "config",
}
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
SAFE_VARIANT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
SAFE_REMOTE_ROOT = re.compile(r"^/[A-Za-z0-9._/-]+$")


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(
        description="Atomic independent HexLive content build/publish (§152)")
    commands = root.add_subparsers(dest="command", required=True)

    build = commands.add_parser("build", help="build exactly one self-contained AssetBundle")
    identity(build)
    build.add_argument("--platform", choices=PLATFORMS, required=True)
    build.add_argument("--runtime-profile", default=RUNTIME_PROFILE)
    build.add_argument("--output", type=Path)
    build.add_argument("--unity", type=Path, help="Unity executable; normally auto-detected")
    build.add_argument("--main", help="explicit Unity asset path")
    build.add_argument("--metadata", help="explicit metadata Unity asset path")
    build.add_argument("--icon", help="explicit icon Unity asset path (embedded in this bundle)")

    raw = commands.add_parser("file", help="make a candidate for one raw file payload")
    identity(raw)
    raw.add_argument("--platform", choices=PLATFORMS, required=True)
    raw.add_argument("--runtime-profile", default=RUNTIME_PROFILE)
    raw.add_argument("--payload", type=Path, required=True)
    raw.add_argument("--metadata-json", type=Path)
    raw.add_argument(
        "--attachment", action="append", default=[], metavar="NAME=PATH",
        help="immutable sidecar owned by this revision, e.g. vis=voice.vis")
    raw.add_argument("--output", type=Path)

    publish = commands.add_parser(
        "publish", help="stage and atomically promote one object (local or over SSH)")
    publish.add_argument("--candidate", type=Path, action="append", required=True)
    publish.add_argument("--required-platform", choices=PLATFORMS, action="append")
    destination = publish.add_mutually_exclusive_group(required=True)
    destination.add_argument("--host", help="SSH host, for example hexlive-server")
    destination.add_argument("--asset-root", type=Path, help="local server asset root")
    publish.add_argument("--remote-root", default="/var/lib/hexlive/assets")
    publish.add_argument(
        "--server-dll", default="/opt/hexlive/current/HexLive.Server.dll",
        help="remote installed server DLL, or a local DLL with --asset-root")

    return root


def identity(command: argparse.ArgumentParser) -> None:
    command.add_argument("--type", choices=sorted(TYPES), required=True)
    command.add_argument("--id", required=True)


def validate_identity(content_type: str, content_id: str, profile: str | None = None) -> None:
    if content_type not in TYPES or not SAFE_ID.fullmatch(content_id):
        raise ValueError(f"invalid content identity {content_type}/{content_id}")
    if profile is not None and not SAFE_VARIANT.fullmatch(profile):
        raise ValueError(f"invalid runtime profile {profile!r}")


def find_unity(explicit: Path | None) -> Path:
    if explicit:
        executable = explicit.expanduser().resolve()
        if executable.is_file():
            return executable
        raise FileNotFoundError(f"Unity executable does not exist: {executable}")

    environment = os.environ.get("UNITY_PATH")
    if environment:
        return find_unity(Path(environment))

    version_file = REPO / "ProjectSettings" / "ProjectVersion.txt"
    match = re.search(r"^m_EditorVersion:\s*(\S+)", version_file.read_text(), re.MULTILINE)
    if not match:
        raise RuntimeError(f"cannot read Unity version from {version_file}")
    version = match.group(1)
    candidates = [
        Path(f"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity"),
        Path(f"C:/Program Files/Unity/Hub/Editor/{version}/Editor/Unity.exe"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(
        f"Unity {version} was not found; pass --unity or set UNITY_PATH")


def build_bundle(args: argparse.Namespace) -> int:
    validate_identity(args.type, args.id, args.runtime_profile)
    output = (args.output or (
        REPO / "Build" / "AtomicContent" / args.type / args.id / args.platform)).resolve()
    output.mkdir(parents=True, exist_ok=True)
    unity = find_unity(args.unity)
    log = output / "unity.log"
    command = [
        str(unity), "-batchmode", "-quit", "-nographics", "-refresh", "-accept-apiupdate",
        "-projectPath", str(REPO),
        "-buildTarget", args.platform,
        "-executeMethod", "AtomicContentBatchBuild.Build",
        "-content-type", args.type,
        "-content-id", args.id,
        "-content-platform", args.platform,
        "-content-runtime-profile", args.runtime_profile,
        "-content-output", str(output),
        "-logFile", str(log),
    ]
    for flag, value in (
        ("-content-main", args.main),
        ("-content-metadata", args.metadata),
        ("-content-icon", args.icon),
    ):
        if value:
            command.extend((flag, value))

    print(f"[content] building only {args.type}/{args.id} for {args.platform}")
    result = subprocess.run(command, cwd=REPO, check=False)
    candidate = output / "candidate.json"
    if result.returncode or not candidate.is_file():
        tail = log.read_text(errors="replace").splitlines()[-80:] if log.is_file() else []
        if tail:
            print("\n".join(tail), file=sys.stderr)
        raise RuntimeError(
            f"Unity atomic build failed ({result.returncode}); full log: {log}")
    value = load_candidate(candidate)
    variant = value["variants"][0]
    print(
        f"[content] candidate {candidate}\n"
        f"[content] sha256={variant['sha256']} size={variant['size']} "
        f"icon={variant.get('iconAsset', 'none')} dependencies=0")
    return 0


def raw_file(args: argparse.Namespace) -> int:
    validate_identity(args.type, args.id, args.runtime_profile)
    payload = args.payload.expanduser().resolve()
    if not payload.is_file():
        raise FileNotFoundError(payload)
    metadata = {}
    if args.metadata_json:
        metadata = json.loads(args.metadata_json.read_text())
        if not isinstance(metadata, dict):
            raise ValueError("--metadata-json must contain one JSON object")
    digest, size = hash_file(payload)
    attachments = []
    names = set()
    for value in args.attachment:
        if "=" not in value:
            raise ValueError("--attachment must be NAME=PATH")
        name, source_value = value.split("=", 1)
        if not SAFE_VARIANT.fullmatch(name) or name in names:
            raise ValueError(f"invalid or duplicate attachment name {name!r}")
        names.add(name)
        source = Path(source_value).expanduser().resolve()
        attachment_sha, attachment_size = hash_file(source)
        attachments.append({
            "name": name,
            "sha256": attachment_sha,
            "size": attachment_size,
            "stagedPath": str(source),
        })
    candidate = {
        "type": args.type,
        "id": args.id,
        "state": "active",
        "metadata": metadata,
        "variants": [{
            "platform": args.platform,
            "runtimeProfile": args.runtime_profile,
            "sha256": digest,
            "size": size,
            "payloadType": "file",
            "entryAsset": "main",
            "stagedPath": str(payload),
            "attachments": attachments,
        }],
    }
    output = (args.output or (
        REPO / "Build" / "AtomicContent" / args.type / args.id /
        args.platform / "candidate.json")).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(candidate, ensure_ascii=False, indent=2) + "\n")
    print(f"[content] raw candidate {output}: sha256={digest} size={size}")
    return 0


def load_candidate(path: Path) -> dict:
    path = path.expanduser().resolve()
    value = json.loads(path.read_text())
    validate_identity(value.get("type", ""), value.get("id", ""))
    if value.get("state") not in {"active", "retired"}:
        raise ValueError(f"{path}: state must be active or retired")
    if not isinstance(value.get("metadata"), dict) or not isinstance(value.get("variants"), list):
        raise ValueError(f"{path}: invalid candidate structure")
    for variant in value["variants"]:
        if variant.get("platform") not in PLATFORMS:
            raise ValueError(f"{path}: unsupported platform")
        if not SAFE_VARIANT.fullmatch(variant.get("runtimeProfile", "")):
            raise ValueError(f"{path}: invalid runtime profile")
        source = Path(variant.get("stagedPath", "")).expanduser().resolve()
        digest, size = hash_file(source)
        if digest != variant.get("sha256") or size != variant.get("size"):
            raise ValueError(f"{path}: payload fails SHA-256/size validation")
        variant["stagedPath"] = str(source)
        attachment_names = set()
        for attachment in variant.get("attachments", []):
            name = attachment.get("name", "")
            if not SAFE_VARIANT.fullmatch(name) or name in attachment_names:
                raise ValueError(f"{path}: invalid or duplicate attachment {name!r}")
            attachment_names.add(name)
            attachment_source = Path(
                attachment.get("stagedPath", "")).expanduser().resolve()
            attachment_digest, attachment_size = hash_file(attachment_source)
            if (attachment_digest != attachment.get("sha256") or
                    attachment_size != attachment.get("size")):
                raise ValueError(f"{path}: attachment {name} fails SHA-256/size validation")
            attachment["stagedPath"] = str(attachment_source)
    return value


def merge_candidates(paths: list[Path], required_platforms: list[str] | None) -> dict:
    candidates = [load_candidate(path) for path in paths]
    first = candidates[0]
    merged = {
        "type": first["type"],
        "id": first["id"],
        "state": first["state"],
        "metadata": first["metadata"],
        "variants": [],
    }
    seen = set()
    for candidate in candidates:
        for field in ("type", "id", "state", "metadata"):
            if candidate[field] != first[field]:
                raise ValueError(f"candidate mismatch in {field}")
        for variant in candidate["variants"]:
            key = (variant["platform"], variant["runtimeProfile"])
            if key in seen:
                raise ValueError(f"duplicate candidate variant {key[0]}/{key[1]}")
            seen.add(key)
            merged["variants"].append(variant)

    required = required_platforms or list(PLATFORMS)
    if merged["state"] == "active":
        profiles = {variant["runtimeProfile"] for variant in merged["variants"]}
        for profile in profiles:
            present = {
                variant["platform"] for variant in merged["variants"]
                if variant["runtimeProfile"] == profile
            }
            missing = set(required) - present
            if missing:
                raise ValueError(
                    f"profile {profile} is missing required platforms: {', '.join(sorted(missing))}")
    return merged


def publish(args: argparse.Namespace) -> int:
    candidate = merge_candidates(args.candidate, args.required_platform)
    if args.host:
        return publish_remote(candidate, args)
    return publish_local(candidate, args)


def publish_local(candidate: dict, args: argparse.Namespace) -> int:
    root = args.asset_root.expanduser().resolve()
    stage = root / "staging" / uuid.uuid4().hex
    stage.mkdir(parents=True, exist_ok=False)
    staged = stage_candidate(candidate, stage)
    candidate_path = stage / "candidate.json"
    candidate_path.write_text(json.dumps(staged, ensure_ascii=False, indent=2) + "\n")

    server_dll = Path(args.server_dll).expanduser()
    if server_dll.is_file():
        command = ["dotnet", str(server_dll), "--asset-root", str(root),
                   "--publish-candidate", str(candidate_path)]
    else:
        command = [
            "dotnet", "run", "--project", str(REPO / "Server/HexLive.Server"), "--",
            "--asset-root", str(root), "--publish-candidate", str(candidate_path),
        ]
    return subprocess.run(command, cwd=REPO, check=False).returncode


def publish_remote(candidate: dict, args: argparse.Namespace) -> int:
    if not SAFE_REMOTE_ROOT.fullmatch(args.remote_root):
        raise ValueError("--remote-root must be an absolute safe server path")
    token = uuid.uuid4().hex
    remote_stage = f"{args.remote_root.rstrip('/')}/staging/{token}"
    run_checked(["ssh", args.host, "mkdir", "-p", "--", remote_stage])

    staged = json.loads(json.dumps(candidate))
    uploaded = set()
    for variant in staged["variants"]:
        upload_remote_blob(variant, uploaded, remote_stage, args.host)
        for attachment in variant.get("attachments", []):
            upload_remote_blob(attachment, uploaded, remote_stage, args.host)

    with tempfile.TemporaryDirectory(prefix="hexlive-content-") as temporary:
        local_candidate = Path(temporary) / "candidate.json"
        local_candidate.write_text(json.dumps(staged, ensure_ascii=False, indent=2) + "\n")
        remote_candidate = f"{remote_stage}/candidate.json"
        run_checked(["scp", str(local_candidate), f"{args.host}:{remote_candidate}"])

    command = [
        "ssh", args.host, "dotnet", args.server_dll,
        "--asset-root", args.remote_root,
        "--publish-candidate", remote_candidate,
    ]
    return subprocess.run(command, check=False).returncode


def stage_candidate(candidate: dict, stage: Path) -> dict:
    staged = json.loads(json.dumps(candidate))
    installed = set()
    for variant in staged["variants"]:
        stage_blob(variant, installed, stage)
        for attachment in variant.get("attachments", []):
            stage_blob(attachment, installed, stage)
    return staged


def upload_remote_blob(value: dict, uploaded: set[str], remote_stage: str, host: str) -> None:
    source = Path(value["stagedPath"])
    remote_payload = f"{remote_stage}/{value['sha256']}"
    if value["sha256"] not in uploaded:
        run_checked(["scp", str(source), f"{host}:{remote_payload}"])
        uploaded.add(value["sha256"])
    value["stagedPath"] = remote_payload


def stage_blob(value: dict, installed: set[str], stage: Path) -> None:
    source = Path(value["stagedPath"])
    destination = stage / value["sha256"]
    if value["sha256"] not in installed:
        shutil.copyfile(source, destination)
        installed.add(value["sha256"])
    value["stagedPath"] = str(destination)


def hash_file(path: Path) -> tuple[str, int]:
    if not path.is_file():
        raise FileNotFoundError(path)
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
            size += len(chunk)
    return digest.hexdigest(), size


def run_checked(command: list[str]) -> None:
    subprocess.run(command, check=True)


def main() -> int:
    args = parser().parse_args()
    try:
        if args.command == "build":
            return build_bundle(args)
        if args.command == "file":
            return raw_file(args)
        if args.command == "publish":
            return publish(args)
        raise AssertionError(args.command)
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        print(f"[content] error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

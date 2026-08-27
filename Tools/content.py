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
import tarfile
import tempfile
import uuid


REPO = Path(__file__).resolve().parents[1]
RUNTIME_PROFILE = "unity6000-content1"
PLATFORMS = ("StandaloneOSX", "StandaloneWindows64")
TYPES = {
    "wear", "actor", "hair", "prosthetic", "object", "building",
    "mob", "vfx", "audio", "config",
}
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$")
SAFE_VARIANT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
SAFE_REMOTE_ROOT = re.compile(r"^/[A-Za-z0-9._/-]+$")
SAFE_REMOTE_USER = re.compile(r"^[a-z_][a-z0-9_-]{0,31}$")
LFS_POINTER_PREFIX = b"version https://git-lfs.github.com/spec/v1\n"


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

    build_all = commands.add_parser(
        "build-all",
        help="discover and independently build every authored content object for one platform")
    build_all.add_argument("--platform", choices=PLATFORMS, required=True)
    build_all.add_argument("--runtime-profile", default=RUNTIME_PROFILE)
    build_all.add_argument("--output", type=Path)
    build_all.add_argument("--unity", type=Path, help="Unity executable; normally auto-detected")

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
    publish.add_argument("--remote-user", default="hexlive")
    publish.add_argument(
        "--retain-current-variants", action="store_true",
        help="add platform variants while retaining verified server variants with identical metadata")
    publish.add_argument(
        "--server-dll", default="/opt/hexlive/current/HexLive.Server",
        help="remote self-contained server executable/DLL, or a local executable/DLL")

    publish_all = commands.add_parser(
        "publish-all",
        help="publish every candidate below one or more build roots, one object at a time")
    publish_all.add_argument("--input", type=Path, action="append", required=True)
    publish_all.add_argument("--required-platform", choices=PLATFORMS, action="append")
    all_destination = publish_all.add_mutually_exclusive_group(required=True)
    all_destination.add_argument("--host")
    all_destination.add_argument("--asset-root", type=Path)
    publish_all.add_argument("--remote-root", default="/var/lib/hexlive/assets")
    publish_all.add_argument("--remote-user", default="hexlive")
    publish_all.add_argument(
        "--retain-current-variants", action="store_true",
        help="add platform variants while retaining verified server variants with identical metadata")
    publish_all.add_argument(
        "--server-dll", default="/opt/hexlive/current/HexLive.Server")

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


def validate_lfs_payloads() -> None:
    """Refuse to build bundles from unsmudged Git LFS pointer stubs."""
    result = subprocess.run(
        ["git", "lfs", "ls-files", "-n"],
        cwd=REPO,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        details = result.stderr.decode(errors="replace").strip()
        raise RuntimeError(details or "git lfs ls-files failed")

    pointers = []
    for raw_path in result.stdout.splitlines():
        if not raw_path:
            continue
        path = REPO / os.fsdecode(raw_path)
        if not path.is_file():
            continue
        with path.open("rb") as stream:
            if stream.read(len(LFS_POINTER_PREFIX)) == LFS_POINTER_PREFIX:
                pointers.append(path.relative_to(REPO).as_posix())
                if len(pointers) == 20:
                    break
    if pointers:
        preview = "\n".join(f"  {path}" for path in pointers)
        raise RuntimeError(
            "Git LFS payloads are not checked out; refusing to build invalid content. "
            "Run 'git lfs checkout' (and 'git lfs pull' if objects are missing).\n" + preview)


def build_bundle(args: argparse.Namespace) -> int:
    validate_identity(args.type, args.id, args.runtime_profile)
    validate_lfs_payloads()
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


def build_all(args: argparse.Namespace) -> int:
    validate_identity("config", "inventory", args.runtime_profile)
    validate_lfs_payloads()
    output = (args.output or (
        REPO / "Build" / "AtomicContent" / "all" / args.platform)).resolve()
    output.mkdir(parents=True, exist_ok=True)
    unity = find_unity(args.unity)
    log = output / "unity.log"
    command = [
        str(unity), "-batchmode", "-quit", "-nographics", "-refresh", "-accept-apiupdate",
        "-projectPath", str(REPO),
        "-buildTarget", args.platform,
        "-executeMethod", "AtomicContentBatchBuild.BuildAll",
        "-content-platform", args.platform,
        "-content-runtime-profile", args.runtime_profile,
        "-content-output", str(output),
        "-logFile", str(log),
    ]
    print(f"[content] independently building the full authored inventory for {args.platform}")
    result = subprocess.run(command, cwd=REPO, check=False)
    summary = output / "build-all-summary.json"
    if result.returncode or not summary.is_file():
        tail = log.read_text(errors="replace").splitlines()[-120:] if log.is_file() else []
        if tail:
            print("\n".join(tail), file=sys.stderr)
        raise RuntimeError(
            f"Unity full atomic build failed ({result.returncode}); full log: {log}")

    add_raw_candidates(output, args.platform, args.runtime_profile)
    value = json.loads(summary.read_text())
    candidates = list(output.rglob("candidate.json"))
    print(
        f"[content] built {value.get('built', 0)} bundles and "
        f"{len(candidates) - int(value.get('built', 0))} raw payload records; "
        f"candidates={len(candidates)}")
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


def add_raw_candidates(output: Path, platform: str, profile: str) -> None:
    audio_root = REPO / "Assets/HexLiveContent/AudioSource"
    media_root = audio_root / "HexLive"
    for payload in sorted((media_root / "Music").glob("*")):
        if payload.suffix.lower() not in {".mp3", ".ogg", ".wav"}:
            continue
        write_raw_candidate(
            output, "audio", payload.stem, platform, profile, payload,
            {"kind": "music", "trackId": payload.stem})

    sfx_root = media_root / "Sfx"
    for payload in sorted(sfx_root.rglob("*")):
        if payload.suffix.lower() not in {".ogg", ".wav", ".mp3"}:
            continue
        voice = "Voices" in payload.parts
        group = re.sub(r"_[0-9]+$", "", payload.stem)
        attachments = []
        vis = payload.with_suffix(".vis")
        if voice:
            if not vis.is_file():
                raise FileNotFoundError(f"voice payload has no owned .vis attachment: {payload}")
            attachments.append(("vis", vis))
        write_raw_candidate(
            output, "audio", payload.stem, platform, profile, payload,
            {"kind": "voice" if voice else "sfx", "group": group}, attachments)

    for payload in sorted(audio_root.glob("*.bank")):
        content_id = "bank." + payload.stem.lower().replace(".", "-")
        write_raw_candidate(
            output, "audio", content_id, platform, profile, payload,
            {"kind": "bank", "bank": payload.name})

    write_raw_candidate(
        output, "config", "simdata", platform, profile,
        REPO / "SimData/simdata.json", {"kind": "simdata"})


def write_raw_candidate(
        output: Path, content_type: str, content_id: str, platform: str, profile: str,
        payload: Path, metadata: dict, attachments: list[tuple[str, Path]] | None = None) -> None:
    validate_identity(content_type, content_id, profile)
    digest, size = hash_file(payload)
    attachment_values = []
    for name, path in attachments or []:
        attachment_digest, attachment_size = hash_file(path)
        attachment_values.append({
            "name": name,
            "sha256": attachment_digest,
            "size": attachment_size,
            "stagedPath": str(path.resolve()),
        })
    candidate = {
        "type": content_type,
        "id": content_id,
        "state": "active",
        "metadata": metadata,
        "variants": [{
            "platform": platform,
            "runtimeProfile": profile,
            "sha256": digest,
            "size": size,
            "payloadType": "file",
            "entryAsset": "main",
            "stagedPath": str(payload.resolve()),
            "attachments": attachment_values,
        }],
    }
    candidate_path = output / content_type / content_id / platform / "candidate.json"
    candidate_path.parent.mkdir(parents=True, exist_ok=True)
    candidate_path.write_text(json.dumps(candidate, ensure_ascii=False, indent=2) + "\n")


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


def publish_all(args: argparse.Namespace) -> int:
    grouped: dict[tuple[str, str], list[Path]] = {}
    for root_value in args.input:
        root = root_value.expanduser().resolve()
        if not root.is_dir():
            raise FileNotFoundError(root)
        for path in sorted(root.rglob("candidate.json")):
            value = json.loads(path.read_text())
            validate_identity(value.get("type", ""), value.get("id", ""))
            grouped.setdefault((value["type"], value["id"]), []).append(path)
    if not grouped:
        raise ValueError("--input roots contain no candidate.json files")

    candidates = [
        merge_candidates(paths, args.required_platform)
        for _, paths in sorted(grouped.items())
    ]
    print(f"[content] validated {len(candidates)} independent objects for publication")
    if args.host:
        return publish_all_remote(candidates, args)

    for index, candidate in enumerate(candidates, 1):
        code = publish_local(candidate, args)
        if code:
            return code
        print(f"[content] published {index}/{len(candidates)} locally")
    return 0


def publish_local(candidate: dict, args: argparse.Namespace) -> int:
    root = args.asset_root.expanduser().resolve()
    stage = root / "staging" / uuid.uuid4().hex
    stage.mkdir(parents=True, exist_ok=False)
    staged = stage_candidate(candidate, stage)
    candidate_path = stage / "candidate.json"
    candidate_path.write_text(json.dumps(staged, ensure_ascii=False, indent=2) + "\n")

    server_dll = Path(args.server_dll).expanduser()
    if server_dll.is_file():
        command = server_invocation(str(server_dll)) + [
            "--asset-root", str(root), "--publish-candidate", str(candidate_path)]
    else:
        command = [
            "dotnet", "run", "--project", str(REPO / "Server/HexLive.Server"), "--",
            "--asset-root", str(root), "--publish-candidate", str(candidate_path),
        ]
    if args.retain_current_variants:
        command.append("--retain-current-asset-variants")
    return subprocess.run(command, cwd=REPO, check=False).returncode


def publish_remote(candidate: dict, args: argparse.Namespace) -> int:
    validate_remote_destination(args)
    token = uuid.uuid4().hex
    remote_stage = f"{args.remote_root.rstrip('/')}/staging/{token}"
    prepare_remote_stage(args, remote_stage)

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
        "ssh", args.host, *remote_server_invocation(args),
        "--asset-root", args.remote_root,
        "--publish-candidate", remote_candidate,
    ]
    if args.retain_current_variants:
        command.append("--retain-current-asset-variants")
    return subprocess.run(command, check=False).returncode


def publish_all_remote(candidates: list[dict], args: argparse.Namespace) -> int:
    validate_remote_destination(args)
    token = uuid.uuid4().hex
    remote_stage = f"{args.remote_root.rstrip('/')}/staging/bootstrap-{token}"
    remote_archive = f"/tmp/hexlive-content-{token}.tar.gz"

    with tempfile.TemporaryDirectory(prefix="hexlive-content-batch-") as temporary:
        stage = Path(temporary) / "stage"
        payloads = stage / "payloads"
        candidate_dir = stage / "candidates"
        payloads.mkdir(parents=True)
        candidate_dir.mkdir()
        copied = set()
        for candidate in candidates:
            staged = json.loads(json.dumps(candidate))
            for variant in staged["variants"]:
                batch_stage_value(variant, copied, payloads, remote_stage)
                for attachment in variant.get("attachments", []):
                    batch_stage_value(attachment, copied, payloads, remote_stage)
            name = f"{staged['type']}--{staged['id']}.json"
            (candidate_dir / name).write_text(
                json.dumps(staged, ensure_ascii=False, indent=2) + "\n")

        archive = Path(temporary) / "content.tar.gz"
        with tarfile.open(archive, "w:gz") as stream:
            stream.add(stage, arcname=".")
        print(
            f"[content] batch archive: objects={len(candidates)} "
            f"uniqueBlobs={len(copied)} bytes={archive.stat().st_size}")
        run_checked(["scp", str(archive), f"{args.host}:{remote_archive}"])

    prepare_remote_stage(args, remote_stage)
    run_checked(["ssh", args.host, "tar", "-xzf", remote_archive, "-C", remote_stage])
    command = [
        "ssh", args.host, *remote_server_invocation(args),
        "--asset-root", args.remote_root,
        "--publish-candidates", f"{remote_stage}/candidates",
    ]
    if args.retain_current_variants:
        command.append("--retain-current-asset-variants")
    result = subprocess.run(command, check=False)
    if result.returncode == 0:
        run_checked(["ssh", args.host, "rm", "-f", "--", remote_archive])
        run_checked(["ssh", args.host, "rm", "-rf", "--", remote_stage])
    return result.returncode


def batch_stage_value(value: dict, copied: set[str], payloads: Path, remote_stage: str) -> None:
    source = Path(value["stagedPath"])
    sha256 = value["sha256"]
    destination = payloads / sha256
    if sha256 not in copied:
        shutil.copyfile(source, destination)
        copied.add(sha256)
    value["stagedPath"] = f"{remote_stage}/payloads/{sha256}"


def server_invocation(path: str) -> list[str]:
    return ["dotnet", path] if path.lower().endswith(".dll") else [path]


def validate_remote_destination(args: argparse.Namespace) -> None:
    if not SAFE_REMOTE_ROOT.fullmatch(args.remote_root):
        raise ValueError("--remote-root must be an absolute safe server path")
    if not SAFE_REMOTE_USER.fullmatch(args.remote_user):
        raise ValueError("--remote-user must be a safe Linux account name")


def prepare_remote_stage(args: argparse.Namespace, remote_stage: str) -> None:
    run_checked([
        "ssh", args.host, "install", "-d", "-m", "0750",
        "-o", args.remote_user, "-g", args.remote_user, "--",
        args.remote_root, f"{args.remote_root.rstrip('/')}/staging", remote_stage,
    ])


def remote_server_invocation(args: argparse.Namespace) -> list[str]:
    return [
        "runuser", "-u", args.remote_user, "--",
        *server_invocation(args.server_dll),
    ]


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
        prefix = stream.read(len(LFS_POINTER_PREFIX))
        if prefix == LFS_POINTER_PREFIX:
            raise ValueError(
                f"{path} is a Git LFS pointer, not the immutable payload")
        digest.update(prefix)
        size += len(prefix)
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
        if args.command == "build-all":
            return build_all(args)
        if args.command == "file":
            return raw_file(args)
        if args.command == "publish":
            return publish(args)
        if args.command == "publish-all":
            return publish_all(args)
        raise AssertionError(args.command)
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        print(f"[content] error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

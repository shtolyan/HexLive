#!/usr/bin/env python3
"""Prepare the Primal WebGL inventory through Unity CLI; never deploy a server.

The output package is relocatable: extract below the chosen asset-root/staging,
verify it, and run the server's administrative publisher from that directory.
No publication is performed by this tool.
"""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import contextmanager
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

import content


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def read_json(path: Path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate JSON key {key} in {path}")
            result[key] = value
        return result
    return json.loads(path.read_text(), object_pairs_hook=unique)


@contextmanager
def editor_environment(project: Path, output: Path):
    """Official UPM helper avoids this machine's 30s initial IPC timeout."""
    if sys.platform != "darwin":
        raise RuntimeError("This coordinated builder currently supports macOS only")
    version = re.search(r"^m_EditorVersion:\s*(\S+)",
        (project / "ProjectSettings/ProjectVersion.txt").read_text(), re.M).group(1)
    helper = Path(f"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/Helpers/PackageManager/Server/UnityPackageManager")
    if not helper.is_file():
        raise FileNotFoundError(helper)
    sys.path.insert(0, str(project / "Tools"))
    spec = importlib.util.spec_from_file_location("webgl_refresh", project / "Tools/build_release.py")
    release = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(release)
    name = f"Upm-webgl-content-{os.getpid()}"
    sock = Path(f"/tmp/Unity-{name}.sock")
    with (output / "upm.log").open("w") as log:
        process = subprocess.Popen([str(helper), "server", "-s", str(os.getpid()),
            "--ipc-path", str(sock)], stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 60
            while not sock.exists():
                if process.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError("UPM helper did not become ready")
                time.sleep(.2)
            with release.temporary_unity_auto_refresh():
                yield name
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def build(args):
    project = args.project.resolve()
    output = args.output.resolve()
    owner = project.parent / "UNITY_OWNER"
    if not owner.is_file() or owner.read_text().split()[0] != args.owner:
        raise RuntimeError("Acquire the agreed UNITY_OWNER before building")
    processes = subprocess.check_output(["ps", "-axo", "comm"], text=True)
    if any(line.endswith("/Unity.app/Contents/MacOS/Unity") for line in processes.splitlines()):
        raise RuntimeError("An Editor is still running; do not compete with its owner")
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=project, text=True).strip()
    if sha != args.source_sha:
        raise RuntimeError(f"Execution checkout is {sha}, expected {args.source_sha}")
    if output.exists() and any(output.iterdir()):
        raise RuntimeError("Use a fresh output directory; existing builds are never overwritten")
    output.mkdir(parents=True, exist_ok=True)
    if shutil.disk_usage(output).free < 4 * 1024**3:
        raise RuntimeError("Less than 4 GiB free for the build")
    content.REPO = project
    content.validate_lfs_payloads()
    receipt = {"sourceCommit": sha, "peopleCatalog": "primal-v1", "platform": "WebGL",
        "runtimeProfile": content.RUNTIME_PROFILE, "startedUtc": datetime.now(timezone.utc).isoformat(),
        "publicationPerformed": False, "buildAuthorized": True}
    write_json(output / "build-receipt.json", receipt)
    with editor_environment(project, output) as ipc:
        command = ["unity", "run", str(project), "--timeout", str(args.timeout), "--",
            "-nographics", "-upmIpcPath", ipc, "-buildTarget", "WebGL",
            "-executeMethod", "AtomicContentBatchBuild.BuildPreparedWebGL",
            "-content-platform", "WebGL", "-content-people-catalog", "primal-v1",
            "-content-output", str(output), "-logFile", str(output / "unity.log")]
        result = subprocess.run(command, cwd=project)
    receipt["editorExitCode"] = result.returncode
    receipt["finishedUtc"] = datetime.now(timezone.utc).isoformat()
    write_json(output / "build-receipt.json", receipt)
    if result.returncode:
        raise RuntimeError(f"Unity failed ({result.returncode}); inspect {output / 'unity.log'} locally")
    content.add_raw_candidates(output, "WebGL", content.RUNTIME_PROFILE)
    return package(output, args.package.resolve(), project / "Assets/HexLiveContent/People/catalog.json")


def package(build_root: Path, destination: Path, people_catalog: Path):
    summary = read_json(build_root / "build-all-summary.json")
    inventory = read_json(build_root / "inventory.json")
    receipt = read_json(build_root / "build-receipt.json")
    if summary["failed"] or summary["built"] != summary["discovered"] or not inventory["passed"]:
        raise ValueError("A partial or failed build cannot be packaged")
    if receipt.get("editorExitCode") != 0 or receipt.get("platform") != "WebGL":
        raise ValueError("Missing successful WebGL receipt")
    candidates = []
    seen = set()
    for path in sorted(build_root.rglob("candidate.json")):
        read_json(path)  # reject duplicate keys before the canonical validator
        value = content.load_candidate(path)
        key = (value["type"], value["id"])
        if key in seen:
            raise ValueError(f"Duplicate object {key}")
        seen.add(key)
        if len(value["variants"]) != 1 or value["variants"][0]["platform"] != "WebGL":
            raise ValueError(f"Non-WebGL variant in {key}")
        if value["variants"][0]["runtimeProfile"] != content.RUNTIME_PROFILE:
            raise ValueError(f"Wrong runtime profile in {key}")
        candidates.append(value)
    expected = {(r["type"], r["id"]) for r in inventory["records"]} | {("config", "simdata")}
    if seen != expected:
        raise ValueError(f"Inventory mismatch: missing={expected-seen}, unexpected={seen-expected}")
    people = read_json(people_catalog)["records"]
    people_ids = {(r["type"], r["id"]) for r in people}
    if not people_ids <= seen:
        raise ValueError("Incomplete People inventory")
    for family in ("actor", "wear", "hair"):
        if {k for k in seen if k[0] == family} != {k for k in people_ids if k[0] == family}:
            raise ValueError(f"Legacy {family} leaked into WebGL package")
    if destination.exists():
        raise FileExistsError(destination)
    (destination / "payloads").mkdir(parents=True)
    (destination / "candidates").mkdir()
    blobs = {}
    for value in candidates:
        for variant in value["variants"]:
            for blob in [variant, *variant.get("attachments", [])]:
                digest = blob["sha256"]
                target = destination / "payloads" / digest
                if digest not in blobs:
                    shutil.copyfile(blob["stagedPath"], target)
                    blobs[digest] = blob["size"]
                blob["stagedPath"] = "payloads/" + digest
        write_json(destination / "candidates" / (value["type"] + "--" + value["id"] + ".json"), value)
    manifest = {**receipt, "objects": len(candidates), "counts": dict(sorted(Counter(v["type"] for v in candidates).items())),
        "uniqueBlobs": len(blobs), "payloadBytes": sum(blobs.values()), "blobs": blobs,
        "objectKeys": sorted(t + "/" + i for t, i in seen),
        "productionReady": False, "deploymentDecisionRequired": "Choose Singapore WebGL asset-root and legacy-save wardrobe policy before publication"}
    write_json(destination / "manifest.json", manifest)
    shutil.copyfile(build_root / "inventory.json", destination / "inventory.json")
    shutil.copyfile(build_root / "build-all-summary.json", destination / "build-all-summary.json")
    (destination / "README.md").write_text(
        "# WebGL Primal content — staged, not published\n\n"
        "Each candidate is an independent content object for WebGL/unity6000-content1.\n"
        "This is not a Player or server binary. Audio remains with the WebGL Player.\n"
        "No logs or credentials are included. Existing production roots were not changed.\n\n"
        "Verify with Tools/build_webgl_content.py verify --package <this-directory>.\n"
        "After separate publication approval, extract under the chosen asset-root/staging,\n"
        "change working directory to this package, and use the canonical server CLI\n"
        "--asset-root <chosen-root> --publish-candidates ./candidates.\n"
        "Relative payload paths deliberately resolve from this package directory.\n"
        "Do not merge this new wardrobe blindly into the old registry: the server\n"
        "can otherwise spawn desktop-only clothes. Choose the WebGL world/root and\n"
        "old-save migration first. World assets retain their existing identities.\n")
    verify(destination)
    return manifest


def verify(root: Path):
    root = root.resolve()
    manifest = read_json(root / "manifest.json")
    keys = set()
    blobs = {}
    for path in sorted((root / "candidates").glob("*.json")):
        candidate = read_json(path)
        content.validate_identity(candidate["type"], candidate["id"])
        key = candidate["type"] + "/" + candidate["id"]
        if key in keys:
            raise ValueError(f"Duplicate object {key}")
        keys.add(key)
        for variant in candidate["variants"]:
            if variant["platform"] != "WebGL" or variant["runtimeProfile"] != content.RUNTIME_PROFILE:
                raise ValueError(f"Unexpected variant {key}")
            for blob in [variant, *variant.get("attachments", [])]:
                relative = Path(blob["stagedPath"])
                if relative.is_absolute() or relative.parts != ("payloads", blob["sha256"]):
                    raise ValueError(f"Non-portable payload path {relative}")
                payload = (root / relative).resolve()
                if not payload.is_relative_to(root):
                    raise ValueError("Payload escapes package")
                if blob["sha256"] not in blobs:
                    digest, size = content.hash_file(payload)
                    blobs[digest] = size
                if blobs.get(blob["sha256"]) != blob["size"]:
                    raise ValueError(f"Corrupt payload {relative}")
    if sorted(keys) != manifest["objectKeys"] or len(keys) != manifest["objects"] or blobs != manifest["blobs"]:
        raise ValueError("Package inventory/hash manifest does not match candidates")
    print(f"Verified {len(keys)} WebGL objects / {len(blobs)} blobs / {sum(blobs.values())} bytes")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    b = commands.add_parser("build")
    b.add_argument("--project", type=Path, required=True)
    b.add_argument("--output", type=Path, required=True)
    b.add_argument("--package", type=Path, required=True)
    b.add_argument("--source-sha", required=True)
    b.add_argument("--owner", default="codex-webgl-content")
    b.add_argument("--timeout", type=int, default=14400)
    p = commands.add_parser("package")
    p.add_argument("--input", type=Path, required=True)
    p.add_argument("--package", type=Path, required=True)
    p.add_argument("--people-catalog", type=Path, required=True)
    v = commands.add_parser("verify")
    v.add_argument("--package", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "build":
            build(args)
        elif args.command == "package":
            package(args.input.resolve(), args.package.resolve(), args.people_catalog.resolve())
        else:
            verify(args.package)
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError) as error:
        print(f"[webgl-content] {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

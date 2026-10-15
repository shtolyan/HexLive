#!/usr/bin/env python3
"""Offline source proof for -webgl-reuse-published-audio; never copies or publishes audio."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

AUDIO_ROOT = "Assets/StreamingAssets/HexLive"
SOURCE_EXTENSIONS = {".ogg", ".mp3", ".wav", ".vis"}


def manifest_entries(raw: bytes) -> list[str]:
    files = json.loads(raw.decode("utf-8-sig"))["files"]
    if not isinstance(files, list) or not files:
        raise ValueError("EmptyOrInvalidManifest")
    seen = set()
    for entry in files:
        if not isinstance(entry, str):
            raise ValueError("InvalidManifestPath")
        path = PurePosixPath(entry)
        if (not entry or "\\" in entry or ":" in entry or path.is_absolute() or
                entry != path.as_posix() or ".." in path.parts or
                len(path.parts) < 2 or path.parts[0] not in {"Sfx", "Music"} or
                path.suffix not in {".fsb", ".vis"} or entry in seen):
            raise ValueError("InvalidManifestPath")
        seen.add(entry)
    return files


def source_for(entry: str, sources: set[str]) -> str:
    stem = AUDIO_ROOT + "/" + entry
    candidates = [stem[:-4] + ext for ext in (".ogg", ".mp3", ".wav")] if stem.endswith(".fsb") else [stem]
    matches = [p for p in candidates if p in sources]
    if len(matches) != 1:
        raise ValueError("MissingOrAmbiguousSource: " + entry)
    return matches[0]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def committed_digest(blob: bytes) -> str:
    if blob.startswith(b"version https://git-lfs.github.com/spec/v1\n"):
        match = re.search(rb"^oid sha256:([0-9a-f]{64})$", blob, re.MULTILINE)
        if not match:
            raise ValueError("InvalidLfsPointer")
        return match[1].decode("ascii")
    return hashlib.sha256(blob).hexdigest()


def verify(checkout: Path, commit: str, manifest: bytes) -> dict:
    checkout = checkout.resolve()
    def git(*args: str) -> bytes:
        return subprocess.check_output(["git", "-C", str(checkout), *args], stderr=subprocess.DEVNULL)
    commit = git("rev-parse", "--verify", "--end-of-options", commit + "^{commit}").decode().strip()
    entries = manifest_entries(manifest)
    tree = {}
    for record in filter(None, git("ls-tree", "-r", "-z", commit, "--", AUDIO_ROOT + "/Sfx", AUDIO_ROOT + "/Music").split(b"\0")):
        metadata, raw_path = record.split(b"\t", 1)
        mode, kind, oid = metadata.decode().split()
        path = raw_path.decode("utf-8")
        if PurePosixPath(path).suffix.lower() not in SOURCE_EXTENSIONS:
            continue
        if kind != "blob" or mode not in {"100644", "100755"}:
            raise ValueError("InvalidSourceType")
        tree[path] = oid
    if not tree:
        raise ValueError("NoCommittedAudioSources")
    current = set()
    for category in ("Sfx", "Music"):
        for path in (checkout / AUDIO_ROOT / category).rglob("*"):
            if path.is_file() and path.suffix.lower() in SOURCE_EXTENSIONS:
                if not path.resolve().is_relative_to(checkout / AUDIO_ROOT):
                    raise ValueError("SourceOutsideAudioTree")
                current.add(path.relative_to(checkout).as_posix())
    if current != set(tree):
        raise ValueError("SourceTreeChanged")
    mapping = [(entry, source_for(entry, current)) for entry in entries]
    hashes = {}
    process = subprocess.Popen(["git", "-C", str(checkout), "cat-file", "--batch"],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    try:
        for path, oid in sorted(tree.items()):
            process.stdin.write((oid + "\n").encode("ascii")); process.stdin.flush()
            header = process.stdout.readline().split()
            if len(header) != 3 or header[1] != b"blob":
                raise ValueError("InvalidGitBlob")
            blob = process.stdout.read(int(header[2]))
            if process.stdout.read(1) != b"\n":
                raise ValueError("TruncatedGitBlob")
            digest = sha256_file(checkout / path)
            if digest != committed_digest(blob):
                raise ValueError("SourceChanged: " + path)
            hashes[path] = digest
        process.stdin.close()
        if process.wait() != 0:
            raise ValueError("GitBlobReadFailed")
    finally:
        if process.poll() is None:
            process.kill(); process.wait()
        if not process.stdin.closed:
            process.stdin.close()
        process.stdout.close()
    return {"passed": True, "publishedSourceCommit": commit,
            "publishedManifestSha256": hashlib.sha256(manifest).hexdigest(),
            "verifiedSourceCount": len(hashes),
            "files": [{"published": entry, "source": path, "sha256": hashes[path]} for entry, path in mapping]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkout", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--published-source-commit", required=True,
                        help="source SHA from the existing published audio release receipt")
    parser.add_argument("--manifest", type=Path, required=True, help="local public web-audio-manifest.json of that release")
    parser.add_argument("--output", type=Path, required=True, help="local audio-source-proof.json, normally in ignored Build/")
    args = parser.parse_args()
    try:
        proof = verify(args.checkout, args.published_source_commit, args.manifest.read_bytes())
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        proof = {"passed": False, "error": str(error) if isinstance(error, ValueError) else type(error).__name__}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
    print("Audio reuse proof:", "PASS" if proof["passed"] else "FAIL", "sources:", proof.get("verifiedSourceCount", 0))
    if not proof["passed"]:
        print("Reason:", proof["error"])
    return 0 if proof["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())

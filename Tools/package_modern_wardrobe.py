#!/usr/bin/env python3
"""Package and verify the Modern WebGL wardrobe for private storage."""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import io
import json
import os
from pathlib import Path
import tarfile
from uuid import uuid4

import content
from build_webgl_content import read_json, require_live_people_source


PLATFORM = "WebGL"


def record_key(record: dict) -> tuple[str, str]:
    kind, identifier = record["type"], record["id"]
    content.validate_identity(kind, identifier)
    return kind, identifier


def check_modern_metadata(record: dict) -> None:
    metadata = record["metadata"]
    if (metadata.get("contentOrigin") != "modern"
            or metadata.get("legacyWardrobe") is not True
            or metadata.get("finalFit") is not True):
        raise ValueError(f"Missing final Modern wardrobe metadata: {record_key(record)}")


def json_bytes(value: dict) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def descriptor_name(kind: str, identifier: str) -> str:
    return f"wardrobe/{kind}/{identifier}.json"


def release_counts(models: int, counts: dict) -> None:
    if (type(models) is not int or models < 1 or set(counts) != {"wear", "config"}
            or any(type(count) is not int or count < 1 for count in counts.values())
            or counts["config"] != models * 2):
        raise ValueError("Invalid curated wardrobe counts")


def check_exclusions(policy: dict, excluded: list, keys: set) -> None:
    if policy.get("schemaVersion") != 1:
        raise ValueError("Unsupported wardrobe exclusion policy")
    entries = policy["excludedModels"]
    expected = {entry["id"]: entry for entry in entries}
    seen = set()
    removed = set()
    for entry in excluded:
        identifier = entry["id"]
        if identifier in seen or expected.get(identifier) != {k: v for k, v in entry.items() if k != "logicalIds"}:
            raise ValueError(f"Unresolved or duplicate wardrobe exclusion {identifier}")
        seen.add(identifier)
        variants = {("wear", item) for item in entry["logicalIds"]}
        if not variants or len(variants) != len(entry["logicalIds"]) or removed.intersection(variants):
            raise ValueError(f"Invalid excluded variant set {identifier}")
        removed.update(variants)
    if len(expected) != len(entries) or seen != expected.keys() or removed.intersection(keys):
        raise ValueError("An excluded model is missing from the receipt or present in the release")


def verified_source(build: Path, people: Path, review_path: Path):
    catalog_path = people / "Wardrobe/release-catalog.json"
    fit_path = people / "Wardrobe/release-fit.json"
    selection_path = people / "Wardrobe/release-selection.json"
    summary = read_json(build / "build-all-summary.json")
    validation = read_json(build / "people-payload-validation.json")
    review = read_json(review_path)
    catalog = read_json(catalog_path)
    fit = read_json(fit_path)
    selection = read_json(selection_path)
    catalog_digest = content.hash_file(catalog_path)[0]
    fit_digest = content.hash_file(fit_path)[0]
    primal_digest = content.hash_file(people / "catalog.json")[0]
    selection_digest = content.hash_file(selection_path)[0]
    models = fit["counts"]["models"]
    counts = {"wear": fit["counts"]["logicalIds"], "config": fit["counts"]["maps"]}
    release_counts(models, counts)
    if (fit.get("passed") is not True or fit.get("catalogPromoted") is not True
            or fit.get("finalCatalog", {}).get("sha") != catalog_digest
            or fit.get("releaseSelectionSHA256") != selection_digest
            or catalog.get("releaseSelectionSHA256") != selection_digest
            or len(fit["rows"]) != models or fit["counts"]["meshes"] != counts["config"]):
        raise ValueError("Missing exact curated fit receipt")
    total = sum(counts.values())
    if (summary.get("contentOrigin") != "modern" or summary.get("stage") != "prepared-only"
            or summary.get("platform") != PLATFORM or summary.get("runtimeProfile") != content.RUNTIME_PROFILE
            or summary.get("discovered") != total or summary.get("built") != total or summary.get("failed") != 0
            or summary.get("catalogSHA256") != catalog_digest or summary.get("fitManifestSHA256") != fit_digest
            or summary.get("primalCatalogSHA256") != primal_digest or summary.get("releaseSelectionSHA256") != selection_digest):
        raise ValueError("Missing complete WebGL build of this Modern catalog")
    expected_coverage = {"definitions": counts["wear"], "uniquePrefabs": models,
                         "uniqueMeshes": counts["config"], "paintMaps": counts["config"]}
    if (review.get("passed") is not True or review.get("validationStage") != "final-fit"
            or review.get("catalogSHA256") != catalog_digest or review.get("fitManifestSHA256") != fit_digest
            or review.get("coverage") != expected_coverage
            or review.get("gallery", {}).get("renderedPngs") != models * 4):
        raise ValueError("Missing gallery review for this final Modern catalog")
    if (catalog.get("stage") != "prepared-only" or catalog.get("contentOrigin") != "modern"
            or catalog.get("gameReady") is not False or catalog.get("bundleBuildAuthorized") is not False):
        raise ValueError("Modern catalog must be staged only")

    keys = [record_key(record) for record in catalog["records"]]
    if len(keys) != len(set(keys)) or Counter(kind for kind, _ in keys) != counts:
        raise ValueError("Incomplete or duplicate Modern wardrobe inventory")
    excluded = fit["excludedModels"]
    check_exclusions(selection, excluded, set(keys))
    active = require_live_people_source(read_json(people / "catalog.json"))
    if set(keys).intersection(map(record_key, active)):
        raise ValueError("Modern and live Primal inventories overlap")
    for record in catalog["records"]:
        check_modern_metadata(record)
    if (validation.get("passed") is not True or validation.get("coexistingBundles", 0) < 2
            or validation.get("rendererPaintMapContracts") != counts["config"]
            or len(validation["objects"]) != total
            or {record_key(r) for r in validation["objects"] if r.get("passed") is True} != set(keys)):
        raise ValueError("Missing serialized payload checks for the complete wardrobe")
    payload_checks = {record_key(row): (row["sha256"], row["size"]) for row in validation["objects"]}
    return catalog_path, selection_path, set(keys), models, counts, excluded, summary, payload_checks


def write_member(archive: tarfile.TarFile, name: str, data: bytes | Path, size: int) -> None:
    member = tarfile.TarInfo(name)
    member.size = size
    member.mode = 0o600
    if isinstance(data, Path):
        with data.open("rb") as source:
            archive.addfile(member, source)
    else:
        archive.addfile(member, io.BytesIO(data))


def package(build: Path, people: Path, review: Path, output: Path) -> dict:
    build = build.resolve()
    catalog_path, selection_path, keys, models, counts, excluded, summary, payload_checks = verified_source(build, people, review)
    entries: dict[str, tuple[bytes | Path, str, int]] = {}
    catalog_data = catalog_path.read_bytes()
    entries["catalog.json"] = (catalog_data, hashlib.sha256(catalog_data).hexdigest(), len(catalog_data))
    selection_data = selection_path.read_bytes()
    entries["selection.json"] = (selection_data, hashlib.sha256(selection_data).hexdigest(), len(selection_data))
    seen = set()
    for path in sorted(build.rglob("candidate.json")):
        read_json(path)
        record = content.load_candidate(path)
        key = record_key(record)
        if key not in keys or key in seen:
            raise ValueError(f"Unexpected or duplicate Modern bundle: {key}")
        seen.add(key)
        check_modern_metadata(record)
        if len(record["variants"]) != 1:
            raise ValueError(f"Expected one WebGL variant for {key}")
        variant = record["variants"][0]
        blob = Path(variant["stagedPath"])
        if (variant["platform"] != PLATFORM or variant["runtimeProfile"] != content.RUNTIME_PROFILE
                or variant.get("attachments") or not blob.is_relative_to(build)):
            raise ValueError(f"Invalid Modern payload for {key}")
        digest, size = variant["sha256"], variant["size"]
        if (digest, size) != payload_checks[key]:
            raise ValueError(f"Payload changed after serialized validation: {key}")
        name = "payloads/" + digest
        entries[name] = (blob, digest, size)
        variant["stagedPath"] = name
        record["intendedState"] = record["state"]
        record["state"] = "prepared-only"
        data = json_bytes(record)
        entries[descriptor_name(*key)] = (data, hashlib.sha256(data).hexdigest(), len(data))
    if seen != keys:
        raise ValueError("Missing Modern bundles")

    manifest = {
        "schemaVersion": 1,
        "contentOrigin": "modern",
        "stage": "prepared-only",
        "activation": False,
        "platform": PLATFORM,
        "runtimeProfile": content.RUNTIME_PROFILE,
        "unityVersion": summary["unityVersion"],
        "counts": counts,
        "models": models,
        "excludedModels": excluded,
        "catalogSHA256": summary["catalogSHA256"],
        "fitManifestSHA256": summary["fitManifestSHA256"],
        "primalCatalogSHA256": summary["primalCatalogSHA256"],
        "releaseSelectionSHA256": summary["releaseSelectionSHA256"],
        "files": {name: {"sha256": digest, "size": size} for name, (_, digest, size) in entries.items()},
    }
    if output.exists():
        raise FileExistsError(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = output.with_name(f".{output.name}.{uuid4().hex}.building")
    try:
        with tarfile.open(temp, "x:gz", compresslevel=1) as archive:
            data = json_bytes(manifest)
            write_member(archive, "manifest.json", data, len(data))
            for name, (value, _, size) in sorted(entries.items()):
                write_member(archive, name, value, size)
        verify(temp)
        if os.name == "nt":
            temp.rename(output)
        else:
            os.link(temp, output)
    finally:
        temp.unlink(missing_ok=True)
    return manifest


def verify(path: Path) -> dict:
    with tarfile.open(path, "r|gz") as archive:
        header = archive.next()
        if header is None or header.name != "manifest.json" or not header.isfile() or header.size > 2 * 1024**2:
            raise ValueError("Missing Modern staging manifest")
        manifest = json.load(archive.extractfile(header))
        if (manifest.get("schemaVersion") != 1 or manifest.get("contentOrigin") != "modern"
                or manifest.get("stage") != "prepared-only" or manifest.get("activation") is not False
                or manifest.get("platform") != PLATFORM or manifest.get("runtimeProfile") != content.RUNTIME_PROFILE):
            raise ValueError("Archive is not a Modern WebGL staging package")
        counts = manifest["counts"]
        release_counts(manifest["models"], counts)
        files = manifest["files"]
        seen: set[str] = set()
        records = Counter()
        payloads = set()
        catalog_keys = set()
        archive_keys = set()
        selection = None
        while (member := archive.next()) is not None:
            name = member.name
            if name not in files or name in seen or not member.isfile() or member.size != files[name]["size"]:
                raise ValueError(f"Unexpected or corrupt archive member {name}")
            seen.add(name)
            with archive.extractfile(member) as stream:
                if name.startswith("payloads/"):
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                    if name != "payloads/" + digest:
                        raise ValueError(f"Payload filename differs from its digest: {name}")
                else:
                    data = stream.read()
                    digest = hashlib.sha256(data).hexdigest()
                    value = json.loads(data)
                    if name == "catalog.json":
                        if (digest != manifest["catalogSHA256"] or value.get("contentOrigin") != "modern"
                                or value.get("stage") != "prepared-only" or value.get("gameReady") is not False
                                or value.get("bundleBuildAuthorized") is not False
                                or value.get("releaseSelectionSHA256") != manifest["releaseSelectionSHA256"]):
                            raise ValueError("Archive catalog is not staged Modern content")
                        for record in value["records"]:
                            check_modern_metadata(record)
                            key = record_key(record)
                            if key in catalog_keys:
                                raise ValueError(f"Duplicate catalog record {key}")
                            catalog_keys.add(key)
                    elif name == "selection.json":
                        if digest != manifest["releaseSelectionSHA256"]:
                            raise ValueError("Archive exclusion policy digest mismatch")
                        selection = value
                    else:
                        key = record_key(value)
                        check_modern_metadata(value)
                        if (name != descriptor_name(*key) or value.get("state") != "prepared-only"
                                or value.get("intendedState") not in ("active", "retired")):
                            raise ValueError(f"Invalid staged descriptor {name}")
                        records[key[0]] += 1
                        archive_keys.add(key)
                        variant, = value["variants"]
                        blob = variant["stagedPath"]
                        if (variant["platform"] != PLATFORM or variant["runtimeProfile"] != content.RUNTIME_PROFILE
                                or blob != "payloads/" + variant["sha256"] or blob not in files
                                or files[blob]["size"] != variant["size"] or variant.get("attachments")):
                            raise ValueError(f"Invalid archived payload reference {name}")
                        payloads.add(blob)
            if digest != files[name]["sha256"]:
                raise ValueError(f"Archive member digest mismatch: {name}")
        if (seen != files.keys() or records != counts or catalog_keys != archive_keys
                or payloads != {name for name in seen if name.startswith("payloads/")}
                or "catalog.json" not in seen or selection is None):
            raise ValueError("Incomplete Modern archive inventory")
        check_exclusions(selection, manifest["excludedModels"], catalog_keys)
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("package")
    build.add_argument("--build", type=Path, required=True)
    build.add_argument("--people", type=Path, required=True)
    build.add_argument("--review", type=Path, required=True)
    build.add_argument("--output", type=Path, required=True)
    check = commands.add_parser("verify")
    check.add_argument("archive", type=Path)
    args = parser.parse_args()
    if args.command == "package":
        path = args.output
        manifest = package(args.build, args.people, args.review, path)
    else:
        path = args.archive
        manifest = verify(path)
    digest, size = content.hash_file(path)
    print(json.dumps({"stage": manifest["stage"], "counts": manifest["counts"], "sha256": digest, "size": size}))


if __name__ == "__main__":
    main()

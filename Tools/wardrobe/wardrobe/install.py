"""Stage: unpack a vendor archive into the DAZ content library.

The fragile part of the whole pipeline. DAZ products are packaged by hand by
their authors, so the tree inside an archive is never the same twice — the
content root may be the archive root, or sit under `Content/`, `My Library/`,
or a product-named folder. We find it structurally (by looking for the folders
DAZ actually reads) instead of trusting any convention.

After copying, the stage reports which `.duf` files are LOADABLE WEARABLES, as
opposed to the material presets and "!Apply Set" scripts that sit next to them
and outnumber them — that list is what `dress` consumes.
"""
from __future__ import annotations

import gzip
import json
import os
import re
import shutil
import subprocess
import zipfile
from pathlib import Path
from urllib.parse import unquote

from . import config

# Top-level folders DAZ reads. Finding one means we have found the content root.
_CONTENT_DIRS = {"people", "runtime", "data", "props", "environments", "animals",
                 "scripts", "light presets", "render settings", "shader presets",
                 "cameras", "materials", "figures", "presets"}

# `asset_info.type` of a .duf you can actually load onto a figure. Measured
# across products: Sweet Jane and Fitness Idol ship "wearable", dFORCE Stocking
# & Sock ships "scene_subset". Everything else beside them ("preset_material"
# and friends) re-skins an already-loaded item rather than being one.
_WEARABLE_TYPES = {"scene_subset", "wearable"}

_GENERATIONS = [
    ("Genesis 9", "G9"),
    ("Genesis 8.1 Female", "G8_1F"),
    ("Genesis 8 Female", "G8F"),
    ("Genesis 8 Male", "G8M"),
    ("Genesis 3 Female", "G3F"),
    ("Genesis 3 Male", "G3M"),
    ("Genesis 2 Female", "G2F"),
]


def read_duf(path: Path) -> dict | None:
    """A .duf is JSON, gzipped or not, depending on the author's save setting."""
    raw = path.read_bytes()
    try:
        text = gzip.decompress(raw).decode("utf-8") if raw[:2] == b"\x1f\x8b" \
            else raw.decode("utf-8")
        return json.loads(text)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return None


def _extract(archive: Path, into: Path) -> None:
    into.mkdir(parents=True, exist_ok=True)
    suffix = archive.suffix.lower()
    if suffix == ".zip":
        with zipfile.ZipFile(archive) as z:
            z.extractall(into)
        return
    if suffix in (".rar", ".7z"):
        if not config.WINRAR.exists():
            raise FileNotFoundError(
                f"для {suffix} нужен WinRAR, не найден: {config.WINRAR}")
        # -y assume yes, -o+ overwrite; WinRAR wants the destination trailing '\'
        result = subprocess.run(
            [str(config.WINRAR), "x", "-y", "-o+", str(archive), str(into) + "\\"],
            capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"WinRAR вернул {result.returncode}: {result.stderr[:400]}")
        return
    raise ValueError(f"не умею распаковывать {suffix}")


_ARCHIVE_SUFFIXES = {".zip", ".rar", ".7z"}


def unpack_recursive(archive: Path, into: Path, depth: int = 0) -> list[str]:
    """Extract, then extract anything archived INSIDE, up to a sane depth.

    Store bundles are archives of archives: "Sweet Jane…rar" holds a preview
    JPEG plus `SweetJaneG3_251066.zip` and `SweetJaneG8_251067.zip`, and
    "Fitness Idol Set.zip" holds Part 1 and Part 2. Unpacking only the outer
    layer finds no content at all — which is exactly how this was discovered.
    """
    _extract(archive, into)
    nested = []
    if depth >= 3:
        return nested
    for inner in sorted(into.rglob("*")):
        if inner.is_file() and inner.suffix.lower() in _ARCHIVE_SUFFIXES:
            target = inner.parent / f"~{inner.stem}"
            try:
                nested.append(str(inner.relative_to(into)).replace("\\", "/"))
                unpack_recursive(inner, target, depth + 1)
                inner.unlink()  # keep the tree free of already-opened archives
            except Exception as e:  # noqa: BLE001
                nested.append(f"!{inner.name}: {e}")
    return nested


def find_content_roots(tree: Path) -> list[Path]:
    """Every directory whose children are DAZ's own top-level folders.

    A bundle can legitimately carry several (a G3 package and a G8 one, or
    "Part 1" and "Part 2"), so this returns all of them — nested candidates
    inside an accepted root are dropped.
    """
    scored: list[tuple[int, int, Path]] = []
    for directory in [tree, *(p for p in tree.rglob("*") if p.is_dir())]:
        try:
            names = {c.name.lower() for c in directory.iterdir() if c.is_dir()}
        except OSError:
            continue
        hits = len(names & _CONTENT_DIRS)
        if hits:
            scored.append((hits, len(directory.relative_to(tree).parts), directory))

    roots: list[Path] = []
    for _, _, directory in sorted(scored, key=lambda s: (s[1], -s[0])):
        if not any(directory.is_relative_to(root) for root in roots):
            roots.append(directory)
    return roots


def _copy_tree(src: Path, dst: Path) -> list[str]:
    """Merge `src` into `dst`, returning the relative paths actually written."""
    written = []
    for item in src.rglob("*"):
        if item.is_dir():
            continue
        relative = item.relative_to(src)
        target = dst / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(item, target)
        written.append(str(relative).replace("\\", "/"))
    return written


def _match_generation(text: str) -> str | None:
    return next((label for needle, label in _GENERATIONS
                 if needle in text or needle.replace(" ", "") in text), None)


def _generation(duf: Path, data: dict, library: Path) -> str | None:
    """Which figure the item is built for — three sources, best first.

    1. The install path. DAZ files most clothing under
       `People/<generation>/Clothing/...` and so does the store.
    2. The `.duf` contents.
    3. The geometry `.dsf` the item references. Some vendors (measured on
       "dFORCE Stocking & Sock") file everything under `Figures/<vendor>/` and
       never name the figure in the `.duf` at all — the only mention is inside
       the data file, so without this step the generation comes back unknown.
    """
    parts = [p.lower() for p in duf.parts]
    for needle, label in _GENERATIONS:
        if needle.lower() in parts:
            return label

    blob = json.dumps(data)
    found = _match_generation(blob[:400_000])
    if found:
        return found

    for reference in sorted(set(re.findall(r"/data/[^\"#]+\.dsf", blob)))[:4]:
        target = library / unquote(reference.lstrip("/")).replace("/", os.sep)
        if not target.exists():
            continue
        nested = read_duf(target)
        if nested and (found := _match_generation(json.dumps(nested)[:400_000])):
            return found
    return None


def classify(duf: Path, library: Path | None = None) -> dict | None:
    """Describe a .duf: is it loadable, and which figure is it built for?"""
    data = read_duf(duf)
    if data is None:
        return None
    info = data.get("asset_info") or {}
    kind = (info.get("type") or "").lower()

    # Vendors ship an "!Apply Set"-style loader next to the real pieces. It is
    # a wearable too, but it drags the whole outfit in at once and would show
    # up as a duplicate of every item — the pipeline wants the pieces.
    bundle = duf.stem.startswith("!")

    return {
        "file": str(duf),
        "name": duf.stem,
        "type": kind or "?",
        "wearable": kind in _WEARABLE_TYPES and not bundle,
        "bundle": bundle,
        "generation": _generation(duf, data, library or duf.parents[0]),
    }


def install(archives: list[Path]) -> dict:
    report = {"archives": [], "installed": 0, "wearables": [], "errors": []}

    for archive in archives:
        entry = {"archive": str(archive)}
        try:
            staging = config.UNPACKED / archive.stem
            if staging.exists():
                shutil.rmtree(staging)
            entry["nested"] = unpack_recursive(archive, staging)

            roots = find_content_roots(staging)
            if not roots:
                raise RuntimeError(
                    "внутри архива не нашлось контент-корня (People/Runtime/data). "
                    f"Верхний уровень: {[p.name for p in staging.iterdir()][:10]}")

            entry["content_roots"] = [str(r.relative_to(staging)) or "." for r in roots]
            written = []
            for root in roots:
                written.extend(_copy_tree(root, config.DAZ_LIBRARY))
            entry["files"] = len(written)
            report["installed"] += len(written)

            # `asset_info.type` decides what is loadable, NOT the folder. Some
            # vendors file clothing under `People/<gen>/Clothing/`, others under
            # `Figures/<vendor>/<product>/` — filtering on the path missed the
            # latter entirely (measured on "dFORCE Stocking & Sock": 14 items,
            # none of them under a Clothing folder).
            for relative in written:
                lowered = relative.lower()
                if not lowered.endswith(".duf"):
                    continue
                if "/materials" in lowered or "/iray materials" in lowered:
                    continue
                described = classify(config.DAZ_LIBRARY / relative, config.DAZ_LIBRARY)
                if described and described["wearable"]:
                    described["relative"] = relative
                    report["wearables"].append(described)
        except Exception as e:  # noqa: BLE001 — the report is the error channel
            entry["error"] = str(e)
            report["errors"].append(f"{archive.name}: {e}")
        report["archives"].append(entry)

    generations = {w["generation"] for w in report["wearables"] if w["generation"]}
    report["generations"] = sorted(generations)
    report["ok"] = not report["errors"] and bool(report["wearables"])
    if not report["wearables"] and not report["errors"]:
        report["errors"].append("архивы распакованы, но носимых .duf не нашлось")
        report["ok"] = False
    return report

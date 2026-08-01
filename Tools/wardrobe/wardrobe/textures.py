"""Stage: bring a drop's textures into the Unity project.

DAZ never embeds textures — an exported FBX just points back into
`My Library/Runtime/Textures`. So every image has to be copied in, and two
things happen on the way:

  * **downscale to 2048.** Unity caps wear textures there anyway, so committing
    4K source art costs repository size and buys nothing;
  * **composite cutout opacity into alpha.** DAZ ships lace/mesh transparency as
    a SEPARATE greyscale image (`TransparentColor`). Unity's URP/Lit alpha clip
    reads the albedo's alpha channel, so the two have to be merged offline —
    the same shape the fur dress already uses.

The material→image mapping is read straight out of the FBX rather than guessed,
so a garment whose preset points at variant #4 of five gets variant #4.
"""
from __future__ import annotations

import shutil
from pathlib import Path

from PIL import Image

from . import config, fbx


def _fit(image: Image.Image, limit: int) -> Image.Image:
    if max(image.size) <= limit:
        return image
    scale = limit / max(image.size)
    return image.resize((round(image.width * scale), round(image.height * scale)),
                        Image.LANCZOS)


def _resolve(path: str) -> Path | None:
    """Textures paths in the FBX are absolute; tolerate a missing/moved file."""
    candidate = Path(path)
    if candidate.exists():
        return candidate
    # Fall back to a name search under the DAZ texture root.
    matches = list(config.DAZ_TEXTURES.rglob(candidate.name))
    return matches[0] if matches else None


def _save_plain(source: Path, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(source) as raw:
        image = _fit(raw.convert("RGB"), config.MAX_TEXTURE)
        if image.size == raw.size and target.suffix.lower() == source.suffix.lower():
            shutil.copyfile(source, target)  # nothing to do — keep the original bytes
            return
    if target.suffix.lower() in (".jpg", ".jpeg"):
        image.save(target, quality=92, subsampling=0)
    else:
        image.save(target, optimize=True)


def _save_cutout(colour: Path, opacity: Path, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(colour) as c, Image.open(opacity) as o:
        rgb = _fit(c.convert("RGB"), config.MAX_TEXTURE)
        alpha = _fit(o.convert("L"), config.MAX_TEXTURE)
        if alpha.size != rgb.size:
            alpha = alpha.resize(rgb.size, Image.LANCZOS)
        merged = rgb.copy()
        merged.putalpha(alpha)
        merged.save(target, optimize=True)


def stage(fbx_path: Path, folders: dict[str, str]) -> dict:
    """Copy the textures every garment in `fbx_path` needs.

    `folders` maps a garment's DAZ node name to its ImportedActors/Wear folder.
    Returns, per garment, the material→texture-filename mapping plus which
    materials need alpha clipping — exactly what the drop manifest wants.
    """
    mapping = fbx.material_textures(Path(fbx_path))
    report: dict = {"garments": {}, "missing": [], "written": []}

    for node, folder in folders.items():
        materials = mapping.get(node)
        if materials is None:
            report["missing"].append(f"в FBX нет меша {node}")
            continue

        out_dir = config.WEAR_IMPORT / folder / "Textures"
        entry: dict[str, dict] = {}
        for material, channels in materials.items():
            diffuse = channels.get("DiffuseColor")
            opacity = channels.get("TransparentColor")
            if not diffuse:
                entry[material] = {"texture": None, "alphaClip": False}
                continue

            source = _resolve(diffuse)
            if source is None:
                report["missing"].append(f"{folder}/{material}: не найден {diffuse}")
                entry[material] = {"texture": None, "alphaClip": False}
                continue

            if opacity and (mask := _resolve(opacity)):
                name = f"{source.stem}_{mask.stem}.png"
                target = out_dir / name
                if not target.exists():
                    _save_cutout(source, mask, target)
                    report["written"].append(str(target))
                entry[material] = {"texture": name, "alphaClip": True}
            else:
                if opacity:
                    report["missing"].append(f"{folder}/{material}: не найдена маска {opacity}")
                target = out_dir / source.name
                if not target.exists():
                    _save_plain(source, target)
                    report["written"].append(str(target))
                entry[material] = {"texture": source.name, "alphaClip": False}

        report["garments"][node] = entry

    report["ok"] = not report["missing"]
    return report

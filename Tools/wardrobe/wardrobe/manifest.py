"""The drop manifest — the single description of a batch of new clothing.

One JSON file per drop lands in `Assets/Editor/WearDrops/`, and the Unity-side
extractor builds prefabs straight from it. That is what lets a new garment be
added without touching C#.

`propose()` drafts a manifest from the FBX plus the staged textures. It is a
DRAFT on purpose: the slot guess is geometry-based and the names are
placeholders. Everything a human (or the supervising agent) is expected to
review carries its evidence alongside — the measured bounding box — so the
guess can be checked instead of trusted.
"""
from __future__ import annotations

import json
import re
import shutil
from pathlib import Path

from . import config, fbx, heels

# Anatomical zones on the Genesis 3 Female base figure all four girls share:
# ~180 cm tall, hip joint at ~105 cm, fingertips at x ±84. Every vertex lands in
# exactly ONE zone — overlapping regions double-counted a sleeve as chest.
def _zone(x: float, y: float) -> str:
    ax = abs(x)
    if ax > 78:
        return "Hand"
    if ax > 66:
        return "Wrist"
    if ax > 44:
        return "Forearm"
    if ax > 19 and y > 120:  # outside the ribcage and high up = upper arm
        return "Shoulder"
    if y >= 158:
        return "Head"
    if y >= 152 and ax <= 10:  # the neck column proper, not a shoulder strap
        return "Neck"
    if y >= 118:
        return "Chest"
    if y >= 104:
        return "Belly"
    if y >= 84:
        return "Pelvis"
    if y >= 48:
        return "Thigh"
    if y >= 10:
        return "Shin"
    return "Foot"


# Left/right are always claimed as a pair. Asymmetric garments exist (the
# shipped "Got stock" and one glove) but they are rare, and guessing the wrong
# side silently is worse than over-claiming — the reviewer sees the zone table.
_ZONE_SLOTS = {
    "Head": ["Head"], "Neck": ["Neck"], "Chest": ["Chest"], "Belly": ["Belly"],
    "Pelvis": ["Pelvis"],
    "Thigh": ["ThighR", "ThighL"], "Shin": ["ShinR", "ShinL"],
    "Foot": ["FootR", "FootL"],
    "Shoulder": ["ShoulderR", "ShoulderL"], "Forearm": ["ForearmR", "ForearmL"],
    "Wrist": ["WristR", "WristL"], "Hand": ["HandR", "HandL"],
}
_SLOT_ORDER = [s for slots in _ZONE_SLOTS.values() for s in slots]

# A zone counts as covered once this share of the garment's vertices sit in it.
_MIN_SHARE = 0.10
# Anything whose mass sits below the hip is a "bottom". High-waisted leggings
# genuinely reach the navel, but the wardrobe's convention is that trousers do
# not displace a tucked-in top, so bottoms never claim torso slots.
_TORSO_ZONES = ("Head", "Neck", "Chest", "Belly")
_BOTTOM_CENTRE = 100.0

# How many zones a mesh must genuinely cover before it reads as a whole outfit
# rather than a garment. Five is above anything real in the wardrobe: the
# fullest single piece shipped so far is a dress at four (chest, belly, pelvis,
# thigh), while the Rowdy Reiko set reached seven.
_SET_ZONES = 5


def zone_shares(geometry: fbx.Geometry) -> dict[str, float]:
    """Share of the mesh's vertices sitting in each anatomical zone."""
    if not geometry.points:
        return {}
    counts: dict[str, int] = {}
    for x, y, _ in geometry.points:
        zone = _zone(x, y)
        counts[zone] = counts.get(zone, 0) + 1
    total = len(geometry.points)
    return {z: round(c / total, 3) for z, c in sorted(counts.items(), key=lambda kv: -kv[1])}


def infer_slots(geometry: fbx.Geometry) -> list[str]:
    """Proposed wear slots. A PROPOSAL — review it against `zone_shares`."""
    shares = zone_shares(geometry)
    if not shares:
        return []

    centre = sum(y for _, y, _ in geometry.points) / len(geometry.points)
    slots: list[str] = []
    for zone, share in shares.items():
        if share < _MIN_SHARE:
            continue
        if centre < _BOTTOM_CENTRE and zone in _TORSO_ZONES:
            continue
        slots.extend(_ZONE_SLOTS[zone])
    return sorted(set(slots), key=_SLOT_ORDER.index)


def _pretty(mesh_key: str) -> str:
    """`skirt_28126` -> `Skirt`; `FitnessIdol_CroppedTop_G3F` -> `FitnessIdolCroppedTop`.

    DAZ suffixes a garment node with its vertex count, and G3F content often
    carries a generation suffix — neither belongs in an asset name.
    """
    name = re.sub(r"_\d+$", "", mesh_key)
    name = re.sub(r"_(G3F|G8F|G9)$", "", name, flags=re.IGNORECASE)
    parts = [p for p in re.split(r"[^A-Za-z0-9]+", name) if p]
    return "".join(p[:1].upper() + p[1:] for p in parts) or "Garment"


def propose(fbx_path: Path, drop: str, texture_report: dict,
            girls: list[str] | None = None) -> dict:
    """Draft a manifest for every garment mesh found in the export."""
    girls = girls or config.girl_names()
    meshes = fbx.geometries(Path(fbx_path))
    staged = texture_report.get("garments", {})

    garments = []
    for key, geometry in meshes.items():
        if key not in staged:  # the body itself, and anything unstaged
            continue
        name = _pretty(key)
        materials = [
            {
                "source": material,
                "texture": spec["texture"],
                # Только у поверхностей без карты: экспортёр кладёт туда
                # дефолтный серый, и им можно зря притемнить текстуру (§8).
                **({"color": spec["color"]} if spec.get("color") else {}),
                # Matte. Measured, not guessed: the same boot mesh under the same
                # light renders (53,56,64) — bluish grey, no leather left — at
                # 0.3, and (27,21,20) brown at 0. URP mirrors the skybox in the
                # gloss lobe, and a default sky over a dark albedo simply erases
                # it. Turning specular highlights off changes nothing (53,56,64
                # either way), so smoothness is the whole of it. Matte is also
                # what the art style asks for (CLAUDE.md: flat / faceted), and a
                # surface that genuinely wants a sheen can still say so here.
                "smoothness": 0.0,
                "metallic": 0.0,
                "doubleSided": True,
                "alphaClip": spec["alphaClip"],
            }
            for material, spec in staged[key].items()
        ]
        slots = infer_slots(geometry)
        # Footwear gets one more question asked of it: is there a heel? The
        # export answers for free — a shoe modelled for a raised heel, fitted to
        # a girl standing flat, reaches below the floor by the heel height.
        heel, heel_cm = heels.propose(geometry, slots)

        entry = {
            "sourceKey": key,
            "folder": name,
            "name": name,
            # REVIEW: the agent replaces this with a real id + display names.
            "simId": f"clothing.{name.lower()}",
            "layer": "Wear",
            "slots": slots,
            "noHide": [],
            "materials": materials,
            # Evidence for the reviewer: the slot list above is inferred from
            # `zones`, so a wrong guess is visible rather than buried.
            "_measured": {
                "verts": geometry.verts,
                "polys": geometry.polys,
                "bbox": [round(v, 1) for v in geometry.bbox] if geometry.bbox else None,
                "zones": zone_shares(geometry),
                "heelCm": round(heel_cm, 2),
            },
        }
        if heel:
            entry["heelPose"] = heel
        # A DAZ product very often ships the WHOLE OUTFIT as one mesh next to
        # its separate pieces, and `dress` fits both — so the colony ends up
        # owning the set and its own parts as different items. Measured on the
        # Rowdy Reiko boxing outfit: one mesh from chest to feet, alongside the
        # boots, gloves, top and shorts it is made of.
        #
        # A genuine single garment almost never spans this much of a body, so
        # the span is the signal. Flagged rather than dropped: which of the two
        # to keep is a judgement (usually the pieces, for the wardrobe's sake).
        spanned = sum(1 for share in zone_shares(geometry).values() if share >= _MIN_SHARE)
        if spanned >= _SET_ZONES:
            entry["_review"] = (
                f"покрывает {spanned} зон — похоже на КОМПЛЕКТ целиком. "
                "Проверьте, не приехали ли отдельные его части: держать нужно "
                "что-то одно")
        garments.append(entry)

    return {
        "drop": drop,
        "sources": [
            {"fbx": f"Assets/Temp/{girl.lower()} {drop}.fbx", "actor": girl}
            for girl in girls
        ],
        "garments": garments,
    }


def merge(existing: dict, draft: dict) -> dict:
    """Fold a fresh draft into a reviewed manifest without losing decisions.

    Names, ids, layers, slots and material tweaks are human judgement and are
    kept verbatim. Only `_measured` is refreshed, so re-running the pipeline
    after a re-export updates the evidence without undoing the review — the
    first version of this clobbered a hand-written manifest, which is exactly
    the failure mode worth designing against.
    """
    by_key = {g["sourceKey"]: g for g in existing.get("garments", [])}
    merged = dict(existing)
    merged["sources"] = draft["sources"]

    # A garment DELIBERATELY dropped from a reviewed manifest must stay dropped.
    # Without this every `build` re-adds it from the draft under a generated
    # simId — and since the export contains a garment whether we want it or not,
    # that silently duplicates pieces the game already ships (measured: the
    # Cindy bikini and three Deadly Silence pieces came back as
    # `clothing.bra`, `clothing.dstights`, ...).
    skipped = set(existing.get("skipped", []))

    garments = []
    for drafted in draft["garments"]:
        if drafted["sourceKey"] in skipped:
            continue
        prior = by_key.pop(drafted["sourceKey"], None)
        if prior is None:
            garments.append(drafted)
            continue
        kept = dict(prior)
        kept["_measured"] = drafted["_measured"]
        # A heel the reviewed entry never had is not a decision to overrule —
        # it is a measurement the tool could not make yet. Adopt it, flagged.
        # An explicit `"heelPose": null` IS a decision and stays untouched.
        if "heelPose" not in kept and "heelPose" in drafted:
            kept["heelPose"] = drafted["heelPose"]
            kept["_review"] = "каблук замерен впервые — проверьте позу в WardrobeTest"
        garments.append(kept)

    # Anything the export no longer contains stays, flagged rather than dropped.
    for orphan in by_key.values():
        orphan = dict(orphan)
        orphan["_review"] = "меша нет в текущем экспорте — проверьте, не переименовали ли"
        garments.append(orphan)

    merged["garments"] = garments
    return merged


def folder_map(drop: str) -> dict[str, str]:
    """sourceKey -> target folder, as already decided in a saved manifest."""
    if not path_for(drop).exists():
        return {}
    return {g["sourceKey"]: g["folder"] for g in load(drop).get("garments", [])}


def path_for(drop: str) -> Path:
    return config.DROP_MANIFESTS / f"{drop}.json"


def restage_textures(data: dict) -> list[str]:
    """Перенести картинки за переименованной вещью.

    Этап `build` раскладывает текстуры по ЧЕРНОВЫМ именам папок — они берутся из
    ключа меша, потому что других имён в тот момент ещё нет. Настоящие имена
    вещам дают следующим шагом, и картинки остаются под старыми: вещь приезжает
    в игру белой, а на диске всё вроде бы на месте.

    Так вышло на трёх поставках подряд, то есть это не невезение, а порядок
    шагов. Поэтому перенос делается ЗДЕСЬ — там, где имя меняется, — а не
    вспоминается потом каждым, кто заметит белую вещь.

    ⚠️ Переносить надо и за ПРИВАРЕННЫМИ кусками, а не только за главным.
    Черновая папка есть у каждого куска, а `sourceKey` у вещи один: у разных
    перчаток Deadly Silence правая половина несёт свой материал `fabric` со
    своей картинкой, и без неё она приезжала белой — «одна перчатка будто без
    материала». Ровно та же природа, что и у списка материалов сварки (§5).
    """
    root = config.ASSETS / "ImportedActors" / "Wear"
    moved: list[str] = []
    for garment in data.get("garments") or []:
        folder = garment.get("folder")
        keys = [k for k in [garment.get("sourceKey"), *(garment.get("sourceKeys") or [])] if k]
        if not folder or not keys:
            continue
        dest = root / folder / "Textures"
        for key in dict.fromkeys(keys):
            draft = root / _pretty(key) / "Textures"
            if not draft.exists() or draft == dest:
                continue
            dest.mkdir(parents=True, exist_ok=True)
            for image in draft.iterdir():
                # ⚠️ Не только jpg/png: DAZ отдаёт и .tga (Classic Reiko), и .tif.
                # Узкий список молча оставлял такие вещи белыми — картинка на
                # диске есть, но не в той папке, куда вещь переименовали.
                if image.suffix.lower() not in (".jpg", ".jpeg", ".png", ".tga",
                                                ".tif", ".tiff", ".bmp"):
                    continue
                if (dest / image.name).exists():
                    continue
                shutil.copy2(image, dest / image.name)
                moved.append(f"{_pretty(key)} -> {folder}: {image.name}")
    return moved


def save(data: dict, drop: str | None = None) -> Path:
    target = path_for(drop or data["drop"])
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n",
                      encoding="utf-8")
    for line in restage_textures(data):
        print("   картинка переехала:", line)
    return target


def load(drop: str) -> dict:
    return json.loads(path_for(drop).read_text(encoding="utf-8"))

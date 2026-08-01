"""Stage: register a drop's garments in the simulation.

Three files have to agree before a garment exists for the game, and all three
were edited by hand until now:

  * `GarmentLibrary.BuildDefaults` — warmth/armor/capacity and the coarse body
    parts the piece protects;
  * `WearSlotCatalog.Slots` — the fine-grained slots, mirroring the prefab.
    A garment with no row silently falls back to the coarse `Covers` test, so
    a missing row is a bug you only notice as clothes stacking oddly;
  * `Assets/Resources/I2Languages.asset` — `item.<slug>.name` / `.desc` in EN
    and RU. Spec §58 forbids authoring player-facing strings in C#.

Every edit is idempotent: a garment already present is reported and skipped,
never duplicated.
"""
from __future__ import annotations

import io
import re
from pathlib import Path

from . import config

GARMENT_LIBRARY = (config.ASSETS / "HexLive" / "Simulation" / "Content" / "Garments"
                   / "GarmentLibrary.cs")
SLOT_CATALOG = (config.ASSETS / "HexLive" / "Simulation" / "Content" / "Garments"
                / "WearSlotCatalog.cs")
I2_ASSET = config.ASSETS / "Resources" / "I2Languages.asset"

# Fine wear slot -> the coarse body zone the sim uses for warmth, armor and
# wound coverage. Several slots collapse onto one zone by design.
_SLOT_TO_PART = {
    "Head": "Head", "Neck": "Torso", "Chest": "Torso", "Belly": "Torso",
    "Pelvis": "Pelvis",
    "ShoulderR": "ArmR", "ForearmR": "ArmR", "WristR": "ArmR", "HandR": "ArmR",
    "ShoulderL": "ArmL", "ForearmL": "ArmL", "WristL": "ArmL", "HandL": "ArmL",
    "ThighR": "LegR", "ShinR": "LegR", "FootR": "LegR",
    "ThighL": "LegL", "ShinL": "LegL", "FootL": "LegL",
}
_PART_ORDER = ["Head", "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR"]


def slug(garment_id: str) -> str:
    """Mirrors ItemCatalog.Slug — lowercase, every non-alphanumeric to '_'."""
    return "".join(c if c.isascii() and c.isalnum() else "_" for c in garment_id.lower())


def covers_for(slots: list[str]) -> list[str]:
    parts = {_SLOT_TO_PART[s] for s in slots if s in _SLOT_TO_PART}
    return sorted(parts, key=_PART_ORDER.index)


def _read(path: Path) -> str:
    return io.open(path, encoding="utf-8", newline="").read()


def _write(path: Path, text: str) -> None:
    io.open(path, "w", encoding="utf-8", newline="").write(text)


def _newline(text: str) -> str:
    return "\r\n" if "\r\n" in text[:4000] else "\n"


# --- GarmentLibrary ---------------------------------------------------------

_SECTION_END = {
    "Underwear": "// --- Wear: the main clothing layer",
    "Wear": "// --- Outerwear: the top layer",
}


def library_row(sim: dict, garment_id: str, layer: str) -> str:
    covers = ", ".join(f"BodyPart.{p}" for p in sim["covers"])
    return (f'                new("{garment_id}", "{sim["displayName"]}", '
            f'WearLayer.{layer}, {sim["warmth"]:.2f}f, {sim["armor"]:.2f}f, '
            f'{sim["thermalDelta"]:.2f}f, dress, {sim["capacity"]}, {covers}),')


def add_to_library(garments: list[dict]) -> dict:
    text = _read(GARMENT_LIBRARY)
    nl = _newline(text)
    added, skipped, problems = [], [], []

    for g in garments:
        gid, layer, sim = g["simId"], g["layer"], g["sim"]
        if f'new("{gid}"' in text:
            skipped.append(gid)
            continue
        anchor = _SECTION_END.get(layer)
        if anchor is None or anchor not in text:
            problems.append(f"{gid}: не нашёл секцию слоя {layer}")
            continue
        index = text.index(anchor)
        # Step back to the start of the blank line before the section comment.
        insert_at = text.rindex(nl, 0, text.rindex(nl, 0, index))
        text = text[:insert_at] + nl + library_row(sim, gid, layer) + text[insert_at:]
        added.append(gid)

    if added:
        _write(GARMENT_LIBRARY, text)
    return {"file": str(GARMENT_LIBRARY), "added": added, "skipped": skipped,
            "problems": problems}


# --- WearSlotCatalog --------------------------------------------------------

def add_to_slot_catalog(garments: list[dict]) -> dict:
    text = _read(SLOT_CATALOG)
    nl = _newline(text)
    added, skipped, problems = [], [], []

    rows = re.findall(r'^\s*\["([^"]+)"\] = new\[\].*$', text, re.M)
    for g in garments:
        gid, slots = g["simId"], g["slots"]
        if gid in rows or f'["{gid}"]' in text:
            skipped.append(gid)
            continue
        if not slots:
            problems.append(f"{gid}: пустой список слотов — строка не нужна")
            continue

        row = (f'            ["{gid}"] = new[] {{ '
               + ", ".join(f"WearSlot.{s}" for s in slots) + " },")
        # The table is alphabetical; keep it that way.
        following = next((r for r in sorted(rows) if r > gid), None)
        if following:
            marker = next(line for line in text.split(nl)
                          if line.strip().startswith(f'["{following}"]'))
            text = text.replace(marker, row + nl + marker, 1)
        else:
            closing = text.rindex("        };")
            text = text[:closing] + row + nl + text[closing:]
        rows.append(gid)
        added.append(gid)

    if added:
        _write(SLOT_CATALOG, text)
    return {"file": str(SLOT_CATALOG), "added": added, "skipped": skipped,
            "problems": problems}


# --- I2 localization --------------------------------------------------------

def _term_block(term: str, en: str, ru: str, nl: str) -> str:
    def quote(value: str) -> str:
        return "'" + value.replace("'", "''") + "'"

    return nl.join([
        f"    - Term: {quote(term)}",
        "      TermType: 0",
        "      Description: ",
        "      Languages:",
        f"      - {quote(en)}",
        f"      - {quote(ru)}",
        "      Flags: 0101",
        "      Languages_Touch: []",
        "",
    ])


def add_terms(garments: list[dict]) -> dict:
    text = _read(I2_ASSET)
    nl = _newline(text)
    added, skipped, problems = [], [], []

    anchor = next((line for line in text.split(nl)
                   if line.strip().startswith("- Term: 'item.")), None)
    if anchor is None:
        return {"file": str(I2_ASSET), "added": [], "skipped": [],
                "problems": ["в I2 не нашлось ни одного term вида item.*"]}

    payload = ""
    for g in garments:
        sim, key = g["sim"], slug(g["simId"])
        for suffix, en, ru in (("name", sim["displayName"], sim["nameRu"]),
                               ("desc", sim["descEn"], sim["descRu"])):
            term = f"item.{key}.{suffix}"
            if f"- Term: '{term}'" in text:
                skipped.append(term)
                continue
            payload += _term_block(term, en, ru, nl)
            added.append(term)

    if payload:
        text = text.replace(anchor, payload + anchor, 1)
        _write(I2_ASSET, text)
    return {"file": str(I2_ASSET), "added": added, "skipped": skipped,
            "problems": problems}


def register(garments: list[dict]) -> dict:
    """Apply all three registrations for garments carrying a `sim` block."""
    ready = [g for g in garments if g.get("sim")]
    missing = [g["simId"] for g in garments if not g.get("sim")]

    report = {
        "library": add_to_library(ready),
        "slots": add_to_slot_catalog(ready),
        "terms": add_terms(ready),
        "no_sim_block": missing,
    }
    problems = [p for part in ("library", "slots", "terms")
                for p in report[part]["problems"]]
    report["problems"] = problems
    report["ok"] = not problems and not missing
    return report

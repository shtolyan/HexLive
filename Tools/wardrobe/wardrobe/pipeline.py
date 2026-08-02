"""The stage sequence, with a progress callback — shared by the bot and the CLI.

Split in two halves on purpose, with a human in the middle:

  **intake**  fetch → install → dress → build. Everything here is mechanical:
              files move, DAZ fits garments, textures land, a draft manifest
              appears. It ends by handing over the draft.

  **finish**  unity → register → unity → preview → commit. Everything here
              depends on decisions made in between — a garment's id, its name
              in two languages, how warm it is, which slots it really occupies.

The gap between them is not a limitation, it is the point. Inventing a display
name is a judgement call, and a wrong `simId` is frozen forever once art loads
by it. So intake stops and shows its evidence instead of guessing on.
"""
from __future__ import annotations

import re
from pathlib import Path
from typing import Callable

from . import config, daz, dress, fbx, fetch, install, manifest, textures

Progress = Callable[[str], None]


def _noop(_: str) -> None:
    pass


def drop_name(archives: list[str]) -> str:
    """A short, filesystem-safe id for the batch, from the first archive's name.

    Store archives carry a compatibility tail — "Sweet Jane for Genesis 3 and
    Genesis 8 Females" — which is noise in a drop id, so everything from `for`
    onwards is dropped before squashing the rest.
    """
    stem = Path(archives[0]).stem
    stem = re.split(r"\bfor\b", stem, maxsplit=1, flags=re.IGNORECASE)[0]
    stem = re.sub(r"\b(set|pack|bundle|part\s*\d+|dforce)\b", " ", stem, flags=re.IGNORECASE)
    stem = re.sub(r"[^A-Za-z0-9]+", "", stem)[:20]
    return (stem or "drop").lower()


def intake(urls: list[str], progress: Progress = _noop,
           generation: str = "G3F", drop: str | None = None) -> dict:
    """Download → install → dress → draft manifest. Stops before any judgement."""
    report: dict = {"stage": "intake", "ok": False}

    # Say it up front rather than after a 150 MB download: dressing needs DAZ,
    # but fetching and installing are useful on their own, so this warns
    # instead of refusing.
    if not daz.alive():
        progress("⚠ DAZ Studio не отвечает. Скачаю и распакую, но одеть не смогу — "
                 "включите Window → Panes → Daz Script Server → Start Server.")

    progress(f"⬇️ Качаю — ссылок {len(urls)}")
    fetched = fetch.fetch(urls, progress=progress)
    report["fetch"] = fetched
    if not fetched["archives"]:
        report["errors"] = fetched["errors"] or ["скачивать нечего"]
        return report
    reused = sum(1 for f in fetched["files"] if f.get("cached"))
    progress("✅ Всё на месте" if reused == len(fetched["files"]) else "✅ Всё скачано")

    progress("📦 Распаковываю в библиотеку DAZ")
    installed = install.install([Path(a) for a in fetched["archives"]],
                                progress=progress)
    report["install"] = installed
    if not installed["wearables"]:
        report["errors"] = installed["errors"]
        return report

    wanted = [w for w in installed["wearables"] if w["generation"] == generation]
    unknown = [w for w in installed["wearables"] if w["generation"] is None]
    if not wanted and unknown:
        # A product that never names its figure (dForce content does this) is
        # not an error — but which pieces to fit is now a decision, not a fact.
        progress(f"⚠ Поколение не указано ни у одной вещи ({len(unknown)} шт.). "
                 "Беру все — проверьте, что они сядут на Genesis 3.")
        wanted = unknown
    if not wanted:
        report["errors"] = [
            f"вещей для {generation} нет; найдено: "
            + ", ".join(sorted({w['generation'] or '?' for w in installed['wearables']}))]
        return report

    progress(f"✅ Библиотека обновлена — {installed['installed']} файлов, "
             f"вещей для {generation}: {len(wanted)}")
    for w in wanted:
        progress(f"   • {w['name']}")

    drop = drop or drop_name(fetched["archives"])
    report["drop"] = drop

    if not daz.alive():
        report["errors"] = ["DAZ Studio не отвечает: Window → Panes → "
                            "Daz Script Server → Start Server"]
        return report

    progress(f"👗 Одеваю девушек — {', '.join(config.girl_names())}")
    dressed = dress.dress_all([Path(w["file"]) for w in wanted], drop)
    report["dress"] = dressed
    for girl in dressed["girls"]:
        progress(f"   ✔ {girl['girl']} — {girl['size_mb']} МБ")
    if not dressed["ok"]:
        report["errors"] = dressed["errors"]
        return report

    progress("🎨 Переношу текстуры и собираю манифест")
    source = Path(dressed["girls"][0]["fbx"])
    meshes = fbx.geometries(source)
    body = max(meshes.values(), key=lambda m: m.height, default=None)
    decided = manifest.folder_map(drop)
    folders = {key: decided.get(key) or manifest._pretty(key)
               for key, geo in meshes.items() if geo is not body}

    staged = textures.stage(source, folders)
    draft = manifest.propose(source, drop, staged)
    for garment in draft["garments"]:
        garment["folder"] = folders[garment["sourceKey"]]
    existing = manifest.load(drop) if manifest.path_for(drop).exists() else None
    final = manifest.merge(existing, draft) if existing else draft
    report["manifest"] = str(manifest.save(final, drop))
    report["garments"] = final["garments"]
    report["textures"] = staged

    report["ok"] = staged["ok"]
    report["errors"] = staged["missing"]
    return report


def summarise(garments: list[dict]) -> str:
    """The hand-over message: what was found, and the evidence behind each guess."""
    lines = []
    for g in garments:
        measured = g.get("_measured", {})
        zones = ", ".join(f"{z} {s:.0%}"
                          for z, s in (measured.get("zones") or {}).items() if s >= 0.05)
        lines.append(
            f"*{g['name']}*  `{g['simId']}`\n"
            f"  слоты: {', '.join(g['slots']) or '—'}\n"
            f"  зоны:  {zones}\n"
            f"  меш:   {measured.get('verts', '?')} верт., "
            f"{len(g['materials'])} материал(ов)")
    return "\n\n".join(lines)

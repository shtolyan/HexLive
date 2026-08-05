"""CLI for the wardrobe pipeline: `python -m wardrobe <stage>`.

Stages are separate commands on purpose. The supervising agent runs them one at
a time and reads the JSON report each prints, so a failure is a small, named
step rather than an opaque hour-long script.

    python -m wardrobe check
    python -m wardrobe dress   --drop sweetjane --garment "<...>/Sweet Jane Skirt.duf" ...
    python -m wardrobe build   --drop sweetjane          (textures + draft manifest)
    python -m wardrobe show    --drop sweetjane
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import (config, daz, dress, fbx, fetch, heels, install, manifest,
               preview, register, remove, textures, unity)


def _emit(report: dict, path: Path | None = None) -> int:
    text = json.dumps(report, indent=2, ensure_ascii=False)
    if path:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text + "\n", encoding="utf-8")
    print(text)
    return 0 if report.get("ok", True) else 1


def cmd_check(_: argparse.Namespace) -> int:
    problems = config.check("PROJECT", "DAZ_LIBRARY", "BLENDER", "UNITY", "WINRAR")
    for girl, scene in config.GIRL_SCENES.items():
        if not scene.exists():
            problems.append(f"нет сцены для {girl}: {scene}")
    daz_up = daz.alive()
    if not daz_up:
        problems.append(
            "DAZ Studio не отвечает — Window → Panes → Daz Script Server → Start Server")
    return _emit({"ok": not problems, "daz": daz_up, "problems": problems})


def cmd_fetch(args: argparse.Namespace) -> int:
    report = fetch.fetch(args.url)
    return _emit(report, config.REPORTS / "fetch.json")


def cmd_unity(args: argparse.Namespace) -> int:
    if not unity.alive():
        return _emit({"ok": False, "errors": [
            "мост Unity молчит на 127.0.0.1:6400 — редактор запущен?"]})
    report = unity.build_wear(force=args.force, drop=args.drop)
    return _emit(report, config.REPORTS / "unity.json")


def cmd_register(args: argparse.Namespace) -> int:
    data = manifest.load(args.drop)
    report = register.register(data["garments"])
    if report["no_sim_block"]:
        report["next"] = ("добавьте в манифест блок sim (названия EN/RU, описания, "
                          "warmth/armor/thermalDelta/capacity/covers) для: "
                          + ", ".join(report["no_sim_block"]))
    return _emit(report, config.REPORTS / f"{args.drop}-register.json")


def cmd_remove(args: argparse.Namespace) -> int:
    report = remove.remove(args.drop, args.id)
    return _emit(report, config.REPORTS / f"{args.drop}-remove.json")


def cmd_preview(args: argparse.Namespace) -> int:
    data = manifest.load(args.drop)
    report = preview.render(args.drop, data, args.resolution)
    return _emit(report, config.REPORTS / f"{args.drop}-preview.json")


def cmd_install(args: argparse.Namespace) -> int:
    archives = [Path(a) for a in args.archive]
    missing = [str(a) for a in archives if not a.exists()]
    if missing:
        return _emit({"ok": False, "errors": [f"нет архива: {m}" for m in missing]})
    report = install.install(archives)
    # The list of wearables is what `dress --garment` consumes; surface the
    # exact paths so the supervisor can pass them straight through.
    report["dress_hint"] = [w["file"] for w in report["wearables"]
                            if not args.generation or w["generation"] == args.generation]
    return _emit(report, config.REPORTS / "install.json")


def cmd_dress(args: argparse.Namespace) -> int:
    garments = [Path(g) for g in args.garment]
    missing = [str(g) for g in garments if not g.exists()]
    if missing:
        return _emit({"ok": False, "errors": [f"нет файла: {m}" for m in missing]})
    report = dress.dress_all(garments, args.drop, args.girls)
    return _emit(report, config.REPORTS / f"{args.drop}-dress.json")


def cmd_build(args: argparse.Namespace) -> int:
    """Stage textures and draft the manifest from an existing FBX export."""
    # Any girl's export describes the same garments; the first that exists wins.
    source = next((config.DROP_DIR / f"{g.lower()} {args.drop}.fbx"
                   for g in config.girl_names()
                   if (config.DROP_DIR / f"{g.lower()} {args.drop}.fbx").exists()), None)
    if source is None:
        return _emit({"ok": False, "errors": [
            f"в {config.DROP_DIR} нет ни одного FBX поставки '{args.drop}' — сначала dress"]})

    meshes = fbx.geometries(source)
    body = max(meshes.values(), key=lambda m: m.height, default=None)

    # Folder names are a decision, so an already-reviewed manifest wins; only
    # meshes it has never seen get a generated name.
    decided = manifest.folder_map(args.drop)
    folders = {key: decided.get(key) or manifest._pretty(key)
               for key, geo in meshes.items() if geo is not body}

    staged = textures.stage(source, folders)
    draft = manifest.propose(source, args.drop, staged, args.girls)
    for garment in draft["garments"]:
        garment["folder"] = folders[garment["sourceKey"]]

    # Поза каблука, которую вещь навязала фигуре при примерке, — авторская, и
    # она бьёт расчёт по высоте (HEEL_POSE_SPEC.md §2). Отчёт одевания её уже
    # записал, здесь остаётся только положить её той вещи, чей это каблук.
    dress_report_path = config.REPORTS / f"{args.drop}-dress.json"
    if dress_report_path.exists():
        authored = heels.from_dress_report(
            json.loads(dress_report_path.read_text(encoding="utf-8")))
        for garment in draft["garments"]:
            pose = authored.get(garment["sourceKey"])
            if pose:
                garment["heelPose"] = pose

    existing = manifest.load(args.drop) if manifest.path_for(args.drop).exists() else None
    final = manifest.merge(existing, draft) if existing else draft
    target = manifest.save(final, args.drop)

    fresh = [g["sourceKey"] for g in draft["garments"] if g["sourceKey"] not in decided]
    return _emit({
        "ok": staged["ok"],
        "source": str(source),
        "manifest": str(target),
        "merged_into_existing": existing is not None,
        "garments": [g["sourceKey"] for g in final["garments"]],
        "need_review": fresh,
        "textures_written": staged["written"],
        "problems": staged["missing"],
        "next": ("проверьте в манифесте simId, name, слои и слоты у новых вещей — "
                 "потом Unity соберёт префабы"),
    }, config.REPORTS / f"{args.drop}-build.json")


def cmd_show(args: argparse.Namespace) -> int:
    data = manifest.load(args.drop)
    for g in data["garments"]:
        measured = g.get("_measured", {})
        zones = ", ".join(f"{z} {s:.0%}" for z, s in (measured.get("zones") or {}).items()
                          if s >= 0.03)
        print(f"{g['simId']:<32} {g['name']}")
        print(f"   слоты  {', '.join(g['slots']) or '—'}")
        print(f"   зоны   {zones}")
        print(f"   меш    {measured.get('verts', '?')} верт., "
              f"{len(g['materials'])} материал(ов)")
        if g.get("_review"):
            print(f"   ⚠ {g['_review']}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="wardrobe", description=__doc__)
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("check", help="проверить окружение и связь с DAZ").set_defaults(fn=cmd_check)

    p = sub.add_parser("fetch", help="скачать архивы по ссылкам")
    p.add_argument("--url", action="append", required=True)
    p.set_defaults(fn=cmd_fetch)

    p = sub.add_parser("unity", help="прогнать сборку префабов в живом редакторе")
    p.add_argument("--drop", required=True,
                   help="какую поставку собирать; без неё экстрактор берёт ВСЕ манифесты")
    p.add_argument("--force", action="store_true",
                   help="пересобрать даже готовые префабы этой поставки")
    p.set_defaults(fn=cmd_unity)

    p = sub.add_parser("register", help="прописать вещи в GarmentLibrary, слоты и I2")
    p.add_argument("--drop", required=True)
    p.set_defaults(fn=cmd_register)

    p = sub.add_parser("preview", help="отрендерить превью в Blender")
    p.add_argument("--drop", required=True)
    p.add_argument("--resolution", type=int, default=512)
    p.set_defaults(fn=cmd_preview)

    p = sub.add_parser("install", help="распаковать архивы в библиотеку DAZ")
    p.add_argument("--archive", action="append", required=True)
    p.add_argument("--generation", default="G3F",
                   help="какое поколение оставить в подсказке для dress (по умолчанию G3F — все девушки Genesis 3)")
    p.set_defaults(fn=cmd_install)

    p = sub.add_parser("dress", help="одеть девушек и выгрузить FBX")
    p.add_argument("--drop", required=True)
    p.add_argument("--garment", action="append", required=True, help="путь к .duf")
    p.add_argument("--girls", nargs="*", default=None)
    p.set_defaults(fn=cmd_dress)

    p = sub.add_parser("build", help="перенести текстуры и составить манифест")
    p.add_argument("--drop", required=True)
    p.add_argument("--girls", nargs="*", default=None)
    p.set_defaults(fn=cmd_build)

    p = sub.add_parser("remove", help="снять вещь или расцветку из всех семи мест")
    p.add_argument("--drop", required=True)
    p.add_argument("--id", action="append", required=True, help="simId предмета")
    p.set_defaults(fn=cmd_remove)

    p = sub.add_parser("show", help="показать манифест с доказательствами")
    p.add_argument("--drop", required=True)
    p.set_defaults(fn=cmd_show)

    # Отчёты русские, а консоль на этой машине cp1252: печать падала
    # UnicodeEncodeError уже ПОСЛЕ того, как манифест записан на диск, —
    # то есть удачный этап выглядел упавшим.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    args = parser.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())

"""Stage: fit a set of garments onto every girl and export one FBX each.

This is the DAZ half of the pipeline, and it is fully scripted — no clicking.
For each girl we load her base scene, strip whatever she is already wearing,
fit the new garments, and export.

Why strip first: the base scenes still carry the previous drop's clothes, and
exporting them again would triple the file size and re-extract art we already
shipped. Followers are found structurally (a DzFigure with a follow target),
so this keeps working no matter what a scene happens to contain.

Two export details that are load-bearing:
  * `RunSilent` — without it the exporter pops a modal progress dialog and the
    script server blocks until someone clicks it;
  * `IncludeMorphs=false` — morphs are irrelevant for garment meshes and turn a
    9 MB file into a 64 MB one.

The MCP `daz_export_fbx` tool cannot be used: it calls `DzExportMgr.doExport`,
which does not exist in this DAZ build. We drive `DzFbxExporter` directly.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

from . import config, daz, watchdog

_STRIP_AND_FIT = """
(function(){
  var out = { dropped: [], fitted: [], loaded: [] };

  if (!App.getContentMgr().openFile(args.scene, false)) return JSON.stringify({error: "не открылась сцена " + args.scene});
  var fig = Scene.findNodeByLabel(args.figure);
  if (!fig) return JSON.stringify({error: "в сцене нет фигуры " + args.figure});

  // DAZ has TWO ways to wear something, and counting only the first reported a
  // correctly-worn item as missing:
  //   * conforming clothing is a DzFigure that FOLLOWS the figure;
  //   * an accessory (headdress, jewellery) is PARENTED to one of its bones.
  // `worn` is the top level of either — the node whose parent is the figure or
  // one of its bones. Sub-parts hanging off that node are not counted again.
  function ownBone(n) {
    return n && n.inherits("DzBone") && n.getSkeleton && n.getSkeleton() == fig;
  }
  function worn(n) {
    if (n == fig) return false;
    if (n.inherits("DzFigure") && n.getFollowTarget && n.getFollowTarget()) return true;
    if (n.inherits("DzBone")) return false;
    var p = n.getNodeParent ? n.getNodeParent() : null;
    return !!p && (p == fig || ownBone(p));
  }

  function wornNodes() {
    var found = [];
    for (var i = 0; i < Scene.getNumNodes(); i++) {
      var n = Scene.getNode(i);
      if (worn(n)) found.push(n);
    }
    return found;
  }

  // Collect first, remove after — removing shifts the node indices.
  var drop = wornNodes();
  for (var i = 0; i < drop.length; i++) { out.dropped.push(drop[i].getLabel()); Scene.removeNode(drop[i]); }

  // Clothing of the figure's own generation auto-fits on load, with no dialog,
  // as long as the figure is the current selection. `added` is how many things
  // that file actually put ON her — see the note in `dress_one` about why the
  // answer is not always one.
  // Every node in the scene, so a figure that arrives WITHOUT being fitted can
  // be told apart from the ones that were already there.
  function allNodes() {
    var all = [];
    for (var i = 0; i < Scene.getNumNodes(); i++) all.push(Scene.getNode(i));
    return all;
  }
  function has(list, n) {
    for (var i = 0; i < list.length; i++) if (list[i] == n) return true;
    return false;
  }

  for (var i = 0; i < args.garments.length; i++) {
    Scene.selectAllNodes(false);
    Scene.setPrimarySelection(fig);
    var seenWorn = wornNodes();
    var before = seenWorn.length;
    var seen = allNodes();
    var ok = App.getContentMgr().openFile(args.garments[i], true);

    // Auto-fit covers a `wearable` — its own file says what it conforms to.
    // A `scene_subset` says nothing, so DAZ drops it into the scene beside her
    // and it wears nothing: the Ranger vest landed loose on all four girls
    // while the jacket from the same product fitted itself. Anything new that
    // is a rigged figure standing on its own gets conformed by hand.
    var arrived = allNodes();
    for (var j = 0; j < arrived.length; j++) {
      var n = arrived[j];
      if (has(seen, n) || n == fig) continue;
      if (!n.inherits("DzFigure") || !n.setFollowTarget) continue;
      if (n.getFollowTarget && n.getFollowTarget()) continue;
      if (n.getNodeParent && n.getNodeParent()) continue;
      n.setFollowTarget(fig);
      out.conformed = (out.conformed || []).concat([n.getLabel()]);
    }

    // Какие узлы породил ИМЕННО ЭТОТ файл. Имя фигуры вендор выбирает свободно
    // и с именем файла не сверяется: «Riot Girl Backpack.duf» приезжает как
    // `RGBackpack`, и расцветки, разложенные по именам файлов, не находили свою
    // вещь. Здесь связь точная, потому что её сообщает сам DAZ.
    var after = wornNodes();
    var names = [];
    for (var k = 0; k < after.length; k++) {
      if (!has(seenWorn, after[k])) names.push(after[k].getName());
    }

    out.loaded.push({ file: args.garments[i], ok: ok,
                      added: after.length - before, names: names });
  }

  var fitted = wornNodes();
  for (var i = 0; i < fitted.length; i++) {
    out.fitted.push({
      label: fitted[i].getLabel(),
      name: fitted[i].getName(),
      how: (fitted[i].getFollowTarget && fitted[i].getFollowTarget()) ? "conform" : "parent"
    });
  }
  return JSON.stringify(out);
})()
"""

_EXPORT = """
(function(){
  var fbx = App.getExportMgr().findExporterByClassName("DzFbxExporter");
  if (!fbx) return JSON.stringify({error: "FBX-экспортёр не установлен в DAZ Studio"});
  var s = new DzFileIOSettings();
  fbx.getDefaultOptions(s);
  s.setBoolValue("IncludeSelectedOnly", false);
  s.setBoolValue("IncludeVisibleOnly", false);
  s.setBoolValue("IncludeFigures", true);
  s.setBoolValue("IncludeProps", true);
  s.setBoolValue("IncludeLights", false);
  s.setBoolValue("IncludeCameras", false);
  s.setBoolValue("IncludeMorphs", false);
  s.setBoolValue("IncludeAnimations", false);
  s.setBoolValue("IncludeSubD", false);
  s.setBoolValue("EmbedTextures", false);
  s.setBoolValue("CollectTextures", false);
  s.setIntValue("RunSilent", 1);
  fbx.writeFile(args.path, s);
  return JSON.stringify({ path: args.path });
})()
"""


def _daz_path(p: Path) -> str:
    """DAZ wants forward slashes even on Windows."""
    return str(p).replace("\\", "/")


def dress_one(girl: str, garments: list[Path], out_fbx: Path) -> dict:
    scene = config.GIRL_SCENES[girl]
    if not scene.exists():
        raise daz.DazError(f"нет сцены для {girl}: {scene}")

    result = json.loads(daz.execute(_STRIP_AND_FIT, {
        "scene": _daz_path(scene),
        "figure": config.FIGURE_LABEL,
        "garments": [_daz_path(g) for g in garments],
    }))
    if "error" in result:
        raise daz.DazError(f"{girl}: {result['error']}")

    failed = [x["file"] for x in result["loaded"] if not x["ok"]]
    if failed:
        raise daz.DazError(f"{girl}: не загрузились предметы: {', '.join(failed)}")

    # One .duf is not one worn thing, in either direction, so counting is the
    # wrong guard: "Fads Slip Ons" fits a left and a right shoe as two figures,
    # and an outfit preset fits five at once. What must hold is that every file
    # put something ON her — a `scene_subset` that lands in the scene unparented
    # loads fine, reports success, and attaches to nobody.
    silent = [Path(x["file"]).stem for x in result["loaded"] if not x.get("added")]
    if silent:
        raise daz.DazError(
            f"{girl}: загрузились, но ни на ком не сидят: {', '.join(silent)}")

    # DAZ renames a second copy of an already-loaded item to "Classic Boot (2)".
    # Seeing one means two garment files carry the same geometry — the outfit
    # preset beside its own pieces, or one product filed under two vendors.
    labels = {x["label"] for x in result["fitted"]}
    twice = sorted(x["label"] for x in result["fitted"]
                   if (m := re.fullmatch(r"(.+) \(\d+\)", x["label"])) and m[1] in labels)
    if twice:
        raise daz.DazError(f"{girl}: надето дважды: {', '.join(twice)}")

    out_fbx.parent.mkdir(parents=True, exist_ok=True)
    exported = json.loads(daz.execute(_EXPORT, {"path": _daz_path(out_fbx)}))
    if "error" in exported:
        raise daz.DazError(f"{girl}: {exported['error']}")
    if not out_fbx.exists():
        raise daz.DazError(f"{girl}: экспортёр отработал, но файла нет: {out_fbx}")

    return {
        "girl": girl,
        "fbx": str(out_fbx),
        "size_mb": round(out_fbx.stat().st_size / 1024 / 1024, 1),
        "stripped": result["dropped"],
        "fitted": result["fitted"],
        # Какой файл что надел. Нужно расцветкам: пресеты лежат под именем
        # ФАЙЛА вещи, а метка фигуры вендором с ним не сверяется.
        "loaded": result["loaded"],
    }


def dress_all(garments: list[Path], drop: str, girls: list[str] | None = None) -> dict:
    """Fit `garments` on every girl; returns a report dict for the supervisor."""
    girls = girls or config.girl_names()
    report = {"drop": drop, "garments": [str(g) for g in garments], "girls": [], "errors": []}

    # Loading a girl's scene is where DAZ throws its "Missing Files" box, and
    # that box owns the main thread the script server runs on — one missing
    # texture used to stall the whole run until a human clicked OK. The guard
    # itself is started by `daz.execute` and lives for the whole process, so it
    # is up whichever stage hits the dialog; this only notes where this stage's
    # share of the log begins.
    watchdog.ensure_running()
    mark = watchdog.mark()

    for girl in girls:
        out = config.DROP_DIR / f"{girl.lower()} {drop}.fbx"
        try:
            entry = dress_one(girl, garments, out)
        except daz.DazError as e:
            report["errors"].append(str(e))
            continue
        report["girls"].append(entry)

    report["dialogs"] = watchdog.since(mark)
    # Missing content is not cosmetic: it ships as white shoes. Surface it as a
    # finding on the run rather than leaving it in a log nobody opens.
    for line in report["dialogs"]["needs_attention"]:
        report["errors"].append(f"окно DAZ требует человека: {line}")

    # Every girl must yield the same garment node names, or the Unity extractor
    # cannot match one garment across bodies.
    keys = {g["girl"]: sorted(f["name"] for f in g["fitted"]) for g in report["girls"]}
    distinct = {tuple(v) for v in keys.values()}
    if len(distinct) > 1:
        report["errors"].append(f"имена мешей разошлись между девушками: {keys}")
    report["mesh_keys"] = sorted(next(iter(distinct))) if len(distinct) == 1 else []
    report["ok"] = not report["errors"]
    return report

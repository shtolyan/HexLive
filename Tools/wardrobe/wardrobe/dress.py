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
from pathlib import Path

from . import config, daz

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

  // Collect first, remove after — removing shifts the node indices.
  var drop = [];
  for (var i = 0; i < Scene.getNumNodes(); i++) {
    var n = Scene.getNode(i);
    if (worn(n)) drop.push(n);
  }
  for (var i = 0; i < drop.length; i++) { out.dropped.push(drop[i].getLabel()); Scene.removeNode(drop[i]); }

  // Clothing of the figure's own generation auto-fits on load, with no dialog,
  // as long as the figure is the current selection.
  for (var i = 0; i < args.garments.length; i++) {
    Scene.selectAllNodes(false);
    Scene.setPrimarySelection(fig);
    var ok = App.getContentMgr().openFile(args.garments[i], true);
    out.loaded.push({ file: args.garments[i], ok: ok });
  }

  for (var i = 0; i < Scene.getNumNodes(); i++) {
    var n = Scene.getNode(i);
    if (worn(n)) {
      out.fitted.push({
        label: n.getLabel(),
        name: n.getName(),
        how: (n.getFollowTarget && n.getFollowTarget()) ? "conform" : "parent"
      });
    }
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
    if len(result["fitted"]) != len(garments):
        raise daz.DazError(
            f"{girl}: надето {len(result['fitted'])} из {len(garments)} — "
            f"{[x['label'] for x in result['fitted']]}")

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
    }


def dress_all(garments: list[Path], drop: str, girls: list[str] | None = None) -> dict:
    """Fit `garments` on every girl; returns a report dict for the supervisor."""
    girls = girls or config.girl_names()
    report = {"drop": drop, "garments": [str(g) for g in garments], "girls": [], "errors": []}

    for girl in girls:
        out = config.DROP_DIR / f"{girl.lower()} {drop}.fbx"
        try:
            entry = dress_one(girl, garments, out)
        except daz.DazError as e:
            report["errors"].append(str(e))
            continue
        report["girls"].append(entry)

    # Every girl must yield the same garment node names, or the Unity extractor
    # cannot match one garment across bodies.
    keys = {g["girl"]: sorted(f["name"] for f in g["fitted"]) for g in report["girls"]}
    distinct = {tuple(v) for v in keys.values()}
    if len(distinct) > 1:
        report["errors"].append(f"имена мешей разошлись между девушками: {keys}")
    report["mesh_keys"] = sorted(next(iter(distinct))) if len(distinct) == 1 else []
    report["ok"] = not report["errors"]
    return report

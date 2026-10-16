"""Render every resource increment and a stage gallery from the authoring file.

blender <source.blend> -b --factory-startup --python <this-file> -- [--stages-only]
Outputs a local HTML review, PNGs and a machine-readable support/recipe audit.
Contact checks are conservative geometry diagnostics, not a physics proof.
"""
import html
import importlib.util
import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

REPO = Path(__file__).resolve().parents[2]
OUTPUT = REPO / "Build/ConstructionReview-20261016"
OUTPUT.mkdir(parents=True, exist_ok=True)


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, REPO / path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


authoring = module("review_authoring", "Tools/blender/construction_stage_authoring.py")
exporter = module("review_exporter", "Tools/blender/export_construction_stage_workspace.py")
labels = module("review_labels", "Tools/blender/prepare_construction_stage_workspace.py").LABELS


def meshes(unit):
    return [o for o in [unit, *unit.children_recursive] if o.type == "MESH"]


def surface(objects):
    points, faces = [], []
    for obj in objects:
        start = len(points)
        points.extend(obj.matrix_world @ v.co for v in obj.data.vertices)
        faces.extend(tuple(start+i for i in p.vertices) for p in obj.data.polygons)
    return points, faces


def support_diagnostics(sequence, external):
    built = []
    warnings = []
    for index, (stage, material, unit) in enumerate(sequence):
        own = meshes(unit)
        points, faces = surface(own)
        if not points or not faces:
            raise RuntimeError(f"Empty resource {unit.name}")
        low = min(p.z for p in points)
        contact = 0 if low <= .025 else float("inf")
        if built and contact:
            previous_points, previous_faces = surface(built)
            previous = BVHTree.FromPolygons(previous_points, previous_faces)
            current = BVHTree.FromPolygons(points, faces)
            contact = min(min(previous.find_nearest(p)[3] for p in points),
                          min(current.find_nearest(p)[3] for p in previous_points))
        tolerance = .07 if material in {"resource.rope", "resource.palm_leaf"} else .035
        if contact > tolerance and not external:
            warnings.append({"action": index+1, "stage": stage, "unit": unit.name,
                             "part": unit.get("hexlive_part_label", unit.name),
                             "nearestBuiltSurface": None if math.isinf(contact) else round(contact, 5)})
        built.extend(own)
    return warnings


def render_definition(definition, all_pieces):
    root = exporter.validate(definition)
    sequence = authoring.unit_sequence(root, definition)
    external = definition["id"].startswith("architecture.roof")
    diagnostics = support_diagnostics(sequence, external)
    scene = bpy.data.scenes.new("__review_render__")
    bpy.context.window.scene = scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = 560
    scene.render.resolution_y = 480
    scene.render.resolution_percentage = 100
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.display.shading.show_cavity = True
    scene.display.shading.cavity_type = "BOTH"
    scene.display.shading.show_shadows = True
    scene.display.shading.background_type = "WORLD"
    scene.world = bpy.data.worlds.new("Review world")
    scene.world.color = (.035, .044, .052)
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    copied = []
    for _, _, unit in sequence:
        parts = []
        for source in meshes(unit):
            clone = source.copy()
            clone.data = source.data
            scene.collection.objects.link(clone)
            clone.parent = None
            clone.matrix_world = source.matrix_world.copy()
            clone.hide_render = False
            clone.hide_viewport = False
            parts.append(clone)
        copied.append(parts)
    points, _ = surface([o for group in copied for o in group])
    low = Vector([min(p[i] for p in points) for i in range(3)])
    high = Vector([max(p[i] for p in points) for i in range(3)])
    center = (low+high)*.5
    camera = bpy.data.objects.new("Review camera", bpy.data.cameras.new("Review camera"))
    scene.collection.objects.link(camera)
    camera.location = center + Vector((3, -4, 2.65))
    camera.rotation_euler = (center-camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = max(.4, (high-low).length*1.15)
    scene.camera = camera
    directory = OUTPUT / definition["id"]
    directory.mkdir(exist_ok=True)
    stage_ends = []
    cumulative = 0
    for stage in definition["stages"]:
        cumulative += stage["units"]
        stage_ends.append(cumulative)
    frames = range(len(sequence)+1) if all_pieces else sorted({0, *stage_ends})
    for count in frames:
        for index, objects in enumerate(copied):
            for obj in objects:
                obj.hide_render = index >= count
        destination = directory / f"{count:03d}.png"
        scene.render.filepath = str(destination)
        bpy.ops.render.render(write_still=True)
    report = {"id": definition["id"], "title": labels[definition["id"]],
              "units": len(sequence), "stages": definition["stages"], "stageEnds": stage_ends,
              "sizeWu": [round(v, 5) for v in high-low], "floorMinZ": round(low.z, 5),
              "requiresExternalSupports": external, "contactWarnings": diagnostics,
              "actions": [{"stage": stage, "resource": material,
                           "label": unit.get("hexlive_part_label", unit.name)}
                          for stage, material, unit in sequence]}
    for objects in copied:
        for obj in objects:
            bpy.data.objects.remove(obj, do_unlink=True)
    bpy.data.objects.remove(camera, do_unlink=True)
    bpy.data.scenes.remove(scene)
    print("CONSTRUCTION_REVIEW", definition["id"], "units", len(sequence),
          "contact_warnings", len(diagnostics), flush=True)
    return report


def write_gallery(reports, all_pieces):
    sections = []
    for report in reports:
        ident, total = report["id"], report["units"]
        stages = " → ".join(f"{s['units']} {s['material'].split('.')[-1]}" for s in report["stages"])
        images = "".join(f'<figure><img src="{ident}/{count:03d}.png"><figcaption>Стадия {i+1} · {count}/{total}</figcaption></figure>'
                         for i, count in enumerate(report["stageEnds"]))
        slider = (f'<input type="range" min="0" max="{total}" value="{total}" data-id="{ident}">'
                  f'<p class="action">Готово · {total} деталей</p><img class="piece" src="{ident}/{total:03d}.png">') if all_pieces else ""
        warnings = report["contactWarnings"]
        note = (f"Проверить контакт: {len(warnings)} деталей (см. audit.json)." if warnings else
                "Грубая проверка контакта с полом/предыдущими деталями: без предупреждений.")
        if report["requiresExternalSupports"]:
            note = "Кровельный модуль требует внешних игровых опор; отдельно не стоит на земле."
        sections.append(f'<section id="{ident}"><h2>{html.escape(report["title"])}</h2><code>{ident}</code><p>{stages}</p><p>{note}</p>{slider}<div class="stages">{images}</div></section>')
    data = json.dumps({r["id"]: r["actions"] for r in reports}, ensure_ascii=False)
    house = ""
    if (OUTPUT/"production-house/06.png").exists():
        captions = ["Основание пола", "Пол и опоры", "Каркасы стен", "Стены, окна, дверь",
                    "Балки крыши", "Кровля", "Интерьер · крыша скрыта"]
        house = '<section><h2>Дом из production-модулей</h2><p>Ключи и позиции: BlueprintBuildingPlan.Modules. Художественная проверка в Blender; не Unity runtime-прогон.</p><div class="stages">'+"".join(
            f'<figure><a href="production-house/{i:02d}.png"><img src="production-house/{i:02d}.png"></a><figcaption>{caption}</figcaption></figure>'
            for i, caption in enumerate(captions))+"</div></section>"
    document = '''<!doctype html><html lang="ru"><meta charset="utf-8"><title>HexLive · строительные детали</title>
<style>body{margin:32px auto;max-width:1240px;background:#10171b;color:#e5e9e7;font:17px system-ui}h1,h2{color:#c5e77a}section{border-top:1px solid #405047;padding:24px 0}code{color:#abb7bb}.stages{display:flex;overflow-x:auto;gap:12px}figure{margin:0;min-width:240px}figure img{width:240px}.piece{display:block;width:560px;max-width:100%}input{width:560px;max-width:100%;accent-color:#c5e77a}figcaption{font-size:14px}a{color:#c5e77a}</style>
<h1>HexLive · сборка по одной детали</h1><p>14 моделей. Одна оплаченная единица — один шаг. Снимки из Blender, не из Unity. Ползунок показывает все промежуточные состояния; ниже — окончания стадий.</p>
<p>Диагностика контактов — консервативная геометрическая проверка, не расчёт устойчивости. Экспорт и оси проверяются отдельным round-trip тестом. Плечики/одежда не входят в строительную смету.</p>'''+house+"".join(sections)+f'''<script>const actions={data};document.querySelectorAll('input').forEach(x=>x.oninput=()=>{{const n=+x.value,s=x.closest('section');s.querySelector('.piece').src=x.dataset.id+'/'+String(n).padStart(3,'0')+'.png';const a=actions[x.dataset.id][n-1];s.querySelector('.action').textContent=n+' / '+x.max+(a?' · стадия '+a.stage+' · '+a.resource+' · '+a.label:' · пустая площадка');}});</script></html>'''
    (OUTPUT/"index.html").write_text(document, encoding="utf-8")


def main():
    if "--gallery-only" in sys.argv:
        write_gallery(json.loads((OUTPUT/"audit.json").read_text()), True)
        return
    definitions = json.loads((REPO/"Tools/construction_stage_manifest.json").read_text())["definitions"]
    if "--definition" in sys.argv:
        selected = sys.argv[sys.argv.index("--definition")+1]
        definitions = [definition for definition in definitions if definition["id"] == selected]
        if not definitions:
            raise RuntimeError("Unknown construction definition: " + selected)
    all_pieces = "--stages-only" not in sys.argv
    reports = []
    for definition in definitions:
        reports.append(render_definition(definition, all_pieces))
    if "--definition" in sys.argv and (OUTPUT/"audit.json").exists():
        updated = {report["id"]: report for report in reports}
        reports = [updated.get(report["id"], report)
                   for report in json.loads((OUTPUT/"audit.json").read_text())]
    (OUTPUT/"audit.json").write_text(json.dumps(reports, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    write_gallery(reports, all_pieces)
    print("CONSTRUCTION_REVIEW_GALLERY", OUTPUT/"index.html", flush=True)


if __name__ == "__main__":
    main()

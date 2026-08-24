"""Stage: render a preview thumbnail per garment with Blender.

⚠ THIS IS THE LAST STAGE, and it must run AFTER Unity. The thumbnail has to
show what the player will actually see, and that is the Unity-side asset: the
mesh the extractor wrote to `ImportedActors/Wear/<folder>/Meshes/<Actor>.mesh`
wearing the URP material the manifest specifies. Rendering the raw DAZ FBX
instead would preview a different mesh with vendor Iray materials — close
enough to look right and wrong in exactly the ways that matter.

So the flow is: Unity exports each garment's final mesh to OBJ, and Blender
renders those. The Unity-side exporter is still to be written — it needs a
menu item added to the project, which is deliberately on hold while the editor
is in use. Until then the code below reads the FBX and is UNVERIFIED.

Runs headless (`blender -b -P`). The blender-mcp add-on cannot be used for this
— it dispatches onto the main thread via `bpy.app.timers`, which never tick
without an event loop, so it refuses to start under `-b` outright. A plain
script has no such problem: the restriction is the add-on's, not Blender's.

One Blender launch renders every garment in the drop; startup costs a few
seconds and there is no reason to pay it per item.

The garment is rendered alone on a transparent background rather than on the
body — the girls are nude in these exports, and a wardrobe thumbnail wants the
item anyway.
"""
from __future__ import annotations

import json
import subprocess
import tempfile
from pathlib import Path

from . import config

# Executed INSIDE Blender. Reads its job from the path in argv after `--`.
_SCRIPT = r'''
import bpy, json, sys, math
from mathutils import Vector

job = json.load(open(sys.argv[sys.argv.index("--") + 1], encoding="utf-8"))

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=job["fbx"], use_anim=False)

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in \
    [e.bl_idname for e in bpy.types.RenderEngine.__subclasses__()] else "BLENDER_EEVEE"
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = job.get("resolution", 512)
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"

# Three-point-ish rig: a key, a fill and a rim, so faceted low-poly cloth reads.
for name, location, energy in (("key", (3, -4, 4), 900),
                               ("fill", (-4, -3, 1.5), 350),
                               ("rim", (0, 4, 3), 500)):
    light = bpy.data.lights.new(name, "AREA")
    light.energy, light.size = energy, 5.0
    obj = bpy.data.objects.new(name, light)
    obj.location = location
    scene.collection.objects.link(obj)

camera_data = bpy.data.cameras.new("cam")
camera = bpy.data.objects.new("cam", camera_data)
scene.collection.objects.link(camera)
scene.camera = camera

meshes = [o for o in bpy.data.objects if o.type == "MESH"]
for o in meshes:
    o.hide_render = True


def frame(obj):
    """Point the camera at the object's bounding sphere from a 3/4 angle."""
    corners = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    centre = sum(corners, Vector()) / 8.0
    radius = max((c - centre).length for c in corners) or 1.0
    direction = Vector((0.75, -1.0, 0.35)).normalized()
    camera.location = centre + direction * radius * 3.1
    camera.rotation_mode = "QUATERNION"
    camera.rotation_quaternion = (centre - camera.location).to_track_quat("-Z", "Y")
    camera_data.lens = 60


def texture_material(image_path):
    material = bpy.data.materials.new("preview")
    material.use_nodes = True
    bsdf = material.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = 0.75
    if image_path:
        tex = material.node_tree.nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(image_path)
        material.node_tree.links.new(bsdf.inputs["Base Color"], tex.outputs["Color"])
        if tex.image.depth == 32:  # RGBA -> the cutout lace needs clipping
            material.node_tree.links.new(bsdf.inputs["Alpha"], tex.outputs["Alpha"])
            material.blend_method = "CLIP"
    return material


results = []
for item in job["items"]:
    target = next((o for o in meshes if o.name.startswith(item["mesh"])), None)
    if target is None:
        results.append({"mesh": item["mesh"], "error": "меша нет в FBX"})
        continue

    for o in meshes:
        o.hide_render = (o is not target)
    target.data.materials.clear()
    target.data.materials.append(texture_material(item.get("texture")))
    frame(target)

    scene.render.filepath = item["out"]
    bpy.ops.render.render(write_still=True)
    results.append({"mesh": item["mesh"], "out": item["out"]})

json.dump(results, open(job["report"], "w", encoding="utf-8"), ensure_ascii=False)
'''


def render(drop: str, manifest_data: dict, resolution: int = 512) -> dict:
    """Render one thumbnail per garment into HexLiveContent/WearPreviews."""
    if not config.BLENDER.exists():
        return {"ok": False, "errors": [f"Blender не найден: {config.BLENDER}"]}

    girl = config.girl_names()[0]
    source = config.DROP_DIR / f"{girl.lower()} {drop}.fbx"
    if not source.exists():
        source = next((config.DROP_DIR / f"{g.lower()} {drop}.fbx"
                       for g in config.girl_names()
                       if (config.DROP_DIR / f"{g.lower()} {drop}.fbx").exists()), None)
    if source is None:
        return {"ok": False, "errors": [f"нет FBX поставки '{drop}' — сначала dress"]}

    config.PREVIEWS.mkdir(parents=True, exist_ok=True)
    items = []
    for garment in manifest_data["garments"]:
        first = next((m for m in garment["materials"] if m.get("texture")), None)
        texture = (config.WEAR_IMPORT / garment["folder"] / "Textures" / first["texture"]
                   if first else None)
        items.append({
            "mesh": garment["sourceKey"],
            "texture": str(texture) if texture and texture.exists() else None,
            "out": str(config.PREVIEWS / f"{garment['name']}.png"),
        })

    work = Path(tempfile.mkdtemp(prefix="preview-"))
    job_path, report_path = work / "job.json", work / "report.json"
    script_path = work / "render.py"
    script_path.write_text(_SCRIPT, encoding="utf-8")
    job_path.write_text(json.dumps({
        "fbx": str(source), "items": items,
        "resolution": resolution, "report": str(report_path),
    }, ensure_ascii=False), encoding="utf-8")

    process = subprocess.run(
        [str(config.BLENDER), "-b", "-noaudio", "-P", str(script_path),
         "--", str(job_path)],
        capture_output=True, text=True, timeout=1800)

    report: dict = {"source": str(source), "blender_exit": process.returncode}
    if report_path.exists():
        report["rendered"] = json.loads(report_path.read_text(encoding="utf-8"))
    else:
        report["rendered"] = []
        report["stderr"] = process.stdout[-1500:] + process.stderr[-1500:]

    failures = [r for r in report["rendered"] if r.get("error")]
    report["errors"] = [f"{r['mesh']}: {r['error']}" for r in failures]
    if process.returncode != 0 and not report["rendered"]:
        report["errors"].append(f"Blender завершился с кодом {process.returncode}")
    report["ok"] = not report["errors"] and bool(report["rendered"])
    return report

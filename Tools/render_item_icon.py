"""Headless-Blender renderer for inventory item icons (ICON_GENERATION_SPEC.md).

    /Applications/Blender.app/Contents/MacOS/Blender -b -P Tools/render_item_icon.py -- \
        <model.obj|model.glb> <out.png> [--install <itemId>] [--fit 1.25] [--samples 64]

Takes a garment OBJ (from Tools/unity_mesh_to_obj.py) or a prop GLB (the ones
in Assets/Resources/HexLive/Objects) and renders the 512x512 RGBA icon the
inventory expects. The numbers below are NOT free parameters — they reproduce
the framing of the icons that shipped with the game (compare
`Assets/Resources/HexLive/UI/Items/Shorts_10_14636.png`). Change them and the
new icon will not sit in the same row as the old ones.

`--install <itemId>` also copies the PNG to
`Assets/Resources/HexLive/UI/Items/<itemId>.png` and writes a sprite `.meta`
cloned from an existing icon with a fresh guid, so Unity imports it as a Sprite
on next launch without an editor session.
"""
import math
import os
import re
import shutil
import sys
import uuid

import bpy
from mathutils import Vector

# --- the shipped looks (do not tune casually) ----------------------------
# Two presets, because two batches of icons shipped: the cloth one matches the
# Molly garment icons (Shorts_10_14636.png), the tool one matches the AI tool
# icons (tool.axe_stone.png, tool.lighter.png). Both are 3/4 from the same
# side; the tool preset sits a little wider and harder.
STYLES = {
    #        camera direction          fit   roughness  spec  sheen  flat
    "cloth": (Vector((0.55, -1.0, 0.30)), 1.25, 0.62, 0.15, 0.15, False),
    "tool":  (Vector((0.75, -1.0, 0.30)), 1.35, 0.90, 0.00, 0.00, True),
}
SAMPLES = 64        # Cycles + denoise
RESOLUTION = 512
SUNS = (  # direction, energy: key / fill / rim / top
    ((0.7, -1.0, 0.9), 4.0),
    ((-1.0, -0.6, 0.25), 1.8),
    ((-0.2, 1.0, 0.6), 2.2),
    ((0.0, -0.1, 1.0), 1.2),
)

ICON_DIR = "Assets/Resources/HexLive/UI/Items"
META_TEMPLATE = os.path.join(ICON_DIR, "Bikini Bottom.png.meta")


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]
    # OBJ comes from a garment mesh, GLB from a prop — override with --style
    style = "tool" if src.lower().endswith((".glb", ".gltf")) else "cloth"
    if "--style" in argv:
        style = argv[argv.index("--style") + 1]
    opts = {"install": None, "style": style, "fit": None, "samples": SAMPLES}
    if "--install" in argv:
        opts["install"] = argv[argv.index("--install") + 1]
    if "--fit" in argv:
        opts["fit"] = float(argv[argv.index("--fit") + 1])
    if "--samples" in argv:
        opts["samples"] = int(argv[argv.index("--samples") + 1])
    return src, dst, opts


def load(src):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if src.lower().endswith(".glb") or src.lower().endswith(".gltf"):
        bpy.ops.import_scene.gltf(filepath=src)
    else:
        # Unity Y-up / +Z-forward -> Blender Z-up, model front at -Y.
        # The legacy operator was REMOVED in Blender 4.0, so pick whichever this
        # build has: the icons must keep landing in the same orientation whether
        # they are rendered on the 3.2.2 the spec was written against or on a
        # current build. The axis pair is the same, only spelled differently.
        if hasattr(bpy.ops.wm, "obj_import"):
            bpy.ops.wm.obj_import(filepath=src, forward_axis='NEGATIVE_Z', up_axis='Y')
        else:
            bpy.ops.import_scene.obj(filepath=src, axis_forward='-Z', axis_up='Y')
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    assert meshes, f"no mesh in {src}"
    return meshes


def world_bbox(meshes):
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for ob in meshes:
        for corner in ob.bound_box:
            w = ob.matrix_world @ Vector(corner)
            for i in range(3):
                lo[i], hi[i] = min(lo[i], w[i]), max(hi[i], w[i])
    return lo, hi


def _set_input(bsdf, value, *names):
    """Set the first Principled input that exists under any of `names`."""
    for name in names:
        if name in bsdf.inputs:
            bsdf.inputs[name].default_value = value
            return


def setup_materials(meshes, style):
    _, _, roughness, specular, sheen, flat = STYLES[style]
    if flat:
        bpy.ops.object.select_all(action='DESELECT')
        for ob in meshes:
            ob.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        bpy.ops.object.shade_flat()
    for ob in meshes:
        for slot in ob.material_slots:
            mat = slot.material
            if mat is None:
                continue
            mat.use_nodes = True
            bsdf = next((n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
            if bsdf is None:
                continue
            # Blender 4.x renamed several Principled inputs, so address them by
            # whichever name this build knows. Missing ones are skipped rather
            # than fatal: an icon without sheen still matches the shipped set far
            # better than no icon at all.
            _set_input(bsdf, specular, 'Specular', 'Specular IOR Level')
            _set_input(bsdf, roughness, 'Roughness')
            _set_input(bsdf, sheen, 'Sheen', 'Sheen Weight')
            # icons are always opaque: a see-through prop (the plastic bottle)
            # would otherwise render as a ghost on the transparent film
            if 'Alpha' in bsdf.inputs and not bsdf.inputs['Alpha'].links:
                bsdf.inputs['Alpha'].default_value = 1.0
            mat.blend_method = 'OPAQUE'


def render(dst, meshes, style, fit, samples):
    cam_dir, default_fit = STYLES[style][0], STYLES[style][1]
    fit = default_fit if fit is None else fit
    scene = bpy.context.scene
    lo, hi = world_bbox(meshes)
    centre, maxdim = (lo + hi) / 2, max(hi - lo)

    cam_data = bpy.data.cameras.new("Cam")
    cam_data.type = 'ORTHO'
    cam_data.ortho_scale = maxdim * fit
    cam = bpy.data.objects.new("Cam", cam_data)
    scene.collection.objects.link(cam)
    d = cam_dir.normalized()
    cam.location = centre + d * (maxdim * 4.0)
    cam.rotation_mode = 'QUATERNION'
    cam.rotation_quaternion = d.to_track_quat('Z', 'Y')
    scene.camera = cam

    for i, (direction, energy) in enumerate(SUNS):
        data = bpy.data.lights.new(f"sun{i}", type='SUN')
        data.energy, data.angle = energy, math.radians(25)
        ob = bpy.data.objects.new(f"sun{i}", data)
        scene.collection.objects.link(ob)
        ob.rotation_mode = 'QUATERNION'
        ob.rotation_quaternion = Vector(direction).normalized().to_track_quat('Z', 'Y')

    scene.render.engine = 'CYCLES'
    try:
        scene.cycles.device = 'CPU'
    except Exception:
        pass
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = RESOLUTION
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.view_settings.view_transform = 'Standard'
    scene.render.filepath = dst
    bpy.ops.render.render(write_still=True)


def install(png, item_id):
    """Copy into Resources with a hand-written sprite .meta (no Unity needed)."""
    if not os.path.isdir(ICON_DIR):
        raise SystemExit(f"run from the repo root — {ICON_DIR} not found")
    target = os.path.join(ICON_DIR, item_id + ".png")
    shutil.copyfile(png, target)
    meta = open(META_TEMPLATE, encoding="utf-8").read()
    meta = re.sub(r"^guid: [0-9a-f]{32}$", "guid: " + uuid.uuid4().hex, meta,
                  count=1, flags=re.M)
    meta = re.sub(r"spriteID: [0-9a-f]{32}", "spriteID: " + uuid.uuid4().hex, meta, count=1)
    open(target + ".meta", "w", encoding="utf-8").write(meta)
    print("INSTALLED", target)


def main():
    src, dst, opts = parse_args()
    meshes = load(src)
    setup_materials(meshes, opts["style"])
    render(dst, meshes, opts["style"], opts["fit"], opts["samples"])
    print(f"WROTE {dst} (style={opts['style']})")
    if opts["install"]:
        install(dst, opts["install"])


main()

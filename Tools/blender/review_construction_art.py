"""Repeatable construction-art review, applied to the editable stage library.

Uses approved materials, preserves gameplay pivots and wardrobe hanger/socket
matrices, and prepares ordered one-resource units. Run after saving Blender.
Writes a reviewed candidate unless --apply is explicitly supplied.
"""
import importlib.util
import json
import math
from pathlib import Path
import random
import re
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "_ArtSource/Construction/hexlive_construction_stages.blend"
REVIEW = REPO / "Build/ConstructionReview-20261016"
REVIEW.mkdir(parents=True, exist_ok=True)


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, REPO / path)
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


stages = module("review_stage_authoring", "Tools/blender/construction_stage_authoring.py")
art = module("review_arch_art", "Tools/blender/arch_elements_lib.py")


def root_for(definition):
    return next(o for o in bpy.data.objects if o.get("hexlive_definition_id") == definition)


def adopt_mesh(name, mesh, parent, collection):
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.parent = parent
    obj["hexlive_export_name"] = name
    return obj


def world_bounds(obj):
    points = [o.matrix_world @ v.co for o in [obj, *obj.children_recursive]
              if o.type == "MESH" for v in o.data.vertices]
    return (Vector([min(p[i] for p in points) for i in range(3)]),
            Vector([max(p[i] for p in points) for i in range(3)]))


def move_parent(obj, parent):
    bpy.context.view_layer.update()
    world = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_parent_inverse = Matrix.Identity(4)
    obj.matrix_world = world


def review_rack():
    root = root_for("station.drying_rack")
    if root.get("hexlive_art_review"):
        for obj in root.children_recursive:
            if obj.type == "MESH" and obj.name.startswith("Rack_corner_stay_"):
                for face in obj.data.polygons:
                    face.material_index = 1 if len(face.vertices) == 7 else 0
        return
    collection = root.users_collection[0]
    frame = dict(stages.stages_below(root))
    rotation = Matrix.Rotation(math.pi * .5, 4, "Z")
    for obj in root.children_recursive:
        if obj.type == "MESH":
            obj.data.transform(rotation)
    # Plant the upright tips without moving either rail or its attachments.
    posts = sorted(frame[1].children, key=lambda o: o.name)
    for obj in posts:
        for v in obj.data.vertices:
            if v.co.z < .1:
                v.co.z = (v.co.z-.0174) * .1 / (.1-.0174)

    wood = posts[0].data.materials[0]
    endwood = posts[0].data.materials[-1]
    for side, post in zip((-1, 1), posts):
        a, b = Vector((.045, side*.46, .91)), Vector((.045, side*.265, 1.14))
        delta = b-a
        mesh = bpy.data.meshes.new("Rack_corner_stay")
        bm = bmesh.new()
        result = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=7,
                                      radius1=.014, radius2=.012, depth=delta.length)
        orientation = Vector((0, 0, 1)).rotation_difference(delta.normalized()).to_matrix().to_4x4()
        bmesh.ops.transform(bm, matrix=Matrix.Translation((a+b)*.5)@orientation,
                            verts=result["verts"])
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(mesh)
        bm.free()
        mesh.materials.append(wood)
        mesh.materials.append(endwood)
        for face in mesh.polygons:
            face.material_index = 1 if len(face.vertices) == 7 else 0
        brace = adopt_mesh(f"Rack_corner_stay_{side}", mesh, post, collection)
        brace["hexlive_construction_role"] = "decoration"

    for old in list(frame[3].children):
        bpy.data.objects.remove(old, do_unlink=True)
    for index, (along, height) in enumerate(((-.47, 1.14), (.47, 1.14),
                                            (-.47, .73), (.47, .73))):
        mesh = art.pair_lashing(
            f"Rack_lashing_{index:02d}", random.Random(2600+index),
            half_x=.061, half_y=.048, sep_axis="y", turns=4,
            tube=.004, clear=-.001, frap=True,
        )
        mesh.transform(Matrix.Translation((-.012, along, height)))
        adopt_mesh(f"rope_rack_{index:02d}", mesh, frame[3], collection)

    # Socket orientation is identity in the same normalized basis as the art.
    # The game reads these positions; no independent rail-height table is used.
    for index in range(8):
        socket = bpy.data.objects.new(f"DryingSlot_{index:02d}", None)
        collection.objects.link(socket)
        socket.parent = root
        socket.location = (-.075 if index < 4 else .075,
                           -.33 + (index%4)*.22, 1.14 if index < 4 else .73)
        socket.empty_display_type = "SPHERE"
        socket.empty_display_size = .018
        socket["hexlive_export_name"] = f"DryingSlot_{index:02d}"
        socket["hexlive_construction_role"] = "helper"
    root["hexlive_art_review"] = "2026-10-16: +Y basis, planted posts, fitted four-turn lashings, eight rail sockets"


def cut_shelf(mesh, low, high):
    result = mesh.copy()
    bm = bmesh.new()
    bm.from_mesh(result)
    for boundary, outer in ((low, False), (high, True)):
        cut = bmesh.ops.bisect_plane(
            bm, geom=list(bm.verts)+list(bm.edges)+list(bm.faces), dist=1e-7,
            plane_co=(boundary, 0, 0), plane_no=(1, 0, 0),
            clear_inner=not outer, clear_outer=outer)
        edges = [e for e in cut["geom_cut"] if isinstance(e, bmesh.types.BMEdge) and e.is_boundary]
        if edges:
            bmesh.ops.holes_fill(bm, edges=edges, sides=0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(result)
    bm.free()
    for p in result.polygons:
        p.use_smooth = False
    return result


def review_wardrobe():
    root = root_for("furniture.wardrobe")
    if root.get("hexlive_art_review"):
        return
    frame = dict(stages.stages_below(root))
    collection = root.users_collection[0]
    for obj in root.children_recursive:
        if obj.type == "MESH" and obj.name.startswith("Wardrobe_Foot_"):
            low = min(v.co.z for v in obj.data.vertices)
            high = max(v.co.z for v in obj.data.vertices)
            for v in obj.data.vertices:
                v.co.z = (v.co.z-low)*high/(high-low)

    # The previous four end-to-end fragments per shelf had unsupported middle
    # joins. Eight LONG slats now span both foot crosspieces instead.
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(REPO / "Assets/HexLiveContent/RuntimeSource/Objects/furniture.wardrobe.fbx"))
    imported = set(bpy.data.objects)-before
    original_shelves = sorted((o for o in imported if o.type == "MESH"
                               and o.name.startswith("Wardrobe_ShoeShelf_")), key=lambda o: o.name)
    for obj in list(frame[2].children):
        bpy.data.objects.remove(obj, do_unlink=True)
    for board, source in enumerate(original_shelves):
        mesh = source.data.copy()
        mesh.transform(source.matrix_world)
        low = min(v.co.x for v in mesh.vertices)
        high = max(v.co.x for v in mesh.vertices)
        for index in range(4):
            part = cut_shelf(mesh, low+(high-low)*index/4, low+(high-low)*(index+1)/4)
            # Foot top = .14, shelf underside = .195: close the .055 gap.
            part.transform(Matrix.Translation((0, 0, -.055)))
            adopt_mesh(f"Wardrobe_ShoeShelf_{board*4+index:02d}", part, frame[2], collection)
    for obj in imported:
        bpy.data.objects.remove(obj, do_unlink=True)
    for obj in root.children:
        if obj.name.startswith("ShoePairSlot_"):
            obj.location.z -= .055
    top = bpy.data.objects.new("ShoeShelfSurface", None)
    collection.objects.link(top)
    top.parent = root
    top.location = (0, 0, .210)
    top["hexlive_export_name"] = "ShoeShelfSurface"
    top["hexlive_construction_role"] = "helper"
    root["hexlive_art_review"] = "2026-10-16: feet seated at floor; eight spanning shelf slats seated on feet; hanger/garment sockets preserved"


def review_roof(root):
    if root.get("hexlive_art_review"):
        return
    tan30 = math.tan(math.pi/6)
    apothem = 1.5*math.sqrt(3)/2
    leaves = dict(stages.stages_below(root))[2]
    for leaf in leaves.children:
        bm = bmesh.new()
        bm.from_mesh(leaf.data)
        for point, normal in (((apothem, 0, 0), (1, 0, 0)),
                              ((0, 0, 0), (-tan30, 1, 0)),
                              ((0, 0, 0), (-tan30, -1, 0))):
            bmesh.ops.bisect_plane(
                bm, geom=list(bm.verts)+list(bm.edges)+list(bm.faces), dist=1e-6,
                plane_co=point, plane_no=normal, clear_outer=True, clear_inner=False)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(leaf.data)
        bm.free()
        if not leaf.data.polygons:
            raise RuntimeError("Clipped an entire paid leaf: " + leaf.name)
    root["hexlive_art_review"] = "2026-10-16: thatch clipped to the canonical hex sector, 27 paid fronds preserved"


def close_thatch_gaps(root):
    """Each paid palm bundle owns one overlapping band of woven leaf backing.

    Frond leaflets alone are perforated and are not a weatherproof roof. Bands
    follow the same hex plane, not an asset-specific runtime seam correction.
    """
    if root.get("hexlive_thatch_backing_review"):
        return
    sequence = [unit for stage, _, unit in stages.unit_sequence(root) if stage == 2]
    apothem = 1.5*math.sqrt(3)/2
    rise = .135 if root.get("hexlive_definition_id") == "architecture.roof.palm" else 0
    collection = root.users_collection[0]
    for index, unit in enumerate(sequence):
        source = next(obj for obj in unit.children_recursive if obj.type == "MESH")
        # Eave first, peak last; neighbouring bands overlap by 0.008 wu.
        x0 = max(0, apothem*(1-(index+1)/len(sequence))-.004)
        x1 = min(apothem, apothem*(1-index/len(sequence))+.004)
        points = []
        for dz in (-.006, .002):
            for x, sign in ((x0, -1), (x1, -1), (x1, 1), (x0, 1)):
                points.append((x, sign*x/math.sqrt(3), rise*(1-x/apothem)+dz))
        mesh = bpy.data.meshes.new("WovenThatchBacking")
        mesh.from_pydata(points, [], [(3, 2, 1, 0), (4, 5, 6, 7),
            (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)])
        mesh.materials.append(source.data.materials[0])
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(mesh)
        bm.free()
        name = f"ThatchBacking_{index:02d}"
        obj = bpy.data.objects.new(name, mesh)
        collection.objects.link(obj)
        obj.parent = unit
        obj.matrix_world = root.matrix_world.copy()
        obj["hexlive_export_name"] = name
    root["hexlive_thatch_backing_review"] = "27 overlapping paid leaf bands; canonical sector plane"


def review_bed_grounding():
    root = root_for("bed.basic")
    if root.get("hexlive_grounded_log_review"):
        return
    logs = dict(stages.stages_below(root))[1]
    for obj in logs.children_recursive:
        if obj.type != "MESH":
            continue
        low = min(v.co.z for v in obj.data.vertices)
        # Only the two lower sleepers float .04 above the floor. Shape their
        # underside to the floor; never move mattress, footprint or sleep marker.
        if .025 < low < .055:
            ceiling = low+.05
            for v in obj.data.vertices:
                if v.co.z < ceiling:
                    v.co.z = (v.co.z-low)*ceiling/(ceiling-low)
    root["hexlive_grounded_log_review"] = 1


def wrap_units(root):
    if root.get("hexlive_unit_order_version"):
        return
    serial = 0
    for index, stage in stages.stages_below(root):
        units = stages.resource_units(stage)
        # Grounded members before extensions. This fixes WALL/WIN hi before lo,
        # sill before lower board, and crossbar before its posts in a single stage.
        units.sort(key=lambda o: (round(world_bounds(o)[0].z, 3), o.name))
        for order, part in enumerate(units):
            serial += 1
            name = f"unit_{index:02d}_{order:03d}"
            unit = bpy.data.objects.new(name, None)
            stage.users_collection[0].objects.link(unit)
            # The swinging leaf must remain below the real door pivot.
            unit.parent = part.parent
            unit["hexlive_export_name"] = name
            unit["hexlive_construction_role"] = "unit"
            unit["hexlive_build_order"] = serial
            unit["hexlive_resource"] = stage["hexlive_material_id"]
            unit["hexlive_part_label"] = part.name
            move_parent(part, unit)
        # Stage decoration belongs to an actual placed resource, never to an
        # empty stage. Attach to the first resource rather than showing it at 0.
        units = stages.resource_units(stage)
        if units:
            for child in list(stage.children):
                if "_deco_" in child.name:
                    move_parent(child, units[0])
    root["hexlive_unit_order_version"] = 1
    # One Blender file has a global namespace, whereas separate FBXs do not.
    # Remove import-time duplicate suffixes from exported names per owner;
    # exact names such as stick_bar are runtime attachment contracts.
    for obj in root.children_recursive:
        name = str(obj.get("hexlive_export_name", obj.name))
        obj["hexlive_export_name"] = re.sub(r"\.\d{3}$", "", name)


def main():
    if Path(bpy.data.filepath).resolve() != SOURCE:
        raise RuntimeError("Open the stage authoring source first")
    # Keep the complete pre-review source as a recoverable artist snapshot.
    backup = REVIEW / "before-review.blend"
    if not backup.exists():
        bpy.ops.wm.save_as_mainfile(filepath=str(backup), copy=True, compress=True)
    original = bpy.context.window.scene
    try:
        bpy.context.window.scene = next(s for s in bpy.data.scenes
                                        if s.get("hexlive_definition_id") == "station.drying_rack")
        review_rack()
        bpy.context.window.scene = next(s for s in bpy.data.scenes
                                        if s.get("hexlive_definition_id") == "furniture.wardrobe")
        review_wardrobe()
        review_bed_grounding()
        for definition in ("architecture.roof.palm", "architecture.roof.palm.flat"):
            review_roof(root_for(definition))
        for root in [o for o in bpy.data.objects if o.get("hexlive_definition_id")]:
            wrap_units(root)
        for definition in ("architecture.roof.palm", "architecture.roof.palm.flat"):
            close_thatch_gaps(root_for(definition))
        bpy.context.view_layer.update()
        manifest = json.loads((REPO / "Tools/construction_stage_manifest.json").read_text())
        for definition in manifest["definitions"]:
            issues = stages.validate_root(root_for(definition["id"]), definition)
            if issues:
                raise RuntimeError(definition["id"] + ": " + "; ".join(issues))
    finally:
        bpy.context.window.scene = original
    destination = SOURCE if "--apply" in sys.argv else REVIEW / "reviewed.blend"
    launcher = bpy.data.texts.get("ОТКРЫТЬ ПАНЕЛЬ.py")
    if launcher is not None:
        launcher.clear()
        launcher.write("from pathlib import Path\nimport bpy\n"
            "root = next(p for p in Path(bpy.data.filepath).resolve().parents "
            "if (p/'Tools/blender/construction_stage_authoring.py').is_file())\n"
            "p = root/'Tools/blender/construction_stage_authoring.py'\n"
            "exec(compile(p.read_text(encoding='utf-8'),str(p),'exec'), "
            "{'__name__':'__main__','__file__':str(p)})\n")
    bpy.context.preferences.filepaths.save_version = 1
    bpy.ops.wm.save_as_mainfile(filepath=str(destination), compress=True)
    print("CONSTRUCTION_ART_REVIEW", destination)


if __name__ == "__main__":
    main()

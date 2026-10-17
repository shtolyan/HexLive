"""Blender art review from the real BlueprintBuildingPlan module export.

First: dotnet run --project Tools/ConstructionReview
Then run in the construction source .blend. This is NOT a Unity screenshot;
the final factory/loading integration is separately checked in Unity.
"""
import importlib.util
import json
import math
from pathlib import Path
import re

import bpy
from mathutils import Matrix, Vector

REPO = Path(__file__).resolve().parents[2]
OUTPUT = REPO/"Build/ConstructionReview-20261016"


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, REPO/path)
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


authoring = module("house_review_authoring", "Tools/blender/construction_stage_authoring.py")
art = module("house_review_art", "Tools/blender/arch_elements_lib.py")
factory = (REPO/"Assets/HexLive/UnityPresentation/Environment/BlueprintArchitectureFactory.cs").read_text()
eave_offset = float(re.search(r"RoofEaveHeight = HutAssembly.FloorSurfaceLift \+ ([\d.]+)f", factory)[1])
bed_source = (REPO/"Assets/HexLive/UnityPresentation/Environment/BedAssembly.cs").read_text()
bed_width = float(re.search(r"BedWidthScale = ([\d.]+)f", bed_source)[1])
bed_length = float(re.search(r"BedLengthScale = ([\d.]+)f", bed_source)[1])


def main():
    plan = json.loads((OUTPUT/"production-plan.json").read_text())
    definitions = json.loads((REPO/"Tools/construction_stage_manifest.json").read_text())["definitions"]
    roots = {d["id"]: next(o for o in bpy.data.objects if o.get("hexlive_definition_id") == d["id"])
             for d in definitions}
    scene = bpy.data.scenes.new("Production module art review")
    bpy.context.window.scene = scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x, scene.render.resolution_y = 1200, 1000
    scene.render.resolution_percentage = 100
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.display.shading.show_cavity = True
    scene.display.shading.cavity_type = "BOTH"
    scene.display.shading.show_shadows = True
    scene.display.shading.background_type = "WORLD"
    scene.world = bpy.data.worlds.new("Production review world")
    scene.world.color = (.035, .044, .052)
    placed = []

    def add(definition, x, z, yaw, kind):
        root = roots[definition]
        scale_z = 1
        if kind in {"Support", "Wall", "Window", "Door"}:
            points = [o.matrix_world @ v.co for o in root.children_recursive
                      if o.type == "MESH" for v in o.data.vertices]
            height = max(p.z for p in points)-min(p.z for p in points)
            scale_z = (height+art.DECK_TOP)/height
        sector = kind in {"FloorSector", "RoofSector"}
        # Same ModuleRotation convention, expressed in the review's X/Z plane.
        angle = -yaw if sector else 90-yaw
        lift = art.DECK_TOP+eave_offset if kind == "RoofSector" else 0
        scale = Matrix.Diagonal((1, 1, scale_z, 1))
        if kind == "Furniture":
            angle, lift = 180+yaw, art.DECK_TOP
            if definition == "bed.basic":
                scale = Matrix.Diagonal((bed_width, bed_length, 1, 1))
        pose = Matrix.Translation((x, z, lift)) @ Matrix.Rotation(math.radians(angle), 4, "Z") @ scale
        for stage, _, unit in authoring.unit_sequence(root):
            for source in [unit, *unit.children_recursive]:
                if source.type != "MESH": continue
                clone = source.copy()
                scene.collection.objects.link(clone)
                clone.parent = None
                clone.matrix_world = pose @ source.matrix_world
                clone.hide_viewport = False
                clone.hide_render = False
                placed.append((clone, kind, stage, x, z))

    for part in plan["modules"]:
        kind = part["kind"]
        token = {"Support": "support.wood", "FloorSector": "floor.board", "Wall": "wall.wood",
                 "Window": "window.wood", "Door": "door.wood", "RoofSector": "roof.palm"}[kind]
        # Production chooses a flat patch when its hex has fewer than six roof sectors.
        if kind == "RoofSector":
            siblings = [m for m in plan["modules"] if m["kind"] == kind and
                        abs(m["x"]-part["x"]) < .001 and abs(m["z"]-part["z"]) < .001]
            if len(siblings) < 6: token += ".flat"
        add("architecture."+token, part["x"], part["z"], part["yaw"], kind)
    for part in plan["furniture"]:
        add(part["definitionId"], part["x"], part["z"], part["yaw"], "Furniture")

    points = [o.matrix_world @ v.co for o, *_ in placed for v in o.data.vertices]
    low = Vector([min(p[i] for p in points) for i in range(3)])
    high = Vector([max(p[i] for p in points) for i in range(3)])
    center = (low+high)*.5
    camera = bpy.data.objects.new("House review camera", bpy.data.cameras.new("House review camera"))
    scene.collection.objects.link(camera)
    camera.location = center+Vector((5, -7, 6))
    camera.rotation_euler = (center-camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = (high-low).length*1.1
    scene.camera = camera
    directory = OUTPUT/"production-house"
    directory.mkdir(exist_ok=True)
    for phase in range(7):
        for obj, kind, stage, x, z in placed:
            visible = kind == "FloorSector" or kind == "Support"
            if phase == 0: visible = kind == "FloorSector" and stage == 1
            if kind in {"Wall", "Window", "Door"}:
                visible = phase >= 2 and (phase >= 3 or stage == 1)
                if phase == 6 and z < center.y: visible = False
            if kind == "RoofSector": visible = phase in {4, 5} and (phase == 5 or stage == 1)
            if kind == "Furniture": visible = phase == 6
            obj.hide_render = not visible
        scene.render.filepath = str(directory/f"{phase:02d}.png")
        bpy.ops.render.render(write_still=True)
    print("PRODUCTION_HOUSE_REVIEW", plan["blueprintId"], "modules", len(plan["modules"]), directory)


if __name__ == "__main__":
    main()

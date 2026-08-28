"""Author the missing §152 world props as independent low-poly FBX assets.

Run with Blender in background mode.  The script keeps one editable .blend
source, but exports one file per logical ContentObject; no exported FBX refers
to geometry or materials owned by another object.
"""

from __future__ import annotations

import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Assets/ArtSource/Objects/hexlive_atomic_world_props.blend"
OBJECTS = ROOT / "Assets/HexLiveContent/RuntimeSource/Objects"
ANIMALS = ROOT / "Assets/HexLiveContent/RuntimeSource/Animals"


def clean_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in bpy.data.collections:
        if block.name != "Collection":
            bpy.data.collections.remove(block)
    base = bpy.data.collections.get("Collection")
    if base is not None:
        base.name = "authoring"


def material(name: str, color: tuple[float, float, float, float], roughness: float = 0.7,
             metallic: float = 0.0):
    value = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    value.diffuse_color = color
    value.use_nodes = True
    shader = value.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = color
    shader.inputs["Roughness"].default_value = roughness
    shader.inputs["Metallic"].default_value = metallic
    return value


def collection(name: str):
    value = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(value)
    return value


def adopt(obj, owner, name: str, mat=None):
    obj.name = name
    for current in tuple(obj.users_collection):
        current.objects.unlink(obj)
    owner.objects.link(obj)
    if mat is not None:
        obj.data.materials.append(mat)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.shade_flat()
    obj.select_set(False)
    return obj


def bevel(obj, amount: float, segments: int = 2):
    modifier = obj.modifiers.new("soft handcrafted edges", "BEVEL")
    modifier.width = amount
    modifier.segments = segments
    modifier.affect = "EDGES"
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    obj.select_set(False)
    return obj


def cube(owner, name, location, scale, mat, radius=0.0, rotation=(0.0, 0.0, 0.0)):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location, rotation=rotation)
    obj = adopt(bpy.context.object, owner, name, mat)
    obj.dimensions = scale
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if radius:
        bevel(obj, radius)
    return obj


def ico(owner, name, location, scale, mat, subdivisions=2, rotation=(0.0, 0.0, 0.0)):
    bpy.ops.mesh.primitive_ico_sphere_add(
        subdivisions=subdivisions, radius=1.0, location=location, rotation=rotation)
    obj = adopt(bpy.context.object, owner, name, mat)
    obj.scale = scale
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return obj


def cylinder(owner, name, location, radius, depth, mat, vertices=12,
             rotation=(0.0, 0.0, 0.0)):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices, radius=radius, depth=depth,
        location=location, rotation=rotation)
    return adopt(bpy.context.object, owner, name, mat)


def cone(owner, name, location, radius1, radius2, depth, mat, vertices=12,
         rotation=(0.0, 0.0, 0.0)):
    bpy.ops.mesh.primitive_cone_add(
        vertices=vertices, radius1=radius1, radius2=radius2, depth=depth,
        location=location, rotation=rotation)
    return adopt(bpy.context.object, owner, name, mat)


def torus(owner, name, location, major, minor, mat, rotation=(0.0, 0.0, 0.0)):
    bpy.ops.mesh.primitive_torus_add(
        major_segments=16, minor_segments=6,
        location=location, major_radius=major, minor_radius=minor,
        rotation=rotation)
    return adopt(bpy.context.object, owner, name, mat)


def panel(owner, name, vertices, faces, mat):
    mesh = bpy.data.meshes.new(name + ".mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    owner.objects.link(obj)
    obj.data.materials.append(mat)
    return obj


def beam(owner, name, start, end, radius, mat, vertices=8):
    a = Vector(start)
    b = Vector(end)
    delta = b - a
    obj = cylinder(owner, name, (a + b) * 0.5, radius, delta.length, mat, vertices)
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = Vector((0.0, 0.0, 1.0)).rotation_difference(delta.normalized())
    obj.rotation_mode = "XYZ"
    return obj


def capsule(owner, name, location, radius, length, mat, axis="X"):
    rotation = (0.0, math.pi / 2.0, 0.0) if axis == "X" else (0.0, 0.0, 0.0)
    body = cylinder(owner, name + ".body", location, radius, length, mat, 12, rotation)
    offset = Vector((length * 0.5, 0.0, 0.0)) if axis == "X" else Vector((0.0, 0.0, length * 0.5))
    ico(owner, name + ".capA", Vector(location) - offset, (radius, radius, radius), mat, 2)
    ico(owner, name + ".capB", Vector(location) + offset, (radius, radius, radius), mat, 2)
    return body


def pill() -> None:
    owner = collection("item.pill")
    foil = material("Pill.Foil", (0.68, 0.73, 0.78, 1.0), 0.28, 0.6)
    white = material("Pill.White", (0.94, 0.95, 0.89, 1.0), 0.48)
    coral = material("Pill.Coral", (0.88, 0.18, 0.20, 1.0), 0.42)
    cube(owner, "blister foil", (0, 0, 0.025), (0.62, 0.38, 0.05), foil, 0.035)
    for row, y in enumerate((-0.11, 0.11)):
        for col, x in enumerate((-0.19, 0.0, 0.19)):
            capsule(owner, f"capsule {row}-{col}", (x, y, 0.105), 0.045, 0.10,
                    coral if (row + col) % 2 else white, "X")


def plaster() -> None:
    owner = collection("item.plaster")
    fabric = material("Plaster.Fabric", (0.82, 0.55, 0.36, 1.0), 0.9)
    pad = material("Plaster.Pad", (0.96, 0.93, 0.83, 1.0), 1.0)
    cube(owner, "adhesive strip", (0, 0, 0.018), (0.68, 0.22, 0.036), fabric, 0.075)
    cube(owner, "absorbent pad", (0, 0, 0.05), (0.22, 0.18, 0.035), pad, 0.018)
    for side in (-1, 1):
        for index in range(3):
            x = side * (0.19 + index * 0.065)
            cylinder(owner, f"breath hole {side}-{index}", (x, 0, 0.045), 0.012, 0.012,
                     pad, 8)


def cloth() -> None:
    owner = collection("resource.cloth")
    textile = material("Cloth.Indigo", (0.12, 0.32, 0.42, 1.0), 0.96)
    edge = material("Cloth.Edge", (0.07, 0.20, 0.26, 1.0), 0.92)
    cube(owner, "fold bottom", (0.02, 0.01, 0.045), (0.62, 0.46, 0.09), textile, 0.035)
    cube(owner, "fold middle", (-0.035, 0.0, 0.13), (0.54, 0.43, 0.085), textile, 0.032,
         rotation=(0.0, 0.0, math.radians(2.5)))
    cube(owner, "fold top", (0.035, -0.015, 0.21), (0.47, 0.39, 0.075), textile, 0.03,
         rotation=(0.0, 0.0, math.radians(-3.0)))
    for y in (-0.18, 0.18):
        beam(owner, f"stitched edge {y}", (-0.20, y, 0.254), (0.22, y, 0.254), 0.008, edge, 6)


def fiber() -> None:
    owner = collection("resource.fiber")
    straw = material("Fiber.Straw", (0.72, 0.61, 0.25, 1.0), 0.94)
    light = material("Fiber.Light", (0.88, 0.78, 0.38, 1.0), 0.9)
    tie = material("Fiber.Tie", (0.25, 0.14, 0.07, 1.0), 0.95)
    for index in range(15):
        angle = (index / 15.0) * math.tau
        offset = Vector((math.cos(angle) * 0.055, math.sin(angle) * 0.045, 0.0))
        lean = Vector((math.cos(angle * 1.7) * 0.09, math.sin(angle * 1.3) * 0.08, 0.56))
        beam(owner, f"plant strand {index:02d}", offset, offset + lean, 0.011,
             light if index % 4 == 0 else straw, 6)
    torus(owner, "binding", (0, 0, 0.24), 0.072, 0.015, tie)


def mechanical_part() -> None:
    owner = collection("resource.mechanical_part")
    steel = material("MechanicalPart.Steel", (0.30, 0.34, 0.36, 1.0), 0.34, 0.78)
    edge = material("MechanicalPart.Edge", (0.10, 0.12, 0.13, 1.0), 0.42, 0.68)
    brass = material("MechanicalPart.Brass", (0.58, 0.34, 0.08, 1.0), 0.30, 0.72)
    torus(owner, "gear rim", (0, 0, 0.10), 0.25, 0.075, steel)
    cylinder(owner, "gear hub", (0, 0, 0.10), 0.115, 0.13, brass, 12)
    cylinder(owner, "axle cap", (0, 0, 0.175), 0.042, 0.018, edge, 12)
    for index in range(12):
        angle = index * math.tau / 12.0
        centre = Vector((math.cos(angle) * 0.34, math.sin(angle) * 0.34, 0.10))
        cube(owner, f"gear tooth {index:02d}", centre, (0.14, 0.10, 0.13), steel,
             0.018, rotation=(0.0, 0.0, angle))
    for index in range(4):
        angle = index * math.tau / 4.0 + math.pi / 4.0
        start = Vector((math.cos(angle) * 0.105, math.sin(angle) * 0.105, 0.10))
        end = Vector((math.cos(angle) * 0.225, math.sin(angle) * 0.225, 0.10))
        beam(owner, f"gear spoke {index}", start, end, 0.035, brass, 8)


def coconut_branch() -> None:
    owner = collection("palm.coconut_branch")
    stem = material("CoconutBranch.Stem", (0.30, 0.18, 0.055, 1.0), 0.94)
    leaf = material("CoconutBranch.Leaf", (0.10, 0.38, 0.12, 1.0), 0.91)
    light = material("CoconutBranch.LeafLight", (0.23, 0.52, 0.14, 1.0), 0.89)
    husk = material("CoconutBranch.Husk", (0.30, 0.25, 0.07, 1.0), 0.91)
    start = Vector((-0.62, 0.0, 0.14))
    end = Vector((0.68, 0.0, 0.25))
    beam(owner, "branch rachis", start, end, 0.035, stem, 8)
    direction = (end - start).normalized()
    sideways = Vector((-direction.y, direction.x, 0.0))
    for index in range(1, 8):
        centre = start.lerp(end, index / 8.5)
        length = 0.40 - abs(index - 4) * 0.035
        for side in (-1, 1):
            tip = centre + sideways * side * length + direction * 0.08
            tip.z -= 0.06 + abs(index - 4) * 0.008
            leaf_mesh(owner, f"branch leaflet {index}-{side}", centre, tip,
                      length * 0.20, light if (index + side) % 3 == 0 else leaf)
    for index, offset in enumerate(((-0.28, -0.14), (-0.08, 0.15), (0.14, -0.12))):
        centre = Vector((offset[0], offset[1], 0.08 + (index % 2) * 0.035))
        beam(owner, f"coconut stem {index}", centre + Vector((0, 0, 0.18)), centre,
             0.018, stem, 6)
        ico(owner, f"young coconut {index}", centre, (0.13, 0.12, 0.15), husk, 2)


def tent() -> None:
    owner = collection("shelter.tent")
    wood = material("Tent.Poles", (0.30, 0.17, 0.065, 1.0), 0.95)
    rope = material("Tent.Rope", (0.58, 0.43, 0.19, 1.0), 0.96)
    canvas = material("Tent.Canvas", (0.55, 0.34, 0.15, 1.0), 0.98)
    canvas_light = material("Tent.CanvasLight", (0.72, 0.49, 0.23, 1.0), 0.97)
    corners = [(-1.05, -0.72, 0.0), (-1.05, 0.72, 0.0),
               (1.05, -0.72, 0.0), (1.05, 0.72, 0.0)]
    for index, base in enumerate(corners):
        top = (-0.78 if base[0] < 0 else 0.78,
               -0.54 if base[1] < 0 else 0.54, 1.55)
        beam(owner, f"support pole {index}", base, top, 0.055, wood, 9)
        torus(owner, f"pole lashing {index}", top, 0.075, 0.014, rope,
              rotation=(math.pi / 2.0, 0.0, 0.0))
    ridge_a = Vector((-0.84, 0.0, 1.82))
    ridge_b = Vector((0.84, 0.0, 1.82))
    beam(owner, "ridge pole", ridge_a, ridge_b, 0.048, wood, 9)
    # The old tarp stopped at shoulder height and had only front faces. From
    # the production camera both faces were culled, so a valid bundle rendered
    # as four naked poles. An A-frame shelter reaches almost to the ground and
    # owns both windings explicitly; it remains readable from every six-way yaw.
    double_faces = [(0, 1, 2), (0, 2, 3), (2, 1, 0), (3, 2, 0)]
    panel(owner, "left canvas",
          [ridge_a, ridge_b, (1.08, -0.82, 0.12), (-1.08, -0.82, 0.12)],
          double_faces, canvas)
    panel(owner, "right canvas",
          [ridge_b, ridge_a, (-1.08, 0.82, 0.12), (1.08, 0.82, 0.12)],
          double_faces, canvas_light)
    # Close one end while leaving the other open as the entrance. This turns
    # the two roof planes into an unmistakable tent instead of a sun shade.
    panel(owner, "back canvas",
          [ridge_a, (-1.08, -0.82, 0.12), (-1.08, 0.82, 0.12)],
          [(0, 1, 2), (2, 1, 0)], canvas)
    for side in (-1, 1):
        for x in (-1.02, 1.02):
            beam(owner, f"guy rope {side}-{x}", (x, side * 0.74, 1.38),
                 (x * 1.16, side * 1.05, 0.04), 0.012, rope, 6)


def drying_rack() -> None:
    """Four delivered sticks plus four lashings, authored as one owner bundle."""
    owner = collection("station.drying_rack")
    bark = material("DryingRack.Bark", (0.35, 0.19, 0.07, 1.0), 0.96)
    sapwood = material("DryingRack.Sapwood", (0.66, 0.43, 0.18, 1.0), 0.92)
    rope = material("DryingRack.Rope", (0.62, 0.46, 0.21, 1.0), 0.97)

    # Uprights lean very slightly outwards so the silhouette is stable and
    # handmade rather than a rectangular Unity cube. Names are the staged bill
    # contract consumed by BedAssembly: four sticks, then four rope lashings.
    beam(owner, "stick_00", (-0.50, 0.0, 0.02), (-0.44, 0.0, 1.18),
         0.052, bark, 9)
    beam(owner, "stick_01", (0.50, 0.0, 0.02), (0.44, 0.0, 1.18),
         0.052, bark, 9)
    beam(owner, "stick_02", (-0.47, 0.035, 1.14), (0.47, 0.035, 1.14),
         0.043, sapwood, 9)
    beam(owner, "stick_03", (-0.47, -0.04, 0.73), (0.47, -0.04, 0.73),
         0.039, sapwood, 9)

    for index, point in enumerate((
            (-0.45, 0.035, 1.14), (0.45, 0.035, 1.14),
            (-0.45, -0.04, 0.73), (0.45, -0.04, 0.73))):
        torus(owner, f"rope_{index:02d}", point, 0.070, 0.013, rope,
              rotation=(math.pi / 2.0, 0.0, 0.0))


def bow() -> None:
    """A readable recurved hunting bow for old saves which still own tool.bow."""
    owner = collection("tool.bow")
    heartwood = material("Bow.Heartwood", (0.36, 0.13, 0.035, 1.0), 0.88)
    sapwood = material("Bow.Sapwood", (0.68, 0.36, 0.09, 1.0), 0.86)
    leather = material("Bow.Leather", (0.16, 0.055, 0.025, 1.0), 0.96)
    string = material("Bow.String", (0.82, 0.76, 0.55, 1.0), 0.91)

    # A continuous faceted stave is authored as short octagonal limbs. The
    # tips recurve away from the straight string, so the silhouette stays
    # recognisable both in the hand and across an NPC's back.
    points = []
    segment_count = 10
    for index in range(segment_count + 1):
        t = index / segment_count * 2.0 - 1.0
        x = 0.31 * (1.0 - t * t) - 0.055 * (abs(t) ** 7)
        points.append(Vector((x, 0.0, 0.76 + t * 0.72)))
    for index in range(segment_count):
        beam(owner, f"limb {index:02d}", points[index], points[index + 1],
             0.027 if 3 <= index <= 6 else 0.021,
             sapwood if index % 3 else heartwood, 9)

    beam(owner, "bow string", points[0], points[-1], 0.0065, string, 6)
    beam(owner, "leather grip", (0.275, 0.0, 0.62), (0.31, 0.0, 0.90),
         0.043, leather, 10)
    for index, z in enumerate((0.65, 0.71, 0.77, 0.83, 0.89)):
        torus(owner, f"grip wrap {index}", (0.30, 0.0, z), 0.047, 0.007,
              leather, rotation=(math.pi / 2.0, 0.0, 0.0))


def arrow() -> None:
    """One self-contained arrow payload paired logically, not physically, with the bow."""
    owner = collection("resource.arrow")
    shaft = material("Arrow.Shaft", (0.46, 0.24, 0.055, 1.0), 0.92)
    stone = material("Arrow.Head", (0.30, 0.32, 0.34, 1.0), 0.61, 0.18)
    feather = material("Arrow.Feather", (0.74, 0.16, 0.08, 1.0), 0.89)
    binding = material("Arrow.Binding", (0.76, 0.64, 0.35, 1.0), 0.95)

    beam(owner, "shaft", (0.0, 0.0, 0.04), (0.0, 0.0, 1.28), 0.014, shaft, 8)
    cone(owner, "stone point", (0.0, 0.0, 1.36), 0.072, 0.0, 0.18,
         stone, 8)
    torus(owner, "head binding", (0.0, 0.0, 1.275), 0.022, 0.007, binding)
    double_faces = [(0, 1, 2), (2, 1, 0)]
    panel(owner, "fletching left",
          [(0.0, 0.0, 0.09), (0.0, 0.0, 0.34), (0.12, 0.0, 0.16)],
          double_faces, feather)
    panel(owner, "fletching right",
          [(0.0, 0.0, 0.09), (0.0, 0.0, 0.34), (-0.12, 0.0, 0.16)],
          double_faces, feather)
    panel(owner, "fletching cross",
          [(0.0, 0.0, 0.09), (0.0, 0.0, 0.34), (0.0, 0.10, 0.16)],
          double_faces, feather)


def import_mesh_source(path: Path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(path))
    return [obj for obj in bpy.data.objects if obj not in before and obj.type == "MESH"]


def world_bounds(objects):
    points = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
    low = Vector((min(point.x for point in points), min(point.y for point in points),
                  min(point.z for point in points)))
    high = Vector((max(point.x for point in points), max(point.y for point in points),
                   max(point.z for point in points)))
    return low, high


def bake_source_instances(owner, source_objects, transforms) -> None:
    for instance_index, transform in enumerate(transforms):
        for source in source_objects:
            mesh = source.data.copy()
            mesh.transform(transform @ source.matrix_world)
            obj = bpy.data.objects.new(f"{source.name}.{instance_index:02d}", mesh)
            owner.objects.link(obj)
            for polygon in mesh.polygons:
                polygon.use_smooth = False
    for source in source_objects:
        bpy.data.objects.remove(source, do_unlink=True)


def stump() -> None:
    """Bake the pre-§152 stump: the approved native log stood on end."""
    owner = collection("stump.palm")
    source = import_mesh_source(OBJECTS / "log_final_native.fbx")
    low, high = world_bounds(source)
    size = high - low
    native_length = max(0.001, size.x)
    native_diameter = max(0.001, size.y, size.z)
    # Old StumpFactory: Height=.30, diameter=HexRadius(1.5)*.42=.63,
    # local X turned into Unity up. Blender's corresponding up axis is Z.
    scale = Matrix.Diagonal((0.30 / native_length,
                             0.63 / native_diameter,
                             0.63 / native_diameter, 1.0))
    upright = Matrix.Rotation(math.radians(-90.0), 4, "Y")
    bake_source_instances(owner, source, [upright @ scale])


def leaf_mesh(owner, name, base, tip, width, mat):
    base_v = Vector(base)
    tip_v = Vector(tip)
    direction = tip_v - base_v
    side = Vector((-direction.y, direction.x, 0.0)).normalized() * width
    middle = base_v.lerp(tip_v, 0.48)
    vertices = [base_v, middle + side, tip_v, middle - side]
    mesh = bpy.data.meshes.new(name + ".mesh")
    mesh.from_pydata(vertices, [], [(0, 1, 2), (0, 2, 3)])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    owner.objects.link(obj)
    obj.data.materials.append(mat)
    return obj


def crown(owner_name: str, fronds: int, length: float) -> None:
    """Bake the exact pre-§152 PalmCrownFactory assembly into one owner."""
    owner = collection(owner_name)
    source = import_mesh_source(OBJECTS / "palm_frond_native.fbx")
    low, high = world_bounds(source)
    size = high - low
    native_length = max(0.001, size.x, size.y, size.z)
    pitches = (-58.0, -40.0, -22.0, -4.0, 16.0, 30.0)
    transforms = []
    for index in range(max(1, fronds)):
        pitch = pitches[index % len(pitches)]
        ring_scale = 0.85 if pitch > 0.0 else 1.0
        uniform = Matrix.Scale(length / native_length * ring_scale, 4)
        # Unity used Quaternion.Euler(pitch, i*137.5, roll). Under the FBX
        # Y-up→Blender Z-up conversion those axes become Y/Z/X respectively.
        rotation = (
            Matrix.Rotation(math.radians(index * 137.5), 4, "Z") @
            Matrix.Rotation(math.radians(-pitch), 4, "Y") @
            Matrix.Rotation(math.radians((index % 3 - 1) * 10.0), 4, "X"))
        transforms.append(rotation @ uniform)
    bake_source_instances(owner, source, transforms)


def small_palm() -> None:
    owner = collection("tree.palm_small")
    bark = material("SmallPalm.Bark", (0.35, 0.18, 0.07, 1.0), 0.98)
    ring = material("SmallPalm.Rings", (0.22, 0.09, 0.035, 1.0), 0.96)
    green = material("SmallPalm.Leaf", (0.12, 0.43, 0.15, 1.0), 0.9)
    stem = material("SmallPalm.Stem", (0.30, 0.27, 0.08, 1.0), 0.92)
    cone(owner, "young trunk", (0, 0, 1.15), 0.25, 0.16, 2.3, bark, 12)
    for z in (0.30, 0.68, 1.06, 1.44, 1.82):
        torus(owner, f"trunk ring {z}", (0, 0, z), 0.21 - z * 0.022, 0.018, ring)
    top = Vector((0, 0, 2.32))
    for index in range(8):
        angle = index * math.tau / 8.0
        radial = Vector((math.cos(angle), math.sin(angle), 0.0))
        end = top + radial * 0.92 + Vector((0, 0, -0.18))
        beam(owner, f"palm frond {index}", top, end, 0.018, stem, 6)
        side_axis = Vector((-radial.y, radial.x, 0))
        for leaflet in range(1, 7):
            centre = top.lerp(end, leaflet / 7.5)
            leaf_len = 0.21 - abs(leaflet - 3.5) * 0.018
            for side in (-1, 1):
                tip = centre + side_axis * side * leaf_len + radial * 0.04
                tip.z -= 0.04
                leaf_mesh(owner, f"small leaf {index}-{leaflet}-{side}", centre, tip,
                          leaf_len * 0.17, green)


def crab() -> None:
    owner = collection("crab")
    shell = material("Crab.Shell", (0.68, 0.09, 0.045, 1.0), 0.82)
    shell_light = material("Crab.ShellLight", (0.91, 0.23, 0.09, 1.0), 0.78)
    cream = material("Crab.Underside", (0.82, 0.47, 0.22, 1.0), 0.9)
    black = material("Crab.Eyes", (0.015, 0.012, 0.01, 1.0), 0.45)
    ico(owner, "faceted shell", (0, 0, 0.22), (0.58, 0.40, 0.22), shell, 2)
    cube(owner, "underside", (0, -0.015, 0.13), (0.48, 0.31, 0.08), cream, 0.06)
    for side in (-1, 1):
        # Four bent legs per side, each a two-segment readable silhouette.
        for index in range(4):
            y = -0.24 + index * 0.16
            hip = Vector((side * 0.40, y, 0.16))
            knee = Vector((side * (0.62 + index * 0.035), y + (index - 1.5) * 0.035, 0.08))
            foot = Vector((side * (0.78 + index * 0.045), y + (index - 1.5) * 0.07, 0.015))
            beam(owner, f"leg {side}-{index} upper", hip, knee, 0.026, shell, 7)
            beam(owner, f"leg {side}-{index} lower", knee, foot, 0.021, shell_light, 7)
        eye_base = Vector((side * 0.19, 0.27, 0.35))
        beam(owner, f"eye stalk {side}", eye_base, eye_base + Vector((0, 0.035, 0.11)),
             0.025, shell_light, 7)
        ico(owner, f"eye {side}", eye_base + Vector((0, 0.035, 0.125)),
            (0.045, 0.045, 0.045), black, 2)
        shoulder = Vector((side * 0.39, 0.20, 0.20))
        wrist = Vector((side * 0.66, 0.34, 0.17))
        beam(owner, f"claw arm {side}", shoulder, wrist, 0.055, shell, 8)
        ico(owner, f"claw palm {side}", wrist + Vector((side * 0.10, 0.05, 0)),
            (0.17, 0.12, 0.08), shell_light, 2)
        # Two separated fingers make the claw unmistakable from above.
        beam(owner, f"claw fixed finger {side}", wrist + Vector((side * 0.17, 0.07, 0.02)),
             wrist + Vector((side * 0.34, 0.16, 0.04)), 0.045, shell_light, 7)
        beam(owner, f"claw moving finger {side}", wrist + Vector((side * 0.17, 0.03, -0.01)),
             wrist + Vector((side * 0.32, -0.03, 0.01)), 0.04, shell, 7)


def export_collection(owner_name: str, output: Path) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    owner = bpy.data.collections[owner_name]
    for obj in owner.all_objects:
        if obj.type == "MESH":
            obj.select_set(True)
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(output), use_selection=True, object_types={"MESH"},
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y", add_leaf_bones=False,
        bake_anim=False, path_mode="AUTO", embed_textures=False,
    )


def merge_by_material(owner_name: str) -> None:
    """One renderable per material, not one draw group per authored leaflet."""
    owner = bpy.data.collections[owner_name]
    groups = {}
    for obj in tuple(owner.objects):
        if obj.type != "MESH":
            continue
        key = obj.data.materials[0].name if obj.data.materials else "unmaterialed"
        groups.setdefault(key, []).append(obj)
    for key, objects in groups.items():
        if len(objects) < 2:
            objects[0].name = "mesh." + key
            continue
        bpy.ops.object.select_all(action="DESELECT")
        for obj in objects:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = objects[0]
        bpy.ops.object.join()
        objects[0].name = "mesh." + key


def main() -> None:
    if "--only-palm-drops" in sys.argv:
        clean_scene()
        stump()
        crown("resource.palm_crown", fronds=42, length=1.275)
        crown("resource.palm_crown_small", fronds=8, length=1.275)
        for asset_id in (
                "stump.palm", "resource.palm_crown", "resource.palm_crown_small"):
            merge_by_material(asset_id)
            export_collection(asset_id, OBJECTS / f"{asset_id}.fbx")
        print("Restored the three pre-§152 palm-drop visuals as atomic FBXs.")
        return

    clean_scene()
    pill()
    plaster()
    cloth()
    fiber()
    mechanical_part()
    coconut_branch()
    tent()
    drying_rack()
    bow()
    arrow()
    stump()
    crown("resource.palm_crown", fronds=42, length=1.275)
    crown("resource.palm_crown_small", fronds=8, length=1.275)
    small_palm()
    crab()
    for asset_id in (
        "item.pill", "item.plaster", "resource.cloth", "resource.fiber",
        "resource.mechanical_part", "palm.coconut_branch", "shelter.tent",
        "tool.bow", "resource.arrow",
        "stump.palm", "resource.palm_crown", "resource.palm_crown_small",
        "tree.palm_small", "crab",
    ):
        merge_by_material(asset_id)
    SOURCE.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE))
    for asset_id in (
        "item.pill", "item.plaster", "resource.cloth", "resource.fiber",
        "resource.mechanical_part", "palm.coconut_branch", "shelter.tent",
        "station.drying_rack",
        "tool.bow", "resource.arrow",
        "stump.palm", "resource.palm_crown", "resource.palm_crown_small",
        "tree.palm_small",
    ):
        export_collection(asset_id, OBJECTS / f"{asset_id}.fbx")
    export_collection("crab", ANIMALS / "crab.fbx")
    print("Authored 15 independent atomic world props.")


if __name__ == "__main__":
    main()

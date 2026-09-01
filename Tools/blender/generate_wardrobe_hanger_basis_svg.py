"""Validate the shipped wardrobe hanger basis and regenerate its SVG contract.

Run with the authoring scene loaded, after export_wardrobe_module.py:

    blender Assets/ArtSource/Building/hexlive_building_kit.blend -b \
      --python Tools/blender/generate_wardrobe_hanger_basis_svg.py

The drawing is not hand-authored: source matrices and mesh bounds come from the
.blend/FBX, while the hex/junction numbers come from the committed C# sources.
"""

from __future__ import annotations

import math
import re
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


REPO = Path(__file__).resolve().parents[2]
FBX = REPO / "Assets/HexLiveContent/RuntimeSource/Objects/furniture.wardrobe.fbx"
OUTPUT = REPO / "Docs/WardrobeHangerBasis.svg"


def csharp_constant(path: Path, name: str) -> float:
    source = path.read_text()
    match = re.search(
        rf"public const (?:float|int) {re.escape(name)} = (-?\d+(?:\.\d+)?)f?;",
        source,
    )
    if not match:
        raise RuntimeError(f"Missing {name} in {path}")
    return float(match.group(1))


def descendants(root):
    result = []
    stack = list(root.children)
    while stack:
        obj = stack.pop()
        result.append(obj)
        stack.extend(obj.children)
    return result


def bounds_in(root, objects):
    inverse = root.matrix_world.inverted()
    points = []
    for obj in objects:
        if obj.type != 'MESH':
            continue
        transform = inverse @ obj.matrix_world
        points.extend(transform @ vertex.co for vertex in obj.data.vertices)
    if not points:
        raise RuntimeError(f"{root.name} has no mesh geometry")
    low = Vector(tuple(min(point[axis] for point in points) for axis in range(3)))
    high = Vector(tuple(max(point[axis] for point in points) for axis in range(3)))
    return low, high


def nearly_identity(matrix, epsilon=1e-5):
    identity = Matrix.Identity(4)
    return all(abs(matrix[row][column] - identity[row][column]) <= epsilon
               for row in range(4) for column in range(4))


def source_contract():
    left = bpy.data.objects["HL_Wardrobe_Native_FrameL"].matrix_world.translation
    right = bpy.data.objects["HL_Wardrobe_Native_FrameR"].matrix_world.translation
    foot_l = bpy.data.objects["HL_Wardrobe_Foot_L"].matrix_world.translation
    foot_r = bpy.data.objects["HL_Wardrobe_Foot_R"].matrix_world.translation
    pivot = (left + right) * .5
    pivot.z = min(foot_l.z, foot_r.z) - .105
    along = right - left
    angle = math.atan2(along.y, along.x)
    export_basis = Matrix.Translation(pivot) @ Matrix.Rotation(angle - math.pi * .5, 4, 'Z')
    inverse = export_basis.inverted()

    hanger = bpy.data.objects["HL_Wardrobe_RealHanger_00"]
    hanger_to_wardrobe = inverse @ hanger.matrix_world
    hanger_origin = hanger_to_wardrobe.translation.copy()
    normalized_points = []
    for obj in descendants(hanger):
        if obj.type != 'MESH':
            continue
        transform = Matrix.Translation(-hanger_origin) @ inverse @ obj.matrix_world
        normalized_points.extend(transform @ vertex.co for vertex in obj.data.vertices)
    low = Vector(tuple(min(point[axis] for point in normalized_points) for axis in range(3)))
    high = Vector(tuple(max(point[axis] for point in normalized_points) for axis in range(3)))

    slots = []
    for index in range(12):
        item = bpy.data.objects[f"HL_Wardrobe_RealHanger_{index:02}"]
        slots.append((inverse @ item.matrix_world).translation.copy())
    return low, high, slots


def exported_contract():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=str(FBX))
    wardrobe_root = bpy.data.objects.get("WardrobeRoot")
    hanger = bpy.data.objects.get("HangerTemplate")
    if wardrobe_root is None or hanger is None:
        raise RuntimeError("FBX must contain WardrobeRoot and HangerTemplate")
    relative = wardrobe_root.matrix_world.inverted() @ hanger.matrix_world
    if not nearly_identity(relative):
        raise RuntimeError(f"HangerTemplate root is dirty: {relative}")
    low, high = bounds_in(hanger, descendants(hanger))
    slots = []
    for index in range(12):
        slot = bpy.data.objects.get(f"ClothingSlot_{index:02}")
        if slot is None:
            raise RuntimeError(f"Missing ClothingSlot_{index:02}")
        slot_relative = wardrobe_root.matrix_world.inverted() @ slot.matrix_world
        rotation_scale = slot_relative.copy()
        rotation_scale.translation = Vector((0.0, 0.0, 0.0))
        if not nearly_identity(rotation_scale):
            raise RuntimeError(f"ClothingSlot_{index:02} owns rotation/scale")
        slots.append(slot_relative.translation.copy())
    return low, high, slots


def fmt(value):
    return f"{value:.6f}"


def main():
    source_low, source_high, source_slots = source_contract()
    fbx_low, fbx_high, fbx_slots = exported_contract()
    source_size = source_high - source_low
    fbx_size = fbx_high - fbx_low

    # #349's actual invariant: the hanger is a vertical XZ-plane. Its width is
    # across the rail (X), while depth along the rail (Y) is only wood thickness.
    if not (fbx_size.x > fbx_size.y * 8.0 and fbx_size.z > fbx_size.y * 8.0):
        raise RuntimeError(
            f"HangerTemplate is not an XZ-plane: size={tuple(fbx_size)}")
    if (source_size - fbx_size).length > 1e-4:
        raise RuntimeError(
            f"FBX hanger differs from normalized source: source={tuple(source_size)} "
            f"fbx={tuple(fbx_size)}")
    if max((source_slots[i] - fbx_slots[i]).length for i in range(12)) > 1e-4:
        raise RuntimeError("FBX ClothingSlot positions differ from authored source")

    spatial = REPO / "Assets/HexLive/Simulation/Spatial"
    rules = REPO / "Assets/HexLive/Simulation/Runtime/BuildingRules.cs"
    radius = csharp_constant(spatial / "HexSpatialMath.cs", "HexRadius")
    interior = int(csharp_constant(spatial / "HexPointLayout.cs", "InteriorRadius"))
    boundary = int(csharp_constant(spatial / "HexPointLayout.cs", "BoundaryRadius"))
    grid_step = radius / boundary
    wardrobe_x = csharp_constant(rules, "HutWardrobeLocalX")
    wardrobe_z = csharp_constant(rules, "HutWardrobeLocalZ")
    wardrobe_yaw = csharp_constant(rules, "HutWardrobeLocalYaw")
    slot_span = max(slot.y for slot in fbx_slots) - min(slot.y for slot in fbx_slots)
    slot_pitch = slot_span / 11.0

    # Top view in wardrobe-local Blender XY. Rail/footprint is vertical (+Y),
    # one occupied-junction step is 0.375 wu, hanger width is horizontal (+X).
    scale = 500.0
    centre_x, centre_y = 260.0, 270.0
    rail_half = grid_step
    hanger_half_x = fbx_size.x * .5
    hanger_half_y = fbx_size.y * .5

    def sx(x):
        return centre_x + x * scale

    def sy(y):
        return centre_y - y * scale

    slots_svg = []
    for slot in fbx_slots:
        slots_svg.append(
            f"<circle cx='{sx(slot.x):.1f}' cy='{sy(slot.y):.1f}' r='3.5' class='slot'/>")
    hanger_rect_x = sx(-hanger_half_x)
    hanger_rect_y = sy(hanger_half_y)
    hanger_rect_w = fbx_size.x * scale
    hanger_rect_h = max(4.0, fbx_size.y * scale)

    svg = f"""<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 920 600' font-family='sans-serif'>
<style>text{{fill:#252525;font-size:14px}}.small{{fill:#555;font-size:12px}}.title{{font-size:19px;font-weight:600}}.rail{{stroke:#8b7355;stroke-width:8;stroke-linecap:round}}.footprint{{stroke:#4fad74;stroke-width:15;stroke-linecap:round;opacity:.34}}.axis{{stroke:#23683e;stroke-width:2;marker-end:url(#arrow)}}.hanger{{fill:#d19a52;stroke:#70461e;stroke-width:2}}.slot{{fill:#e9b96e;stroke:#70461e;stroke-width:1.5}}.measure{{stroke:#6d6d6d;stroke-width:1.2;stroke-dasharray:4 3}}</style>
<defs><marker id='arrow' markerWidth='8' markerHeight='8' refX='6' refY='3' orient='auto'><path d='M0,0 L0,6 L6,3 z' fill='#23683e'/></marker></defs>
<rect width='920' height='600' fill='#fbfaf7'/>
<text class='title' x='24' y='34'>#349 — нормализованный базис плечика гардероба</text>
<text class='small' x='24' y='57'>Вид сверху, Blender XY → Unity XZ; все числа измерены из source .blend и shipped FBX.</text>
<line x1='{sx(0):.1f}' y1='{sy(-rail_half):.1f}' x2='{sx(0):.1f}' y2='{sy(rail_half):.1f}' class='footprint'/>
<line x1='{sx(0):.1f}' y1='{sy(min(s.y for s in fbx_slots)-.08):.1f}' x2='{sx(0):.1f}' y2='{sy(max(s.y for s in fbx_slots)+.08):.1f}' class='rail'/>
{''.join(slots_svg)}
<rect x='{hanger_rect_x:.1f}' y='{hanger_rect_y:.1f}' width='{hanger_rect_w:.1f}' height='{hanger_rect_h:.1f}' rx='2' class='hanger'/>
<line x1='{sx(0):.1f}' y1='{sy(0):.1f}' x2='{sx(.28):.1f}' y2='{sy(0):.1f}' class='axis'/>
<text x='{sx(.30):.1f}' y='{sy(0)+5:.1f}'>+X / Unity +X — ширина плечика</text>
<line x1='{sx(0):.1f}' y1='{sy(0):.1f}' x2='{sx(0):.1f}' y2='{sy(.28):.1f}' class='axis'/>
<text x='{sx(.03):.1f}' y='{sy(.30):.1f}'>+Y / Unity +Z — рейл и footprint</text>
<line x1='{sx(-hanger_half_x):.1f}' y1='{sy(-.055):.1f}' x2='{sx(hanger_half_x):.1f}' y2='{sy(-.055):.1f}' class='measure'/>
<text x='{sx(0):.1f}' y='{sy(-.085):.1f}' text-anchor='middle'>hanger X = {fmt(fbx_size.x)} wu</text>
<g transform='translate(570,105)'>
  <text font-weight='600'>Проверенный контракт</text>
  <text class='small' y='30'>HexRadius = {fmt(radius)} wu</text>
  <text class='small' y='52'>InteriorRadius = {interior}; BoundaryRadius = {boundary}</text>
  <text class='small' y='74'>junction step = {fmt(grid_step)} wu</text>
  <text class='small' y='96'>wardrobe footprint span = {fmt(2*grid_step)} wu</text>
  <text class='small' y='118'>hanger bounds = {fmt(fbx_size.x)} × {fmt(fbx_size.y)} × {fmt(fbx_size.z)} wu</text>
  <text class='small' y='140'>12 slots span = {fmt(slot_span)} wu; pitch = {fmt(slot_pitch)} wu</text>
  <text class='small' y='162'>HangerTemplate root = identity</text>
  <text class='small' y='184'>ClothingSlot rotation/scale = identity</text>
  <text class='small' y='218'>legacy BuildingRules placement:</text>
  <text class='small' y='240'>X = {fmt(wardrobe_x)}; Z = {fmt(wardrobe_z)}; yaw = {wardrobe_yaw:.0f}°</text>
  <text class='small' y='274'>runtime: shared footprint yaw only</text>
  <text class='small' y='296'>private 30°/90° compensation: forbidden</text>
</g>
<text class='small' x='24' y='560'>Source: Assets/ArtSource/Building/hexlive_building_kit.blend + furniture.wardrobe.fbx.</text>
<text class='small' x='24' y='580'>Generator validates source↔FBX bounds, 12 socket positions and identity exported transforms before writing this file.</text>
</svg>
"""
    OUTPUT.write_text(svg)
    print(
        f"WROTE {OUTPUT}: hanger={tuple(round(v, 6) for v in fbx_size)} "
        f"slot_span={slot_span:.6f} pitch={slot_pitch:.6f}")


if __name__ == '__main__':
    main()

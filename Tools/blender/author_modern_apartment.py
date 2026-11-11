"""Author the portal's 1:1 low-poly apartment kit, atomic model inputs and icons.

Run from the repository root with Blender in background mode:
  blender -b -t 1 --python Tools/blender/author_modern_apartment.py
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import re
import sys
import uuid

import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from author_atomic_world_props import collection, material, panel

ROOT = Path(__file__).resolve().parents[2]
OBJECTS = ROOT / "Assets/HexLiveContent/RuntimeSource/Objects"
ICONS = ROOT / "Assets/HexLiveContent/Icons"
ART = ROOT / "_ArtSource/ModernApartment"
RUNTIME = ROOT / "Assets/HexLiveContent/Portal"
MATERIALS = RUNTIME / "Materials"
SOURCE = ART / "modern_apartment.blend"
IMPORTER = OBJECTS / "furniture.wardrobe.fbx.meta"
SHADER = "933532a4fcc9baf4fa0491de14d08ed7"
NAMESPACE = uuid.UUID("77504e91-5d5e-4e48-93f2-df3856aaf1eb")
WALL_HEIGHT = 2.35
WALL_DEPTH = .095
DOOR_WIDTH = .500
DOOR_HEIGHT = 1.500
OPENING_CLEAR_WIDTH = .426
OPENING_CLEAR_HEIGHT = 1.452
WINDOW_APERTURE = (1.048, .866, 1.946)

# Linear colours. Timber and wall trim are derived from the authored island palette.
PALETTE = {
    "ApartmentIvory": ((.36, .32, .25), .77),
    "ApartmentCotton": ((.39, .40, .36), .90),
    "ApartmentSage": ((.11, .23, .19), .87),
    "ApartmentSageLight": ((.19, .34, .29), .88),
    "ApartmentTerra": ((.34, .13, .077), .83),
    "ApartmentSteel": ((.27, .30, .30), .44),
    "ApartmentPaleSteel": ((.36, .39, .38), .44),
    "ApartmentCharcoal": ((.045, .056, .064), .65),
    "ApartmentScreen": ((.026, .064, .095), .35),
    "ApartmentScreenLight": ((.067, .14, .18), .37),
    "ApartmentCream": ((.46, .38, .28), .88),
    "ApartmentCreamStripe": ((.52, .44, .34), .88),
    "ApartmentBlue": ((.16, .26, .36), .88),
    "ApartmentBlueStripe": ((.22, .34, .45), .88),
    "ApartmentBlueFloor": ((.15, .18, .23), .90),
    "ApartmentBlueFloorLight": ((.20, .23, .27), .90),
    "ApartmentGreen": ((.21, .32, .24), .87),
    "ApartmentGreenStripe": ((.28, .40, .30), .84),
    "ApartmentGreenFloor": ((.26, .31, .27), .72),
    "ApartmentGreenFloorLight": ((.32, .37, .32), .72),
    "ApartmentCitySky": ((.13, .21, .27), .85),
    "ApartmentCityHaze": ((.18, .26, .29), .85),
    "ApartmentCityFar": ((.074, .12, .16), .88),
    "ApartmentCityNear": ((.040, .073, .090), .90),
    "ApartmentCityLights": ((.39, .32, .17), .83),
}
STYLES = {
    "cream": ("ApartmentCream", "ApartmentCreamStripe", "ApartmentWalnut", "ApartmentWalnutLight"),
    "blue": ("ApartmentBlue", "ApartmentBlueStripe", "ApartmentBlueFloor", "ApartmentBlueFloorLight"),
    "green": ("ApartmentGreen", "ApartmentGreenStripe", "ApartmentGreenFloor", "ApartmentGreenFloorLight"),
}

MATS = {}
MODELS = {}
SHELF_LEVELS = {
    "shelf_low": (.03, .22, .12),
    "shelf_mid_low": (.03, .58, -.08),
    "shelf_mid_high": (.03, .94, .10),
    "shelf_top": (.03, 1.30, -.12),
}


def stable_guid(path):
    path = Path(path).relative_to(ROOT).as_posix()
    return uuid.uuid5(NAMESPACE, path).hex


def guid_for(path):
    meta = Path(str(path) + ".meta")
    if meta.exists():
        found = re.search(r"^guid: ([a-f0-9]{32})$", meta.read_text(encoding="utf-8"), re.M)
        if found:
            return found[1]
    return stable_guid(path)


def write_meta(path, importer):
    Path(str(path) + ".meta").write_text(
        "fileFormatVersion: 2\nguid: " + guid_for(path) + "\n" + importer,
        encoding="utf-8", newline="\n",
    )


def load_dimensions():
    spatial = (ROOT / "Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs").read_text(encoding="utf-8")
    radius = float(re.search(r"const float HexRadius = ([\d.]+)f;", spatial)[1])
    sqrt3 = float(re.search(r"const float Sqrt3 = ([\d.]+)f;", spatial)[1])
    layout = (ROOT / "Assets/HexLive/Simulation/Spatial/HexPointLayout.cs").read_text(encoding="utf-8")
    ring = int(re.search(r"const int BoundaryRadius = (\d+);", layout)[1])
    return radius, sqrt3 * radius / 2, radius / ring


def load_palette():
    with bpy.data.libraries.load(str(ROOT / "Assets/HexLiveContent/source.blend")) as (source, target):
        target.materials = [name for name in ("Heartwood", "Sapwood") if name in source.materials]
    woods = {entry.name: entry for entry in target.materials if entry}
    if set(woods) != {"Heartwood", "Sapwood"}:
        raise ValueError("The apartment palette requires the project Heartwood and Sapwood")
    def source_color(name):
        source = woods[name]
        shader = source.node_tree.nodes.get("Principled BSDF") if source.use_nodes else None
        return shader.inputs["Base Color"].default_value if shader is not None else source.diffuse_color

    heartwood, sapwood = source_color("Heartwood"), source_color("Sapwood")
    PALETTE["ApartmentWalnut"] = (tuple(float(channel) * factor for channel, factor in
                                        zip(heartwood[:3], (.50, .33, .24))), .72)
    PALETTE["ApartmentWalnutLight"] = (tuple(float(channel) * factor for channel, factor in
                                             zip(sapwood[:3], (.44, .30, .19))), .72)
    for name, (rgb, roughness) in PALETTE.items():
        MATS[name] = material(name, (*rgb, 1), roughness)


def model(identifier, kind, icon=False):
    owner = collection(identifier)
    MODELS[identifier] = (owner, kind, icon)
    return owner


def block(owner, label, center, size, finish, bevel=0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=center)
    obj = bpy.context.object
    obj.name = label
    for previous in tuple(obj.users_collection):
        previous.objects.unlink(obj)
    owner.objects.link(obj)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    obj.dimensions = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel:
        modifier = obj.modifiers.new("chamfer", "BEVEL")
        modifier.width = bevel
        modifier.segments = 1
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    obj.data.materials.append(MATS[finish] if isinstance(finish, str) else finish)
    return obj


def four_feet(owner, x, y, height, width, finish):
    for index, (px, py) in enumerate(((-x, -y), (-x, y), (x, -y), (x, y)), 1):
        block(owner, f"foot_{index}", (px, py, height / 2), (width, width, height), finish, .007)


def bed():
    owner = model("furniture.modern_bed", "low", True)
    four_feet(owner, .36, .75, .17, .080, "ApartmentWalnut")
    block(owner, "frame", (0, -.015, .205), (.92, 1.82, .12), "ApartmentWalnut", .021)
    block(owner, "headboard", (0, .862, .42), (.90, .084, .57), "ApartmentWalnutLight", .025)
    block(owner, "mattress", (0, -.055, .328), (.87, 1.69, .155), "ApartmentCotton", .032)
    block(owner, "quilt", (0, -.30, .421), (.89, 1.21, .078), "ApartmentSage", .025)
    block(owner, "quilt_cuff", (0, .272, .449), (.89, .104, .041), "ApartmentSageLight", .013)
    block(owner, "foot_drape", (0, -.884, .348), (.88, .047, .18), "ApartmentSage", .018)
    for i, x in enumerate((-.215, .215), 1):
        block(owner, f"pillow_{i}", (x, .581, .441), (.38, .315, .105), "ApartmentIvory", .038)


def fridge():
    owner = model("furniture.modern_fridge", "standing", True)
    four_feet(owner, .218, .24, .052, .075, "ApartmentCharcoal")
    block(owner, "insulated_shell", (0, .021, .811), (.612, .61, 1.528), "ApartmentSteel", .024)
    block(owner, "lower_door", (0, -.292, .565), (.606, .082, 1.01), "ApartmentPaleSteel", .020)
    block(owner, "freezer_door", (0, -.292, 1.315), (.606, .082, .462), "ApartmentPaleSteel", .020)
    block(owner, "lower_handle", (-.227, -.346, .873), (.034, .051, .222), "ApartmentCharcoal", .009)
    block(owner, "upper_handle", (-.227, -.346, 1.206), (.034, .051, .147), "ApartmentCharcoal", .009)
    block(owner, "control_panel", (.145, -.337, 1.412), (.125, .016, .049), "ApartmentScreen", .004)
    block(owner, "control_indicator", (.158, -.347, 1.414), (.034, .005, .012), "ApartmentSageLight")
    block(owner, "toe_vent", (0, -.289, .084), (.461, .034, .029), "ApartmentCharcoal")


def table():
    owner = model("furniture.modern_table", "low", True)
    four_feet(owner, .275, .407, .695, .058, "ApartmentWalnut")
    block(owner, "apron", (0, 0, .648), (.639, .924, .12), "ApartmentWalnut", .012)
    block(owner, "tabletop", (0, 0, .725), (.76, 1.06, .075), "ApartmentWalnutLight", .018)


def chair():
    owner = model("furniture.modern_chair", "standing", True)
    four_feet(owner, .163, .192, .428, .046, "ApartmentWalnut")
    block(owner, "seat_frame", (0, -.025, .407), (.425, .452, .065), "ApartmentWalnut", .012)
    block(owner, "seat_pad", (0, -.025, .451), (.430, .453, .072), "ApartmentTerra", .023)
    for i, x in enumerate((-.17, .17), 1):
        block(owner, f"back_stile_{i}", (x, .228, .670), (.047, .053, .485), "ApartmentWalnut", .008)
    block(owner, "upper_back_rail", (0, .231, .875), (.427, .060, .090), "ApartmentWalnutLight", .013)
    block(owner, "lower_back_rail", (0, .231, .728), (.369, .050, .064), "ApartmentWalnutLight", .011)


def sofa():
    owner = model("furniture.modern_sofa", "low", True)
    four_feet(owner, .275, .749, .115, .080, "ApartmentWalnut")
    block(owner, "base", (0, 0, .212), (.745, 1.83, .225), "ApartmentSage", .047)
    block(owner, "back", (.309, 0, .549), (.192, 1.68, .540), "ApartmentSage", .058)
    for i, y in enumerate((-.803, .803), 1):
        block(owner, f"arm_{i}", (-.022, y, .475), (.706, .224, .361), "ApartmentSage", .049)
    for i, y in enumerate((-.35, .35), 1):
        block(owner, f"seat_cushion_{i}", (-.085, y, .368), (.531, .677, .105), "ApartmentSageLight", .037)
        block(owner, f"back_cushion_{i}", (.197, y, .566), (.140, .662, .303), "ApartmentSageLight", .036)
    block(owner, "throw_pillow", (.066, -.472, .524), (.109, .304, .246), "ApartmentTerra", .041)


def television():
    owner = model("furniture.modern_tv", "low", True)
    four_feet(owner, .141, .447, .082, .059, "ApartmentWalnut")
    block(owner, "console", (0, 0, .210), (.402, 1.08, .265), "ApartmentWalnut", .023)
    block(owner, "console_top", (0, 0, .350), (.432, 1.11, .038), "ApartmentWalnutLight", .013)
    for i, y in enumerate((-.361, .361), 1):
        block(owner, f"cupboard_{i}", (-.211, y, .213), (.033, .305, .193), "ApartmentWalnutLight", .008)
        block(owner, f"cupboard_pull_{i}", (-.235, y, .242), (.022, .083, .018), "ApartmentCharcoal", .003)
    block(owner, "receiver", (-.209, 0, .206), (.042, .271, .099), "ApartmentCharcoal", .010)
    block(owner, "stand", (0, 0, .397), (.236, .398, .051), "ApartmentCharcoal", .011)
    block(owner, "screen_frame", (.029, 0, .674), (.103, .918, .516), "ApartmentCharcoal", .021)
    block(owner, "screen_glass", (-.029, 0, .674), (.021, .854, .451), "ApartmentScreen", .011)
    block(owner, "status_led", (-.027, -.382, .438), (.013, .031, .013), "ApartmentScreenLight", .003)


def shelf():
    owner = model("furniture.modern_shelf", "standing", True)
    for i, (x, y) in enumerate(((-.127, -.329), (-.127, .329), (.127, -.329), (.127, .329)), 1):
        block(owner, f"shelf_post_{i}", (x, y, .75), (.044, .042, 1.50), "ApartmentWalnut", .006)
    for name, (_, height, _) in SHELF_LEVELS.items():
        block(owner, name, (0, 0, height - .019), (.300, .670, .038), "ApartmentWalnutLight", .007)
        block(owner, name + "_back_rail", (.134, 0, height + .043),
              (.029, .632, .086), "ApartmentWalnut", .004)


def portal():
    owner = model("portal.modern_door", "door", True)
    # Same finishes as the apartment pocket doors. The portal hint is added
    # to the world object at runtime, leaving a single ordinary door asset.
    block(owner, "threshold", (0, 0, .009), (DOOR_WIDTH, .104, .018), "ApartmentWalnutLight", .004)
    for side in (-1, 1):
        block(owner, f"frame_stile_{side}", (side * .2315, 0, .745),
              (.037, .094, 1.472), "ApartmentWalnutLight", .004)
    block(owner, "frame_header", (0, 0, 1.476), (DOOR_WIDTH, .094, .048), "ApartmentWalnutLight", .005)
    block(owner, "door_leaf", (0, 0, .733), (.420, .043, 1.427), "ApartmentWalnut", .004)
    for face in (-1, 1):
        for i, z in enumerate((.380, 1.071), 1):
            block(owner, f"panel_{face}_{i}", (0, face * .023, z),
                  (.315, .007, .541), "ApartmentWalnutLight", .005)
        block(owner, f"latch_plate_{face}", (.166, face * .023, .714),
              (.035, .007, .073), "ApartmentCharcoal", .004)
        block(owner, f"latch_stem_{face}", (.166, face * .032, .724),
              (.019, .015, .017), "ApartmentSteel", .003)
        block(owner, f"latch_lever_{face}", (.141, face * .042, .724),
              (.067, .011, .016), "ApartmentSteel", .003)
        block(owner, f"glow_top_{face}", (0, face * .026, 1.447),
              (.420, .008, .004), "ApartmentIvory")
        block(owner, f"glow_latch_{face}", (.211, face * .026, .735),
              (.004, .008, 1.425), "ApartmentIvory")


def window():
    owner = model("architecture.modern_window", "window")
    block(owner, "sky", (0, 0, 1.408), (1.012, .012, 1.010), "ApartmentCitySky")
    skyline = (
        (-.443, .104, .510), (-.326, .083, .391), (-.237, .120, .615),
        (-.119, .107, .427), (.001, .109, .691), (.112, .090, .477),
        (.208, .097, .553), (.310, .094, .412), (.433, .117, .590),
    )
    for face in (-1, 1):
        block(owner, f"haze_{face}", (0, face * .010, 1.192), (1.012, .006, .542), "ApartmentCityHaze")
        for i, (x, width, height) in enumerate(skyline):
            depth = face * (.017 if i % 2 else .025)
            base = .920
            color = "ApartmentCityNear" if i % 2 else "ApartmentCityFar"
            block(owner, f"tower_{face}_{i}", (x, depth, base + height / 2),
                  (width, .004, height), color)
            for level in range(1, int(height / .080)):
                for side in (-1, 1):
                    if (i * 3 + level + side) % 3:
                        cx, cy, cz = x + side * width * .215, depth + face * .004, base + level * .080
                        points = [(cx + dx, cy, cz + dz) for dx, dz in
                                  ((-.006, -.0105), (.006, -.0105), (.006, .0105), (-.006, .0105))]
                        winding = (0, 1, 2, 3) if face < 0 else (3, 2, 1, 0)
                        panel(owner, f"light_{face}_{i}_{level}_{side}", points, [winding], MATS["ApartmentCityLights"])
    for side in (-1, 1):
        block(owner, f"window_stile_{side}", (side * .509, 0, 1.406),
              (.046, .094, 1.066), "ApartmentIvory", .003)
    block(owner, "window_head", (0, 0, 1.928), (1.064, .094, .052), "ApartmentIvory", .003)
    block(owner, "window_sill", (0, 0, .884), (1.064, .094, .052), "ApartmentIvory", .003)
    block(owner, "window_mullion", (0, 0, 1.406), (.033, .092, 1.044), "ApartmentIvory", .003)


def wall(style, radius):
    color, accent, _, _ = STYLES[style]
    owner = model("architecture.modern_wall_" + style, "wall")
    width, height = radius, 2.35
    block(owner, "wall_panel", (0, 0, height / 2), (width, .095, height), color)
    for side in (-1, 1):
        y = .053 * side
        block(owner, f"skirting_{side}", (0, y, .052), (width, .025, .104), "ApartmentIvory")
        if style == "cream":
            for i in range(-3, 4):
                block(owner, f"wallpaper_line_{side}_{i}", (i * .202, side * .049, 1.219),
                      (.038, .004, 2.224), accent)
        else:
            block(owner, f"wainscot_{side}", (0, side * .050, .477), (width, .006, .742), accent)
            block(owner, f"chair_rail_{side}", (0, y, .863), (width, .022, .042), "ApartmentIvory")
            if style == "green":
                for i in range(-2, 3):
                    block(owner, f"tile_joint_{side}_{i}", (i * .275, side * .054, .481),
                          (.010, .004, .734), color)
                block(owner, f"tile_course_{side}", (0, side * .054, .484), (width, .004, .010), color)


def wall_aperture(owner, radius, width, bottom, top):
    side_width = (radius - width) / 2
    for side in (-1, 1):
        block(owner, f"wall_stile_{side}", (side * (width + side_width) / 2, 0, (bottom + top) / 2),
              (side_width, WALL_DEPTH, top - bottom), "ApartmentCream")
    if bottom > 0:
        block(owner, "wall_sill", (0, 0, bottom / 2), (radius, WALL_DEPTH, bottom), "ApartmentCream")
    block(owner, "wall_header", (0, 0, (WALL_HEIGHT + top) / 2),
          (radius, WALL_DEPTH, WALL_HEIGHT - top), "ApartmentCream")


def opening(radius):
    owner = model("architecture.modern_opening", "opening")
    wall_aperture(owner, radius, DOOR_WIDTH, 0, DOOR_HEIGHT)
    stile_width = (DOOR_WIDTH - OPENING_CLEAR_WIDTH) / 2
    for side in (-1, 1):
        block(owner, f"opening_jamb_{side}", (side * (OPENING_CLEAR_WIDTH + stile_width) / 2, 0,
                                               OPENING_CLEAR_HEIGHT / 2),
              (stile_width, .114, OPENING_CLEAR_HEIGHT), "ApartmentWalnutLight")
    block(owner, "opening_header_trim", (0, 0, (DOOR_HEIGHT + OPENING_CLEAR_HEIGHT) / 2),
          (DOOR_WIDTH, .114, DOOR_HEIGHT - OPENING_CLEAR_HEIGHT), "ApartmentWalnutLight")
    # Open pocket door, with just the pull and leading edge visible at the jamb.
    block(owner, "open_pocket_door", (.2315, .054, .718),
          (.031, .017, 1.407), "ApartmentWalnut")
    block(owner, "door_pull", (.2315, .064, .724), (.018, .007, .067), "ApartmentSteel", .002)


def exit_jamb(radius):
    owner = model("architecture.modern_exit_jamb", "aperture")
    wall_aperture(owner, radius, DOOR_WIDTH, 0, DOOR_HEIGHT)


def window_jamb(radius):
    owner = model("architecture.modern_window_jamb", "aperture")
    wall_aperture(owner, radius, *WINDOW_APERTURE)


def clip_polygon(polygon, edge_a, edge_b):
    ax, ay = edge_a
    ex, ey = edge_b[0] - ax, edge_b[1] - ay

    def side(point):
        return ex * (point[1] - ay) - ey * (point[0] - ax)

    clipped = []
    for i, point in enumerate(polygon):
        before = polygon[i - 1]
        distance, prior = side(point), side(before)
        if (distance >= 0) != (prior >= 0):
            t = prior / (prior - distance)
            clipped.append(tuple(start + t * (end - start) for start, end in zip(before, point)))
        if distance >= 0:
            clipped.append(point)
    return clipped


def floor(style, radius, apothem):
    _, _, base, accent = STYLES[style]
    owner = model("architecture.modern_floor_" + style, "floor")
    # Simulation axial tiles are pointy-top. Rotating 30 degrees would expose seams.
    corners = [(0, -radius), (apothem, -radius / 2), (apothem, radius / 2),
               (0, radius), (-apothem, radius / 2), (-apothem, -radius / 2)]
    vertices = [(x, y, z) for z in (-.048, -.003) for x, y in corners]
    faces = [(5, 4, 3, 2, 1, 0), (6, 7, 8, 9, 10, 11)]
    faces += [(i, (i + 1) % 6, (i + 1) % 6 + 6, i + 6) for i in range(6)]
    panel(owner, "floor_substrate", vertices, faces, MATS[base])
    x_step, y_step, joint = {
        "cream": (.72, .225, .010), "blue": (.455, .455, .008), "green": (.435, .435, .013)
    }[style]
    for row in range(-7, 8):
        for col in range(-4, 5):
            x = col * x_step + (x_step * .5 if row % 2 and style == "cream" else 0)
            y = row * y_step
            rect = [(x + joint, y + joint), (x + x_step - joint, y + joint),
                    (x + x_step - joint, y + y_step - joint), (x + joint, y + y_step - joint)]
            for i, edge in enumerate(corners):
                rect = clip_polygon(rect, edge, corners[(i + 1) % 6])
                if len(rect) < 3:
                    break
            if len(rect) >= 3:
                tint = accent if (row * 11 + col * 17) % 7 < 3 else base
                panel(owner, f"floor_finish_{row}_{col}", [(px, py, 0) for px, py in rect],
                      [tuple(range(len(rect)))], MATS[tint])


def srgb(channel):
    return 12.92 * channel if channel <= .0031308 else 1.055 * channel ** (1 / 2.4) - .055


def color_yaml(rgb):
    return ", ".join(f"{key}: {value:.7g}" for key, value in zip("rgba", (*rgb, 1)))


def write_unity_material(name, directory=MATERIALS, palette=PALETTE):
    mat = directory / (name + ".mat")
    rgb, roughness = palette[name]
    mat.write_text(f"""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: {name}
  m_Shader: {{fileID: 4800000, guid: {SHADER}, type: 3}}
  m_Parent: {{fileID: 0}}
  m_ModifiedSerializedProperties: 0
  m_ValidKeywords: []
  m_InvalidKeywords: []
  m_LightmapFlags: 4
  m_EnableInstancingVariants: 1
  m_DoubleSidedGI: 0
  m_CustomRenderQueue: -1
  stringTagMap: {{}}
  disabledShaderPasses: []
  m_LockedProperties: 
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs: []
    m_Ints: []
    m_Floats:
    - _Cull: 2
    - _Smoothness: {1 - roughness:.7g}
    - _Metallic: 0
    - _Surface: 0
    - _SrcBlend: 1
    - _DstBlend: 0
    - _ZWrite: 1
    m_Colors:
    - _BaseColor: {{{color_yaml(tuple(map(srgb, rgb)))}}}
    - _EmissionColor: {{r: 0, g: 0, b: 0, a: 1}}
  m_BuildTextureStacks: []
""", encoding="utf-8", newline="\n")
    write_meta(mat, "NativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 2100000\n  userData: \n"
                    "  assetBundleName: \n  assetBundleVariant: \n")


def bake_parts(name, sources):
    export = bpy.data.objects.new(name, bpy.data.meshes.new(name))
    bpy.context.scene.collection.objects.link(export)
    bpy.ops.object.select_all(action="DESELECT")
    export.select_set(True)
    bpy.context.view_layer.objects.active = export
    for source in sources:
        mesh = source.data.copy()
        mesh.transform(source.matrix_world)
        copy = bpy.data.objects.new(source.name + "_baked", mesh)
        bpy.context.scene.collection.objects.link(copy)
        copy.select_set(True)
    bpy.ops.object.join()
    bpy.context.view_layer.objects.active = export
    export.data.name = name
    export.data.validate()
    for face in export.data.polygons:
        face.use_smooth = False
    export.data.calc_loop_triangles()
    return export


def export_model(identifier, owner, kind, has_icon, material_directory=MATERIALS, origin="modern"):
    # Keep a single Renderer per prop unless presentation needs an independently
    # addressable seam or wallpaper shell. Both retain the same identity pivot.
    groups = {
        "door": (("glow_", "door_glow"), (None, "door_panel")),
        "opening": (("wall_", "wall_shell"), (None, "door_frame")),
        "window": (("window_", "window_frame"), (None, "city_backdrop")),
        "aperture": ((None, "wall_shell"),),
    }.get(kind)
    parts = []
    if groups:
        root = bpy.data.objects.new(identifier, None)
        bpy.context.scene.collection.objects.link(root)
        remaining = list(owner.objects)
        for prefix, name in groups:
            sources = remaining if prefix is None else [obj for obj in remaining if obj.name.startswith(prefix)]
            part = bake_parts(name, sources)
            part.parent = root
            parts.append(part)
            remaining = [obj for obj in remaining if obj not in sources]
        if remaining:
            raise ValueError(f"{identifier} contains unassigned source objects")
    else:
        root = bake_parts(identifier, owner.objects)
        parts.append(root)
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for part in parts:
        part.select_set(True)
    triangles = sum(len(part.data.loop_triangles) for part in parts)
    if triangles > 1500:
        raise ValueError(f"{identifier} exceeds the WebGL mesh budget: {triangles}")

    path = OBJECTS / (identifier + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=str(path), use_selection=True, object_types={"MESH", "EMPTY"},
        global_scale=1.0, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y", add_leaf_bones=False, bake_anim=False,
        use_mesh_modifiers=True, mesh_smooth_type="FACE", path_mode="STRIP",
    )
    names = list(dict.fromkeys(slot.material.name for part in parts for slot in part.material_slots if slot.material))
    points = [vertex.co for part in parts for vertex in part.data.vertices]
    minimum = [min(point[i] for point in points) for i in range(3)]
    maximum = [max(point[i] for point in points) for i in range(3)]
    node_names = [part.name for part in parts]
    for part in parts:
        bpy.data.objects.remove(part, do_unlink=True)
    if groups:
        bpy.data.objects.remove(root, do_unlink=True)

    template = IMPORTER.read_text(encoding="utf-8").split("ModelImporter:", 1)[1]
    remaps = "  externalObjects:\n" + "".join(
        "  - first:\n      type: UnityEngine:Material\n      assembly: UnityEngine.CoreModule\n"
        f"      name: {name}\n    second: {{fileID: 2100000, guid: {guid_for(material_directory / (name + '.mat'))}, type: 2}}\n"
        for name in names
    )
    template = template.replace("  externalObjects: {}\n", remaps).replace("  importAnimation: 1", "  importAnimation: 0")
    write_meta(path, "ModelImporter:" + template)
    result = {"type": "object", "id": identifier, "main": path.relative_to(ROOT).as_posix(),
              "metadata": {"contentOrigin": origin, "hasIcon": has_icon,
                           "legacyResourcePath": "HexLive/Objects/" + identifier,
                           "legacyResourceFolder": "HexLive/Objects"},
              "shape": kind, "materials": names, "triangles": triangles,
              "blenderBounds": {"min": minimum, "max": maximum}}
    if has_icon:
        result["icon"] = (ICONS / (identifier + ".png")).relative_to(ROOT).as_posix()
    if groups:
        result["meshNodes"] = node_names
    if identifier in ("architecture.modern_opening", "architecture.modern_exit_jamb"):
        result["wallAperture"] = (DOOR_WIDTH, 0, DOOR_HEIGHT)
    if identifier == "architecture.modern_window_jamb":
        result["wallAperture"] = WINDOW_APERTURE
    if identifier == "architecture.modern_opening":
        result["clearPassage"] = (OPENING_CLEAR_WIDTH, OPENING_CLEAR_HEIGHT)
    if kind in ("low", "standing"):
        x, y, z = (b - a for a, b in zip(minimum, maximum))
        result["targetWorldSize"] = max(x, y) if identifier.endswith("_bed") else max(x, y, z)
    if identifier == "furniture.modern_shelf":
        result["placementSlotsUnity"] = SHELF_LEVELS
    if identifier == "furniture.modern_table":
        result["placementSurfaceHeight"] = .7625
    print(f"{identifier}: {triangles} triangles, {len(names)} materials, {minimum} -> {maximum}", flush=True)
    return result


def verify_export(row, radius, apothem):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=str(ROOT / row["main"]))
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    roots = [obj for obj in bpy.context.scene.objects if obj.parent is None]
    node_names = row.get("meshNodes", [row["id"]])
    if len(roots) != 1 or {mesh.name for mesh in meshes} != set(node_names):
        raise ValueError(f"{row['id']} did not reimport with the expected hierarchy: {[mesh.name for mesh in meshes]}")
    for obj in bpy.context.scene.objects:
        identity = [float(v) for v in obj.location] + [float(v) for v in obj.rotation_euler]
        if max(map(abs, identity)) > .0001 or any(abs(scale - 1) > .0001 for scale in obj.scale):
            raise ValueError(f"{row['id']} reimported with a non-identity transform: {obj.name}")
    points = [mesh.matrix_world @ vertex.co for mesh in meshes for vertex in mesh.data.vertices]
    minimum = [min(point[i] for point in points) for i in range(3)]
    maximum = [max(point[i] for point in points) for i in range(3)]
    if any(abs(a - b) > .0001 for a, b in zip(minimum + maximum,
           row["blenderBounds"]["min"] + row["blenderBounds"]["max"])):
        raise ValueError(f"{row['id']} changed bounds on FBX round trip")
    for mesh in meshes:
        mesh.data.calc_loop_triangles()
    if sum(len(mesh.data.loop_triangles) for mesh in meshes) != row["triangles"]:
        raise ValueError(f"{row['id']} changed topology on FBX round trip")
    if {slot.material.name for mesh in meshes for slot in mesh.material_slots} != set(row["materials"]):
        raise ValueError(f"{row['id']} lost an FBX material binding")
    x, y, z = (b - a for a, b in zip(minimum, maximum))
    shape = row["shape"]
    if shape in ("standing", "low"):
        if abs(minimum[2]) > .0001 or y < x or abs(minimum[1] + maximum[1]) > .11:
            raise ValueError(f"{row['id']} violates the centered furniture pivot or local +Y footprint")
        if (shape == "standing") != (z > max(x, y)):
            raise ValueError(f"{row['id']} has the wrong height profile")
        # A circle at the footprint's largest corner fits in every six-way yaw.
        if max(math.hypot(point.x, point.y) for point in points) >= apothem:
            raise ValueError(f"{row['id']} reaches beyond its tile")
    elif shape == "floor":
        if abs(maximum[2]) > .0001 or abs(x - 2 * apothem) > .0001 or abs(y - 2 * radius) > .0001:
            raise ValueError(f"{row['id']} does not match the pointy-top tile")
    elif shape in ("wall", "opening", "aperture") and (abs(minimum[2]) > .0001 or abs(x - radius) > .0001):
        raise ValueError(f"{row['id']} does not span exactly one boundary edge")
    elif shape == "door" and (abs(minimum[2]) > .0001 or abs(z - DOOR_HEIGHT) > .0001 or abs(x - DOOR_WIDTH) > .0001):
        raise ValueError(f"{row['id']} does not fit the exact return-door aperture")
    elif shape == "window" and (abs(x - 1.064) > .0001 or abs(minimum[2] - .858) > .0001 or
                                abs(maximum[2] - 1.954) > .0001 or abs(minimum[1] + .047) > .0001 or
                                abs(maximum[1] - .047) > .0001):
        raise ValueError(f"{row['id']} does not match the wall-mounted window envelope")
    if "wallAperture" in row:
        width, bottom, top = row["wallAperture"]
        shell = next(mesh for mesh in meshes if mesh.name == "wall_shell")
        half_width = width / 2
        middle = (bottom + top) / 2
        for px in (-half_width + .002, 0, half_width - .002):
            for pz in (bottom + .002, middle, top - .002):
                hit, *_ = shell.ray_cast(Vector((px, -.10, pz)), Vector((0, 1, 0)), distance=.20)
                if hit:
                    raise ValueError(f"{row['id']} wall geometry occupies its authored aperture")
        surround = [(-half_width - .002, middle), (half_width + .002, middle), (0, top + .002)]
        if bottom > 0:
            surround.append((0, bottom - .002))
        for px, pz in surround:
            hit, *_ = shell.ray_cast(Vector((px, -.10, pz)), Vector((0, 1, 0)), distance=.20)
            if not hit:
                raise ValueError(f"{row['id']} leaves an air gap outside its aperture")
    if "clearPassage" in row:
        width, height = row["clearPassage"]
        for px in (-width / 2 + .002, 0, width / 2 - .002):
            for pz in (.002, height / 2, height - .002):
                for mesh in meshes:
                    hit, *_ = mesh.ray_cast(Vector((px, -.10, pz)), Vector((0, 1, 0)), distance=.20)
                    if hit:
                        raise ValueError(f"{row['id']} trim obstructs the clear passage")
    surfaces = row.get("placementSlotsUnity", {})
    if "placementSurfaceHeight" in row:
        surfaces = {"table_top": (0, row["placementSurfaceHeight"], 0)}
    for name, (local_x, height, local_z) in surfaces.items():
        origin = Vector((-local_x, -local_z, height + .010))
        hit, point, normal, _ = meshes[0].ray_cast(origin, Vector((0, 0, -1)), distance=.025)
        if not hit or abs(point.z - height) > .0001 or normal.z < .99:
            raise ValueError(f"{row['id']} surface slot {name} is not on its authored mesh")
    return meshes


def render_icon(row, meshes):
    sys.path.insert(0, str(ROOT / "Tools"))
    from render_item_icon import STYLES, install, render, setup_materials

    identifier = row["id"]
    icon_dir = ROOT / "Build/Diagnostics/portal-furniture/icons"
    icon_dir.mkdir(parents=True, exist_ok=True)
    icon = icon_dir / (identifier + ".png")
    default = STYLES["tool"]
    direction = Vector((-1, -.58, .61) if identifier in ("furniture.modern_sofa", "furniture.modern_tv", "furniture.modern_shelf")
                       else (.72, -1, .54))
    STYLES["tool"] = (direction, *default[1:])
    try:
        bpy.context.scene.render.threads_mode = "FIXED"
        bpy.context.scene.render.threads = 1
        setup_materials(meshes, "tool")
        fit = {"furniture.modern_chair": 1.25, "chair.basic": 1.43}.get(identifier, 1.18)
        render(str(icon), meshes, "tool", fit, 20)
        install(str(icon), identifier)
    finally:
        STYLES["tool"] = default


def main():
    radius, apothem, junction_step = load_dimensions()
    os_dirs = (ART, RUNTIME, MATERIALS)
    for directory in os_dirs:
        directory.mkdir(parents=True, exist_ok=True)
    for directory in (RUNTIME, MATERIALS):
        if not Path(str(directory) + ".meta").exists():
            write_meta(directory, "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n"
                       "  assetBundleName: \n  assetBundleVariant: \n")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    load_palette()
    for author in (bed, fridge, table, chair, sofa, television, shelf, portal, window):
        author()
    for style in STYLES:
        wall(style, radius)
        floor(style, radius, apothem)
    opening(radius)
    exit_jamb(radius)
    window_jamb(radius)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE), compress=True)
    for name in MATS:
        write_unity_material(name)
    rows = [export_model(identifier, *values) for identifier, values in MODELS.items()]
    receipt = {"schemaVersion": 1, "origin": "modern", "stage": "authoring", "hexRadius": radius,
               "hexApothem": apothem, "junctionStep": junction_step, "records": rows}
    manifest = RUNTIME / "modern-apartment-art.json"
    manifest.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8", newline="\n")
    write_meta(manifest, "TextScriptImporter:\n  externalObjects: {}\n  userData: \n"
                         "  assetBundleName: \n  assetBundleVariant: \n")
    build = ROOT / "Build/Diagnostics/portal-furniture"
    build.mkdir(parents=True, exist_ok=True)
    for row in rows:
        meshes = verify_export(row, radius, apothem)
        print(f"VERIFIED {row['id']}", flush=True)
        if "icon" in row:
            render_icon(row, meshes)
    (build / "fbx-verification.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()


"""Build HexLive's simple modular prosthetic set inside the open Blender scene.

The meshes deliberately follow the construction language already present in the
project's workbench/bed scene: 8-sided wood, 12-sided bindings, flat faces and
separate material blocks.  Every limb is authored along local +Y with its joint
at Y=0 and organic hand/foot endpoint at Y=1, allowing Unity to fit one asset to
any actor by scaling it to the live bone-to-bone distance.

Run from Blender (or Blender MCP):
    exec(compile(open(".../Tools/make_prosthetic_models.py").read(),
                 "make_prosthetic_models.py", "exec"))
"""

from __future__ import annotations

import math
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


PROJECT_ROOT = Path("/Volumes/ORICO/HexLive")
EXPORT_ROOT = PROJECT_ROOT / "Assets/HexLiveContent/Prosthetics"
SOURCE_ROOT = PROJECT_ROOT / "Assets/ArtSource/Prosthetics"
COLLECTION_NAME = "PROSTHETIC_EXPORT"
REFERENCE_COLLECTION_NAME = "PROSTHETIC_REFERENCE"
WOOD_PREVIEW_COLLECTION_NAME = "PROSTHETIC_FIT_WOOD"
MECHANICAL_PREVIEW_COLLECTION_NAME = "PROSTHETIC_FIT_MECHANICAL"
FIT_SCENE_NAME = "ProstheticFit"
MARTA_FBX = PROJECT_ROOT / "Assets/ImportedActors/Actors/Marta/Marta.new.fbx"

# Measured from Marta.new.fbx, rest pose. The exported assets remain normalised
# joint-to-end = 1; these numbers fix the *cross-section and distal overhang*
# which uniform runtime scaling cannot infer.
MARTA_ARM_LENGTH_M = 0.263512  # r/lForearmBend -> r/lHand (wrist)
MARTA_LEG_LENGTH_M = 0.435129  # r/lShin -> r/lFoot (ankle)
ARM_HAND_OVERHANG = 0.66       # Marta hand is 0.1825 m = 0.692 of forearm
ARM_HAND_WIDTH = 0.44          # Marta hand incl. thumb is 0.1255 m = 0.476
ARM_HAND_THICKNESS = 0.19      # Marta hand is 0.0620 m = 0.235
LEG_FOOT_LENGTH = 0.55         # Marta foot is 0.2396 m = 0.551 of shin
LEG_FOOT_WIDTH = 0.255         # Marta foot is 0.1107 m = 0.254
LEG_FOOT_HEIGHT = 0.225        # Marta foot is 0.1066 m = 0.245
LEG_FOOT_FORWARD_CENTER = 0.14 # heel -0.134, toe +0.416 from ankle

AUTHORING_SCENE = bpy.data.scenes.get("Scene") or bpy.context.scene
if bpy.context.window is not None:
    bpy.context.window.scene = AUTHORING_SCENE


def material(name: str, color: tuple[float, float, float, float], *, metallic=0.0, roughness=0.75):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = color
    mat.use_nodes = True
    principled = next((node for node in mat.node_tree.nodes if node.type == "BSDF_PRINCIPLED"), None)
    if principled:
        base = principled.inputs.get("Base Color")
        metal = principled.inputs.get("Metallic")
        rough = principled.inputs.get("Roughness")
        alpha = principled.inputs.get("Alpha")
        if base:
            base.default_value = color
        if metal:
            metal.default_value = metallic
        if rough:
            rough.default_value = roughness
        if alpha:
            alpha.default_value = color[3]
    if color[3] < 0.999:
        if hasattr(mat, "surface_render_method"):
            try:
                mat.surface_render_method = "DITHERED"
            except (TypeError, ValueError):
                mat.surface_render_method = "BLENDED"
        elif hasattr(mat, "blend_method"):
            mat.blend_method = "BLEND"
    return mat


WOOD = material("Prosthetic_Wood", (0.55, 0.36, 0.17, 1.0), roughness=0.86)
WOOD_LIGHT = material("Prosthetic_WoodLight", (0.72, 0.53, 0.28, 1.0), roughness=0.82)
WOOD_DARK = material("Prosthetic_WoodDark", (0.25, 0.14, 0.065, 1.0), roughness=0.92)
ROPE = material("Prosthetic_Rope", (0.67, 0.57, 0.38, 1.0), roughness=0.96)
LEATHER = material("Prosthetic_Leather", (0.23, 0.105, 0.055, 1.0), roughness=0.9)
METAL = material("Prosthetic_Metal", (0.29, 0.32, 0.34, 1.0), metallic=0.72, roughness=0.48)
METAL_DARK = material("Prosthetic_MetalDark", (0.085, 0.095, 0.105, 1.0), metallic=0.82, roughness=0.38)
COPPER = material("Prosthetic_Copper", (0.45, 0.20, 0.075, 1.0), metallic=0.58, roughness=0.52)
REFERENCE_MATERIAL = material("Marta_ProstheticReference", (0.12, 0.42, 0.68, 0.22), roughness=0.9)


def collection():
    old = bpy.data.collections.get(COLLECTION_NAME)
    if old:
        for obj in list(old.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(old)
    made = bpy.data.collections.new(COLLECTION_NAME)
    AUTHORING_SCENE.collection.children.link(made)
    return made


TARGET_COLLECTION = collection()


def move_to_collection(obj):
    for owner in list(obj.users_collection):
        owner.objects.unlink(obj)
    TARGET_COLLECTION.objects.link(obj)


def finish(obj, parent, mat, name):
    obj.name = name
    obj.data.name = f"{name}_Mesh"
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    for polygon in obj.data.polygons:
        polygon.use_smooth = False
    move_to_collection(obj)
    obj.parent = parent
    return obj


def cylinder(name, parent, radius, depth, y, mat, *, vertices=8, x=0.0, z=0.0):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices,
        radius=radius,
        depth=depth,
        location=(x, y, z),
        rotation=(math.pi * 0.5, 0.0, 0.0),
    )
    obj = bpy.context.object
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def cone(name, parent, radius_start, radius_end, depth, y, mat, *, vertices=8, x=0.0, z=0.0):
    # The default cone runs along Z; after the X rotation its bottom/top radii
    # become the two ends along the prosthetic's +Y construction axis.
    bpy.ops.mesh.primitive_cone_add(
        vertices=vertices,
        radius1=radius_start,
        radius2=radius_end,
        depth=depth,
        location=(x, y, z),
        rotation=(math.pi * 0.5, 0.0, 0.0),
    )
    obj = bpy.context.object
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def box(name, parent, size, location, mat, *, bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location)
    obj = bpy.context.object
    obj.dimensions = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel > 0.0:
        mod = obj.modifiers.new("OneFacetBevel", "BEVEL")
        mod.width = bevel
        mod.segments = 1
        mod.affect = "EDGES"
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=mod.name)
    return finish(obj, parent, mat, name)


def rod_between(name, parent, start, end, radius, mat, *, vertices=8):
    start_v = Vector(start)
    end_v = Vector(end)
    direction = end_v - start_v
    midpoint = (start_v + end_v) * 0.5
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices,
        radius=radius,
        depth=direction.length,
        location=midpoint,
    )
    obj = bpy.context.object
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = direction.to_track_quat("Z", "Y")
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def torus_binding(name, parent, y, major_radius, minor_radius, mat, *, x=0.0, z=0.0):
    bpy.ops.mesh.primitive_torus_add(
        major_radius=major_radius,
        minor_radius=minor_radius,
        major_segments=12,
        minor_segments=4,
        location=(x, y, z),
        rotation=(math.pi * 0.5, 0.0, 0.0),
    )
    obj = bpy.context.object
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def empty(name, parent, location, *, role=None):
    obj = bpy.data.objects.new(name, None)
    TARGET_COLLECTION.objects.link(obj)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.045
    obj.location = location
    obj.parent = parent
    if role:
        obj["hexlive_landmark"] = role
    return obj


def root(name, limb):
    obj = bpy.data.objects.new(name, None)
    TARGET_COLLECTION.objects.link(obj)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.075
    obj["hexlive_landmark"] = "root"
    obj["hexlive_limb"] = limb
    obj["hexlive_prosthetic_axis"] = "+Y"
    obj["hexlive_reference_length"] = 1.0
    obj["hexlive_marta_length_m"] = (
        MARTA_ARM_LENGTH_M if limb == "arm" else MARTA_LEG_LENGTH_M
    )
    return obj


def add_landmarks(root_obj, limb):
    """Stable authoring anchors shipped in every FBX.

    Joint is the amputated-limb attachment; End is the wrist/ankle bone target.
    Grip sits inside the primitive palm. It is authoring metadata today (the
    game recreates a non-collapsing live grip), but makes the asset independently
    inspectable and ready for a future skeleton-driven wearer.
    """
    prefix = root_obj.name
    empty(f"{prefix}__joint", root_obj, (0.0, 0.0, 0.0), role="joint")
    empty(f"{prefix}__end", root_obj, (0.0, 1.0, 0.0), role="end")
    grip_y = 1.20 if limb == "arm" else 1.0
    empty(f"{prefix}__grip", root_obj, (0.0, grip_y, 0.0), role="grip")


def wooden_arm(side):
    suffix = "L" if side < 0 else "R"
    r = root(f"WoodArm{suffix}", "arm")
    cone("Socket", r, 0.18, 0.135, 0.20, 0.08, LEATHER, vertices=10)
    torus_binding("BindingUpper", r, -0.005, 0.173, 0.020, ROPE)
    torus_binding("BindingLower", r, 0.16, 0.132, 0.017, ROPE)
    rod_between("ElbowPin", r, (-0.20, 0.105, 0.0),
                (0.20, 0.105, 0.0), 0.052, WOOD_DARK, vertices=10)
    cone("ForearmWood", r, 0.105, 0.070, 0.72, 0.55, WOOD_LIGHT, vertices=8)
    torus_binding("WristBinding", r, 0.93, 0.077, 0.015, ROPE)

    # Wrist is Y=1. The primitive hand continues beyond it instead of ending
    # there; its proportions come from Marta's full hand + finger vertex groups.
    box("Palm", r, (0.34, 0.32, ARM_HAND_THICKNESS),
        (0.0, 1.15, -0.006), WOOD, bevel=0.024)
    rod_between("FixedFingerOuter", r, (0.115 * side, 1.27, 0.018),
                (0.145 * side, 1.0 + ARM_HAND_OVERHANG, 0.025),
                0.040, WOOD_LIGHT, vertices=7)
    rod_between("FixedFingerInner", r, (-0.105 * side, 1.27, -0.018),
                (-0.105 * side, 1.60, 0.018), 0.037, WOOD_LIGHT, vertices=7)
    rod_between("Thumb", r, (0.17 * side, 1.12, -0.01),
                (0.22 * side, 1.38, 0.055), 0.038, WOOD_LIGHT, vertices=7)
    torus_binding("PalmBinding", r, 0.985, 0.090, 0.013, ROPE)
    add_landmarks(r, "arm")
    return r


def wooden_leg(side):
    suffix = "L" if side < 0 else "R"
    r = root(f"WoodLeg{suffix}", "leg")
    cone("Socket", r, 0.19, 0.15, 0.22, 0.085, LEATHER, vertices=10)
    torus_binding("BindingUpper", r, -0.01, 0.184, 0.021, ROPE)
    torus_binding("BindingLower", r, 0.175, 0.146, 0.018, ROPE)
    rod_between("KneePin", r, (-0.22, 0.13, 0.0),
                (0.22, 0.13, 0.0), 0.064, WOOD_DARK, vertices=10)
    cone("ShinWood", r, 0.115, 0.075, 0.68, 0.55, WOOD_LIGHT, vertices=8)
    torus_binding("AnkleBinding", r, 0.91, 0.081, 0.015, ROPE)
    box("Foot", r, (LEG_FOOT_WIDTH, LEG_FOOT_HEIGHT - 0.045, LEG_FOOT_LENGTH),
        (0.0, 1.035, LEG_FOOT_FORWARD_CENTER), WOOD, bevel=0.025)
    box("Sole", r, (LEG_FOOT_WIDTH + 0.018, 0.045, LEG_FOOT_LENGTH + 0.02),
        (0.0, 1.145, LEG_FOOT_FORWARD_CENTER), WOOD_DARK, bevel=0.012)
    add_landmarks(r, "leg")
    return r


def mechanical_arm(side):
    suffix = "L" if side < 0 else "R"
    r = root(f"MechanicalArm{suffix}", "arm")
    cone("Socket", r, 0.18, 0.136, 0.20, 0.08, METAL_DARK, vertices=10)
    torus_binding("SocketBandUpper", r, -0.005, 0.174, 0.017, COPPER)
    torus_binding("SocketBandLower", r, 0.16, 0.132, 0.014, COPPER)
    rod_between("ElbowHub", r, (-0.19, 0.11, 0.0),
                (0.19, 0.11, 0.0), 0.066, METAL_DARK, vertices=12)
    rod_between("ElbowAxle", r, (-0.205, 0.11, 0.0),
                (0.205, 0.11, 0.0), 0.043, COPPER, vertices=12)
    rod_between("RailOuter", r, (0.075 * side, 0.20, 0.035),
                (0.060 * side, 0.91, 0.027), 0.028, METAL, vertices=8)
    rod_between("RailInner", r, (-0.075 * side, 0.20, -0.035),
                (-0.060 * side, 0.91, -0.027), 0.028, METAL, vertices=8)
    rod_between("Piston", r, (0.0, 0.23, -0.025),
                (0.0, 0.89, 0.025), 0.021, COPPER, vertices=8)
    cylinder("WristHub", r, 0.090, 0.11, 0.94, METAL_DARK, vertices=12)
    box("Palm", r, (0.34, 0.32, ARM_HAND_THICKNESS),
        (0.0, 1.15, -0.006), METAL, bevel=0.022)
    rod_between("FingerOuter", r, (0.115 * side, 1.27, 0.020),
                (0.145 * side, 1.0 + ARM_HAND_OVERHANG, 0.025),
                0.036, METAL_DARK, vertices=8)
    rod_between("FingerInner", r, (-0.105 * side, 1.27, -0.020),
                (-0.105 * side, 1.60, 0.018), 0.034, METAL_DARK, vertices=8)
    rod_between("FingerCenter", r, (0.0, 1.28, -0.045),
                (0.0, 1.64, -0.025), 0.029, COPPER, vertices=8)
    rod_between("Thumb", r, (0.17 * side, 1.12, -0.01),
                (0.22 * side, 1.38, 0.055), 0.034, METAL_DARK, vertices=8)
    add_landmarks(r, "arm")
    return r


def mechanical_leg(side):
    suffix = "L" if side < 0 else "R"
    r = root(f"MechanicalLeg{suffix}", "leg")
    cone("Socket", r, 0.19, 0.15, 0.22, 0.085, METAL_DARK, vertices=10)
    torus_binding("SocketBandUpper", r, -0.01, 0.184, 0.019, COPPER)
    torus_binding("SocketBandLower", r, 0.175, 0.146, 0.016, COPPER)
    rod_between("KneeHub", r, (-0.22, 0.13, 0.0),
                (0.22, 0.13, 0.0), 0.075, METAL_DARK, vertices=12)
    rod_between("KneeAxle", r, (-0.235, 0.13, 0.0),
                (0.235, 0.13, 0.0), 0.048, COPPER, vertices=12)
    rod_between("ShinRailOuter", r, (0.078 * side, 0.22, 0.035),
                (0.062 * side, 0.91, 0.025), 0.032, METAL, vertices=8)
    rod_between("ShinRailInner", r, (-0.078 * side, 0.22, -0.035),
                (-0.062 * side, 0.91, -0.025), 0.032, METAL, vertices=8)
    rod_between("ShinPiston", r, (0.0, 0.25, -0.03),
                (0.0, 0.89, 0.03), 0.023, COPPER, vertices=8)
    cylinder("AnkleHub", r, 0.094, 0.11, 0.92, METAL_DARK, vertices=12)
    box("FootFrame", r, (LEG_FOOT_WIDTH, LEG_FOOT_HEIGHT - 0.045, LEG_FOOT_LENGTH),
        (0.0, 1.035, LEG_FOOT_FORWARD_CENTER), METAL, bevel=0.022)
    box("Sole", r, (LEG_FOOT_WIDTH + 0.018, 0.042, LEG_FOOT_LENGTH + 0.02),
        (0.0, 1.145, LEG_FOOT_FORWARD_CENTER), METAL_DARK, bevel=0.010)
    rod_between("ToeBrace", r,
                (-0.105, 1.02, LEG_FOOT_FORWARD_CENTER + 0.24),
                (0.105, 1.02, LEG_FOOT_FORWARD_CENTER + 0.24),
                0.020, COPPER, vertices=8)
    add_landmarks(r, "leg")
    return r


def hierarchy(root_obj):
    found = [root_obj]
    stack = list(root_obj.children)
    while stack:
        child = stack.pop()
        found.append(child)
        stack.extend(child.children)
    return found


def export(root_obj, filename):
    for obj in bpy.context.scene.objects:
        obj.select_set(False)
    old_location = root_obj.location.copy()
    root_obj.location = (0.0, 0.0, 0.0)
    for obj in hierarchy(root_obj):
        obj.hide_set(False)
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root_obj
    filepath = EXPORT_ROOT / filename
    bpy.ops.export_scene.fbx(
        filepath=str(filepath),
        use_selection=True,
        object_types={"EMPTY", "MESH"},
        use_custom_props=True,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        bake_anim=False,
        axis_forward="-Z",
        axis_up="Y",
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        path_mode="AUTO",
    )
    root_obj.location = old_location


def replace_collection(name):
    old = bpy.data.collections.get(name)
    if old:
        for obj in list(old.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(old)
    return bpy.data.collections.new(name)


def ensure_marta_reference():
    reference = bpy.data.collections.get(REFERENCE_COLLECTION_NAME)
    if reference and any(obj.type == "ARMATURE" for obj in reference.objects):
        return reference

    if reference:
        for obj in list(reference.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(reference)

    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(MARTA_FBX), use_anim=False)
    imported = [obj for obj in bpy.data.objects if obj not in before]
    reference = bpy.data.collections.new(REFERENCE_COLLECTION_NAME)
    AUTHORING_SCENE.collection.children.link(reference)
    for obj in imported:
        for owner in list(obj.users_collection):
            owner.objects.unlink(obj)
        reference.objects.link(obj)
        obj["hexlive_reference_only"] = True

    return reference


def style_reference(reference):
    for obj in reference.objects:
        obj.hide_render = False
        if obj.type == "MESH":
            obj.display_type = "SOLID"
            obj.color = REFERENCE_MATERIAL.diffuse_color
            obj.data.materials.clear()
            obj.data.materials.append(REFERENCE_MATERIAL)
        elif obj.type == "ARMATURE":
            obj.show_in_front = True
            obj.data.display_type = "OCTAHEDRAL"


def duplicate_hierarchy(root_obj, target_collection, prefix):
    originals = hierarchy(root_obj)
    clones = {}
    for original in originals:
        clone = original.copy()
        clone.name = f"{prefix}_{original.name}"
        if original.data is not None:
            clone.data = original.data
        target_collection.objects.link(clone)
        clones[original] = clone

    for original, clone in clones.items():
        if original.parent in clones:
            clone.parent = clones[original.parent]
            clone.matrix_parent_inverse = original.matrix_parent_inverse.copy()
            clone.matrix_basis = original.matrix_basis.copy()
        else:
            clone.parent = None
            clone.matrix_basis = Matrix.Identity(4)
    return clones[root_obj]


def bone_origin(armature, bone_name):
    # The imported reference mesh is displayed in its authored pose, not raw
    # armature rest space. Use the evaluated pose origin so the visible skin and
    # the fitted preview cannot drift apart (Marta's shins are already rotated).
    return armature.matrix_world @ armature.pose.bones[bone_name].head


def fit_preview(root_obj, armature, start_name, end_name):
    start = bone_origin(armature, start_name)
    end = bone_origin(armature, end_name)
    direction = end - start
    length = direction.length
    local_y = direction.normalized()

    # Marta's imported rest pose faces -Y. This is the Blender equivalent of
    # ProstheticVisual's LookRotation(actorForward, boneDirection): local +Y
    # follows joint->end and local +Z follows the actor's forward direction.
    local_z = Vector((0.0, -1.0, 0.0))
    local_z -= local_y * local_z.dot(local_y)
    if local_z.length_squared < 0.000001:
        local_z = Vector((0.0, 0.0, 1.0))
        local_z -= local_y * local_z.dot(local_y)
    local_z.normalize()
    local_x = local_y.cross(local_z).normalized()
    local_z = local_x.cross(local_y).normalized()
    rotation = Matrix((local_x, local_y, local_z)).transposed().to_quaternion()

    root_obj.location = start
    root_obj.rotation_mode = "QUATERNION"
    root_obj.rotation_quaternion = rotation
    root_obj.scale = (length, length, length)
    root_obj["hexlive_fit_start_bone"] = start_name
    root_obj["hexlive_fit_end_bone"] = end_name
    root_obj["hexlive_fit_length_m"] = length


def link_once(scene, collection_to_link):
    if collection_to_link.name not in {child.name for child in scene.collection.children}:
        scene.collection.children.link(collection_to_link)


def look_at(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()


def add_fit_stage(scene):
    stage = replace_collection("PROSTHETIC_FIT_STAGE")
    scene.collection.children.link(stage)

    camera_data = bpy.data.cameras.new("ProstheticFitCamera")
    camera = bpy.data.objects.new("ProstheticFitCamera", camera_data)
    stage.objects.link(camera)
    camera.location = (2.55, -4.6, 1.48)
    camera.data.lens = 62.0
    look_at(camera, (0.0, 0.0, 0.86))
    scene.camera = camera

    key_data = bpy.data.lights.new("ProstheticFitKey", "AREA")
    key_data.energy = 900.0
    key_data.shape = "DISK"
    key_data.size = 4.0
    key = bpy.data.objects.new("ProstheticFitKey", key_data)
    stage.objects.link(key)
    key.location = (-2.6, -3.4, 4.0)
    look_at(key, (0.0, 0.0, 0.85))

    fill_data = bpy.data.lights.new("ProstheticFitFill", "AREA")
    fill_data.energy = 500.0
    fill_data.size = 3.0
    fill = bpy.data.objects.new("ProstheticFitFill", fill_data)
    stage.objects.link(fill)
    fill.location = (2.8, -1.2, 2.2)
    look_at(fill, (0.0, 0.0, 0.9))

    scene.render.resolution_x = 900
    scene.render.resolution_y = 1050
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = "/private/tmp/hexlive_prosthetic_fit.png"
    if scene.world is None:
        scene.world = bpy.data.worlds.new("ProstheticFitWorld")
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    if background:
        background.inputs["Color"].default_value = (0.018, 0.024, 0.032, 1.0)
        background.inputs["Strength"].default_value = 0.35


def build_fit_scene(model_roots):
    reference = ensure_marta_reference()
    style_reference(reference)
    armature = next(obj for obj in reference.objects if obj.type == "ARMATURE")

    wood = replace_collection(WOOD_PREVIEW_COLLECTION_NAME)
    mechanical = replace_collection(MECHANICAL_PREVIEW_COLLECTION_NAME)

    targets = {
        "WoodArmR": (wood, "rForearmBend", "rHand"),
        "WoodArmL": (wood, "lForearmBend", "lHand"),
        "WoodLegR": (wood, "rShin", "rFoot"),
        "WoodLegL": (wood, "lShin", "lFoot"),
        "MechanicalArmR": (mechanical, "rForearmBend", "rHand"),
        "MechanicalArmL": (mechanical, "lForearmBend", "lHand"),
        "MechanicalLegR": (mechanical, "rShin", "rFoot"),
        "MechanicalLegL": (mechanical, "lShin", "lFoot"),
    }
    for model_name, (target, start_name, end_name) in targets.items():
        preview = duplicate_hierarchy(
            model_roots[model_name], target, f"Fit_{model_name}")
        fit_preview(preview, armature, start_name, end_name)

    # Wood is the default review layer; the mechanical layer uses the same fit
    # and is one collection toggle away without double-drawing both devices.
    wood.hide_viewport = False
    wood.hide_render = False
    mechanical.hide_viewport = True
    mechanical.hide_render = True

    old_scene = bpy.data.scenes.get(FIT_SCENE_NAME)
    if old_scene:
        bpy.data.scenes.remove(old_scene)
    fit_scene = bpy.data.scenes.new(FIT_SCENE_NAME)
    link_once(fit_scene, reference)
    link_once(fit_scene, wood)
    link_once(fit_scene, mechanical)
    add_fit_stage(fit_scene)

    if bpy.context.window is not None:
        bpy.context.window.scene = fit_scene
    bpy.ops.render.render(write_still=True)
    return fit_scene


EXPORT_ROOT.mkdir(parents=True, exist_ok=True)
SOURCE_ROOT.mkdir(parents=True, exist_ok=True)

models = [
    (wooden_arm(1), "prosthetic_arm_wood_r.fbx", (-1.8, 0.0, 0.0)),
    (wooden_arm(-1), "prosthetic_arm_wood_l.fbx", (-0.6, 0.0, 0.0)),
    (wooden_leg(1), "prosthetic_leg_wood_r.fbx", (0.6, 0.0, 0.0)),
    (wooden_leg(-1), "prosthetic_leg_wood_l.fbx", (1.8, 0.0, 0.0)),
    (mechanical_arm(1), "prosthetic_arm_mechanical_r.fbx", (-1.8, 0.0, -1.4)),
    (mechanical_arm(-1), "prosthetic_arm_mechanical_l.fbx", (-0.6, 0.0, -1.4)),
    (mechanical_leg(1), "prosthetic_leg_mechanical_r.fbx", (0.6, 0.0, -1.4)),
    (mechanical_leg(-1), "prosthetic_leg_mechanical_l.fbx", (1.8, 0.0, -1.4)),
]

for model, filename, _ in models:
    export(model, filename)

for model, _, preview_location in models:
    model.location = preview_location

model_roots = {model.name: model for model, _, _ in models}
fit_scene = build_fit_scene(model_roots)

# Preserve the user's multi-item working file: save a project-owned COPY with
# the prosthetic collection, Marta reference and fitted review scene instead of
# changing the open working file's path.
bpy.ops.wm.save_as_mainfile(
    filepath=str(SOURCE_ROOT / "hexlive_prosthetics.blend"),
    copy=True,
)

for obj in fit_scene.objects:
    obj.select_set(False)
wood_preview = next(
    (obj for obj in fit_scene.objects if obj.get("hexlive_fit_start_bone") == "rForearmBend"),
    None,
)
if wood_preview:
    wood_preview.select_set(True)
    bpy.context.view_layer.objects.active = wood_preview

print(
    "HEX_PROSTHETICS_OK",
    len(models),
    str(EXPORT_ROOT),
    "MARTA_ARM_M", MARTA_ARM_LENGTH_M,
    "MARTA_LEG_M", MARTA_LEG_LENGTH_M,
    "PREVIEW", "/private/tmp/hexlive_prosthetic_fit.png",
)

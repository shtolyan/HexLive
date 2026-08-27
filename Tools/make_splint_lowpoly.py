"""Hand-build med.splint as a genuine low-poly model (TOOL_GENERATION_SPEC art rules).

    Blender -b -P Tools/make_splint_lowpoly.py -- <out.glb> <out.fbx>

A splint is what its recipe says it is (SimData: 2x resource.stick +
1x resource.rope): two wooden sticks lashed together by two rope wraps.
Cheaper to state than to generate: like Tools/make_saw_lowpoly.py this
models the prop directly instead of decimating an AI mesh.

Frame follows the shipped tools: long axis along Blender +Z (-> Unity +Y),
recentred on x/y, min z = 0. No size baking - ObjectFit normalizes at
runtime. Flat shading, flat colours: the faceting IS the art style.
"""
import math
import os
import sys

import bpy
import bmesh
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out_glb = args[0] if len(args) > 0 else "splint.glb"
out_fbx = args[1] if len(args) > 1 else ""

# Linear-space flat colours in the family of the shipped wooden props.
WOOD = (0.301, 0.171, 0.083, 1.0)
WOOD2 = (0.353, 0.213, 0.103, 1.0)   # the second stick differs one shade
ROPE = (0.487, 0.361, 0.171, 1.0)

bpy.ops.wm.read_factory_settings(use_empty=True)
me = bpy.data.meshes.new("splint")
obj = bpy.data.objects.new("splint", me)
bpy.context.collection.objects.link(obj)

bm = bmesh.new()
WOOD_FACES, WOOD2_FACES, ROPE_FACES = [], [], []


def ngon_prism(sides, radius_lo, radius_hi, z0, z1, center, tilt_deg, bucket):
    """A tapered n-gon column: the low-poly stick. Capped both ends."""
    tilt = Matrix.Rotation(math.radians(tilt_deg), 4, 'X')
    lo, hi = [], []
    for ring, (radius, z) in ((lo, (radius_lo, z0)), (hi, (radius_hi, z1))):
        for i in range(sides):
            angle = (i + 0.5) * 2.0 * math.pi / sides
            local = Vector((math.cos(angle) * radius, math.sin(angle) * radius, z))
            ring.append(bm.verts.new(tilt @ local + Vector((center[0], center[1], 0.0))))
    bucket.append(bm.faces.new(lo[::-1]))
    bucket.append(bm.faces.new(hi))
    for i in range(sides):
        j = (i + 1) % sides
        bucket.append(bm.faces.new((lo[i], lo[j], hi[j], hi[i])))


def wrap(z0, z1, half_x, half_y, bucket):
    """One rope lashing: a flat octagonal band around both sticks."""
    profile = []
    cut = 0.55
    for sx, sy in ((1, cut), (cut, 1), (-cut, 1), (-1, cut),
                   (-1, -cut), (-cut, -1), (cut, -1), (1, -cut)):
        profile.append((sx * half_x, sy * half_y))
    lo = [bm.verts.new((x, y, z0)) for x, y in profile]
    hi = [bm.verts.new((x, y, z1)) for x, y in profile]
    bucket.append(bm.faces.new(lo[::-1]))
    bucket.append(bm.faces.new(hi))
    for i in range(len(profile)):
        j = (i + 1) % len(profile)
        bucket.append(bm.faces.new((lo[i], lo[j], hi[j], hi[i])))


STICK_R = 0.052
GAP = 0.062
# Two sticks, slightly different taper/tilt so the pair reads hand-made.
ngon_prism(6, STICK_R, STICK_R * 0.82, 0.0, 1.00, (-GAP, 0.0), 1.6, WOOD_FACES)
ngon_prism(6, STICK_R * 0.90, STICK_R * 0.78, 0.02, 0.97, (GAP, 0.012), -1.2, WOOD2_FACES)

# Two lashings near the ends - exactly where a splint is tied.
wrap(0.14, 0.26, GAP + STICK_R * 1.28, STICK_R * 1.42, ROPE_FACES)
wrap(0.72, 0.84, GAP + STICK_R * 1.24, STICK_R * 1.38, ROPE_FACES)

# Fresh bmesh faces carry index -1 until an explicit update; capturing the
# buckets without it silently lands every polygon in the last material.
bm.faces.index_update()
wood_idx = {f.index for f in WOOD_FACES if f.is_valid}
wood2_idx = {f.index for f in WOOD2_FACES if f.is_valid}
bm.to_mesh(me)
bm.free()

for name, rgba in (("splint_wood", WOOD), ("splint_wood2", WOOD2), ("splint_rope", ROPE)):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    principled = material.node_tree.nodes["Principled BSDF"]
    principled.inputs["Base Color"].default_value = rgba
    principled.inputs["Roughness"].default_value = 0.9
    principled.inputs["Specular"].default_value = 0.05
    me.materials.append(material)
for polygon in me.polygons:
    polygon.material_index = (
        0 if polygon.index in wood_idx else 1 if polygon.index in wood2_idx else 2)

bpy.context.view_layer.objects.active = obj
obj.select_set(True)
bpy.ops.object.shade_flat()
if hasattr(me, "use_auto_smooth"):
    me.use_auto_smooth = False
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.normals_make_consistent(inside=False)
bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.02)
bpy.ops.object.mode_set(mode='OBJECT')

# Recentre x/y, floor min z to 0 (pivot convention of every shipped prop).
me.calc_loop_triangles()
xs = [v.co.x for v in me.vertices]
ys = [v.co.y for v in me.vertices]
zs = [v.co.z for v in me.vertices]
offset = Vector((-(min(xs) + max(xs)) * 0.5, -(min(ys) + max(ys)) * 0.5, -min(zs)))
for vertex in me.vertices:
    vertex.co += offset

bounds = [Vector(c) for c in obj.bound_box]
print(f"[splint] {len(me.loop_triangles)} tris, {len(me.vertices)} verts")
print(f"[splint] bbox min {tuple(round(v, 4) for v in bounds[0])} "
      f"max {tuple(round(v, 4) for v in bounds[6])}")

bpy.ops.export_scene.gltf(filepath=out_glb, export_format='GLB', export_yup=True,
                          export_materials='EXPORT', export_normals=True,
                          export_tangents=False)
print(f"[splint] wrote {out_glb} ({os.path.getsize(out_glb) / 1e3:.0f} KB)")
if out_fbx:
    bpy.ops.export_scene.fbx(filepath=out_fbx, use_selection=False,
                             apply_scale_options='FBX_SCALE_ALL',
                             axis_forward='-Z', axis_up='Y',
                             add_leaf_bones=False, bake_anim=False)
    print(f"[splint] wrote {out_fbx} ({os.path.getsize(out_fbx) / 1e3:.0f} KB)")

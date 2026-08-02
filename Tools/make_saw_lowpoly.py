"""Hand-build tool.saw as a genuine low-poly GLB (TOOL_GENERATION_SPEC art rules).

    Blender -b -P Tools/make_saw_lowpoly.py -- <out.glb>

Why this exists instead of the AI pipeline: a trellis-2 saw is a shredded
triangle soup (~5.5 k disconnected shells) around a blade plate thinner than any
workable voxel, so it cannot be reduced. Measured on the shipped 20 k model:
DECIMATE/COLLAPSE floors at 27 041 faces at ratio 0.005 AND at 0.001, and
QuadriFlow refuses the mesh. A saw is a plate, teeth and a grip — cheaper to
state than to reconstruct.

Dimensions and pivot deliberately match the model this replaces
(x +/-0.0293, y +/-0.1796, z 0..1.0029) so the hand pose does not move.
Blender +Z -> Unity +Y (grip axis), Blender -Y -> Unity +Z (working edge), so
the teeth point at -Y and the grip base sits at z = 0.
"""
import bpy, bmesh, sys
from mathutils import Vector

out = sys.argv[sys.argv.index("--")+1] if "--" in sys.argv else "saw.glb"

STEEL = (0.548, 0.539, 0.546, 1.0)   # linear; k-means of the old model's albedo
WOOD  = (0.113, 0.063, 0.042, 1.0)   # linear; same source, so the colour did not move

bpy.ops.wm.read_factory_settings(use_empty=True)
me = bpy.data.meshes.new("saw")
obj = bpy.data.objects.new("saw", me)
bpy.context.collection.objects.link(obj)

bm = bmesh.new()
STEEL_FACES, WOOD_FACES = [], []

def box(x0, x1, y0, y1, z0, z1, bucket):
    verts = [bm.verts.new((x, y, z)) for x in (x0, x1) for y in (y0, y1) for z in (z0, z1)]
    v000, v001, v010, v011, v100, v101, v110, v111 = verts
    quads = [(v000, v010, v011, v001), (v100, v101, v111, v110),
             (v000, v001, v101, v100), (v010, v110, v111, v011),
             (v000, v100, v110, v010), (v001, v011, v111, v101)]
    for q in quads:
        bucket.append(bm.faces.new(q))

def tooth(x0, x1, y_base, y_tip, z0, z1, bucket):
    """One triangular prism sticking out along -Y, extruded through the blade."""
    tri = [(y_base, z0), (y_base, z1), (y_tip, (z0 + z1) * 0.5)]
    a = [bm.verts.new((x0, y, z)) for y, z in tri]
    b = [bm.verts.new((x1, y, z)) for y, z in tri]
    bucket.append(bm.faces.new(a))
    bucket.append(bm.faces.new(b[::-1]))
    for i in range(3):
        j = (i + 1) % 3
        bucket.append(bm.faces.new((a[i], a[j], b[j], b[i])))

# --- blade ---------------------------------------------------------------
BT = 0.011                       # half thickness of the plate
box(-BT, BT, -0.150, 0.128, 0.320, 1.0029, STEEL_FACES)       # plate
box(-0.016, 0.016, 0.124, 0.150, 0.320, 1.0029, STEEL_FACES)  # spine rail

N_TEETH = 20
z0, z1 = 0.345, 0.945
step = (z1 - z0) / N_TEETH
for i in range(N_TEETH):
    tooth(-BT, BT, -0.150, -0.1796, z0 + i * step, z0 + (i + 1) * step, STEEL_FACES)

# --- grip ----------------------------------------------------------------
box(-0.024, 0.024, -0.125, 0.125, 0.288, 0.348, WOOD_FACES)   # ferrule
box(-0.0293, 0.0293, -0.030, 0.108, 0.000, 0.300, WOOD_FACES) # grip column
box(-0.0293, 0.0293, -0.165, -0.030, 0.000, 0.105, WOOD_FACES)# pistol-grip foot

bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
bm.to_mesh(me)
steel_idx = {f.index for f in STEEL_FACES if f.is_valid}
bm.free()

mats = []
for name, rgba in (("saw_steel", STEEL), ("saw_wood", WOOD)):
    m = bpy.data.materials.new(name); m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = rgba
    b.inputs["Roughness"].default_value = 0.85
    b.inputs["Specular"].default_value = 0.05
    me.materials.append(m); mats.append(m)
for p in me.polygons:
    p.material_index = 0 if p.index in steel_idx else 1

# flat shading — the faceting IS the art style
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

me.calc_loop_triangles()
bb = [Vector(c) for c in obj.bound_box]
print(f"[saw] {len(me.loop_triangles)} tris, {len(me.vertices)} verts")
print(f"[saw] bbox min {tuple(round(v,4) for v in bb[0])} max {tuple(round(v,4) for v in bb[6])}")

bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', export_yup=True,
                          export_materials='EXPORT', export_normals=True,
                          export_tangents=False)
import os
print(f"[saw] wrote {out} ({os.path.getsize(out)/1e3:.0f} KB)")

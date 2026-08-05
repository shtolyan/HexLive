"""Rebuild an AI-generated tool GLB as a genuine low-poly mesh.

    Blender -b -P Tools/retopo_tool_glb.py -- <src.glb> <dst.glb> <target_tris> [voxel] [tex]

TOOL_GENERATION_SPEC.md's 20 000-triangle decimate target is 50-250x the rest of
the art (the handmade Kenney props are 28-400 tris), and the reason it was never
pushed lower is that a trellis-2 mesh CANNOT be decimated: it is a triangle soup
with three unshared verts per triangle and ~5 500 disconnected shells, so
COLLAPSE punches holes in it instead of merging anything.

So do not decimate it — replace it. Voxel-remesh to a clean manifold, decimate
THAT, re-unwrap, and bake the original albedo onto the new UVs. Measured on
tool.machete: 20 000 -> 898 tris, 3.19 -> 0.36 MB, and the result reads as MORE
on-style, because the facets finally survive.

Pick `voxel` from the thinnest feature you must keep: it has to be a few voxels
across or that feature dissolves. 0.0035 suits a blade on a ~1.0-unit tool.

⚠️ Does not work on a thin plate (a saw blade). There COLLAPSE floors out — 27 041
faces at ratio 0.005 AND at 0.001, since merging across a shell a few voxels
thick would self-intersect — and QuadriFlow refuses the mesh whatever you feed
it. Model that shape instead; see Tools/make_saw_lowpoly.py.
"""
import bpy, bmesh, sys, os

argv = sys.argv[sys.argv.index("--")+1:]
src, dst, target = argv[0], argv[1], int(argv[2])
voxel = float(argv[3]) if len(argv) > 3 else 0.0035
texsize = int(argv[4]) if len(argv) > 4 else 1024

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
hi = [o for o in bpy.context.scene.objects if o.type == 'MESH']
assert len(hi) == 1, f"expected one mesh, got {len(hi)}"
hi = hi[0]
print(f"[retopo] source {len(hi.data.polygons)} tris, dims {tuple(round(v,4) for v in hi.dimensions)}")

# --- 1. clean manifold copy -----------------------------------------------
lo = hi.copy(); lo.data = hi.data.copy(); lo.name = "lowpoly"
bpy.context.collection.objects.link(lo)
bpy.ops.object.select_all(action='DESELECT')
lo.select_set(True); bpy.context.view_layer.objects.active = lo

m = lo.modifiers.new("remesh", 'REMESH')
m.mode = 'VOXEL'; m.voxel_size = voxel; m.adaptivity = 0.0
bpy.ops.object.modifier_apply(modifier=m.name)
print(f"[retopo] remeshed -> {len(lo.data.polygons)} faces")

# Remeshing a shredded source leaves specks — free-floating blobs off the
# surface. A closed shell cannot decimate below 4 faces, so they set a floor on
# the triangle count. Drop anything too small to be part of the tool.
bm = bmesh.new(); bm.from_mesh(lo.data); bm.faces.ensure_lookup_table()
seen = set(); shells = []
for f in bm.faces:
    if f.index in seen: continue
    shell = []; stack = [f]
    while stack:
        c = stack.pop()
        if c.index in seen: continue
        seen.add(c.index); shell.append(c)
        for e in c.edges:
            for lf in e.link_faces:
                if lf.index not in seen: stack.append(lf)
    shells.append(shell)
biggest = max(len(s) for s in shells)
junk = [f for s in shells if len(s) < max(64, biggest * 0.01) for f in s]
if junk:
    bmesh.ops.delete(bm, geom=junk, context='FACES')
    bm.to_mesh(lo.data); lo.data.update()
print(f"[retopo] {len(shells)} shells, dropped {len(junk)} speck faces -> {len(lo.data.polygons)}")
bm.free()

m = lo.modifiers.new("tri", 'TRIANGULATE')
bpy.ops.object.modifier_apply(modifier=m.name)
tris = len(lo.data.polygons)
m = lo.modifiers.new("dec", 'DECIMATE')
m.decimate_type = 'COLLAPSE'; m.use_collapse_triangulate = True
m.ratio = min(1.0, target / max(1, tris))
bpy.ops.object.modifier_apply(modifier=m.name)
print(f"[retopo] decimated {tris} -> {len(lo.data.polygons)} tris")

# --- 2. fresh UVs ----------------------------------------------------------
bpy.ops.object.select_all(action='DESELECT')
lo.select_set(True); bpy.context.view_layer.objects.active = lo
while lo.data.uv_layers:
    lo.data.uv_layers.remove(lo.data.uv_layers[0])
lo.data.uv_layers.new(name="UVMap")
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.02)
bpy.ops.object.mode_set(mode='OBJECT')

# --- 3. bake the high-poly albedo onto them --------------------------------
img = bpy.data.images.new("baked", texsize, texsize, alpha=False)
mat = bpy.data.materials.new("lowpoly_mat"); mat.use_nodes = True
nt = mat.node_tree
bsdf = nt.nodes["Principled BSDF"]
bsdf.inputs["Roughness"].default_value = 0.9
bsdf.inputs["Specular"].default_value = 0.0
tex = nt.nodes.new("ShaderNodeTexImage"); tex.image = img
nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
nt.nodes.active = tex
lo.data.materials.clear(); lo.data.materials.append(mat)

scn = bpy.context.scene
scn.render.engine = 'CYCLES'; scn.cycles.device = 'CPU'; scn.cycles.samples = 4
b = scn.render.bake
b.use_selected_to_active = True
b.cage_extrusion = max(voxel * 3, 0.01)
b.max_ray_distance = max(voxel * 6, 0.02)
b.use_pass_direct = False; b.use_pass_indirect = False; b.margin = 8

bpy.ops.object.select_all(action='DESELECT')
hi.select_set(True); lo.select_set(True)
bpy.context.view_layer.objects.active = lo
bpy.ops.object.bake(type='DIFFUSE')
img.pack()
print("[retopo] baked albedo")

# --- 4. flat shading is the art style, then export -------------------------
bpy.ops.object.select_all(action='DESELECT')
lo.select_set(True); bpy.context.view_layer.objects.active = lo
bpy.ops.object.shade_flat()
if hasattr(lo.data, "use_auto_smooth"):
    lo.data.use_auto_smooth = False

bpy.data.objects.remove(hi, do_unlink=True)
bpy.ops.export_scene.gltf(filepath=dst, export_format='GLB', export_yup=True,
                          export_materials='EXPORT', export_normals=True,
                          export_tangents=False)
print(f"[retopo] wrote {dst}: {len(lo.data.polygons)} tris, {os.path.getsize(dst)/1e6:.2f} MB")

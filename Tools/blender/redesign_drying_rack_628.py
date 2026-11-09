"""Promote the outdoor hanger rail without rebuilding other construction art.

Run on the canonical construction-stage .blend; export through the shared
export_construction_stage_workspace.py afterwards. All eight paid units stay.
"""
import bpy
from mathutils import Vector

ID = "station.drying_rack"
scene = next(s for s in bpy.data.scenes if s.get("hexlive_definition_id") == ID)
bpy.context.window.scene = scene
root = next(o for o in scene.objects if o.get("hexlive_definition_id") == ID)
if root.get("hexlive_rack_design") == "hangers-628":
    raise RuntimeError("Rack redesign already applied; export the saved source")

# Retain every existing resource unit, joint and material. Stretch the two
# uprights and their corner stays; move the two complete rails and lashings.
for obj in list(root.children_recursive):
    if obj.type != "MESH":
        continue
    name = obj.name
    obj.data = obj.data.copy()
    for v in obj.data.vertices:
        p = obj.matrix_world @ v.co
        if name.startswith(("stick_00", "stick_01", "Rack_corner_stay_")):
            p.z *= 1.32 / 1.14
        elif name.startswith(("stick_02", "rope_rack_00", "rope_rack_01")):
            p.z += .18
        elif name.startswith(("stick_03", "rope_rack_02", "rope_rack_03")):
            p.z -= .55
        v.co = obj.matrix_world.inverted() @ p

for i in range(8):
    socket = next(o for o in root.children_recursive if o.name == f"DryingSlot_{i:02d}")
    socket.location = (-.036, -.35 + i * .10, 1.32)

# Exactly the wardrobe's hanger template and exactly the same runtime factory.
wardrobe = next(o for o in bpy.data.objects if o.get("hexlive_definition_id") == "furniture.wardrobe")
source = next(o for o in wardrobe.children_recursive if o.get("hexlive_export_name", o.name) == "HangerTemplate")
collection = root.users_collection[0]
copies = {}
for obj in [source, *source.children_recursive]:
    clone = obj.copy()
    if obj.type == "MESH":
        clone.data = obj.data.copy()
    clone.name = "DryingRack_" + obj.name
    clone["hexlive_export_name"] = obj.get("hexlive_export_name", obj.name)
    collection.objects.link(clone)
    copies[obj] = clone
for obj, clone in copies.items():
    clone.parent = root if obj == source else copies[obj.parent]
    clone.matrix_parent_inverse.identity()
    clone.matrix_basis = obj.matrix_basis.copy()
    clone["hexlive_construction_role"] = "helper"
    clone.hide_render = True
root["hexlive_rack_design"] = "hangers-628"
bpy.context.view_layer.update()
bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)
print("RACK_628_AUTHORED: 4 sticks,4 ropes;8 hanger sockets at1.32wu")

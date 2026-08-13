"""Render isolated perspective and top checks of the normalized wardrobe."""

import bpy
from mathutils import Vector

root = bpy.data.objects["HL_Wardrobe_Module"]
scene = bpy.context.scene
output_dir = "/Volumes/ORICO/HexLive/Assets/ArtSource/Building/_ArtSource"

descendants = set()
stack = [root]
while stack:
    obj = stack.pop()
    descendants.add(obj)
    stack.extend(obj.children)

# Flatten temporary render proxies into the scene root. This bypasses legacy
# collection/layer visibility without changing or reparenting authored data.
proxies = []
for source in descendants:
    if source.type != 'MESH':
        continue
    proxy = source.copy()
    proxy.data = source.data
    proxy.name = source.name + "_REVIEW_TEMP"
    bpy.context.scene.collection.objects.link(proxy)
    proxy.parent = None
    proxy.matrix_world = source.matrix_world.copy()
    proxy.hide_viewport = False
    proxy.hide_render = False
    proxies.append(proxy)

old_visibility = {obj: obj.hide_render for obj in bpy.data.objects}
old_collection_visibility = {collection: collection.hide_render for collection in bpy.data.collections}
old_layer_visibility = {}
def walk_layers(layer):
    old_layer_visibility[layer] = (layer.exclude, layer.hide_viewport)
    layer.exclude = False
    layer.hide_viewport = False
    for child in layer.children:
        walk_layers(child)
walk_layers(bpy.context.view_layer.layer_collection)
old_camera = scene.camera
old_resolution = (scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage)
old_filepath = scene.render.filepath

for obj in bpy.data.objects:
    obj.hide_render = obj not in proxies
for collection in bpy.data.collections:
    collection.hide_render = False

# Temporary neutral ground, camera and lights are never saved.
bpy.ops.mesh.primitive_plane_add(size=5, location=(root.location.x, root.location.y, root.location.z-.01))
ground = bpy.context.object
ground.name = "HL_Wardrobe_ReviewGround_TEMP"
ground_mat = bpy.data.materials.get("HL_Wardrobe_ReviewGround") or bpy.data.materials.new("HL_Wardrobe_ReviewGround")
ground_mat.diffuse_color = (.12, .16, .13, 1)
ground.data.materials.append(ground_mat)
ground.hide_render = False

def point_camera(camera, target):
    camera.rotation_euler = (Vector(target) - camera.location).to_track_quat('-Z', 'Y').to_euler()

bpy.ops.object.camera_add()
camera = bpy.context.object
camera.name = "HL_Wardrobe_ReviewCamera_TEMP"
camera.data.lens = 56
camera.hide_render = False
scene.camera = camera

lights = []
for name, location, energy, size in (
    ("Key", root.location + Vector((-2.2, -2.4, 3.2)), 900, 3.0),
    ("Fill", root.location + Vector((2.0, .8, 2.2)), 550, 2.5),
):
    bpy.ops.object.light_add(type='AREA', location=location)
    light = bpy.context.object
    light.name = f"HL_Wardrobe_Review{name}_TEMP"
    light.data.energy = energy
    light.data.shape = 'DISK'
    light.data.size = size
    point_camera(light, root.location + Vector((0, 0, .8)))
    light.hide_render = False
    lights.append(light)

scene.render.engine = 'BLENDER_EEVEE'
scene.render.resolution_x = 900
scene.render.resolution_y = 900
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'

camera.location = root.location + Vector((-2.45, -2.75, 2.05))
point_camera(camera, root.location + Vector((0, 0, .78)))
scene.render.filepath = output_dir + "/wardrobe_module_perspective.png"
bpy.ops.render.render(write_still=True)

camera.data.type = 'ORTHO'
camera.data.ortho_scale = 2.2
camera.location = root.location + Vector((0, 0, 4.0))
camera.rotation_euler = (0, 0, 0)
point_camera(camera, root.location)
scene.render.filepath = output_dir + "/wardrobe_module_top.png"
bpy.ops.render.render(write_still=True)

for obj, hidden in old_visibility.items():
    if obj.name in bpy.data.objects:
        obj.hide_render = hidden
for collection, hidden in old_collection_visibility.items():
    if collection.name in bpy.data.collections:
        collection.hide_render = hidden
for layer, state in old_layer_visibility.items():
    layer.exclude, layer.hide_viewport = state
for obj in [ground, camera, *lights, *proxies]:
    bpy.data.objects.remove(obj, do_unlink=True)
scene.camera = old_camera
scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = old_resolution
scene.render.filepath = old_filepath

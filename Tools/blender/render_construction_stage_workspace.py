"""Render the prepared rack and wardrobe stages without saving the .blend."""
from pathlib import Path
import bpy
from mathutils import Vector

REPO = Path(__file__).resolve().parents[2]
scene = bpy.data.scenes.new("Stage preview")
bpy.context.window.scene = scene
scene.render.engine = "BLENDER_WORKBENCH"
scene.render.resolution_x = 1500
scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.display.shading.light = "STUDIO"
scene.display.shading.color_type = "MATERIAL"
scene.display.shading.show_cavity = True
scene.display.shading.cavity_type = "BOTH"
scene.display.shading.show_shadows = True
scene.display.shading.background_type = "WORLD"
scene.world = bpy.data.worlds.new("Preview World")
scene.world.color = (.075, .075, .075)

for row, definition in enumerate(("station.drying_rack", "furniture.wardrobe")):
    root = next(o for o in bpy.data.objects if o.get("hexlive_definition_id") == definition)
    for column, through in enumerate((1, 2, 3)):
        offset = Vector((column * 2.1, row * 2.7, 0))
        for source in root.children_recursive:
            if source.type != "MESH" or source.hide_render:
                continue
            node = source
            index = None
            while node and node != root:
                if node.get("hexlive_stage_index"):
                    index = node["hexlive_stage_index"]
                    break
                node = node.parent
            if index is None or index > through:
                continue
            obj = source.copy()
            obj.data = source.data
            scene.collection.objects.link(obj)
            obj.parent = None
            obj.matrix_world = source.matrix_world.copy()
            obj.location += offset
        font = bpy.data.curves.new("Label", "FONT")
        font.body = ("Drying rack" if row == 0 else "Wardrobe") + f" / {through}"
        font.size = .18
        text = bpy.data.objects.new("Label", font)
        scene.collection.objects.link(text)
        text.location = offset + Vector((-.1, -.8, -.08))

camera = bpy.data.objects.new("Preview Camera", bpy.data.cameras.new("Preview Camera"))
scene.collection.objects.link(camera)
camera.location = (10, -10, 10)
target = Vector((2.1, 1.4, .6))
camera.rotation_euler = (target-camera.location).to_track_quat("-Z", "Y").to_euler()
camera.data.type = "ORTHO"
camera.data.ortho_scale = 9
scene.camera = camera
scene.render.image_settings.file_format = "PNG"
scene.render.filepath = "/tmp/hexlive-construction-stages-preview.png"
bpy.ops.render.render(write_still=True)
print("STAGE_PREVIEW", scene.render.filepath)

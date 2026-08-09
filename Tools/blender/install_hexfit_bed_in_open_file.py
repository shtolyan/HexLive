import bpy
import os


ROOT = os.path.abspath(os.path.join(os.path.dirname(bpy.data.filepath), "..", "..", ".."))
SOURCE = os.path.join(ROOT, "Assets", "ArtSource", "Building", "hexlive_hexfit_bed.blend")
EXPORT = "HL_HEX_FIT_BED_EXPORT"
PRESENTATION = "HL_HEX_FIT_BED_PRESENTATION"
SCENE_NAME = "HexFitBed_Review"


def remove_collection(name):
    collection = bpy.data.collections.get(name)
    if collection is None:
        return
    for scene in bpy.data.scenes:
        if collection in scene.collection.children.values():
            scene.collection.children.unlink(collection)
    bpy.data.collections.remove(collection)


def main():
    if not os.path.exists(SOURCE):
        raise RuntimeError("Missing source: " + SOURCE)

    old_scene = bpy.data.scenes.get(SCENE_NAME)
    if old_scene is not None:
        bpy.data.scenes.remove(old_scene)
    remove_collection(EXPORT)
    remove_collection(PRESENTATION)

    with bpy.data.libraries.load(SOURCE, link=False) as (source, target):
        target.collections = [name for name in (EXPORT, PRESENTATION)
                              if name in source.collections]

    export = bpy.data.collections.get(EXPORT)
    presentation = bpy.data.collections.get(PRESENTATION)
    if export is None or presentation is None:
        raise RuntimeError("Hex-fit bed collections were not appended")

    review = bpy.data.scenes.new(SCENE_NAME)
    review.collection.children.link(presentation)
    review.render.engine = "BLENDER_WORKBENCH"
    review.render.resolution_x = 1000
    review.render.resolution_y = 820
    review.render.resolution_percentage = 100
    review.display.shading.light = "STUDIO"
    review.display.shading.studio_light = "rim.sl"
    review.display.shading.color_type = "MATERIAL"
    review.display.shading.show_shadows = True
    review.display.shading.show_cavity = True
    review.display.shading.cavity_type = "BOTH"
    camera = bpy.data.objects.get("HexFitBedCam")
    if camera is not None:
        review.camera = camera

    # The export collection remains a datablock used as the authoritative
    # furniture model. Presentation owns review copies and exact grid markers.
    review["logical_width_wu"] = 1.5 * (3 ** .5) / 4
    review["logical_length_wu"] = 1.5
    review["art_width_wu"] = .57
    review["art_length_wu"] = 1.42
    review["reserved_junctions_per_bed"] = 14
    review["placement_layer"] = "Furniture"

    if bpy.context.window is not None:
        bpy.context.window.scene = review
        for area in bpy.context.screen.areas:
            if area.type != "VIEW_3D":
                continue
            space = area.spaces.active
            space.region_3d.view_perspective = "CAMERA"
            space.overlay.show_floor = False
            space.overlay.show_axis_x = False
            space.overlay.show_axis_y = False
            space.overlay.show_relationship_lines = False
            space.shading.type = "SOLID"
            space.shading.color_type = "MATERIAL"
            break

    bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)
    print("INSTALLED_SCENE=" + SCENE_NAME)
    print("EXPORT_OBJECTS=" + str(len(export.objects)))
    print("PRESENTATION_OBJECTS=" + str(len(presentation.objects)))
    print("LOGICAL=0.649519x1.500 ART=0.570x1.420 RESERVED=14_PER_BED")


main()

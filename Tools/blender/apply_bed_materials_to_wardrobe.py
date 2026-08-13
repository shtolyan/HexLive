"""Use the canonical bed source materials on the wardrobe structure."""

import bpy

SOURCE = "/Volumes/ORICO/HexLive/Assets/HexLiveContent/source.blend"
COLLECTION = bpy.data.collections["HL_WARDROBE_REVIEW"]

with bpy.data.libraries.load(SOURCE, link=False) as (src, dst):
    dst.materials = [name for name in src.materials if name in ("Bark", "Heartwood", "Sapwood", "Rope")]

materials = {}
for mat in dst.materials:
    if mat:
        # Blender may suffix appended names; preserve explicit canonical role.
        role = mat.name.split(".")[0]
        mat.name = f"HL_BedSource_{role}"
        materials[role] = mat

for obj in COLLECTION.objects:
    if obj.type != 'MESH':
        continue
    role = None
    if any(token in obj.name for token in ("Binding",)):
        role = "Rope"
    elif any(token in obj.name for token in ("FramePost", "TopRail")):
        role = "Bark"
    elif any(token in obj.name for token in ("HangRail", "RealHanger")):
        role = "Sapwood"
    elif "ShoeShelf" in obj.name:
        role = "Heartwood"
    if role and role in materials:
        obj.data.materials.clear()
        obj.data.materials.append(materials[role])

bpy.context.scene["wardrobeMaterialSource"] = "Assets/HexLiveContent/source.blend (bed canonical)"
bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")

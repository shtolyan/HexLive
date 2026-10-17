"""Export the preserved legacy whole-hut art without regeneration or roof mats.

blender _ArtSource/Construction/legacy_hut_1hex.blend -b --python-exit-code 1 \
  --python Tools/blender/export_hut_1hex.py -- [--output <review.fbx>]

The source preserves shipped geometry. The old building-kit generator changes
unrelated floor/leaf geometry and must not overwrite this owner.
"""
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import sys
import bpy

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / '_ArtSource/Construction/legacy_hut_1hex.blend'
TARGET = REPO / 'Assets/HexLiveContent/RuntimeSource/Objects/building.hut_1hex.fbx'
if Path(bpy.data.filepath).resolve() != SOURCE:
    raise RuntimeError('Open the preserved whole-hut authoring source: ' + str(SOURCE))
roots = [o for o in bpy.data.objects if o.get('hexlive_content_id') == 'building.hut_1hex']
if len(roots) != 1:
    raise RuntimeError('Expected exactly one whole-hut owner')
root = roots[0]
objects = [root, *root.children_recursive]
for obj in objects:
    if 'roof_woven_mat_' in obj.name.lower() or 'thatchbacking' in obj.name.lower():
        raise RuntimeError('Technical roof backing is forbidden: ' + obj.name)
    obj.update_tag(refresh={'OBJECT'})
bpy.context.view_layer.update()
args = sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
output = Path(args[args.index('--output')+1]).resolve() if '--output' in args else TARGET
if output == TARGET and output.exists():
    backup = REPO/'Build/FurnitureExportBackups'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    backup.mkdir(parents=True)
    shutil.copy2(output, backup/output.name)
output.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.object.select_all(action='DESELECT')
for obj in objects:obj.select_set(True)
bpy.context.view_layer.objects.active = root
bpy.ops.export_scene.fbx(filepath=str(output), use_selection=True,
    object_types={'EMPTY','MESH'}, apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
    use_space_transform=True, bake_space_transform=False, axis_forward='-Z', axis_up='Y',
    add_leaf_bones=False, mesh_smooth_type='FACE', use_mesh_modifiers=True,
    use_triangles=False, use_tspace=False, use_custom_props=False,
    bake_anim=False, path_mode='AUTO', embed_textures=False)
print(json.dumps(dict(source=str(SOURCE), fbx=str(output), fbxBytes=output.stat().st_size,
    meshes=sum(o.type=='MESH' for o in objects), technicalRoofBacking=0)))

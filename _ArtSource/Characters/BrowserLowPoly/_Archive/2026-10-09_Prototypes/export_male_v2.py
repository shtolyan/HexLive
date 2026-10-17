import bpy,sys
from pathlib import Path
OUT=Path(__file__).resolve().parent;sys.path.insert(0,str(OUT))
from export_compat import preserve_all_skin_weight_sets
preserve_all_skin_weight_sets()
bpy.ops.wm.open_mainfile(filepath=str(OUT/'HexLive_Browser_Characters_Rigged_v2.blend'))
for label in ['Kshishtof']:
 rig=bpy.data.objects[label+'_Rig'];rig.location.x=0;bpy.context.view_layer.update()
 for lod in [0,1]:
  obj=bpy.data.objects[f'{label}_LOD{lod}']
  for coll in obj.users_collection:coll.hide_viewport=False
  bpy.ops.object.select_all(action='DESELECT');obj.hide_set(False);obj.select_set(True);rig.select_set(True);bpy.context.view_layer.objects.active=obj
  bpy.ops.export_scene.gltf(filepath=str(OUT/'Rigged_v2'/(obj.name+'.glb')),export_format='GLB',use_selection=True,export_animations=False,export_morph=True,export_morph_normal=True,export_all_influences=True,export_yup=True)
  bpy.ops.export_scene.fbx(filepath=str(OUT/'Rigged_v2'/(obj.name+'.fbx')),use_selection=True,object_types={'MESH','ARMATURE'},add_leaf_bones=False,bake_anim=False,use_mesh_modifiers=False,mesh_smooth_type='FACE',path_mode='COPY',embed_textures=True)
print('EXPORT_FULL_SKIN_COMPLETE',flush=True)

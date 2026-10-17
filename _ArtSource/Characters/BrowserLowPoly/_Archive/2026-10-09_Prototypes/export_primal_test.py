import bpy,json,sys
from pathlib import Path
from mathutils import Matrix
OUT=Path(__file__).resolve().parent;D=OUT/'Clothing_Primal';E=D/'Exports';E.mkdir(exist_ok=True)
sys.path.insert(0,str(OUT));from export_compat import preserve_all_skin_weight_sets
preserve_all_skin_weight_sets()
def select(obs):
 bpy.ops.object.select_all(action='DESELECT')
 for o in obs:o.hide_set(False);o.select_set(True)
 bpy.context.view_layer.objects.active=next((o for o in obs if o.type=='MESH'),obs[0])
def rest(rig):
 rig.animation_data_clear();rig.location.x=0
 for pb in rig.pose.bones:pb.matrix_basis=Matrix.Identity(4)
 rig.data.pose_position='REST';bpy.context.view_layer.update()
def export(name,objects):
 select(objects)
 bpy.ops.export_scene.fbx(filepath=str(E/(name+'.fbx')),use_selection=True,object_types={'EMPTY','MESH','ARMATURE'},add_leaf_bones=False,bake_anim=False,use_mesh_modifiers=False,mesh_smooth_type='FACE',path_mode='COPY',embed_textures=True)
 bpy.ops.export_scene.gltf(filepath=str(E/(name+'.glb')),export_format='GLB',use_selection=True,export_animations=False,export_morph=True,export_morph_normal=True,export_all_influences=True,export_extras=True)
 print('EXPORTED',name,flush=True)
for name in ['Marta_PrimalBriefs','Marta_PrimalTop','Kshishtof_PrimalBriefs']:
 bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
 o=bpy.data.objects[name];rig=bpy.data.objects[name+'_Skeleton'];root=bpy.data.objects[name+'_Wear']
 for prop in ['hide_render','hide_viewport']:
  o.driver_remove(prop);setattr(o,prop,False)
 rest(rig);export(name,[o,rig,root])
bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
rig=bpy.data.objects['Kshishtof_Rig'];body=bpy.data.objects['Kshishtof_LOD0'];gen=bpy.data.objects['Kshishtof_Genitals']
for prop in ['hide_render','hide_viewport']:gen.driver_remove(prop);setattr(gen,prop,False)
rest(rig);export('Kshishtof_Body_SeparateGenitals',[body,gen,rig])
bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
objects=[]
for label in ['Marta','Kshishtof']:objects.extend([bpy.data.objects[label+'_LOD0'],bpy.data.objects[label+'_Rig']])
for name in ['Marta_PrimalBriefs','Marta_PrimalTop','Kshishtof_PrimalBriefs']:objects.extend([bpy.data.objects[name],bpy.data.objects[name+'_Skeleton'],bpy.data.objects[name+'_Wear']])
select(objects)
bpy.ops.export_scene.gltf(filepath=str(E/'Primal_Run_Test.glb'),export_format='GLB',use_selection=True,export_animations=True,export_frame_range=True,export_force_sampling=True,export_nla_strips=False,export_morph=True,export_morph_normal=True,export_all_influences=True,export_extras=True)
print('ALL_PRIMAL_EXPORTS_COMPLETE',flush=True)

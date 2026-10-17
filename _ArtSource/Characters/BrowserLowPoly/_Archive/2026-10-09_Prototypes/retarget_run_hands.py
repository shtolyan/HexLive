import bpy
from pathlib import Path
from mathutils import Matrix
OUT=Path(__file__).resolve().parent;D=OUT/'Clothing_Primal';ROOT=OUT.parents[2]
bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'));sc=bpy.context.scene
before=set(bpy.data.objects);bpy.ops.import_scene.fbx(filepath=str(ROOT/'Assets/ImportedActors/AnimLibrary/Male@Run.fbx'))
new=set(bpy.data.objects)-before;source=next(o for o in new if o.type=='ARMATURE')
for frame in range(1,21):
 sc.frame_set(frame)
 for label in ['Marta','Kshishtof']:
  rig=bpy.data.objects[label+'_Rig']
  for side,ss in [('l','Left'),('r','Right')]:
   for finger,sf in [('Thumb','Thumb'),('Index','Index'),('Mid','Middle'),('Ring','Ring'),('Pinky','Pinky')]:
    for j in range(1,4):
     b=rig.pose.bones[f'{side}{finger}{j}'];s=source.pose.bones[f'mixamorig:{ss}Hand{sf}{j}'];delta=s.matrix.to_quaternion()@s.bone.matrix_local.to_quaternion().inverted();m=(delta@b.bone.matrix_local.to_quaternion()).to_matrix().to_4x4();m.translation=b.matrix.translation;b.matrix=m;b.location=(0,0,0);bpy.context.view_layer.update();b.keyframe_insert('rotation_quaternion',frame=frame)
for label in ['Marta','Kshishtof']:
 for fc in bpy.data.objects[label+'_Rig'].animation_data.action.fcurves:
  for kp in fc.keyframe_points:kp.interpolation='LINEAR'
  if not any(m.type=='CYCLES' for m in fc.modifiers):fc.modifiers.new('CYCLES')
for o in new:bpy.data.objects.remove(o,do_unlink=True)
sc.frame_set(1);bpy.context.view_layer.update();bpy.ops.wm.save_as_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
print('RUN_HANDS_RETARGETED',flush=True)

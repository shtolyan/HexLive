import bpy,json,math,sys
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent;D=OUT/'Rigged'
bpy.ops.wm.open_mainfile(filepath=str(OUT/'HexLive_Browser_Characters_Rigged.blend'))
sc=bpy.context.scene;sc.render.resolution_x=480;sc.render.resolution_y=480;sc.render.resolution_percentage=100;sc.cycles.samples=16
cam=sc.camera;cam.data.ortho_scale=.34
expressions=[('Neutral',None),('AA','eCTRLvAA'),('UW','eCTRLvUW'),('Smile','eCTRLSmile'),('Angry','eCTRLAngry'),('Blink','eCTRLEyesClosed')]
for label in (['Kshishtof'] if '--male-only' in sys.argv else ['Marta','Kshishtof']):
 obj=bpy.data.objects[label+'_LOD0'];keys=obj.data.shape_keys.key_blocks
 pts=[obj.matrix_world@v.co for v in obj.data.vertices];x=obj.matrix_world.translation.x;z=max(p.z for p in pts)-.11
 cam.location=(x,-3,z);cam.rotation_euler=(Vector((x,0,z))-cam.location).to_track_quat('-Z','Y').to_euler()
 for title,suffix in expressions:
  for key in keys:key.value=0
  if suffix:next(k for k in keys if k.name.endswith('__'+suffix)).value=1
  bpy.context.view_layer.update();sc.render.filepath=str(D/f'{label}_{title}.png');bpy.ops.render.render(write_still=True)
 print('FACE_REVIEW_COMPLETE',label,flush=True)

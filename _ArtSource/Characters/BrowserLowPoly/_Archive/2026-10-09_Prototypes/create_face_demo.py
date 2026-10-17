import bpy,math
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(OUT/'HexLive_Browser_Characters_Rigged.blend'))
sc=bpy.context.scene;sc.frame_start=1;sc.frame_end=360;sc.render.fps=30
visemes=['AA','IY','UW','EE','OW','M','F','S','SH','T','ER','L','K','TH']
for label in ['Marta','Kshishtof']:
 obj=bpy.data.objects[label+'_LOD0'];keys=obj.data.shape_keys
 for key in list(keys.key_blocks)[1:]:key.value=0;key.keyframe_insert('value',frame=1)
 def pulse(suffix,frame,value=1):
  k=next(k for k in keys.key_blocks if k.name.endswith('__'+suffix))
  for f,v in [(frame-5,0),(frame,value),(frame+5,0)]:k.value=v;k.keyframe_insert('value',frame=f)
 for i,v in enumerate(visemes):pulse('eCTRLv'+v,12+14*i)
 for suffix,frame in [('eCTRLSmile',230),('eCTRLAngry',265),('eCTRLSurprised',300),('eCTRLEyesClosed',335)]:pulse(suffix,frame)
 keys.animation_data.action.name=label+'_Speech_Emotion_Demo'
 for fc in keys.animation_data.action.fcurves:
  for k in fc.keyframe_points:k.interpolation='LINEAR'
for i,v in enumerate(visemes):sc.timeline_markers.new(v,frame=12+14*i)
for label,frame in [('Smile',230),('Angry',265),('Surprise',300),('Blink',335)]:sc.timeline_markers.new(label,frame=frame)
sc.frame_set(1)
cam=sc.camera;cam.location=(0,-5,1.715);cam.rotation_euler=(Vector((0,0,1.715))-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.ortho_scale=2.05
sc.render.resolution_x=1500;sc.render.resolution_y=500
for screen in bpy.data.screens:
 for area in screen.areas:
  if area.type=='VIEW_3D':area.spaces.active.region_3d.view_perspective='CAMERA';area.spaces.active.overlay.show_overlays=False
sc['demo_note']='Silent blendshape preview: press Space. 14 original speech visemes, then smile, anger, surprise, blink. No audio or game runtime changes.'
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Rigged'/'Face_Demo.blend'))
print('DEMO_SAVED')

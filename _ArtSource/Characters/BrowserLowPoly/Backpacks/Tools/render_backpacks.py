import bpy,json,traceback
from pathlib import Path
from mathutils import Vector
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Backpacks');R=D/'Review';sc=bpy.context.scene;cam=sc.camera;c=bpy.data.objects['Backpack_Controls'];jobs=[(style,label,view) for view in ['back','front','side'] for style in [1,2,3] for label in ['Marta','Kshishtof']];done=[]
sc.render.engine='CYCLES';sc.cycles.samples=6;sc.render.resolution_x=450;sc.render.resolution_y=560;sc.render.resolution_percentage=100;sc.render.image_settings.file_format='PNG'
def step():
 if not jobs:(R/'status.json').write_text(json.dumps({'state':'done','renders':done},indent=2));return None
 style,label,view=jobs.pop(0)
 try:
  c['Marta']=style;c['Kshishtof']=style;c.update_tag();sc.frame_set(10 if view=='side' else 1);x=bpy.data.objects[label+'_Rig'].matrix_world.translation.x;target=Vector((x,.1,1.30));cam.location=target+Vector((.50,3,.2) if view=='back' else ((.3,-3,.2) if view=='front' else (3,1,.15)));cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.type='ORTHO';cam.data.ortho_scale=1.03;sc.render.filepath=str(R/(str(style)+'_'+label+'_'+view+'.png'));bpy.ops.render.render(write_still=True);done.append(str(style)+'_'+label+'_'+view)
 except:(R/'status.json').write_text(json.dumps({'state':'error','error':traceback.format_exc()},indent=2));return None
 (R/'status.json').write_text(json.dumps({'state':'rendering','current':done[-1],'remaining':len(jobs)},indent=2));return .2
bpy.app.driver_namespace['backpack_reviews']=globals();bpy.app.timers.register(step,first_interval=.3)

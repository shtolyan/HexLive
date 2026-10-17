import bpy,json,traceback
from pathlib import Path
from mathutils import Vector
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Hair');R=D/'Review';R.mkdir(exist_ok=True);sc=bpy.context.scene;cam=sc.camera;control=bpy.data.objects['Hair_Controls'];names=list(json.loads((D/'build_status.json').read_text())['hairs']);jobs=[(i+1,n,v) for v in ['front','back','far'] for i,n in enumerate(names)];completed=[]
sc.render.engine='CYCLES';sc.cycles.samples=6;sc.render.resolution_x=360;sc.render.resolution_y=440;sc.render.resolution_percentage=100;sc.render.image_settings.file_format='PNG';sc.frame_set(1)
def step():
 if not jobs:
  (R/'status.json').write_text(json.dumps({'state':'done','renders':completed},indent=2));return None
 index,name,view=jobs.pop(0)
 try:
  control['style']=index;control['lod']=int(view=='far');control.update_tag();sc.frame_set(1);cam.location=(-.68,-3,1.65) if view!='back' else (-.68,3,1.65);target=Vector((-.79,0,1.50));cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.type='ORTHO';cam.data.ortho_scale=.89;sc.render.filepath=str(R/(name+'_'+view+'.png'));bpy.ops.render.render(write_still=True);completed.append(name+'_'+view)
 except:
  (R/'status.json').write_text(json.dumps({'state':'error','error':traceback.format_exc(),'name':name},indent=2));return None
 (R/'status.json').write_text(json.dumps({'state':'rendering','current':name+'_'+view,'remaining':len(jobs)},indent=2));return .3
bpy.app.timers.register(step,first_interval=.3)

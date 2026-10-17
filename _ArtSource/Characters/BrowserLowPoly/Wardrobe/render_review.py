import bpy,json,traceback
from pathlib import Path
from mathutils import Vector
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=OUT/'Wardrobe';R=D/'Review';R.mkdir(exist_ok=True);sc=bpy.context.scene
exec(compile((D/'finish_wardrobe.py').read_text(),'finish_wardrobe.py','exec'),{})
bpy.ops.primal.outfit(fur=False)
for o in bpy.data.objects:
 if o.type=='EMPTY' and 'material_variant' in o:o['material_variant']=0;o.update_tag()
sc.render.threads_mode='FIXED';sc.render.threads=8
cam=sc.camera;camera_start=(cam.location.copy(),cam.rotation_euler.copy());jobs=[('base',f) for f in range(1,20)]+[('back',1),('fur',6),('fur_back',11)]
def step():
 try:
  if not jobs:
   bpy.ops.primal.outfit(fur=False)
   for o in bpy.data.objects:
    if o.type=='EMPTY' and 'material_variant' in o:o['material_variant']=0;o.update_tag()
   cam.location,cam.rotation_euler=camera_start;sc.frame_set(1);sc.cycles.samples=24;sc.render.resolution_x=1200;sc.render.resolution_y=850;bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'));(D/'render_status.json').write_text(json.dumps({'state':'done'}));return None
  kind,frame=jobs.pop(0);sc.frame_set(frame)
  if kind=='base':
   sc.render.resolution_x=800;sc.render.resolution_y=566;sc.cycles.samples=6;sc.render.filepath=str(R/f'base_{frame:04}.png')
  else:
   sc.render.resolution_x=1200;sc.render.resolution_y=850;sc.cycles.samples=12;sc.render.filepath=str(R/(kind+'.png'))
   if kind.startswith('fur'):
    bpy.ops.primal.outfit(fur=True)
    for o in bpy.data.objects:
     if o.type=='EMPTY' and o.get('source_item') in ['PrimalDress','PrimalCollar']:o['material_variant']=3;o.update_tag()
   if kind in ['back','fur_back']:
    cam.location=(2.1,7.8,2.5);cam.rotation_euler=(Vector((0,0,1.03))-cam.location).to_track_quat('-Z','Y').to_euler()
   else:cam.location,cam.rotation_euler=camera_start
  bpy.context.view_layer.update();(D/'render_status.json').write_text(json.dumps({'state':'rendering','view':kind,'frame':frame,'remaining':len(jobs)}));bpy.ops.render.render(write_still=True);return .2
 except:
  (D/'render_status.json').write_text(json.dumps({'state':'error','error':traceback.format_exc()}));return None
bpy.app.timers.register(step,first_interval=.3)

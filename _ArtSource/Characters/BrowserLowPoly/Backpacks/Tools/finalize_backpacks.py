import bpy,json,traceback
from pathlib import Path
from mathutils import Vector
O=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=O/'Backpacks';sc=bpy.context.scene
stage='wait';outfit={}
def step():
 global stage,outfit
 try:
  if stage=='wait':
   p=D/'verification.json'
   if not p.exists():return 1
   s=json.loads(p.read_text())
   if s['state']=='checking':return 1
   if s['state']!='done':return None
   c=bpy.data.objects['Backpack_Controls'];c['Marta']=2;c['Kshishtof']=3;c.update_tag();sc.frame_set(1);sc.camera.location=(1.8,7.8,2.2);sc.camera.rotation_euler=(Vector((0,.05,1.08))-sc.camera.location).to_track_quat('-Z','Y').to_euler();sc.camera.data.type='ORTHO';sc.camera.data.ortho_scale=3.6;sc.render.resolution_x=1200;sc.render.resolution_y=850;sc.render.resolution_percentage=100;sc.cycles.samples=12;sc.render.filepath=str(D/'Review/Primal_Backpacks.png');bpy.ops.render.render(write_still=True)
   for label in ['Marta','Kshishtof']:
    for item in ['PrimalDress','PrimalTop','PrimalSkirt']:
     o=bpy.data.objects.get(label+'_'+item+'_Wear')
     if o:outfit[o.name]=o['equipped'];o['equipped']=int(item=='PrimalDress');o.update_tag()
   sc.frame_set(10);stage='fur';return .3
  if stage=='fur':
   sc.render.filepath=str(D/'Review/Primal_Backpacks_Fur.png');bpy.ops.render.render(write_still=True)
   for name,val in outfit.items():o=bpy.data.objects[name];o['equipped']=val;o.update_tag()
   sc.frame_set(1);stage='save';return .3
  for kind in ['meshes','armatures','materials','images','actions']:
   group=getattr(bpy.data,kind)
   for x in list(group):
    if x.users==0:group.remove(x)
  bpy.ops.object.select_all(action='DESELECT')
  for screen in bpy.data.screens:
   for a in screen.areas:
    if a.type=='VIEW_3D':a.spaces.active.region_3d.view_perspective='CAMERA';a.spaces.active.shading.type='MATERIAL';a.spaces.active.overlay.show_overlays=False
  sc.render.filepath=str(D/'Review/Primal_Backpacks.png');bpy.ops.wm.save_as_mainfile(filepath=str(O/'Primal_Wardrobe.blend'))
  (D/'final_status.json').write_text(json.dumps({'state':'done','file':bpy.data.filepath,'outfit_restored':True,'preview_selection':{'Marta':2,'Kshishtof':3}},indent=2))
  if bpy.context.screen and not bpy.context.screen.is_animation_playing:bpy.ops.screen.animation_play()
 except:
  for name,val in outfit.items():
   if name in bpy.data.objects:bpy.data.objects[name]['equipped']=val;bpy.data.objects[name].update_tag()
  (D/'final_status.json').write_text(json.dumps({'state':'error','error':traceback.format_exc()},indent=2))
 return None
bpy.app.timers.register(step,first_interval=.3)

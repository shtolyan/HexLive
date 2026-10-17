import bpy,json,traceback
from pathlib import Path
from mathutils import Vector
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=OUT/'Hair';R=D/'Review'
def finish_scene():
 try:
  state=json.loads((R/'status.json').read_text())['state']
  if state=='rendering':return 1
  if state!='done':return None
  sc=bpy.context.scene;c=bpy.data.objects['Hair_Controls'];c['style']=1;c['lod']=0;c.update_tag();sc.frame_set(1)
  code=(D/'Tools/hair_controls.py').read_text();exec(compile(code,'hair_controls.py','exec'),{})
  t=bpy.data.texts['Primal Controls.py'];marker='\n# ---- Hair controls ----\n';original=t.as_string().split(marker)[0];t.clear();t.write(original+marker+code)
  sc.camera.location=(2.1,-7.8,2.5);sc.camera.rotation_euler=(Vector((0,0,1.03))-sc.camera.location).to_track_quat('-Z','Y').to_euler();sc.camera.data.type='ORTHO';sc.camera.data.ortho_scale=3.65
  sc.render.resolution_x=1200;sc.render.resolution_y=850;sc.render.resolution_percentage=100;sc.cycles.samples=24;sc.render.filepath=str(R/'Primal_Hair_Preview.png');bpy.ops.render.render(write_still=True)
  bpy.ops.object.select_all(action='DESELECT')
  for screen in bpy.data.screens:
   for area in screen.areas:
    if area.type=='VIEW_3D':
     area.spaces.active.region_3d.view_perspective='CAMERA';area.spaces.active.shading.type='MATERIAL';area.spaces.active.overlay.show_overlays=False
  for kind in ['meshes','armatures','materials','images','actions']:
   group=getattr(bpy.data,kind)
   for x in list(group):
    if x.users==0:group.remove(x)
  bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'))
  (D/'final_status.json').write_text(json.dumps({'state':'done','file':bpy.data.filepath,'shape_keys':{n:len(bpy.data.objects[n+'_LOD0'].data.shape_keys.key_blocks)-1 for n in ['Marta','Kshishtof']},'hair_objects':sum(o.name.endswith(('_LOD0','_LOD1')) and 'hairstyle_id' in o for o in bpy.data.objects),'preview':str(R/'Primal_Hair_Preview.png')},indent=2))
  if bpy.context.screen and not bpy.context.screen.is_animation_playing:bpy.ops.screen.animation_play()
 except:(D/'final_status.json').write_text(json.dumps({'state':'error','error':traceback.format_exc()},indent=2))
 return None
bpy.app.timers.register(finish_scene,first_interval=.5)

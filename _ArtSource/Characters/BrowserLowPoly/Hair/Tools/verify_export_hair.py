import bpy,json,traceback
from pathlib import Path
import numpy as np
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=OUT/'Hair';sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();control=bpy.data.objects['Hair_Controls'];report={};names=json.loads((D/'build_status.json').read_text())['hairs'];bodyrig=bpy.data.objects['Marta_Rig'];jobs=list(names)
def active(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o

def check(name):
 gr=bpy.data.objects[name+'_HairSkeleton'];base=bpy.data.objects[name+'_LOD0'];far=bpy.data.objects[name+'_LOD1'];index=base.animation_data.drivers[0].driver.expression.split()[2];control['style']=int(index);control['lod']=0;control.update_tag();sc.frame_set(1);dg.update();bind_error=max(max(abs(float(x)) for row in (b.matrix_local-bodyrig.data.bones[b.name].matrix_local) for x in row) for b in gr.data.bones);motion=0;bone_error=0;first=None
 for frame in range(1,20):
  sc.frame_set(frame);dg.update();bone_error=max(bone_error,max(max(abs(float(x)) for row in (b.matrix-bodyrig.pose.bones[b.name].matrix) for x in row) for b in gr.pose.bones));ev=base.evaluated_get(dg);me=ev.to_mesh();co=np.array([v.co[:] for v in me.vertices]);ev.to_mesh_clear()
  if first is None:first=co
  motion=max(motion,float(np.max(np.linalg.norm(co-first,axis=1)))*.01)
 data={'bind_error':bind_error,'animated_bone_error':bone_error,'motion_m':motion,'lods':{}}
 assert bind_error<1e-5 and bone_error<1e-4
 for lod,o in enumerate([base,far]):
  control['lod']=lod;control.update_tag();dg.update();assert not o.hide_render
  used={v for p in o.data.polygons for v in p.vertices};err=max(abs(sum(g.weight for g in v.groups)-1) for v in o.data.vertices);maxw=max(len(v.groups) for v in o.data.vertices);tris=sum(len(p.vertices)-2 for p in o.data.polygons);images=[n.image for m in o.data.materials for n in m.node_tree.nodes if n.type=='TEX_IMAGE'];assert len(used)==len(o.data.vertices) and err<1e-5 and maxw<=4 and len(o.data.materials)==1 and all(im.packed_file and tuple(im.size)==(512,512) for im in images)
  data['lods'][lod]={'vertices':len(o.data.vertices),'triangles':tris,'max_weights':maxw,'weight_sum_error':err,'one_packed_atlas':True}
  # Export the single hair with its own original-name rig, in neutral body coordinates.
  old_action=gr.animation_data.action;gr.animation_data.action=None;oldpose=gr.data.pose_position;gr.data.pose_position='REST';world=gr.matrix_world.copy();export_world=world.copy();export_world.translation.x=0;gr.matrix_world=export_world;dg.update();active(o);gr.hide_set(False);gr.select_set(True)
  bpy.ops.export_scene.gltf(filepath=str(D/'Exports'/(name+f'_LOD{lod}.glb')),export_format='GLB',use_selection=True,export_skins=True,export_morph=False,export_animations=False,export_apply=False,export_extras=True)
  gr.matrix_world=world;gr.data.pose_position=oldpose;gr.animation_data.action=old_action;gr.hide_set(True)
 report[name]=data

def step():
 if not jobs:
  control['style']=1;control['lod']=0;control.update_tag();sc.frame_set(1);(D/'verification.json').write_text(json.dumps({'state':'done','hairs':report},indent=2));return None
 name=jobs.pop(0)
 try:check(name)
 except:(D/'verification.json').write_text(json.dumps({'state':'error','name':name,'error':traceback.format_exc(),'hairs':report},indent=2));return None
 (D/'verification.json').write_text(json.dumps({'state':'checking','current':name,'remaining':len(jobs),'hairs':report},indent=2));return .2
bpy.app.timers.register(step,first_interval=.3)

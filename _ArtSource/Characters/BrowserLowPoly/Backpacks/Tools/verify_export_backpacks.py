import bpy,json,traceback,hashlib,math
from pathlib import Path
from mathutils.bvhtree import BVHTree
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Backpacks');sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();control=bpy.data.objects['Backpack_Controls'];report={'packs':{},'body_contract':{},'toggle_checks':{}};jobs=list(json.loads((D/'build_status.json').read_text())['packs'])
before=json.loads((D/'body_before.json').read_text())
for label in ['Marta','Kshishtof']:
 o=bpy.data.objects[label+'_LOD0'];current={'bones':list(bpy.data.objects[label+'_Rig'].data.bones.keys()),'shapes':list(o.data.shape_keys.key_blocks.keys()),'coordinates_sha256':hashlib.sha256(str([[v.co[:] for v in k.data] for k in o.data.shape_keys.key_blocks]).encode()).hexdigest()};assert current==before[label];report['body_contract'][label]={'unchanged':True,'bones':len(current['bones']),'blendshapes':len(current['shapes'])-1}
 for style in range(4):
  control[label]=style;control.update_tag();sc.frame_set(1);dg.update();visible=[x.name for x in bpy.data.objects if x.type=='MESH' and x.get('actor_fit')==label and 'pack_style' in x and not x.hide_render];assert len(visible)==int(style>0);assert all(bpy.data.objects[n]['pack_style']==style for n in visible)
 report['toggle_checks'][label]='0 none; 1–3 exactly one pack'
def check(name):
 o=bpy.data.objects[name];label=o['actor_fit'];rig=next(m.object for m in o.modifiers if m.type=='ARMATURE');bodyrig=bpy.data.objects[label+'_Rig'];control[label]=o['pack_style'];control.update_tag();sc.frame_set(1);dg.update();bind=max(abs(v) for b in rig.data.bones for row in (b.matrix_local-bodyrig.data.bones[b.name].matrix_local) for v in row);pose_error=0;max_motion=0;first=None;skin_deep=[]
 for frame in range(1,20):
  sc.frame_set(frame);dg.update();pose_error=max(pose_error,max(abs(v) for b in rig.pose.bones for row in (b.matrix-bodyrig.pose.bones[b.name].matrix) for v in row));ev=o.evaluated_get(dg);me=ev.to_mesh();co=[v.co.copy() for v in me.vertices]
  if first is None:first=co
  max_motion=max(max_motion,max((p-q).length for p,q in zip(co,first))*.01)
  assert all(math.isfinite(x) for p in co for x in p)
  if frame in [1,5,10,15,19]:
   b=bpy.data.objects[label+'_LOD0'].evaluated_get(dg);bm=b.to_mesh();bm.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in bm.vertices],[list(t.vertices) for t in bm.loop_triangles],all_triangles=True);depths=[]
   for v in co:
    p,n,fi,dist=bv.find_nearest(v);signed=(v-p).dot(n)
    if dist<3 and signed<-.3:depths.append(-signed)
   skin_deep.append({'frame':frame,'vertices_deeper_than_3mm':len(depths),'max_depth_cm':max(depths,default=0)});b.to_mesh_clear()
  ev.to_mesh_clear()
 assert bind<1e-5 and pose_error<1e-4 and max_motion>.01
 sums=[sum(g.weight for g in v.groups) for v in o.data.vertices];maxw=max(len(v.groups) for v in o.data.vertices);assert maxw<=4 and max(abs(x-1) for x in sums)<1e-5
 used={v for p in o.data.polygons for v in p.vertices};assert len(used)==len(o.data.vertices)
 ims=[n.image for n in o.data.materials[0].node_tree.nodes if n.type=='TEX_IMAGE'];assert len(o.data.materials)==1 and len(ims)==1 and ims[0].packed_file and tuple(ims[0].size)==(512,512)
 old=rig.animation_data.action;pose=rig.data.pose_position;world=rig.matrix_world.copy();rig.animation_data.action=None;rig.data.pose_position='REST';m=world.copy();m.translation.x=0;rig.matrix_world=m;dg.update();bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);rig.hide_set(False);rig.select_set(True);bpy.context.view_layer.objects.active=o
 try:bpy.ops.export_scene.gltf(filepath=str(D/'Exports'/(name+'.glb')),export_format='GLB',use_selection=True,export_skins=True,export_morph=False,export_animations=False,export_apply=False,export_extras=True)
 finally:rig.matrix_world=world;rig.data.pose_position=pose;rig.animation_data.action=old;rig.hide_set(True)
 report['packs'][name]={'triangles':sum(len(p.vertices)-2 for p in o.data.polygons),'vertices':len(o.data.vertices),'bones':len(rig.data.bones),'max_weights':maxw,'bind_error':bind,'pose_error':pose_error,'motion_m':max_motion,'packed_atlas_512':True,'skin_contact_samples':skin_deep}
def step():
 if not jobs:
  control['Marta']=2;control['Kshishtof']=3;control.update_tag();sc.frame_set(1);(D/'verification.json').write_text(json.dumps({'state':'done',**report},indent=2));return None
 name=jobs.pop(0)
 try:check(name)
 except:(D/'verification.json').write_text(json.dumps({'state':'error','name':name,'error':traceback.format_exc(),**report},indent=2));return None
 (D/'verification.json').write_text(json.dumps({'state':'checking','current':name,**report},indent=2));return .3
bpy.app.timers.register(step,first_interval=.3)

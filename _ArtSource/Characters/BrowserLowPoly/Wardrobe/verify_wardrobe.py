import bpy,json,hashlib,traceback
import numpy as np
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=OUT/'Wardrobe';sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();build=json.loads((D/'build_status.json').read_text());result={'bodies':{},'garments':{},'errors':[]};caches={}
for label,old in build['body_contract'].items():
 o=bpy.data.objects[label+'_LOD0'];r=bpy.data.objects[label+'_Rig'];h=hashlib.sha256(str([[v.co[:] for v in k.data] for k in o.data.shape_keys.key_blocks]).encode()).hexdigest();result['bodies'][label]={'bone_names_unchanged':list(r.data.bones.keys())==old['bones'],'shape_names_unchanged':list(o.data.shape_keys.key_blocks.keys())==old['shapes'],'all_morph_coordinates_unchanged':h==old['mesh_hash']}
 assert all(result['bodies'][label].values())
 cache=[]
 for frame in range(1,20):
  sc.frame_set(frame);dg.update();ev=o.evaluated_get(dg);mesh=ev.to_mesh();mesh.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in mesh.vertices],[t.vertices[:] for t in mesh.loop_triangles],all_triangles=True);ev.to_mesh_clear();cache.append(bv)
 caches[label]=cache
jobs=list(build['garments'])
def check_one(name):
 o=bpy.data.objects[name];label=o['fitted_for'];rig=o.parent;bodyrig=bpy.data.objects[label+'_Rig'];root=bpy.data.objects[name+'_Wear'];weight_error=max(abs(sum(g.weight for g in v.groups)-1) for v in o.data.vertices);missing=sorted({o.vertex_groups[g.group].name for v in o.data.vertices for g in v.groups if o.vertex_groups[g.group].name not in rig.data.bones});bind_error=max(max(abs(float(x)) for row in (b.matrix_local-bodyrig.data.bones[b.name].matrix_local) for x in row) for b in rig.data.bones)
 o.data.calc_loop_triangles();triangles=[t.vertices[:] for t in o.data.loop_triangles if not o.data.materials[t.material_index].get('alpha_mask_retained_for_all_variants',False)];rest=np.array([v.co[:] for v in o.data.vertices]);refs=np.array(triangles);mins=[];bads=[];motion=0;bone_error=0;start=None
 for fi,frame in enumerate(range(1,20)):
  sc.frame_set(frame);dg.update();transforms={p.name:np.array(p.matrix@p.bone.matrix_local.inverted()) for p in rig.pose.bones};mats=np.array([sum((transforms[o.vertex_groups[g.group].name]*g.weight for g in v.groups),np.zeros((4,4))) for v in o.data.vertices]);posed=np.einsum('nij,nj->ni',mats[:,:3,:3],rest)+mats[:,:3,3]
  if start is None:start=posed
  motion=max(motion,float(np.max(np.linalg.norm(posed-start,axis=1))))
  bone_error=max(bone_error,max(max(abs(float(x)) for row in (p.matrix-bodyrig.pose.bones[p.name].matrix) for x in row) for p in rig.pose.bones))
  # Opaque triangles catch actual visible body intersections; fur alpha cards are deliberately layered.
  used=np.unique(refs.flatten());points=np.vstack([posed[used],posed[refs].mean(axis=1)]);worst=0;bad=0
  for p in points:
   pos=Vector(p);q,n,_,_=caches[label][fi].find_nearest(pos);signed=(pos-q).dot(n);worst=min(worst,signed);bad+=int(signed<-.1)
  mins.append(worst*.01);bads.append(bad)
 old=int(root['material_variant']);maxvar=len(root['variant_names'].split(' | '))-1;variant_ok=True
 for i in range(maxvar+1):
  root['material_variant']=i;root.update_tag();sc.frame_set(sc.frame_current);dg.update()
  for m in o.data.materials:
   for n in m.node_tree.nodes:
    if n.name.startswith('Variant '):
     idx=root['variant_names'].split(' | ').index(n.name[len('Variant '):]);variant_ok&=abs(n.inputs[0].default_value-(1 if i==idx else 0))<.01
 root['material_variant']=old;root.update_tag();dg.update()
 imgs=[n.image for m in o.data.materials for n in m.node_tree.nodes if n.type=='TEX_IMAGE']
 result['garments'][name]={'triangles':sum(len(p.vertices)-2 for p in o.data.polygons),'material_slots':len(o.data.materials),'weight_sum_error':weight_error,'missing_bones':missing,'bind_matrix_error':bind_error,'animated_bone_matrix_error':bone_error,'motion_m':motion*.01,'variant_switches_work':bool(variant_ok),'all_images_packed':all(im.packed_file for im in imgs),'min_signed_body_distance_m_by_frame':mins,'samples_below_minus_1mm_by_frame':bads}
 assert weight_error<1e-5 and not missing and bind_error<1e-5 and bone_error<1e-4 and variant_ok

def step():
 if not jobs:
  sc.frame_set(1);(D/'verification.json').write_text(json.dumps({'state':'done',**result},indent=2));return None
 name=jobs.pop(0)
 try:
  (D/'verification.json').write_text(json.dumps({'state':'checking','current':name,'remaining':len(jobs),**result},indent=2));check_one(name)
 except Exception:
  result['errors'].append({'name':name,'error':traceback.format_exc()});(D/'verification.json').write_text(json.dumps({'state':'error',**result},indent=2));return None
 return .3
bpy.app.timers.register(step,first_interval=.3)
print('VERIFY_STARTED')

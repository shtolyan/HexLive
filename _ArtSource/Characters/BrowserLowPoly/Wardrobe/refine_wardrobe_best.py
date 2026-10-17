import bpy,numpy as np,json,traceback
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=OUT/'Wardrobe';sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();verify=json.loads((D/'verification.json').read_text());jobs=[n for n,v in verify['garments'].items() if min(v['min_signed_body_distance_m_by_frame'])<-.001];history={};state={};bodycache={}
for label in ['Marta','Kshishtof']:
 body=bpy.data.objects[label+'_LOD0'];c=[]
 for frame in range(1,20):
  sc.frame_set(frame);dg.update();ev=body.evaluated_get(dg);me=ev.to_mesh();me.calc_loop_triangles();c.append(BVHTree.FromPolygons([v.co for v in me.vertices],[t.vertices[:] for t in me.loop_triangles],all_triangles=True));ev.to_mesh_clear()
 bodycache[label]=c

def begin(name):
 o=bpy.data.objects[name];rig=o.parent;label=o['fitted_for'];o.data.calc_loop_triangles();tris=np.array([t.vertices[:] for t in o.data.loop_triangles if not o.data.materials[t.material_index].get('alpha_mask_retained_for_all_variants',False)]);ids=np.unique(tris.flatten());count=len(o.data.vertices)
 refs=np.vstack([np.column_stack([ids]*3),tris,tris,tris,tris]);bary=np.vstack([np.tile([1.,0,0],(len(ids),1)),np.tile([1/3]*3,(len(tris),1)),np.tile([.5,.5,0],(len(tris),1)),np.tile([0,.5,.5],(len(tris),1)),np.tile([.5,0,.5],(len(tris),1))]);cache=[]
 for fi,frame in enumerate(range(1,20)):
  sc.frame_set(frame);dg.update();t={p.name:np.array(p.matrix@p.bone.matrix_local.inverted()) for p in rig.pose.bones};mats=np.array([sum((t[o.vertex_groups[g.group].name]*g.weight for g in v.groups),np.zeros((4,4))) for v in o.data.vertices]);cache.append((bodycache[label][fi],mats,np.linalg.inv(mats[:,:3,:3])))
 state.update(o=o,refs=refs,bary=bary,cache=cache,iteration=0,best_score=-1e9,best_rest=None,stale=0);history[name]=[]

def step():
 try:
  if not state:
   if not jobs:
    sc.frame_set(1);bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'));(D/'refinement_best.json').write_text(json.dumps({'state':'done','history':history},indent=2));return None
   begin(jobs.pop(0))
  o=state['o'];rest=np.array([v.co[:] for v in o.data.vertices]);sums=np.zeros_like(rest);weights=np.zeros(len(rest));worst=0;bad=0
  for bv,mats,inv in state['cache']:
   posed=np.einsum('nij,nj->ni',mats[:,:3,:3],rest)+mats[:,:3,3];samples=(posed[state['refs']]*state['bary'][:,:,None]).sum(axis=1)
   for ids,bs,p in zip(state['refs'],state['bary'],samples):
    point,n,_,_=bv.find_nearest(Vector(p));signed=(Vector(p)-point).dot(n);worst=min(worst,signed);bad+=int(signed<-.1)
    if signed>=.25:continue
    push=np.array(n)*min(.8,.3-signed)
    for vi,w in zip(ids,bs):
     if w:sums[vi]+=inv[vi]@push*w;weights[vi]+=w
  history[o.name].append({'iteration':state['iteration'],'worst_m':worst*.01,'samples_below_minus_1mm':bad})
  if worst>state['best_score']+0.005 or state['best_rest'] is None:
   state['best_score']=worst;state['best_rest']=rest.copy();state['stale']=0
  else:state['stale']+=1
  if bad and state['iteration']<20 and state['stale']<5:
   change=sums/np.maximum(weights[:,None],1)
   for v,d in zip(o.data.vertices,change):v.co+=Vector(d)
   o.data.update();state['iteration']+=1
  else:
   for v,co in zip(o.data.vertices,state['best_rest']):v.co=Vector(co)
   o.data.update();history[o.name].append({'kept_best_worst_m':state['best_score']*.01});state.clear()
   bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'))
  (D/'refinement_best.json').write_text(json.dumps({'state':'fitting','current':o.name,'remaining':len(jobs),'history':history},indent=2))
  return .15
 except:
  (D/'refinement_best.json').write_text(json.dumps({'state':'error','error':traceback.format_exc(),'history':history},indent=2));return None
bpy.app.timers.register(step,first_interval=.2);print('REFINEMENT_STARTED',len(jobs))

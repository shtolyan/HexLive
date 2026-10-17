"""Compare original garment skin against a surface-matched skin over the complete run; keep only an improvement."""
import bpy,numpy as np,json,traceback
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=OUT/'Wardrobe';sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();history={};jobs=[l+'_'+i for l in ['Marta','Kshishtof'] for i in ['PrimalWrapBoots','PrimalArmWraps']]
def bary(p,a,b,c):
 e=b-a;f=c-a;g=p-a;den=e.dot(e)*f.dot(f)-e.dot(f)**2
 if abs(den)<1e-12:return [1.,0,0]
 v=(f.dot(f)*g.dot(e)-e.dot(f)*g.dot(f))/den;w=(e.dot(e)*g.dot(f)-e.dot(f)*g.dot(e))/den;q=np.maximum([1-v-w,v,w],0);return q/q.sum()
def test(name):
 o=bpy.data.objects[name];body=bpy.data.objects[o['fitted_for']+'_LOD0'];rig=o.parent;bodyrig=bpy.data.objects[o['fitted_for']+'_Rig'];body.data.calc_loop_triangles();btris=[t.vertices[:] for t in body.data.loop_triangles];bvh=BVHTree.FromPolygons([v.co for v in body.data.vertices],btris,all_triangles=True);original=[{o.vertex_groups[g.group].name:g.weight for g in v.groups} for v in o.data.vertices];new=[]
 for v in o.data.vertices:
  point,n,ti,dist=bvh.find_nearest(v.co);ids=btris[ti];bw=bary(point,*[body.data.vertices[i].co for i in ids]);weights={}
  for vi,w in zip(ids,bw):
   for g in body.data.vertices[vi].groups:
    bn=body.vertex_groups[g.group].name
    while bn not in rig.data.bones:
     par=bodyrig.data.bones[bn].parent
     if par is None:raise ValueError(bn)
     bn=par.name
    weights[bn]=weights.get(bn,0)+g.weight*w
  total=sum(weights.values());new.append({k:v/total for k,v in weights.items() if v>1e-8})
 o.data.calc_loop_triangles();tr=np.array([t.vertices[:] for t in o.data.loop_triangles]);rest=np.array([v.co[:] for v in o.data.vertices]);cache=[]
 for frame in range(1,20):
  sc.frame_set(frame);dg.update();ev=body.evaluated_get(dg);me=ev.to_mesh();me.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in me.vertices],[t.vertices[:] for t in me.loop_triangles],all_triangles=True);ev.to_mesh_clear();t={p.name:np.array(p.matrix@p.bone.matrix_local.inverted()) for p in rig.pose.bones};cache.append((bv,t))
 scores=[];candidates=[]
 for alpha in [0,.5,1.]:
  ws=[]
  for a,b in zip(original,new):ws.append({k:a.get(k,0)*(1-alpha)+b.get(k,0)*alpha for k in a.keys()|b.keys()})
  worst=0;bad=0
  for bv,t in cache:
   mats=np.array([sum((t[k]*w for k,w in ws1.items()),np.zeros((4,4))) for ws1 in ws]);pos=np.einsum('nij,nj->ni',mats[:,:3,:3],rest)+mats[:,:3,3];samples=np.vstack([pos,pos[tr].mean(axis=1),(pos[tr[:,0]]+pos[tr[:,1]])*.5,(pos[tr[:,1]]+pos[tr[:,2]])*.5,(pos[tr[:,2]]+pos[tr[:,0]])*.5])
   for p in samples:
    q,n,_,_=bv.find_nearest(Vector(p));s=(Vector(p)-q).dot(n);worst=min(worst,s);bad+=int(s<-.1)
  scores.append({'surface_weight_fraction':alpha,'worst_m':worst*.01,'samples_below_minus_1mm':bad});candidates.append(ws)
 best=max(range(len(scores)),key=lambda i:scores[i]['worst_m'])
 if best:
  # Preserve group names and the same separate rig; only replace normalized weights.
  for g in o.vertex_groups:g.remove(list(range(len(o.data.vertices))))
  for vi,ws in enumerate(candidates[best]):
   for bn,w in ws.items():
    if w>1e-7:(o.vertex_groups.get(bn) or o.vertex_groups.new(name=bn)).add([vi],w,'REPLACE')
  bpy.context.view_layer.objects.active=o
 history[name]={'trials':scores,'kept':best}

def step():
 if not jobs:
  sc.frame_set(1);bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'));(D/'weights_comparison.json').write_text(json.dumps({'state':'done','results':history},indent=2));return None
 name=jobs.pop(0)
 try:test(name)
 except Exception:
  (D/'weights_comparison.json').write_text(json.dumps({'state':'error','error':traceback.format_exc(),'results':history},indent=2));return None
 (D/'weights_comparison.json').write_text(json.dumps({'state':'checking','current':name,'remaining':len(jobs),'results':history},indent=2));return .2
bpy.app.timers.register(step,first_interval=.3)

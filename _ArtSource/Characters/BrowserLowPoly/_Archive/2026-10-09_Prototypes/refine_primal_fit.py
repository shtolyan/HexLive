"""Fit low-poly clothing against all sampled frames of the actual retargeted run."""
import bpy,numpy as np,json
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
D=Path(__file__).resolve().parent/'Clothing_Primal'
bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();history={}
for label,item in [('Marta','PrimalBriefs'),('Marta','PrimalTop'),('Kshishtof','PrimalBriefs')]:
 o=bpy.data.objects[label+'_'+item];body=bpy.data.objects[label+'_LOD0'];rig=bpy.data.objects[o.name+'_Skeleton'];count=len(o.data.vertices)
 cache=[];o.data.calc_loop_triangles();triangles=np.array([t.vertices[:] for t in o.data.loop_triangles])
 # Vertices, edge midpoints and triangle centroids catch penetration between garment vertices.
 refs=np.vstack([np.column_stack([np.arange(count)]*3),triangles,triangles,triangles,triangles])
 bary=np.vstack([np.tile([1.,0,0],(count,1)),np.tile([1/3]*3,(len(triangles),1)),np.tile([.5,.5,0],(len(triangles),1)),np.tile([0,.5,.5],(len(triangles),1)),np.tile([.5,0,.5],(len(triangles),1))])
 for frame in range(1,20):
  sc.frame_set(frame);dg.update();ev=body.evaluated_get(dg);mesh=ev.to_mesh();mesh.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in mesh.vertices],[t.vertices[:] for t in mesh.loop_triangles],all_triangles=True);ev.to_mesh_clear()
  transforms={pb.name:np.array(pb.matrix@pb.bone.matrix_local.inverted()) for pb in rig.pose.bones}
  mats=np.array([sum((transforms[o.vertex_groups[g.group].name]*g.weight for g in v.groups),np.zeros((4,4))) for v in o.data.vertices]);cache.append((bv,mats,np.linalg.inv(mats[:,:3,:3])))
 iterations=[]
 for iteration in range(32):
  rest=np.array([v.co[:] for v in o.data.vertices]);sums=np.zeros_like(rest);weights=np.zeros(count);worst=0;bad=0
  for bv,mats,inv in cache:
   posed=np.einsum('nij,nj->ni',mats[:,:3,:3],rest)+mats[:,:3,3];samples=(posed[refs]*bary[:,:,None]).sum(axis=1)
   for ids,bs,p in zip(refs,bary,samples):
    point,normal,_,_=bv.find_nearest(Vector(p));signed=(Vector(p)-point).dot(normal);worst=min(worst,signed)
    if signed<-.10:bad+=1
    if signed>=.35:continue
    push=np.array(normal)*min(.7,.40-signed)
    for vi,w in zip(ids,bs):
     if w:
      sums[vi]+=inv[vi]@push*w;weights[vi]+=w
  iterations.append({'iteration':iteration,'worst_m':worst*.01,'samples_below_minus_1mm':bad})
  print('REFINE',o.name,iterations[-1],flush=True)
  if bad==0:break
  change=sums/np.maximum(weights[:,None],1);change*=1.0
  for v,d in zip(o.data.vertices,change):v.co+=Vector(d)
  o.data.update()
 history[o.name]=iterations
 # A literal initial visibility state keeps the saved render state deterministic before drivers update.
sc.frame_set(1);bpy.context.view_layer.update()
# Reset both toggle states and leave underwear equipped.
r=bpy.data.objects['Kshishtof_Rig'];r['primal_briefs_equipped']=0;r.update_tag();dg.update();r['primal_briefs_equipped']=1;r.update_tag();dg.update()
bpy.ops.wm.save_as_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
(D/'fit_refinement.json').write_text(json.dumps(history,indent=2))
sc.render.resolution_percentage=75;sc.cycles.samples=12;sc.render.filepath=str(D/'preview_run.png');bpy.ops.render.render(write_still=True)

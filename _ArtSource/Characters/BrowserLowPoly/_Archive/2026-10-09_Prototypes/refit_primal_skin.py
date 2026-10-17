import bpy,json,numpy as np
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
D=Path(__file__).resolve().parent/'Clothing_Primal';bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
def bary(p,a,b,c):
 v0=b-a;v1=c-a;v2=p-a;den=v0.dot(v0)*v1.dot(v1)-v0.dot(v1)**2
 if abs(den)<1e-12:return np.array([1.,0,0])
 v=(v1.dot(v1)*v2.dot(v0)-v0.dot(v1)*v2.dot(v1))/den;w=(v0.dot(v0)*v2.dot(v1)-v0.dot(v1)*v2.dot(v0))/den;q=np.maximum([1-v-w,v,w],0);return q/q.sum()
for label,item in [('Marta','PrimalBriefs'),('Kshishtof','PrimalBriefs')]:
 o=bpy.data.objects[label+'_'+item];body=bpy.data.objects[label+'_LOD0'];rig=bpy.data.objects[o.name+'_Skeleton'];body.data.calc_loop_triangles();triangles=[t.vertices[:] for t in body.data.loop_triangles];bv=BVHTree.FromPolygons([v.co for v in body.data.vertices],triangles,all_triangles=True)
 bw=[{body.vertex_groups[g.group].name:g.weight for g in v.groups} for v in body.data.vertices]
 for g in o.vertex_groups:g.remove(list(range(len(o.data.vertices))))
 for v in o.data.vertices:
  hit,normal,ti,dist=bv.find_nearest(v.co);weights=bary(hit,*[body.data.vertices[i].co for i in triangles[ti]]);skin={}
  for si,w in zip(triangles[ti],weights):
   for n,b in bw[si].items():
    if n in rig.data.bones:skin[n]=skin.get(n,0)+b*w
  total=sum(skin.values());assert total>0
  for n,w in skin.items():
   if w>1e-7:(o.vertex_groups.get(n) or o.vertex_groups.new(name=n)).add([v.index],w/total,'REPLACE')
 print('SKIN_REFIT',o.name,flush=True)
bpy.ops.wm.save_as_mainfile(filepath=str(D/'Primal_Run_Test.blend'))

import bpy,json,numpy as np
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
D=Path(__file__).resolve().parent/'Clothing_Primal'
bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
sc=bpy.context.scene;report={};dg=bpy.context.evaluated_depsgraph_get()
for label,item in [('Marta','PrimalBriefs'),('Marta','PrimalTop'),('Kshishtof','PrimalBriefs')]:
 o=bpy.data.objects[label+'_'+item];b=bpy.data.objects[label+'_LOD0'];gr=bpy.data.objects[o.name+'_Skeleton'];rig=bpy.data.objects[label+'_Rig'];mins=[];counts=[];bone_error=0
 for f in range(1,20):
  sc.frame_set(f);dg.update();be=b.evaluated_get(dg);bm=be.to_mesh();bm.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in bm.vertices],[list(t.vertices) for t in bm.loop_triangles],all_triangles=True)
  oe=o.evaluated_get(dg);om=oe.to_mesh();om.calc_loop_triangles();positions=[v.co.copy() for v in om.vertices];positions.extend(sum((om.vertices[i].co for i in t.vertices),Vector())/3 for t in om.loop_triangles)
  distances=[]
  for p in positions:
   hit,normal,_,_=bv.find_nearest(p);distances.append((p-hit).dot(normal)*.01)
  mins.append(min(distances));counts.append(sum(d<-.001 for d in distances))
  bone_error=max(bone_error,max(max(abs(pb.matrix[i][j]-rig.pose.bones[pb.name].matrix[i][j]) for i in range(4) for j in range(4)) for pb in gr.pose.bones))
  be.to_mesh_clear();oe.to_mesh_clear()
 report[o.name]={'min_surface_distance_m':min(mins),'penetrating_samples_per_frame_over_1mm':counts,'frames':19,'bone_matrix_max_error':bone_error,'skin_normalized':all(abs(sum(g.weight for g in v.groups)-1)<1e-5 for v in o.data.vertices)}
 print('FIT',o.name,report[o.name],flush=True)
rig=bpy.data.objects['Kshishtof_Rig'];gen=bpy.data.objects['Kshishtof_Genitals'];briefs=bpy.data.objects['Kshishtof_PrimalBriefs'];toggle=[]
for value in [0,1]:
 rig['primal_briefs_equipped']=value;rig.update_tag();sc.frame_set(1);dg.update();toggle.append({'equipped':bool(value),'genitals_hidden':bool(gen.hide_render),'briefs_hidden':bool(briefs.hide_render)});assert gen.hide_render==bool(value) and briefs.hide_render!=bool(value)
report['toggle']=toggle;(D/'fit_verification.json').write_text(json.dumps(report,indent=2))
sc.frame_set(1);sc.cycles.samples=12;sc.render.resolution_percentage=75;sc.render.filepath=str(D/'preview_run.png');bpy.ops.render.render(write_still=True)

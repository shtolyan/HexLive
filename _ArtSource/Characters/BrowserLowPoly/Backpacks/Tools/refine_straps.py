import bpy,json,gc,types,traceback
from pathlib import Path
from mathutils import Matrix,Vector
from mathutils.bvhtree import BVHTree
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Backpacks');sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();c=bpy.data.objects['Backpack_Controls'];report={}
for fn in gc.get_objects():
 if type(fn) is types.FunctionType and fn.__code__.co_filename=='finalize_backpacks.py':
  if bpy.app.timers.is_registered(fn):bpy.app.timers.unregister(fn)
  for name,val in fn.__globals__.get('outfit',{}).items():
   if name in bpy.data.objects:bpy.data.objects[name]['equipped']=val;bpy.data.objects[name].update_tag()
if bpy.context.screen.is_animation_playing:bpy.ops.screen.animation_play()
jobs=[n for n in json.loads((D/'build_status.json').read_text())['packs'] if not n.endswith('PrimalTrailPack')]
def refine(name):
 o=bpy.data.objects[name];label=o['actor_fit'];rig=next(m.object for m in o.modifiers if m.type=='ARMATURE');body=bpy.data.objects[label+'_LOD0'];c[label]=o['pack_style'];c.update_tag();initial=[v.co.copy() for v in o.data.vertices];counts=[]
 for iteration in range(3):
  count=0
  for frame in range(1,20):
   sc.frame_set(frame);dg.update();be=body.evaluated_get(dg);bm=be.to_mesh();bm.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co.copy() for v in bm.vertices],[list(t.vertices) for t in bm.loop_triangles],all_triangles=True);be.to_mesh_clear();ev=o.evaluated_get(dg);me=ev.to_mesh();co=[v.co.copy() for v in me.vertices];ev.to_mesh_clear();trans={b.name:(rig.pose.bones[b.name].matrix @ b.matrix_local.inverted()).to_3x3() for b in rig.data.bones}
   for vi,v in enumerate(co):
    p,n,fi,dist=bv.find_nearest(v);signed=(v-p).dot(n)
    if dist<4 and signed<.25:
     weights=o.data.vertices[vi].groups;a=Matrix(((0,0,0),(0,0,0),(0,0,0)))
     for g in weights:
      mat=trans[o.vertex_groups[g.group].name]
      for row in range(3):
       for col in range(3):a[row][col]+=g.weight*mat[row][col]
     delta=a.inverted_safe() @ (n*(.65-signed));o.data.vertices[vi].co+=delta;count+=1
   o.data.update()
  counts.append(count)
  if count==0:break
 shifts=[(v.co-p).length for v,p in zip(o.data.vertices,initial)];report[name]={'iterations_adjustments':counts,'moved_vertices':sum(x>1e-5 for x in shifts),'max_adjustment_cm':max(shifts)}
def step():
 if not jobs:
  (D/'fit_refinement.json').write_text(json.dumps({'state':'done','packs':report},indent=2));exec(compile((D/'Tools/verify_export_backpacks.py').read_text(),'verify_export_backpacks.py','exec'),{});return None
 name=jobs.pop(0)
 try:refine(name)
 except:(D/'fit_refinement.json').write_text(json.dumps({'state':'error','error':traceback.format_exc()},indent=2));return None
 (D/'fit_refinement.json').write_text(json.dumps({'state':'refining','current':name,'packs':report},indent=2));return .3
bpy.app.timers.register(step,first_interval=.3)

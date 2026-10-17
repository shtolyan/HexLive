import bpy,json
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Hair');body=bpy.data.objects['Marta_LOD0'];bm=body.data;bm.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co.copy() for v in bm.vertices],[list(t.vertices) for t in bm.loop_triangles],all_triangles=True);report={}
if bpy.context.screen.is_animation_playing:bpy.ops.screen.animation_play()
for name in ['BendineHair','ChunkyHair','LeonyPonytail']:
 for lod in [0,1]:
  o=bpy.data.objects[name+f'_LOD{lod}'];count=0;maximum=0
  for v in o.data.vertices:
   pos,normal,index,dist=bv.find_nearest(v.co)
   # Fit only close scalp/head surfaces. Preserve long locks and silhouette away from the head.
   if pos is not None and v.co.y>bpy.data.objects['Marta_Rig'].data.bones['head'].head_local.y and dist<2:
    signed=(v.co-pos).dot(normal)
    if signed<.65:
     new=pos+normal*.65;maximum=max(maximum,(new-v.co).length);v.co=new;count+=1
  o.data.update();report[o.name]={'adjusted_vertices':count,'max_displacement_cm':maximum}
(D/'scalp_fit.json').write_text(json.dumps(report,indent=2));print(report)

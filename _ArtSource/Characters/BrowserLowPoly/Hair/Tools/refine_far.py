import bpy,bmesh,json
from pathlib import Path
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Hair');ns=bpy.app.driver_namespace['hair_builder'];c=bpy.data.objects['Hair_Controls']
for name in ['Hair07','TootsieRollHair']:
 o=bpy.data.objects[name+'_LOD1'];o.data=bpy.data.objects[name+'_LOD0'].data.copy();c['style']=ns['NAMES'].index(name)+1;c['lod']=1;c.update_tag();bpy.context.view_layer.update();ns['active'](o)
 bm=bmesh.new();bm.from_mesh(o.data);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1.0);bm.to_mesh(o.data);bm.free();ns['deduplicate'](o)
 mod=o.modifiers.new('Distant silhouette reduction','DECIMATE');mod.ratio=min(1,950/sum(len(p.vertices)-2 for p in o.data.polygons));mod.use_collapse_triangulate=True;bpy.ops.object.modifier_move_up(modifier=mod.name);bpy.ops.object.modifier_apply(modifier=mod.name);ns['clean_loose'](o);ns['trim_weights'](o)
 print(name,sum(len(p.vertices)-2 for p in o.data.polygons))
# The distant scalp requires clearance for the longer planar triangles.
code=(D/'Tools/fit_scalp.py').read_text().replace("['BendineHair','ChunkyHair','LeonyPonytail']",repr(ns['NAMES'])).replace('for lod in [0,1]:','for lod in [1]:').replace("'scalp_fit.json'","'far_scalp_fit.json'");exec(compile(code,'far_fit','exec'),{})

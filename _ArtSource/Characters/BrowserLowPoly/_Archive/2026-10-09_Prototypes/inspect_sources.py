import bpy,json
from pathlib import Path
root=Path('/Volumes/ORICO/HexLive')
result=[]
for source in ['Assets/ImportedActors/Actors/Marta/Marta.new.fbx','Assets/ImportedActors/Daz3D/Kshishtof/Kshishtof.fbx']:
 bpy.ops.wm.read_factory_settings(use_empty=True)
 bpy.ops.import_scene.fbx(filepath=str(root/source),use_anim=False)
 objs=[]
 for o in bpy.data.objects:
  if o.type=='MESH':
   o.data.calc_loop_triangles()
   objs.append(dict(name=o.name,verts=len(o.data.vertices),tris=len(o.data.loop_triangles),dimensions=list(o.dimensions),location=list(o.location),materials=[m.name if m else '' for m in o.data.materials],uv=[u.name for u in o.data.uv_layers],shapes=len(o.data.shape_keys.key_blocks) if o.data.shape_keys else 0,groups=len(o.vertex_groups)))
  elif o.type=='ARMATURE': objs.append(dict(name=o.name,bones=len(o.data.bones)))
 imgs=[dict(name=i.name,path=i.filepath,size=list(i.size)) for i in bpy.data.images]
 result.append(dict(source=source,objects=objs,images=imgs))
(root/'_ArtSource/Characters/BrowserLowPoly/source_inventory.json').write_text(json.dumps(result,indent=2))

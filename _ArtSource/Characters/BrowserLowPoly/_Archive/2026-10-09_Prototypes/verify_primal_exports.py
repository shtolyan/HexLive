import bpy,json,math
from pathlib import Path
D=Path(__file__).resolve().parent/'Clothing_Primal';E=D/'Exports'
bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
expected={}
for name in ['Marta_PrimalBriefs','Marta_PrimalTop','Kshishtof_PrimalBriefs']:
 o=bpy.data.objects[name];r=bpy.data.objects[name+'_Skeleton'];expected[name]={'triangles':sum(len(p.vertices)-2 for p in o.data.polygons),'bones':{b.name:(b.parent.name if b.parent else None) for b in r.data.bones}}
source_names=list(bpy.data.objects['Kshishtof_LOD0'].data.shape_keys.key_blocks.keys());result={}
for name,c in expected.items():
 for ext in ['fbx','glb']:
  bpy.ops.wm.read_factory_settings(use_empty=True);f=E/(name+'.'+ext)
  if ext=='fbx':bpy.ops.import_scene.fbx(filepath=str(f),use_anim=False)
  else:bpy.ops.import_scene.gltf(filepath=str(f))
  meshes=[o for o in bpy.data.objects if o.type=='MESH'];rigs=[o for o in bpy.data.objects if o.type=='ARMATURE'];assert len(meshes)==1 and len(rigs)==1
  o=meshes[0];r=rigs[0];assert 'hip' in r.data.bones and r.data.bones['hip'].parent is None
  assert {b.name:(b.parent.name if b.parent else None) for b in r.data.bones}==c['bones']
  tris=sum(len(p.vertices)-2 for p in o.data.polygons);assert tris==c['triangles'];assert len(o.data.materials)==1
  sums=[sum(g.weight for g in v.groups) for v in o.data.vertices];assert min(sums)>.9999 and max(sums)<1.0001
  assert all(math.isfinite(x) for v in o.data.vertices for x in v.co)
  result[f.name]={'triangles':tris,'own_hip_hierarchy':True,'bone_names_and_parents_exact':True,'normalized_skin':True,'materials':1,'bytes':f.stat().st_size}
  print('VERIFIED',f.name,flush=True)
for ext in ['fbx','glb']:
 bpy.ops.wm.read_factory_settings(use_empty=True);f=E/('Kshishtof_Body_SeparateGenitals.'+ext)
 if ext=='fbx':bpy.ops.import_scene.fbx(filepath=str(f),use_anim=False)
 else:bpy.ops.import_scene.gltf(filepath=str(f))
 meshes=[o for o in bpy.data.objects if o.type=='MESH'];assert len(meshes)==2
 body=next(o for o in meshes if o.data.shape_keys);gen=next(o for o in meshes if o!=body)
 assert list(body.data.shape_keys.key_blocks.keys())==source_names
 rig=next(o for o in bpy.data.objects if o.type=='ARMATURE');assert len(rig.data.bones)==172
 assert len(gen.data.vertices)>=50 and not gen.data.shape_keys
 result[f.name]={'separate_genitals_mesh':True,'bones':172,'exact_morph_names':True,'morphs':109,'bytes':f.stat().st_size}
 print('VERIFIED',f.name,flush=True)
(D/'export_verification.json').write_text(json.dumps(result,indent=2));print('ALL_PRIMAL_EXPORTS_VERIFIED',flush=True)

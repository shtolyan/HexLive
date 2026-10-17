"""Verify every exported blendshape and every bone after FBX/GLB round trip."""
import bpy,json,math
from pathlib import Path
from mathutils import Vector,Matrix
OUT=Path(__file__).resolve().parent;D=OUT/'Rigged_v2'
contract=json.loads((D/'source_contract.json').read_text());transfer=json.loads((D/'morph_transfer.json').read_text());results={}
visemes=['AA','IY','UW','EE','OW','M','F','S','SH','T','ER','L','K','TH']
for label in ['Kshishtof']:
 if 'rig_world' not in contract[label]:
  bpy.ops.wm.read_factory_settings(use_empty=True)
  bpy.ops.import_scene.fbx(filepath=str(OUT.parents[2]/contract[label]['source']),use_anim=False)
  source_rig=next(o for o in bpy.data.objects if o.type=='ARMATURE')
  contract[label]['rig_world']=[list(row) for row in source_rig.matrix_world]
  (D/'source_contract.json').write_text(json.dumps(contract,indent=2))
 expected=[k['name'] for k in contract[label]['shapes']][1:]
 for lod in [0,1]:
  name=f'{label}_LOD{lod}'
  for ext in ['glb','fbx']:
   bpy.ops.wm.read_factory_settings(use_empty=True)
   path=D/(name+'.'+ext)
   if ext=='glb':bpy.ops.import_scene.gltf(filepath=str(path))
   else:bpy.ops.import_scene.fbx(filepath=str(path),use_anim=False)
   obj=next(o for o in bpy.data.objects if o.type=='MESH');rig=next(o for o in bpy.data.objects if o.type=='ARMATURE')
   keys=obj.data.shape_keys.key_blocks;names=[k.name for k in keys][1:]
   assert names==expected,(name,ext,'shape names/order differ',list(zip(names,expected))[:3])
   assert set(rig.data.bones.keys())==set(contract[label]['bones']),(name,ext,'bones differ')
   for bone in rig.data.bones:
    assert (bone.parent.name if bone.parent else None)==contract[label]['bones'][bone.name]['parent'],(name,ext,bone.name,'parent differs')
   rest_errors=[]
   if 'rig_world' in contract[label]:
    source_world=Matrix(contract[label]['rig_world'])
    for bone in rig.data.bones:
     expected_head=source_world@Vector(contract[label]['bones'][bone.name]['head'])
     error=((rig.matrix_world@bone.head_local)-expected_head).length;rest_errors.append(error)
    assert max(rest_errors)<.00005,(name,ext,'rest joint drift',max(rest_errors))
   sums=[sum(g.weight for g in v.groups) for v in obj.data.vertices]
   assert min(sums)>.998 and max(sums)<1.002,(name,ext,min(sums),max(sums))
   assert max(len(v.groups) for v in obj.data.vertices)>4,(name,ext,'full weights lost')
   basis=keys[0];matrix=obj.matrix_world.to_3x3();max_errors=[];moving=0
   for k in list(keys)[1:]:
    displacements=[(matrix@(p.co-q.co)).length for p,q in zip(k.data,basis.data)]
    assert all(math.isfinite(d) for d in displacements)
    extent=max(displacements);reference=transfer[name]['shapes'][k.name]['max_displacement_m']
    assert abs(extent-reference)<.00002,(name,ext,k.name,extent,reference)
    max_errors.append(abs(extent-reference));moving+=extent>1e-8
   for suffix in ['eCTRLv'+v for v in visemes]+['eCTRLEyesClosed','eCTRLSmile','eCTRLAngry']:
    key=next(k for k in list(keys)[1:] if k.name.endswith('__'+suffix))
    assert transfer[name]['shapes'][key.name]['moving_vertices']>0,(name,ext,suffix,'empty')
   results[path.name]={'morph_count':len(names),'all_names_and_order_exact':True,'bones':len(rig.data.bones),'bone_names_and_parents_exact':True,'rest_joint_max_error_m':max(rest_errors) if rest_errors else None,'weights_normalized':True,'max_skin_influences':max(len(v.groups) for v in obj.data.vertices),'nonempty_morphs':moving,'max_morph_roundtrip_error_m':max(max_errors),'fourteen_game_visemes_and_blink_smile_anger':'passed','bytes':path.stat().st_size}
   print('VERIFIED',path.name,flush=True)
(D/'rigged_verification.json').write_text(json.dumps(results,indent=2));print('ALL_RIGGED_CHECKS_PASSED',flush=True)

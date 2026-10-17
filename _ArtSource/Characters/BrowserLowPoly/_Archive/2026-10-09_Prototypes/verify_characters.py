"""Round-trip check and joint deformation preview; run with Blender -b --python."""
import bpy,json,math,struct
from pathlib import Path
from mathutils import Vector,Quaternion
OUT=Path(__file__).resolve().parent
checks={}
for label in ['Marta','Kshishtof']:
 for lod in [0,1]:
  name=f'{label}_LOD{lod}'
  p=OUT/(name+'.glb');raw=p.read_bytes();size,kind=struct.unpack_from('<II',raw,12);doc=json.loads(raw[20:20+size])
  primitives=[pr for mesh in doc['meshes'] for pr in mesh['primitives']]
  assert len(primitives)==1,(name,'expected one opaque primitive')
  assert len(doc['materials'])==1
  pr=primitives[0];assert 'JOINTS_0' in pr['attributes'] and 'WEIGHTS_0' in pr['attributes'];assert 'WEIGHTS_1' not in pr['attributes']
  assert not pr.get('targets')
  assert doc['materials'][0].get('alphaMode','OPAQUE')=='OPAQUE'
  bpy.ops.wm.read_factory_settings(use_empty=True);bpy.ops.import_scene.gltf(filepath=str(p))
  meshes=[o for o in bpy.data.objects if o.type=='MESH'];assert len(meshes)==1
  mesh=meshes[0];rig=next(o for o in bpy.data.objects if o.type=='ARMATURE')
  assert all(all(math.isfinite(c) for c in v.co) for v in mesh.data.vertices)
  assert all(abs(sum(g.weight for g in v.groups)-1)<.002 for v in mesh.data.vertices)
  assert all(v.groups for v in mesh.data.vertices)
  corners=[mesh.matrix_world@Vector(v) for v in mesh.bound_box]
  dims=[max(v[i] for v in corners)-min(v[i] for v in corners) for i in range(3)]
  assert 1.6<dims[2]<2.1,(name,dims)
  checks[name]={'bytes':len(raw),'triangles':doc['accessors'][pr['indices']]['count']//3,'gpu_vertices':doc['accessors'][pr['attributes']['POSITION']]['count'],'draw_primitives':len(primitives),'joints':len(doc['skins'][0]['joints']),'height_m':round(dims[2],3),'weights_normalized':True,'unweighted_vertices':0,'glb_reimport':'passed'}
# Review actual Blender skin deformation, using the same retained source weights.
bpy.ops.wm.open_mainfile(filepath=str(OUT/'HexLive_Browser_Characters.blend'))
for label in ['Marta','Kshishtof']:
 rig=bpy.data.objects[label+'_Rig']
 for bone,angle,axis in [('lShldrBend',65,(0,1,0)),('rShldrBend',-65,(0,1,0)),('lForearmBend',-35,(1,0,0)),('lShin',45,(1,0,0))]:
  b=rig.pose.bones[bone];q=(rig.matrix_world@b.matrix).to_quaternion();local_axis=q.inverted()@Vector(axis);b.rotation_mode='QUATERNION';b.rotation_quaternion=Quaternion(local_axis,math.radians(angle));bpy.context.view_layer.update()
 mesh=bpy.data.objects[label+'_LOD0'];ev=mesh.evaluated_get(bpy.context.evaluated_depsgraph_get());coords=[ev.matrix_world@v.co for v in ev.data.vertices]
 assert all(all(math.isfinite(c) for c in v) for v in coords)
 assert max(p.z for p in coords)-min(p.z for p in coords)<2.3
checks['deformation']='shoulders 65 degrees, elbow 35 degrees, knee 45 degrees; finite bounds; render supplied'
sc=bpy.context.scene;sc.render.filepath=str(OUT/'preview_joint_check.png');sc.cycles.samples=24;bpy.ops.render.render(write_still=True)
(OUT/'verification.json').write_text(json.dumps(checks,indent=2));print('VERIFICATION_OK',json.dumps(checks))

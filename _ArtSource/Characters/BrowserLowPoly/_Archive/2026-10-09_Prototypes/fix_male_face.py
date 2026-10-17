"""Preserve original facial topology and exact morph deltas; reduce only static body vertices."""
import bpy,bmesh,json,sys,re
import numpy as np
from pathlib import Path
from mathutils import Matrix,Vector
from mathutils.kdtree import KDTree
OUT=Path(__file__).resolve().parent;ROOT=OUT.parents[2];D=OUT/'Rigged_v2';D.mkdir(exist_ok=True)
sys.path.insert(0,str(OUT))
from export_compat import preserve_all_skin_weight_sets
preserve_all_skin_weight_sets()
def xyz(data):
 a=np.empty(len(data)*3);data.foreach_get('co',a);return a.reshape(-1,3)
def active(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o
MAP={'Face':'Face','Lips':'Face','Ears':'Face','EyeSocket':'Face','Torso':'Torso','Arms':'Arms','Fingernails':'Arms','Legs':'Legs','Toenails':'Legs','Pupils':'Eyes','Sclera':'Eyes','Irises':'Eyes','Teeth':'Mouth','Mouth':'Mouth'}
SLOTS=dict(zip(['Face','Torso','Arms','Legs','Eyes','Mouth'],[(8,8,880),(904,8,880),(8,904,880),(904,904,880),(1800,8,240),(1800,264,240)]))
bpy.ops.wm.open_mainfile(filepath=str(OUT/'HexLive_Browser_Characters_Rigged.blend'))
rig=bpy.data.objects['Kshishtof_Rig'];old=bpy.data.objects['Kshishtof_LOD0'];old_world=old.matrix_world.copy();mat=old.data.materials[0]
# Preserve separately authored hair and neutral anatomy, identified from atlas palette and appended source range.
extra=old.copy();extra.data=old.data.copy();bpy.context.scene.collection.objects.link(extra);extra.name='Authored details';extra.shape_key_clear()
uv=extra.data.uv_layers.active
keep=set(range(len(extra.data.vertices)-50,len(extra.data.vertices)))
for p in extra.data.polygons:
 if all((uv.data[i].uv-Vector((128/2048,1920/2048))).length<1e-6 for i in p.loop_indices):keep.update(p.vertices)
bm=bmesh.new();bm.from_mesh(extra.data);bm.verts.ensure_lookup_table();bmesh.ops.delete(bm,geom=[v for v in bm.verts if v.index not in keep],context='VERTS');bm.to_mesh(extra.data);bm.free()
before=set(bpy.data.objects);bpy.ops.import_scene.fbx(filepath=str(ROOT/'Assets/ImportedActors/Daz3D/Kshishtof/Kshishtof.fbx'),use_anim=False)
imported=set(bpy.data.objects)-before;src=next(o for o in imported if o.type=='MESH');donor=next(o for o in imported if o.type=='ARMATURE')
for b in donor.pose.bones:b.matrix_basis=Matrix.Identity(4)
bpy.context.view_layer.update()
base=xyz(src.data.vertices);keys=src.data.shape_keys.key_blocks;keymeta=[];deltas=[]
for k in list(keys)[1:]:
 delta=xyz(k.data)-base;derived=[]
 if not np.any(delta):
  paired=[k.name+'R',k.name+'L']
  if k.name.endswith('__CTRLCheeksDimpleCreaseHD'):paired=[k.name.replace('__CTRL','__PHM')+s for s in ['R','L']]
  elif k.name.endswith('__eCTRLCheekCrease'):paired=[k.name.replace('__eCTRL','__ePHM')+s for s in ['R','L']]
  if all(n in keys for n in paired):delta=sum(xyz(keys[n].data)-base for n in paired);derived=paired
 deltas.append(delta);keymeta.append(dict(name=k.name,min=k.slider_min,max=k.slider_max,derived=derived))
deltas=np.array(deltas);moving=np.max(np.linalg.norm(deltas,axis=2),axis=0)>1e-7
names=[re.sub(r'\.\d+$','',m.name) for m in src.data.materials]
protected=set(np.flatnonzero(moving).tolist())
for p in src.data.polygons:
 if names[p.material_index] in ['Face','Lips','EyeSocket','Sclera','Irises','Pupils','Mouth','Teeth']:protected.update(p.vertices)
# One intact collar of neighboring faces prevents a changing edge from touching the collapse region.
seed=set(protected)
for p in src.data.polygons:
 if any(i in seed for i in p.vertices):protected.update(p.vertices)
source_faces=[tuple(p.vertices) for p in src.data.polygons if all(i in protected for i in p.vertices) and names[p.material_index] not in ['Cornea','EyeMoisture','Eyelashes']]
kdt=KDTree(len(base))
for i,v in enumerate(base):kdt.insert(v,i)
kdt.balance()
src.shape_key_clear()
# An integer identity attribute survives for unchanged vertices, used to verify topology after collapse.
bm=bmesh.new();bm.from_mesh(src.data);layer=bm.verts.layers.int.new('source_vertex');bm.verts.ensure_lookup_table()
for v in bm.verts:v[layer]=v.index
bmesh.ops.delete(bm,geom=[f for f in bm.faces if names[f.material_index] in ['Cornea','EyeMoisture','Eyelashes']],context='FACES')
bm.to_mesh(src.data);bm.free()
# Store reduced-source original ids before modifiers.
attr=src.data.attributes['source_vertex'];ids=np.array([v.value for v in attr.data])
protect_used=set(ids)&protected
for p in src.data.polygons:
 group=MAP.get(names[p.material_index],'Torso');x,y,s=SLOTS[group]
 for li in p.loop_indices:
  u,v=src.data.uv_layers.active.data[li].uv;src.data.uv_layers.active.data[li].uv=((x+(u%1)*s)/2048,(y+(v%1)*s)/2048)
 p.use_smooth=names[p.material_index] in ['Face','Lips','Ears','EyeSocket','Sclera','Irises','Pupils','Mouth','Teeth'];p.material_index=0
src.data.materials.clear();src.data.materials.append(mat)
g=src.vertex_groups.new(name='_StaticReduction')
for v,si in zip(src.data.vertices,ids):
 if si not in protected:g.add([v.index],1,'REPLACE')
report={};audit={}
for lod,budget in [(0,15500),(1,13000)]:
 o=src.copy();o.data=src.data.copy();bpy.context.scene.collection.objects.link(o);active(o)
 for mod in list(o.modifiers):o.modifiers.remove(mod)
 o.data.calc_loop_triangles();mod=o.modifiers.new('Protect facial articulation','DECIMATE');mod.ratio=min(1,budget/len(o.data.loop_triangles));mod.vertex_group=g.name;mod.vertex_group_factor=1000;mod.use_collapse_triangulate=True
 bpy.ops.object.modifier_apply(modifier=mod.name);o.vertex_groups.remove(o.vertex_groups[g.name])
 coords=xyz(o.data.vertices);nearest=[kdt.find(v) for v in coords];source_ids=np.array([n[1] for n in nearest]);distances=np.array([n[2] for n in nearest]);exact=distances<1e-5
 present=set(source_ids[exact]);assert protect_used<=present,('lost facial vertices',len(protect_used-present))
 # Every protected original polygon must retain its original edges, even if triangulated.
 edges={tuple(sorted((int(source_ids[e.vertices[0]]),int(source_ids[e.vertices[1]])))) for e in o.data.edges}
 for face in source_faces:
  for a,b in zip(face,face[1:]+face[:1]):assert tuple(sorted((a,b))) in edges,('lost facial edge',a,b)
 # No interpolated target is allowed in the moving region.
 for i in np.flatnonzero(~exact):assert not moving[source_ids[i]],('collapsed animated vertex',i)
 o.parent=rig;o.matrix_world=old_world;arm=o.modifiers.new('Original skeleton','ARMATURE');arm.object=rig
 # Join existing authored details before assigning shape keys; they stay static under facial keys.
 detail=extra.copy();detail.data=extra.data.copy();bpy.context.scene.collection.objects.link(detail);active(o);detail.select_set(True);bpy.ops.object.join()
 basis=xyz(o.data.vertices);o.shape_key_add(name='Basis');stats={}
 for meta,delta in zip(keymeta,deltas):
  transferred=np.zeros_like(basis);transferred[:len(coords)][exact]=delta[source_ids[exact]]
  k=o.shape_key_add(name=meta['name']);k.data.foreach_set('co',(basis+transferred).ravel());k.slider_min=meta['min'];k.slider_max=meta['max']
  stats[k.name]={'max_displacement_m':float(np.linalg.norm(transferred,axis=1).max()*.01),'moving_vertices':int((np.linalg.norm(transferred,axis=1)>1e-5).sum()),'rebuilt_from':meta['derived'],'exact_source_face':True}
 old_lod=bpy.data.objects['Kshishtof_LOD'+str(lod)];collections=list(old_lod.users_collection);bpy.data.objects.remove(old_lod,do_unlink=True)
 o.name='Kshishtof_LOD'+str(lod)
 for c in list(o.users_collection):c.objects.unlink(o)
 for c in collections:c.objects.link(o);c.hide_viewport=False;c.hide_render=False
 active(o);bpy.ops.object.vertex_group_normalize_all(lock_active=False)
 o['face_revision']='v2: source facial vertices, edge loops and morph deltas preserved exactly; no UV transfer or eyelid plane correction'
 triangles=sum(len(p.vertices)-2 for p in o.data.polygons)
 report[o.name]={'triangles':triangles,'morphs':109,'shapes':stats}
 audit[o.name]={'triangles':triangles,'preserved_face_vertices':len(protect_used),'all_original_facial_edges_preserved':True,'facial_morph_mapping_error_m':0,'max_skin_influences':max(len(v.groups) for v in o.data.vertices)}
 print('FACE_REBUILT',o.name,audit[o.name],flush=True)
 if lod==1:
  for c in collections:c.hide_viewport=True;c.hide_render=True
for ob in imported|{extra}:bpy.data.objects.remove(ob,do_unlink=True)
for dbs in [bpy.data.meshes,bpy.data.armatures,bpy.data.materials,bpy.data.images]:
 for db in list(dbs):
  if db.users==0:dbs.remove(db)
bpy.context.view_layer.update();active(bpy.data.objects['Kshishtof_LOD0'])
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'HexLive_Browser_Characters_Rigged_v2.blend'))
(D/'morph_transfer.json').write_text(json.dumps(report,indent=2));(D/'face_topology_verification.json').write_text(json.dumps(audit,indent=2))
print('MALE_FACE_V2_COMPLETE',flush=True)

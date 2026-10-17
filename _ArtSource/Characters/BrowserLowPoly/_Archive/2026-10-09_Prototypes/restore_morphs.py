"""Transfer original facial shapes and full skin weights onto the reviewed low-poly meshes.
Keeps topology, proportions, UVs and all source bone names/rest matrices intact.
Run after build_characters.py with Blender --background --python this_file.
"""
import bpy,json,math,re,sys
import numpy as np
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
ROOT=Path(__file__).resolve().parents[3];OUT=Path(__file__).resolve().parent
DEST=OUT/'Rigged';DEST.mkdir(exist_ok=True)
sys.path.insert(0,str(OUT))
from export_compat import preserve_all_skin_weight_sets
preserve_all_skin_weight_sets()
SOURCES={'Marta':'Assets/ImportedActors/Actors/Marta/Marta.new.fbx','Kshishtof':'Assets/ImportedActors/Daz3D/Kshishtof/Kshishtof.fbx'}
MAP={'Face':'Face','Lips':'Face','Ears':'Face','EyeSocket':'Face','Torso':'Torso','Arms':'Arms','Fingernails':'Arms','Legs':'Legs','Toenails':'Legs','Pupils':'Eyes','Sclera':'Eyes','Irises':'Eyes','Teeth':'Mouth','Mouth':'Mouth'}
SLOTS=dict(zip(['Face','Torso','Arms','Legs','Eyes','Mouth'],[(8,8,880),(904,8,880),(8,904,880),(904,904,880),(1800,8,240),(1800,264,240)]))

def xyz(data):
 a=np.empty(len(data)*3,dtype=np.float64);data.foreach_get('co',a);return a.reshape(-1,3)
def activate(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o

def bary(p,a,b,c):
 v0=b-a;v1=c-a;v2=p-a;d00=v0.dot(v0);d01=v0.dot(v1);d11=v1.dot(v1);d20=v2.dot(v0);d21=v2.dot(v1);den=d00*d11-d01*d01
 if abs(den)<1e-14:return np.array([1.,0,0])
 v=(d11*d20-d01*d21)/den;w=(d00*d21-d01*d20)/den
 weights=np.clip([1-v-w,v,w],0,1);return weights/sum(weights)

def rig_signature(rig):
 return {b.name:{'parent':b.parent.name if b.parent else None,'matrix':[list(row) for row in b.matrix_local],'head':list(b.head_local),'tail':list(b.tail_local),'deform':b.use_deform} for b in rig.data.bones}

class SourceSurface:
 def __init__(self,obj):
  self.obj=obj;self.base=xyz(obj.data.shape_keys.key_blocks[0].data);self.surfaces={}
  obj.data.calc_loop_triangles();uv=obj.data.uv_layers.active
  for group in SLOTS:
   tris=[];uvs=[]
   for t in obj.data.loop_triangles:
    name=re.sub(r'\.\d+$','',obj.data.materials[t.material_index].name)
    if MAP.get(name)!=group:continue
    tris.append(list(t.vertices));uvs.append([(uv.data[l].uv.x%1,uv.data[l].uv.y%1,0) for l in t.loops])
   uvflat=[co for tri in uvs for co in tri];indices=[(i*3,i*3+1,i*3+2) for i in range(len(tris))]
   self.surfaces[group]=(np.array(tris),np.array(uvs),BVHTree.FromPolygons(uvflat,indices,all_triangles=True),BVHTree.FromPolygons(self.base.tolist(),tris,all_triangles=True))
 def locate(self,obj):
  uv=obj.data.uv_layers.active;options=[[] for _ in obj.data.vertices]
  for poly in obj.data.polygons:
   for li in poly.loop_indices:
    co=uv.data[li].uv*2048
    for group,(x,y,s) in SLOTS.items():
     if x-.2<=co.x<=x+s+.2 and y-.2<=co.y<=y+s+.2:
      options[obj.data.loops[li].vertex_index].append((group,Vector(((co.x-x)/s,(co.y-y)/s,0))));break
  ids=np.zeros((len(obj.data.vertices),3),dtype=int);ws=np.zeros_like(ids,dtype=float);errors=[];mapped=[]
  for v in obj.data.vertices:
   best=None
   for group,uvco in options[v.index]:
    tris,uvs,uvtree,tree=self.surfaces[group];near=uvtree.find_nearest(uvco)
    if near[2] is None:continue
    hit,_,i,_=near;weights=bary(np.array(hit),*uvs[i]);pos=(self.base[tris[i]]*weights[:,None]).sum(axis=0);error=np.linalg.norm(pos-np.array(v.co))
    candidate=(error,tris[i],weights)
    # Large UV drift can occur during edge collapse: constrain fallback to the same surface group.
    if error>.45:
     pt,_,j,dist=tree.find_nearest(v.co)
     if dist<error:candidate=(dist,tris[j],bary(np.array(pt),*self.base[tris[j]]))
    if best is None or candidate[0]<best[0]:best=candidate
   if best is None:continue # New solid hair uses a palette swatch: no facial deformation.
   error,tri,weights=best
   # The newly authored neutral accessory has a constant UV and pelvis-only weights.
   if error>2 and len(v.groups)==1 and obj.vertex_groups[v.groups[0].group].name=='pelvis':continue
   ids[v.index]=tri;ws[v.index]=weights;errors.append(error*.01);mapped.append(v.index)
  return ids,ws,mapped,errors

bpy.ops.wm.open_mainfile(filepath=str(OUT/'HexLive_Browser_Characters.blend'))
report={};source_contract={}
for label,path in SOURCES.items():
 target_rig=bpy.data.objects[label+'_Rig'];targets=[bpy.data.objects[label+'_LOD0'],bpy.data.objects[label+'_LOD1']]
 before=set(bpy.data.objects);bpy.ops.import_scene.fbx(filepath=str(ROOT/path),use_anim=False)
 imported=set(bpy.data.objects)-before;src=next(o for o in imported if o.type=='MESH');rig=next(o for o in imported if o.type=='ARMATURE')
 sig=rig_signature(rig);actual=rig_signature(target_rig)
 assert list(sig)==list(actual),(label,'bone order/names changed')
 for n,a in actual.items():
  assert a['parent']==sig[n]['parent'] and a['deform']==sig[n]['deform']
  assert np.max(np.abs(np.array(a['matrix'])-np.array(sig[n]['matrix'])))<1e-5,(label,n,'bind matrix changed')
 src_keys=src.data.shape_keys.key_blocks;names=[k.name for k in src_keys]
 source_contract[label]={'source':path,'rig_world':[list(row) for row in rig.matrix_world],'bones':sig,'shapes':[dict(name=k.name,min=k.slider_min,max=k.slider_max,relative=k.relative_key.name) for k in src_keys]}
 surface=SourceSurface(src)
 src_weights=[{src.vertex_groups[g.group].name:g.weight for g in v.groups if g.weight>1e-8} for v in src.data.vertices]
 for target in targets:
  for coll in target.users_collection:coll.hide_viewport=False;coll.hide_render=False
  original=xyz(target.data.vertices);original_tris=sum(len(p.vertices)-2 for p in target.data.polygons)
  ids,weights,mapped,errors=surface.locate(target)
  if target.data.shape_keys:target.shape_key_clear()
  basis=target.shape_key_add(name=names[0],from_mix=False)
  stats={}
  for source_key in list(src_keys)[1:]:
   delta=xyz(source_key.data)-surface.base
   derived=[]
   if not np.any(delta):
    paired=[source_key.name+'R',source_key.name+'L']
    if source_key.name.endswith('__CTRLCheeksDimpleCreaseHD'):
     paired=[source_key.name.replace('__CTRL','__PHM')+side for side in ['R','L']]
    elif source_key.name.endswith('__eCTRLCheekCrease'):
     paired=[source_key.name.replace('__eCTRL','__ePHM')+side for side in ['R','L']]
    if all(name in src_keys for name in paired):
     delta=sum((xyz(src_keys[name].data)-surface.base for name in paired));derived=paired
   transferred=(delta[ids]*weights[:,:,None]).sum(axis=1)
   key=target.shape_key_add(name=source_key.name,from_mix=False);key.data.foreach_set('co',(original+transferred).ravel())
   key.slider_min=source_key.slider_min;key.slider_max=source_key.slider_max;key.value=0;key.relative_key=basis
   refined=False
   if label=='Kshishtof' and source_key.name.split('__')[-1] in ['eCTRLEyesClosed','eCTRLEyesClosedL','eCTRLEyesClosedR']:
    suffix=source_key.name.split('__')[-1]
    sides=['lEye','rEye'] if suffix=='eCTRLEyesClosed' else (['lEye'] if suffix.endswith('L') else ['rEye'])
    inverse=target.matrix_world.inverted()
    for side in sides:
     center=target_rig.matrix_world@target_rig.data.bones[side].head_local
     for point,base_point in zip(key.data,basis.data):
      wp=target.matrix_world@base_point.co
      movement=(target.matrix_world.to_3x3()@(point.co-base_point.co)).length
      if movement>.0005 and abs(wp.x-center.x)<.015 and center.z-.006<wp.z<center.z+.011:
       # Bring opposing coarse lid margins to one closure line, in front of the eyeball.
       # This changes only the full-blink target; neutral head proportions stay exact.
       wp.y=center.y-.018;wp.z=center.z-.002;point.co=inverse@wp
    transferred=xyz(key.data)-original;refined=True
   stats[key.name]={'eyelid_closure_refined':refined,'rebuilt_from':derived,'max_displacement_m':float(np.linalg.norm(transferred,axis=1).max()*.01),'moving_vertices':int((np.linalg.norm(transferred,axis=1)>1e-5).sum()),'source_max_m':float(np.linalg.norm(delta,axis=1).max()*.01)}
  # Restore complete source influences; no artificial 4-weight truncation in the master.
  for group in target.vertex_groups:group.remove(mapped)
  for vi in mapped:
   interpolated={}
   for vertex_id,w in zip(ids[vi],weights[vi]):
    for name,value in src_weights[vertex_id].items():interpolated[name]=interpolated.get(name,0)+w*value
   total=sum(interpolated.values())
   assert total>0
   for name,value in interpolated.items():
    if value>1e-8:
     group=target.vertex_groups.get(name) or target.vertex_groups.new(name=name);group.add([vi],value/total,'REPLACE')
  assert np.max(np.abs(original-xyz(target.data.vertices)))<1e-6,'Basis changed'
  assert [k.name for k in target.data.shape_keys.key_blocks]==names
  assert original_tris==sum(len(p.vertices)-2 for p in target.data.polygons)
  target['morph_source']=path;target['morph_count']=len(names)-1;target['rig_contract']='Original 172-bone hierarchy and bind matrices; full interpolated influences'
  report[target.name]={'triangles':original_tris,'morphs':len(names)-1,'names_exact':True,'basis_unchanged':True,'source_bones':len(sig),'bone_names_parents_bind_matrices_exact':True,'max_weights':max(len(v.groups) for v in target.data.vertices),'mapping_rms_m':float(np.sqrt(np.mean(np.square(errors)))),'mapping_max_m':max(errors),'shapes':stats}
  print('TRANSFERRED',target.name,report[target.name]['mapping_rms_m'],report[target.name]['mapping_max_m'],flush=True)
 # Remove donor objects and their unused materials: deliver only the reviewed meshes.
 for o in imported:bpy.data.objects.remove(o,do_unlink=True)
 # Export each model at its original origin. Scene layout is restored afterwards.
 shift=target_rig.location.copy();target_rig.location.x=0;bpy.context.view_layer.update()
 for target in targets:
  activate(target);target_rig.select_set(True)
  bpy.ops.export_scene.gltf(filepath=str(DEST/(target.name+'.glb')),export_format='GLB',use_selection=True,export_animations=False,export_morph=True,export_morph_normal=True,export_all_influences=True,export_yup=True)
  bpy.ops.export_scene.fbx(filepath=str(DEST/(target.name+'.fbx')),use_selection=True,object_types={'MESH','ARMATURE'},add_leaf_bones=False,bake_anim=False,use_mesh_modifiers=False,mesh_smooth_type='FACE',path_mode='COPY',embed_textures=True)
 target_rig.location=shift
 for coll in targets[1].users_collection:coll.hide_viewport=True;coll.hide_render=True
 # Protect the view while keeping bones editable through the Outliner.
 target_rig.show_in_front=False

# Source images and source mesh datablocks must not inflate the saved scene.
for collection in [bpy.data.meshes,bpy.data.armatures,bpy.data.materials,bpy.data.images]:
 for datablock in list(collection):
  if datablock.users==0:collection.remove(datablock)
for screen in bpy.data.screens:
 for area in screen.areas:
  if area.type=='VIEW_3D':
   area.spaces.active.overlay.show_overlays=False
   area.spaces.active.region_3d.view_rotation=bpy.context.scene.camera.rotation_euler.to_quaternion()
   area.spaces.active.region_3d.view_distance=4.6;area.spaces.active.region_3d.view_location=(0,0,.95)
   area.spaces.active.shading.type='MATERIAL'
activate(bpy.data.objects['Marta_LOD0'])
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'HexLive_Browser_Characters_Rigged.blend'))
(DEST/'source_contract.json').write_text(json.dumps(source_contract,indent=2))
(DEST/'morph_transfer.json').write_text(json.dumps(report,indent=2))
print('MORPH_TRANSFER_COMPLETE',flush=True)

"""Standalone Primal wardrobe fitting and original-project run retarget. No Unity assets changed."""
import bpy,bmesh,json,sys,math
import numpy as np
from pathlib import Path
from mathutils import Matrix,Vector
from mathutils.bvhtree import BVHTree
OUT=Path(__file__).resolve().parent;ROOT=OUT.parents[2];D=OUT/'Clothing_Primal';T=D/'textures';T.mkdir(exist_ok=True)
sys.path.insert(0,str(OUT));from export_compat import preserve_all_skin_weight_sets
preserve_all_skin_weight_sets()
def active(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o
def coords(data):
 a=np.empty(len(data)*3);data.foreach_get('co',a);return a.reshape(-1,3)
def tree(obj):
 obj.data.calc_loop_triangles();tris=[list(t.vertices) for t in obj.data.loop_triangles];return BVHTree.FromPolygons([v.co for v in obj.data.vertices],tris,all_triangles=True),tris
def bary(p,a,b,c):
 v0=b-a;v1=c-a;v2=p-a;den=v0.dot(v0)*v1.dot(v1)-v0.dot(v1)**2
 if abs(den)<1e-12:return [1,0,0]
 v=(v1.dot(v1)*v2.dot(v0)-v0.dot(v1)*v2.dot(v1))/den;w=(v0.dot(v0)*v2.dot(v1)-v0.dot(v1)*v2.dot(v0))/den
 q=np.maximum([1-v-w,v,w],0);return q/q.sum()
def texmat(item):
 image=bpy.data.images.load(str(ROOT/f'Assets/ImportedActors/Wear/{item}/Textures'/('Primal_Top.jpg' if item=='PrimalTop' else 'Primal_Panty.jpg')));image.scale(1024,1024);image.filepath_raw=str(T/(item+'.jpg'));image.file_format='JPEG';image.save();image.pack()
 mat=bpy.data.materials.new(item+'_Hide_1024');mat.use_nodes=True;p=mat.node_tree.nodes.get('Principled BSDF');p.inputs['Roughness'].default_value=.92;p.inputs['Specular'].default_value=.18
 node=mat.node_tree.nodes.new('ShaderNodeTexImage');node.image=image;mat.node_tree.links.new(node.outputs['Color'],p.inputs['Base Color']);mat.use_backface_culling=False
 return mat
bpy.ops.wm.open_mainfile(filepath=str(OUT/'HexLive_Browser_Characters_Rigged_v2.blend'))
sc=bpy.context.scene;sc.frame_set(1);sc.render.fps=30
bodies={l:bpy.data.objects[l+'_LOD0'] for l in ['Marta','Kshishtof']};rigs={l:bpy.data.objects[l+'_Rig'] for l in bodies}
# Keep the authored male anatomy as its own independently switchable mesh.
body=bodies['Kshishtof'];oldkeys={k.name:coords(k.data) for k in body.data.shape_keys.key_blocks};n=len(body.data.vertices);genital_ids=set(range(n-50,n))
gen=body.copy();gen.data=body.data.copy();bpy.context.scene.collection.objects.link(gen);gen.name='Kshishtof_Genitals';gen.shape_key_clear()
bm=bmesh.new();bm.from_mesh(gen.data);bm.verts.ensure_lookup_table();bmesh.ops.delete(bm,geom=[v for v in bm.verts if v.index not in genital_ids],context='VERTS');bm.to_mesh(gen.data);bm.free()
active(body);bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='DESELECT');bpy.ops.object.mode_set(mode='OBJECT')
for v in body.data.vertices:v.select=v.index in genital_ids
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.delete(type='VERT');bpy.ops.object.mode_set(mode='OBJECT')
for k in body.data.shape_keys.key_blocks:assert np.max(np.abs(coords(k.data)-oldkeys[k.name][:-50]))<1e-6
rigs['Kshishtof']['primal_briefs_equipped']=True
gen.hide_render=True;gen.hide_viewport=True
gen['runtime_binding']='Assign this separate GameObject to BodyBones.genitals; hide when Underwear/Pelvis is equipped.'
for prop in ['hide_render','hide_viewport']:
 drv=gen.driver_add(prop).driver;v=drv.variables.new();v.name='equipped';v.targets[0].id=rigs['Kshishtof'];v.targets[0].data_path='["primal_briefs_equipped"]';drv.expression='equipped'
report={'sources':{},'garments':{},'genitals':{'separate_object':gen.name,'vertices':50,'body_morphs_preserved_exactly':True,'default_hidden':True,'toggle':'Kshishtof_Rig[primal_briefs_equipped]'},'animation':{}}
materials={item:texmat(item) for item in ['PrimalTop','PrimalBriefs']};wear=[]
for label,item in [('Marta','PrimalBriefs'),('Marta','PrimalTop'),('Kshishtof','PrimalBriefs')]:
 data=json.loads((D/(item+'_source.json')).read_text());parts=[];budgets=[1000,200] if item=='PrimalBriefs' else [300,1450,300]
 report['sources'][item]={'path':data['source'],'triangles':sum(len(p) for p in data['parts'])}
 for part_idx,faces in enumerate(data['parts']):
  used=sorted({i for f in faces for i in f});remap={v:i for i,v in enumerate(used)}
  verts=[Vector((-data['vertices'][i][0]*100,data['vertices'][i][1]*100,data['vertices'][i][2]*100)) for i in used]
  if label=='Kshishtof':
   for vi,si in enumerate(used):
    delta=Vector()
    for ji,w in zip(data['joints'][si],data['weights'][si]):
     if w:
      bn=data['bones'][ji];delta+=(rigs[label].data.bones[bn].head_local-rigs['Marta'].data.bones[bn].head_local)*w
    verts[vi]+=delta
  mesh=bpy.data.meshes.new(f'{label}_{item}_{part_idx}');mesh.from_pydata(verts,[],[(remap[f[2]],remap[f[1]],remap[f[0]]) for f in faces]);mesh.update()
  o=bpy.data.objects.new(mesh.name,mesh);sc.collection.objects.link(o);o.matrix_world=bodies[label].matrix_world.copy();mesh.materials.append(materials[item]);uv=mesh.uv_layers.new(name='UVMap')
  for loop in mesh.loops:uv.data[loop.index].uv=data['uv'][used[loop.vertex_index]]
  for vi,si in enumerate(used):
   for ji,w in zip(data['joints'][si],data['weights'][si]):
    if w>1e-8:
     bn=data['bones'][ji];(o.vertex_groups.get(bn) or o.vertex_groups.new(name=bn)).add([vi],w,'REPLACE')
  # Weld only coincident geometry; loop UVs retain texture seams.
  bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.0001);bm.to_mesh(mesh);bm.free()
  active(o);mod=o.modifiers.new('Low polygon Primal','DECIMATE');mod.ratio=min(1,budgets[part_idx]/len(faces));mod.use_collapse_triangulate=True;bpy.ops.object.modifier_apply(modifier=mod.name)
  for p in mesh.polygons:p.use_smooth=True
  parts.append(o)
 active(parts[0])
 for o in parts:o.select_set(True)
 bpy.ops.object.join();o=parts[0];o.name=f'{label}_{item}'
 # Preserve the authored Primal skin weights; a nearest-body rebind incorrectly drags the crotch into alternating thighs.
 b=bodies[label];bvh,tris=tree(b);sourceweights=[{b.vertex_groups[g.group].name:g.weight for g in v.groups} for v in b.data.vertices]
 for v in o.data.vertices:
  p,normal,ti,dist=bvh.find_nearest(v.co);signed=(v.co-p).dot(normal)
  if signed<.65:v.co+=normal*(.65-signed)
 active(o);bpy.ops.object.vertex_group_normalize_all(lock_active=False)
 # Own hip hierarchy with original names/rest transforms, matching Wear.Construct.
 parent=bpy.data.objects.new(o.name+'_Wear',None);sc.collection.objects.link(parent);parent['layer']='Underwear' if item=='PrimalBriefs' else 'Wear';parent['slots']='Belly, Pelvis' if item=='PrimalBriefs' else 'Chest';parent['actor_fit']=label
 gr=rigs[label].copy();gr.data=rigs[label].data.copy();gr.animation_data_clear();sc.collection.objects.link(gr);gr.name=o.name+'_Skeleton';gr.parent=parent;gr.matrix_world=rigs[label].matrix_world.copy()
 needed=set(g.name for g in o.vertex_groups)
 for bn in list(needed):
  par=gr.data.bones[bn].parent
  while par:needed.add(par.name);par=par.parent
 active(gr);bpy.ops.object.mode_set(mode='EDIT')
 for bone in list(gr.data.edit_bones):
  if bone.name not in needed:gr.data.edit_bones.remove(bone)
 bpy.ops.object.mode_set(mode='OBJECT')
 world=o.matrix_world.copy();o.parent=gr;o.matrix_world=world;mod=o.modifiers.new('Wear bone binding','ARMATURE');mod.object=gr
 o['source_item']=item;o['source_fit']='Marta';o['fitted_for']=label;o['bind_contract']='Own hip skeleton; original body bone names and bind transforms. Runtime uses Wear.Construct/ParentConnection.'
 if label=='Kshishtof':
  for prop in ['hide_render','hide_viewport']:
   drv=o.driver_add(prop).driver;v=drv.variables.new();v.name='equipped';v.targets[0].id=rigs[label];v.targets[0].data_path='["primal_briefs_equipped"]';drv.expression='not equipped'
 wear.append((o,gr,parent,label))
 report['garments'][o.name]={'triangles':sum(len(p.vertices)-2 for p in o.data.polygons),'vertices':len(o.data.vertices),'bones':list(gr.data.bones.keys()),'max_weights':max(len(v.groups) for v in o.data.vertices),'material_count':len(o.data.materials)}
 print('GARMENT',o.name,report['garments'][o.name]['triangles'],flush=True)
# Transfer the project's existing Mixamo run by global rest-space rotation deltas.
before=set(bpy.data.objects);bpy.ops.import_scene.fbx(filepath=str(ROOT/'Assets/ImportedActors/AnimLibrary/Male@Run.fbx'))
imported=set(bpy.data.objects)-before;donor=next(o for o in imported if o.type=='ARMATURE');action=donor.animation_data.action
mapping={'hip':'Hips','abdomenLower':'Spine','abdomenUpper':'Spine1','chestLower':'Spine2','neckLower':'Neck','head':'Head'}
for side,source in [('l','Left'),('r','Right')]:
 for target,frombone in [('Collar','Shoulder'),('ShldrBend','Arm'),('ForearmBend','ForeArm'),('Hand','Hand'),('ThighBend','UpLeg'),('Shin','Leg'),('Foot','Foot'),('Toe','ToeBase')]:mapping[side+target]=source+frombone
for side,ss in [('l','Left'),('r','Right')]:
 for finger,sf in [('Thumb','Thumb'),('Index','Index'),('Mid','Middle'),('Ring','Ring'),('Pinky','Pinky')]:
  for j in range(1,4):mapping[f'{side}{finger}{j}']=f'{ss}Hand{sf}{j}'
frames=range(1,21)
for rig in rigs.values():
 rig.animation_data_clear()
 for pb in rig.pose.bones:pb.matrix_basis=Matrix.Identity(4);pb.rotation_mode='QUATERNION'
for frame in frames:
 sc.frame_set(frame)
 for label,rig in rigs.items():
  for pb in rig.pose.bones:pb.matrix_basis=Matrix.Identity(4)
  bpy.context.view_layer.update()
  for pb in rig.pose.bones:
   if pb.name not in mapping:continue
   sb=donor.pose.bones['mixamorig:'+mapping[pb.name]]
   delta=sb.matrix.to_quaternion()@sb.bone.matrix_local.to_quaternion().inverted();desired=delta@pb.bone.matrix_local.to_quaternion()
   matrix=desired.to_matrix().to_4x4();matrix.translation=pb.matrix.translation;pb.matrix=matrix;pb.location=(0,0,0)
   if pb.name=='hip':
    displacement=sb.matrix.translation-sb.bone.head_local;ratio=pb.bone.head_local.y/sb.bone.head_local.y
    # Forward translation removed: hip retains side sway and vertical bounce.
    pb.location=(displacement.x*ratio,displacement.y*ratio,0)
   bpy.context.view_layer.update()
  # Correct only floor penetration caused by different source/target foot proportions.
  evaluated=bodies[label].evaluated_get(bpy.context.evaluated_depsgraph_get());mesh=evaluated.to_mesh();min_z=min((evaluated.matrix_world@v.co).z for v in mesh.vertices);evaluated.to_mesh_clear()
  if min_z<.005:rig.pose.bones['hip'].location.y+=(.005-min_z)*100;bpy.context.view_layer.update()
  for pb in rig.pose.bones:
   if pb.name in mapping:pb.keyframe_insert('rotation_quaternion',frame=frame);pb.keyframe_insert('location',frame=frame)
for label,rig in rigs.items():
 rig.animation_data.action.name=label+'_MaleRun_InPlace'
 for fc in rig.animation_data.action.fcurves:
  for kp in fc.keyframe_points:kp.interpolation='LINEAR'
  fc.modifiers.new('CYCLES')
# Garments have independent skeletons; copy the mapped animation channels on matching bones.
# This is the Blender equivalent of runtime ParentConnection. Bind matrices are identical.
for o,gr,parent,label in wear:
 gr.animation_data_create();gr.animation_data.action=rigs[label].animation_data.action.copy();gr.animation_data.action.name=o.name+'_RunBinding'
 for fc in list(gr.animation_data.action.fcurves):
  match=__import__('re').match(r'pose.bones\["([^\"]+)"\]',fc.data_path)
  if match and match.group(1) not in gr.pose.bones:gr.animation_data.action.fcurves.remove(fc)
for ob in imported:bpy.data.objects.remove(ob,do_unlink=True)
sc.frame_start=1;sc.frame_end=19;sc.frame_set(1)
report['animation']={'source':'Assets/ImportedActors/AnimLibrary/Male@Run.fbx','fps':30,'source_frames':[1,20],'loop_frames':[1,19],'root_motion_removed':True,'mapped_bones':mapping,'method':'Global rest-space quaternion retarget; garment bone animation matches wearer.'}
# Render-friendly view and visible controls.
cam=sc.camera;cam.location=(2.1,-7.8,2.5);cam.rotation_euler=(Vector((0,0,1.03))-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.ortho_scale=3.65
sc.render.resolution_x=1200;sc.render.resolution_y=850;sc.render.resolution_percentage=100;sc.cycles.samples=24
for screen in bpy.data.screens:
 for area in screen.areas:
  if area.type=='VIEW_3D':area.spaces.active.region_3d.view_perspective='CAMERA';area.spaces.active.overlay.show_overlays=False;area.spaces.active.shading.type='MATERIAL'
sc['review_note']='Space: loop the project run. Kshishtof_Rig custom property primal_briefs_equipped switches briefs/genitals. Separate Wear items each retain their own hip skeleton.'
active(bodies['Marta'])
# Remove unused donor resources before packing.
for dbs in [bpy.data.meshes,bpy.data.armatures,bpy.data.materials,bpy.data.images]:
 for db in list(dbs):
  if db.users==0:dbs.remove(db)
bpy.ops.wm.save_as_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
(D/'clothing_report.json').write_text(json.dumps(report,indent=2))
sc.render.filepath=str(D/'preview_run.png');bpy.ops.render.render(write_still=True)
print('PRIMAL_TEST_CREATED',flush=True)

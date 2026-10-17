"""Blender 3.2+: author standalone browser character study from project actor FBXs.
Run: Blender -b --python _ArtSource/Characters/BrowserLowPoly/build_characters.py
No runtime assets are overwritten. Source rigs retained; facial morphs omitted.
"""
import bpy, bmesh, json, math, sys
import numpy as np
from pathlib import Path
from mathutils import Vector, Quaternion, Matrix
ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
TEX=OUT/'textures'; TEX.mkdir(exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
report={}

def active(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o

def tri_count(o):
 o.data.calc_loop_triangles();return len(o.data.loop_triangles)

def material(name,color):
 m=bpy.data.materials.new(name);m.diffuse_color=(*color,1);m.use_nodes=True
 p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Base Color'].default_value=(*color,1);p.inputs['Roughness'].default_value=.78
 return m

def make_atlas(label,folder,files):
 # Four 880px body maps, independent 240px eyes/mouth. 8px edge extrusion.
 atlas=np.zeros((2048,2048,4),dtype=np.float32);atlas[:]=(.38,.20,.13,1)
 slots={}; coords=[(8,8,880),(904,8,880),(8,904,880),(904,904,880),(1800,8,240),(1800,264,240)]
 for key,(x,y,n) in zip(['Face','Torso','Arms','Legs','Eyes','Mouth'],coords):
  im=bpy.data.images.load(str(folder/files[key]),check_existing=False);im.scale(n,n)
  data=np.array(im.pixels[:],dtype=np.float32).reshape(n,n,4)
  atlas[y-8:y+n+8,x-8:x+n+8]=np.pad(data,((8,8),(8,8),(0,0)),mode='edge')
  slots[key]=(x/2048,y/2048,n/2048)
  bpy.data.images.remove(im)
 # Palette used by solid low-poly hair geometry: same material/draw as skin.
 atlas[1856:1984,64:192]=(.075,.027,.018,1) if label=='Marta' else (.046,.027,.019,1)
 im=bpy.data.images.new(label+'_BaseColor_2048',2048,2048,alpha=False)
 im.pixels.foreach_set(atlas.ravel());im.filepath_raw=str(TEX/(label+'_BaseColor.jpg'));im.file_format='JPEG';im.save();im.pack()
 m=material(label+'_Atlas',(.7,.5,.4));p=m.node_tree.nodes.get('Principled BSDF')
 node=m.node_tree.nodes.new('ShaderNodeTexImage');node.image=im;m.node_tree.links.new(node.outputs['Color'],p.inputs['Base Color'])
 p.inputs['Specular'].default_value=.22
 return m,slots

MAP={'Face':'Face','Lips':'Face','Ears':'Face','EyeSocket':'Face','Torso':'Torso','Arms':'Arms','Fingernails':'Arms','Legs':'Legs','Toenails':'Legs','Pupils':'Eyes','Sclera':'Eyes','Irises':'Eyes','Teeth':'Mouth','Mouth':'Mouth'}

def add_hair(body,rig,label):
 # A fitted faceted scalp cap, plus an angular bob for Marta.
 # Construct in world metres; mesh then returns to the original FBX local basis.
 pts=[body.matrix_world@v.co for v in body.data.vertices]
 top=max(p.z for p in pts); cz=top-.115
 verts=[];faces=[];N=20;R=7
 rx=.104 if label=='Marta' else .110;ry=.122 if label=='Marta' else .124
 cy=-.002
 for j in range(R):
  t=j/(R-1)
  for i in range(N):
   a=2*math.pi*i/N
   # front is -Y; hairline higher at the forehead, lower at the nape.
   front=max(0,-math.sin(a))
   limit=(1.98-.76*front) if label=='Marta' else (1.64-.62*front)
   phi=.065+(limit-.065)*t
   radius=1+.035*math.cos(3*a)
   verts.append((rx*math.sin(phi)*math.cos(a)*radius,cy+ry*math.sin(phi)*math.sin(a)*radius,cz+.124*math.cos(phi)))
 for j in range(R-1):
  for i in range(N):
   a=j*N+i;b=j*N+(i+1)%N;c=(j+1)*N+(i+1)%N;d=(j+1)*N+i
   faces.extend([(a,b,c),(a,c,d)])
 faces.append(tuple(reversed(range(N))))
 if label=='Marta':
  # Bob sides and back. Forehead/face remain open.
  for j in range(3):
   for i in range(N):
    a=2*math.pi*i/N;phi=1.98-.76*max(0,-math.sin(a))
    verts.append((rx*math.sin(phi)*math.cos(a)*(1.06+.07*j),cy+ry*math.sin(phi)*math.sin(a)*(1.06+.07*j),cz+.124*math.cos(phi)-.061*(j+1)))
  for j in range(3):
   for i in range(N):
    mid=2*math.pi*(i+.5)/N
    if math.sin(mid)<-.55:continue
    a=(R-1+j)*N+i;b=(R-1+j)*N+(i+1)%N;c=(R+j)*N+(i+1)%N;d=(R+j)*N+i
    faces.extend([(a,b,c),(a,c,d)])
 mesh=bpy.data.meshes.new(label+'_HairMesh');inv=body.matrix_world.inverted();mesh.from_pydata([inv@Vector(v) for v in verts],[],faces);mesh.update()
 hair=bpy.data.objects.new(label+'_Hair',mesh);bpy.context.collection.objects.link(hair);hair.matrix_world=body.matrix_world.copy()
 uv=mesh.uv_layers.new(name=body.data.uv_layers.active.name)
 for l in uv.data:l.uv=(128/2048,1920/2048)
 hair.vertex_groups.new(name='head').add(list(range(len(verts))),1,'REPLACE');mesh.materials.append(body.data.materials[0])
 active(body);hair.select_set(True);bpy.ops.object.join()

def add_neutral_anatomy(body):
 # Simplified adult anatomical silhouette, relaxed and unstylized; no internal detail.
 verts=[];faces=[];N=10
 rings=[(1.012,-.112,.024),(.989,-.137,.021),(.959,-.147,.019),(.926,-.149,.016),(.916,-.148,.006)]
 for z,y,r in rings:
  for i in range(N):
   a=2*math.pi*i/N;verts.append((r*math.cos(a),y+r*.68*math.sin(a),z))
 for j in range(len(rings)-1):
  for i in range(N):
   a=j*N+i;b=j*N+(i+1)%N;c=(j+1)*N+(i+1)%N;d=(j+1)*N+i;faces.extend([(a,c,b),(a,d,c)])
 faces.extend([tuple(range(N)),tuple(reversed(range((len(rings)-1)*N,len(rings)*N)))])
 inv=body.matrix_world.inverted();mesh=bpy.data.meshes.new('Male_Anatomy_Neutral');mesh.from_pydata([inv@Vector(v) for v in verts],[],faces);mesh.update()
 part=bpy.data.objects.new('Male_Anatomy_Neutral',mesh);bpy.context.collection.objects.link(part);part.matrix_world=body.matrix_world.copy()
 # Sample matching skin colour from the neighbouring lower abdomen on the atlas.
 poly=min(body.data.polygons,key=lambda p:((body.matrix_world@p.center)-Vector((0,-.125,1.06))).length)
 coord=body.data.uv_layers.active.data[poly.loop_indices[0]].uv.copy();uv=mesh.uv_layers.new(name=body.data.uv_layers.active.name)
 for l in uv.data:l.uv=coord
 part.vertex_groups.new(name='pelvis').add(list(range(len(verts))),1,'REPLACE');mesh.materials.append(body.data.materials[0])
 active(body);part.select_set(True);bpy.ops.object.join()

sources=[('Marta','Assets/ImportedActors/Actors/Marta/Marta.new.fbx','Assets/ImportedActors/Daz3D/Martanaked/Genesis3Female',dict(Face='Vex3DS--Alydia--Face_1001.jpg',Torso='Vex3DS--Alydia--Torso_1002.jpg',Arms='Vex3DS--Alydia--Arms_1004.jpg',Legs='Vex3DS--Alydia--Legs_1003.jpg',Eyes='Vex3DS--Alydia--Eyes01_1007.jpg',Mouth='Vex3DS--AlydiaFull--Mouth_1005.jpg')),('Kshishtof','Assets/ImportedActors/Daz3D/Kshishtof/Kshishtof.fbx','Assets/ImportedActors/Daz3D/Kshishtof/Genesis3Male',dict(Face='coilin_face_disp_base.jpg',Torso='coilin_torso_disp_base.jpg',Arms='coilin_arm_disp_base.jpg',Legs='coilin_leg_disp_base.jpg',Eyes='coilin_eyes_blue.jpg',Mouth='coilin_mouth_disp.jpg'))]
models=[]
for label,path,folder,files in sources:
 before=set(bpy.data.objects)
 bpy.ops.import_scene.fbx(filepath=str(ROOT/path),use_anim=False)
 new=set(bpy.data.objects)-before;body=next(o for o in new if o.type=='MESH');rig=next(o for o in new if o.type=='ARMATURE')
 active(body);source_tris=tri_count(body);body.shape_key_clear()
 for bone in rig.pose.bones:bone.matrix_basis=Matrix.Identity(4)
 bpy.context.view_layer.update()
 # Transparent multi-layer eyes and alpha eyelashes are omitted for the opaque browser study.
 names=[m.name.split('.')[0] for m in body.data.materials]
 bm=bmesh.new();bm.from_mesh(body.data)
 bmesh.ops.delete(bm,geom=[f for f in bm.faces if names[f.material_index] in ('Cornea','EyeMoisture','Eyelashes')],context='FACES')
 bm.to_mesh(body.data);bm.free()
 # Collapse gently around the face, fingers and articulations.
 group=body.vertex_groups.new(name='_ReductionWeight')
 for v in body.data.vertices:
  p=body.matrix_world@v.co
  weight=.18 if p.z>1.52 else (.35 if abs(p.x)>.43 else 1)
  if .40<p.z<.61 or .89<p.z<1.04:weight=min(weight,.55)
  group.add([v.index],weight,'REPLACE')
 mod=body.modifiers.new('Silhouette-aware reduction','DECIMATE');mod.ratio=7200/tri_count(body);mod.use_collapse_triangulate=True;mod.vertex_group=group.name;mod.vertex_group_factor=2.5
 bpy.ops.object.modifier_apply(modifier=mod.name);body.vertex_groups.remove(body.vertex_groups['_ReductionWeight'])
 atlas,slots=make_atlas(label,ROOT/folder,files)
 uv=body.data.uv_layers.active
 for poly in body.data.polygons:
  old=names[poly.material_index];x,y,s=slots[MAP.get(old,'Torso')]
  for li in poly.loop_indices:
   u,v=uv.data[li].uv;uv.data[li].uv=(x+(u%1)*s,y+(v%1)*s)
  # Silhouette reads as low-poly; face retains gentle normals for recognizability.
  poly.use_smooth=old in ('Face','Lips','Ears','Pupils','Sclera','Irises','EyeSocket')
  poly.material_index=0
 body.data.materials.clear();body.data.materials.append(atlas)
 add_hair(body,rig,label)
 if label=='Kshishtof':add_neutral_anatomy(body)
 active(body);bpy.ops.object.vertex_group_limit_total(limit=4);bpy.ops.object.vertex_group_normalize_all(lock_active=False)
 body.name=label+'_LOD0';rig.name=label+'_Rig'
 # Preserve original bone names/hierarchy and bind matrices; no facial morph payload.
 body['source_fbx']=path;body['adult_character']=True;body['purpose']='Low-poly Blender/browser visual prototype; runtime integration not performed'
 body['source_triangles']=source_tris
 # Remove unused FBX source images before packing (atlas already packed).
 for im in list(bpy.data.images):
  if not im.name.endswith('_BaseColor_2048'):bpy.data.images.remove(im)
 active(body);rig.select_set(True)
 bpy.ops.export_scene.gltf(filepath=str(OUT/(label+'_LOD0.glb')),export_format='GLB',use_selection=True,export_animations=False,export_morph=False,export_yup=True)
 bpy.ops.export_scene.fbx(filepath=str(OUT/(label+'_LOD0.fbx')),use_selection=True,object_types={'MESH','ARMATURE'},add_leaf_bones=False,bake_anim=False,mesh_smooth_type='FACE',path_mode='COPY',embed_textures=True)
 lod=body.copy();lod.data=body.data.copy();bpy.context.collection.objects.link(lod);lod.name=label+'_LOD1';active(lod)
 mod=lod.modifiers.new('Distance reduction','DECIMATE');mod.ratio=.48;mod.use_collapse_triangulate=True;bpy.ops.object.modifier_apply(modifier=mod.name)
 bpy.ops.object.vertex_group_limit_total(limit=4);bpy.ops.object.vertex_group_normalize_all(lock_active=False)
 active(lod);rig.select_set(True)
 bpy.ops.export_scene.gltf(filepath=str(OUT/(label+'_LOD1.glb')),export_format='GLB',use_selection=True,export_animations=False,export_morph=False,export_yup=True)
 report[label]={'source':path,'source_triangles':source_tris,'lod0_triangles':tri_count(body),'lod1_triangles':tri_count(lod),'mesh_vertices':len(body.data.vertices),'materials':len(body.data.materials),'bones':len(rig.data.bones),'max_weights':max(len(v.groups) for v in body.data.vertices),'atlas':[2048,2048],'facial_morphs':0}
 # LOD1 kept in a hidden collection for editing, independent of export visibility.
 coll=bpy.data.collections.new(label+' — LOD1 (hidden)');bpy.context.scene.collection.children.link(coll)
 for c in list(lod.users_collection):c.objects.unlink(lod)
 coll.objects.link(lod);coll.hide_render=True;coll.hide_viewport=True
 models.append((body,rig))

# Neutral studio with both adult characters, no erotic poses or framing.
for (body,rig),x in zip(models,[-.79,.79]):rig.location.x+=x
floor=material('Studio / warm slate',(.105,.12,.125))
bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,-.012));bpy.context.object.data.materials.append(floor);bpy.context.object.name='Studio Floor'
for x in [-.79,.79]:
 bpy.ops.mesh.primitive_cylinder_add(vertices=64,radius=.69,depth=.055,location=(x,0,-.036));bpy.context.object.data.materials.append(floor)
world=bpy.context.scene.world or bpy.data.worlds.new('Studio');bpy.context.scene.world=world;world.use_nodes=True;world.node_tree.nodes['Background'].inputs[0].default_value=(.19,.22,.25,1);world.node_tree.nodes['Background'].inputs[1].default_value=.45

def aim(obj,point):obj.rotation_euler=(Vector(point)-obj.location).to_track_quat('-Z','Y').to_euler()
for name,loc,power,size in [('Key',(-3,-4,5),300,4),('Fill',(4,-2,3),180,3),('Rim',(0,3,4),450,3)]:
 d=bpy.data.lights.new(name,'AREA');d.energy=power;d.shape='DISK';d.size=size;o=bpy.data.objects.new(name,d);bpy.context.collection.objects.link(o);o.location=loc;aim(o,(0,0,1))
d=bpy.data.cameras.new('Review Camera');cam=bpy.data.objects.new('Review Camera',d);bpy.context.collection.objects.link(cam);cam.location=(2.75,-7.8,2.7);aim(cam,(0,0,.95));d.type='ORTHO';d.ortho_scale=3.6;bpy.context.scene.camera=cam
sc=bpy.context.scene;sc.render.engine='CYCLES';sc.cycles.samples=32;sc.cycles.use_denoising=True;sc.render.resolution_x=1500;sc.render.resolution_y=1100;sc.render.resolution_percentage=100
sc.view_settings.view_transform='Standard';sc.view_settings.look='Medium High Contrast';sc.view_settings.exposure=0;sc.view_settings.gamma=1
# Convenient initial Blender viewport, textured with soft studio lighting.
for screen in bpy.data.screens:
 for area in screen.areas:
  if area.type=='VIEW_3D':
   area.spaces.active.region_3d.view_rotation=Quaternion((.79,.60,.08,.1)).normalized();area.spaces.active.region_3d.view_distance=4.6;area.spaces.active.region_3d.view_location=(0,0,.95);area.spaces.active.shading.type='MATERIAL';area.spaces.active.overlay.show_extras=False
active(models[0][0]);models[1][0].select_set(True)
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'HexLive_Browser_Characters.blend'))
(OUT/'metrics.json').write_text(json.dumps(report,indent=2))
sc.render.filepath=str(OUT/'preview_studio.png');bpy.ops.render.render(write_still=True)
cam.location=(0,-8,1.04);aim(cam,(0,0,.96));sc.render.filepath=str(OUT/'preview_front.png');bpy.ops.render.render(write_still=True)
cam.location=(0,8,1.4);aim(cam,(0,0,.96));sc.render.filepath=str(OUT/'preview_back.png');bpy.ops.render.render(write_still=True)
print('BROWSER_CHARACTERS_COMPLETE',json.dumps(report))

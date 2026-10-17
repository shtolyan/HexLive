"""Executed in the existing Blender GUI. Opaque outer-card reduction, one baked atlas, two LODs."""
import bpy,bmesh,numpy as np,json,gzip,math,traceback,re
from pathlib import Path
from mathutils import Vector,Matrix
from mathutils.bvhtree import BVHTree
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');ROOT=OUT.parents[2];D=OUT/'Hair';DATA=D/'Data';T=D/'Textures';E=D/'Exports';T.mkdir(exist_ok=True);E.mkdir(exist_ok=True)
sc=bpy.context.scene;body=bpy.data.objects['Marta_LOD0'];rig=bpy.data.objects['Marta_Rig'];summary=json.loads((D/'build_status.json').read_text()).get('hairs',{}) if (D/'build_status.json').exists() else {};collection=bpy.data.collections.get('04 Женские причёски')
if not collection:collection=bpy.data.collections.new('04 Женские причёски');sc.collection.children.link(collection)
control=bpy.data.objects.get('Hair_Controls')
if not control:
 control=bpy.data.objects.new('Hair_Controls',None);collection.objects.link(control);control['style']=1;control['lod']=0;control['male_hair']='none — awaiting supplied male assets';control.empty_display_size=.02
NAMES=['AdellHair','AsukaHair','BendineHair','Bob3Hair','ChunkyHair','EilisHair','Hair07','JelikaHair_32434','JenniferHair','LeonyPonytail','LoonaHair','LowPonytail','Neu09Hair','OnyxHair','ShilohHair','TootsieRollHair']
control.id_properties_ui('style').update(min=0,max=16,description='0 Bald; '+', '.join(str(i+1)+' '+s for i,s in enumerate(NAMES)));control.id_properties_ui('lod').update(min=0,max=1)
def active(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o

def source_material(config):
 m=bpy.data.materials.new('HairSource_'+config['name']);m.use_nodes=True;nodes=m.node_tree.nodes;nodes.clear();out=nodes.new('ShaderNodeOutputMaterial');emit=nodes.new('ShaderNodeEmission');m.node_tree.links.new(emit.outputs[0],out.inputs['Surface']);color=config['color'];emit.inputs['Color'].default_value=(color['r'],color['g'],color['b'],1)
 if config['texture']:
  im=bpy.data.images.load(str(ROOT/config['texture']),check_existing=True)
  n=nodes.new('ShaderNodeTexImage');n.image=im;n.extension='REPEAT';rgb=nodes.new('ShaderNodeMixRGB');rgb.blend_type='MULTIPLY';rgb.inputs[0].default_value=1;rgb.inputs[2].default_value=(color['r'],color['g'],color['b'],1);m.node_tree.links.new(n.outputs['Color'],rgb.inputs[1]);m.node_tree.links.new(rgb.outputs[0],emit.inputs['Color'])
 m.use_backface_culling=False;m['original_slot']=config['name'];return m

def mesh_source(name):
 with gzip.open(DATA/(name+'.json.gz'),'rt') as f:data=json.load(f)
 verts=[Vector((-p[0]*100,p[1]*100,p[2]*100)) for p in data['vertices']];sourceheads=[]
 for b in data['bindposes']:
  mat=Matrix([[b['e'+str(i)+str(j)] for j in range(4)] for i in range(4)]).inverted();sourceheads.append(Vector((-mat.translation.x*100,mat.translation.y*100,mat.translation.z*100)))
 for vi,v in enumerate(verts):
  for ji,w in zip(data['joints'][vi],data['weights'][vi]):
   if w:
    bn=data['bones'][ji]
    if bn in rig.data.bones:v+=(rig.data.bones[bn].head_local-sourceheads[ji])*w
 # Keep per-actor tuned height on existing Adell source.
 if name=='AdellHair':
  for v in verts:v.y+=.5
 mesh=bpy.data.meshes.new(name+'_Source');faces=[(f[2],f[1],f[0]) for part in data['parts'] for f in part];mesh.from_pydata(verts,[],faces);mesh.update();o=bpy.data.objects.new(mesh.name,mesh);collection.objects.link(o);o.matrix_world=body.matrix_world.copy();uv=mesh.uv_layers.new(name='SourceUV')
 for lp in mesh.loops:uv.data[lp.index].uv=data['uv'][lp.vertex_index]
 start=0
 for mi,part in enumerate(data['parts']):
  for p in list(mesh.polygons)[start:start+len(part)]:p.material_index=mi
  mesh.materials.append(source_material(data['materials'][mi]));start+=len(part)
 for vi in range(len(verts)):
  for ji,w in zip(data['joints'][vi],data['weights'][vi]):
   if w>1e-6:
    bn=data['bones'][ji]
    if bn not in rig.data.bones:bn='head'
    (o.vertex_groups.get(bn) or o.vertex_groups.new(name=bn)).add([vi],w,'ADD')
 return o,data

def outer_cards(o):
 me=o.data;me.calc_loop_triangles();verts=np.array([v.co[:] for v in me.vertices]);faces=np.array([t.vertices[:] for t in me.loop_triangles]);parent=list(range(len(verts)))
 def root(i):
  while parent[i]!=i:parent[i]=parent[parent[i]];i=parent[i]
  return i
 for a,b,c in faces:
  ra,rb,rc=root(int(a)),root(int(b)),root(int(c));parent[rb]=ra;parent[rc]=ra
 roots=np.array([root(i) for i in range(len(verts))]);face_roots=roots[faces[:,0]];unique=len(set(roots.tolist()));bv=BVHTree.FromPolygons([Vector(v) for v in verts],faces.tolist(),all_triangles=True);hits={};center=verts.mean(axis=0);radius=float(np.linalg.norm(verts.max(axis=0)-verts.min(axis=0)))+2
 # Each hit votes for an entire authored card. Hidden interior cards contribute no silhouette in opaque rendering.
 for x in [-1,0,1]:
  for y in [-1,0,1]:
   for z in [-1,0,1]:
    if x==y==z==0:continue
    direction=Vector((x,y,z)).normalized();right=direction.cross(Vector((0,1,0)))
    if right.length<.1:right=direction.cross(Vector((1,0,0)))
    right.normalize();up=direction.cross(right).normalized();u=(verts-center)@np.array(right);v=(verts-center)@np.array(up);front=Vector(center)+direction*radius
    for a in np.linspace(u.min(),u.max(),64):
     for b in np.linspace(v.min(),v.max(),64):
      _,_,fi,_=bv.ray_cast(front+right*float(a)+up*float(b),-direction,2*radius)
      if fi is not None:
       r=int(face_roots[fi]);hits[r]=hits.get(r,0)+1
 keep={r for r,_ in sorted(hits.items(),key=lambda kv:kv[1],reverse=True)[:750]}
 for p in me.polygons:
  if me.materials[p.material_index]['original_slot'].lower() in ['scalp','cap','base']:keep.add(int(roots[p.vertices[0]]))
 bm=bmesh.new();bm.from_mesh(me);bm.verts.ensure_lookup_table();bmesh.ops.delete(bm,geom=[v for v in bm.verts if int(roots[v.index]) not in keep],context='VERTS');bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.00015);bm.to_mesh(me);bm.free();me.update();return {'source_islands':unique,'visible_islands':len(hits),'kept_islands':len(keep)}

def clean_loose(o):
 bm=bmesh.new();bm.from_mesh(o.data);bmesh.ops.delete(bm,geom=[v for v in bm.verts if not v.link_faces],context='VERTS');bm.to_mesh(o.data);bm.free();o.data.update()

def deduplicate(o):
 bm=bmesh.new();bm.from_mesh(o.data);bm.verts.ensure_lookup_table();seen=set();bad=[]
 for f in bm.faces:
  key=tuple(sorted(v.index for v in f.verts))
  if key in seen:bad.append(f)
  else:seen.add(key)
 if bad:bmesh.ops.delete(bm,geom=bad,context='FACES_ONLY')
 bm.to_mesh(o.data);bm.free();clean_loose(o)

def simplify(o,budget,scalp_slots):
 # Preserve the scalp separately: collapse of hair cards must not erase its coverage.
 scalp=o.copy();scalp.data=o.data.copy();collection.objects.link(scalp);scalp.name=o.name+'_CapWork'
 for ob,keep in [(scalp,True),(o,False)]:
  bm=bmesh.new();bm.from_mesh(ob.data);bad=[f for f in bm.faces if (f.material_index in scalp_slots)!=keep]
  if bad:bmesh.ops.delete(bm,geom=bad,context='FACES_ONLY')
  bm.to_mesh(ob.data);bm.free();deduplicate(ob)
 scalp_budget=180 if budget>1000 else 140
 for ob,target in [(scalp,scalp_budget),(o,budget-min(scalp_budget,len(scalp.data.polygons)))]:
  if not ob.data.polygons:continue
  active(ob);mod=ob.modifiers.new('WebGL surface reduction','DECIMATE');mod.ratio=min(1,target/sum(len(p.vertices)-2 for p in ob.data.polygons));mod.use_collapse_triangulate=True
  while list(ob.modifiers).index(mod)>0:bpy.ops.object.modifier_move_up(modifier=mod.name)
  bpy.ops.object.modifier_apply(modifier=mod.name);clean_loose(ob)
 if len(scalp.data.polygons):
  active(o);scalp.select_set(True);bpy.ops.object.join()
 else:bpy.data.objects.remove(scalp,do_unlink=True)
 clean_loose(o)

def trim_weights(o):
 for v in o.data.vertices:
  ws=sorted([(g.group,g.weight) for g in v.groups],key=lambda q:q[1],reverse=True)[:4];total=sum(w for _,w in ws)
  for gi in [g.group for g in v.groups]:o.vertex_groups[gi].remove([v.index])
  if total:
   for i,w in ws:o.vertex_groups[i].add([v.index],w/total,'REPLACE')
  else:(o.vertex_groups.get('head') or o.vertex_groups.new(name='head')).add([v.index],1,'REPLACE')

def bind(o,name):
 gr=rig.copy();gr.data=rig.data.copy();gr.animation_data_clear();collection.objects.link(gr);gr.name=name+'_HairSkeleton';needed={o.vertex_groups[g.group].name for v in o.data.vertices for g in v.groups}
 for bn in list(needed):
  p=gr.data.bones[bn].parent
  while p:needed.add(p.name);p=p.parent
 active(gr);bpy.ops.object.mode_set(mode='EDIT')
 for b in list(gr.data.edit_bones):
  if b.name not in needed:gr.data.edit_bones.remove(b)
 bpy.ops.object.mode_set(mode='OBJECT');gr.animation_data_create();gr.animation_data.action=rig.animation_data.action.copy();gr.animation_data.action.name=name+'_RunPreview'
 for fc in list(gr.animation_data.action.fcurves):
  m=re.match(r'pose.bones\["([^\"]+)"\]',fc.data_path)
  if m and m.group(1) not in gr.data.bones:gr.animation_data.action.fcurves.remove(fc)
 world=o.matrix_world.copy();o.parent=gr;o.matrix_world=world;mod=o.modifiers.new('Original bone binding','ARMATURE');mod.object=gr;gr.hide_set(True);return gr

def switch(o,index,lod):
 for prop in ['hide_render','hide_viewport']:
  d=o.driver_add(prop).driver
  for name,field in [('style','style'),('level','lod')]:
   v=d.variables.new();v.name=name;v.targets[0].id=control;v.targets[0].data_path='["'+field+'"]'
  d.expression=f'style != {index} or level != {lod}'
 o.hide_render=index!=int(control['style']) or lod!=int(control['lod']);o.hide_viewport=o.hide_render

def build(name):
 source,data=mesh_source(name);original_tri=sum(len(p.vertices)-2 for p in source.data.polygons);o=source.copy();o.data=source.data.copy();collection.objects.link(o);o.name=name+'_LOD0';prune=outer_cards(o);scalp_slots={i for i,m in enumerate(o.data.materials) if m['original_slot'].lower() in ['scalp','cap','base']};simplify(o,2400,scalp_slots);scalp_faces={p.index for p in o.data.polygons if p.material_index in scalp_slots}
 for p in o.data.polygons:p.use_smooth=True
 clean_loose(o);trim_weights(o)
 # Bake the original colour to a single 512 atlas; alpha is deliberately unused per project hair art direction.
 atlas=bpy.data.images.new(name+'_Atlas512',width=512,height=512,alpha=False);mat=bpy.data.materials.new(name+'_WebGL');mat.use_nodes=True;p=mat.node_tree.nodes.get('Principled BSDF');p.inputs['Roughness'].default_value=1;p.inputs['Specular'].default_value=.15;tex=mat.node_tree.nodes.new('ShaderNodeTexImage');tex.image=atlas;mat.node_tree.nodes.active=tex;mat.node_tree.links.new(tex.outputs['Color'],p.inputs['Base Color']);mat.use_backface_culling=False
 o.data.materials.clear();o.data.materials.append(mat);o.data.materials.append(mat)
 for p in o.data.polygons:p.material_index=int(p.index in scalp_faces)
 uv=o.data.uv_layers.new(name='WebAtlas');o.data.uv_layers.active=uv;uv.active_render=True
 active(o);bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.uv.smart_project(angle_limit=1.15,island_margin=.012);bpy.ops.object.mode_set(mode='OBJECT')
 sc.render.engine='CYCLES';sc.cycles.samples=1;sc.render.bake.use_selected_to_active=True;sc.render.bake.cage_extrusion=.01;sc.render.bake.max_ray_distance=.06;sc.render.bake.margin=6;source.select_set(True);bpy.context.view_layer.objects.active=o;bpy.ops.object.bake(type='EMIT',use_selected_to_active=True,uv_layer='WebAtlas',use_clear=True,use_cage=False,margin=6,cage_extrusion=.01,max_ray_distance=.06)
 atlas.filepath_raw=str(T/(name+'.png'));atlas.file_format='PNG';atlas.save();atlas.pack()
 # Only the atlas UV set ships.
 for layername in list(o.data.uv_layers.keys()):
  if layername!='WebAtlas':o.data.uv_layers.remove(o.data.uv_layers[layername])
 bpy.data.objects.remove(source,do_unlink=True);gr=bind(o,name)
 low=o.copy();low.data=o.data.copy();collection.objects.link(low);low.name=name+'_LOD1';simplify(low,800,{1});trim_weights(low)
 for ob in [o,low]:
  ob.data.materials.clear();ob.data.materials.append(mat)
  for p in ob.data.polygons:p.material_index=0
 index=NAMES.index(name)+1
 for ob,lod in [(o,0),(low,1)]:
  ob['hairstyle_id']=name;ob['lod']=lod;ob['source_mesh']=data['source'];ob['source_material_slots']=json.dumps([m['name'] for m in data['materials']]);ob['runtime_binding']='Own hip hierarchy; original bone names; max 4 influences; one opaque 512 atlas.';switch(ob,index,lod)
 summary[name]={'source_vertices':len(data['vertices']),'source_triangles':original_tri,'lod0_vertices':len(o.data.vertices),'lod0_triangles':sum(len(p.vertices)-2 for p in o.data.polygons),'lod1_triangles':sum(len(p.vertices)-2 for p in low.data.polygons),'material_count':1,'texture_size':[512,512],'bones':list(gr.data.bones.keys()),'pruning':prune,'max_weights':max(len(v.groups) for v in o.data.vertices),'source_materials':[m['name'] for m in data['materials']]}
 bpy.context.view_layer.update();return o

def start_jobs(names):
 jobs=list(names)
 def step():
  if not jobs:
   sc.frame_set(1);bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'));(D/'build_status.json').write_text(json.dumps({'state':'done','hairs':summary},indent=2));return None
  name=jobs.pop(0)
  try:
   (D/'build_status.json').write_text(json.dumps({'state':'building','current':name,'remaining':len(jobs),'hairs':summary},indent=2));build(name)
   bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'))
  except:
   (D/'build_status.json').write_text(json.dumps({'state':'error','error':traceback.format_exc(),'current':name,'hairs':summary},indent=2));return None
  return .3
 bpy.app.timers.register(step,first_interval=.2)

bpy.app.driver_namespace["hair_builder"]=globals()

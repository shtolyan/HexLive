"""Run inside the ONE open Blender through its existing local API; never opens another file/process."""
import bpy,bmesh,json,traceback,re,hashlib
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');ROOT=OUT.parents[2];D=OUT/'Wardrobe';DATA=D/'Data'
sc=bpy.context.scene
catalog=json.loads((DATA/'catalog.json').read_text());specs={g['name']:g for g in catalog}
rigs={l:bpy.data.objects[l+'_Rig'] for l in ['Marta','Kshishtof']};bodies={l:bpy.data.objects[l+'_LOD0'] for l in rigs}
budgets={'PrimalArmWraps':[1400,600],'PrimalHeadband':[200,700],'PrimalWrapBoots':[500,850,2300],'PrimalSkirt':[250,350,1800,500],'PrimalDress':[1700,3800],'PrimalCollar':[1800,1300]}
images={};report={'garments':{},'sources':{},'body_contract':{},'errors':[]}
for l,o in bodies.items():
 report['body_contract'][l]={'bones':list(rigs[l].data.bones.keys()),'shapes':list(o.data.shape_keys.key_blocks.keys()),'mesh_hash':hashlib.sha256(str([[v.co[:] for v in k.data] for k in o.data.shape_keys.key_blocks]).encode()).hexdigest()}
def active(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o
def img(item,filename,normal=False):
 path=ROOT/'Assets/ImportedActors/Wear'/item/'Textures'/filename
 key=(str(path),normal)
 if key not in images:
  im=bpy.data.images.load(str(path),check_existing=True)
  if max(im.size)>1024:im.scale(1024,1024)
  if normal:im.colorspace_settings.name='Non-Color'
  im.pack();images[key]=im
 return images[key]
def driver(target,path,owner,prop,expr):
 d=target.driver_add(path).driver;v=d.variables.new();v.name='v';v.targets[0].id=owner;v.targets[0].data_path='["'+prop+'"]';d.expression=expr
 return d
def make_material(item,part,root):
 config=specs[item]['materials'][part];source=config['source'];m=bpy.data.materials.new(root.name+' / '+source);m.use_nodes=True;m['source_slot']=source;m['source_item']=item
 p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Roughness'].default_value=1-config.get('smoothness',0);p.inputs['Metallic'].default_value=config.get('metallic',0);p.inputs['Specular'].default_value=.2
 m.use_backface_culling=not config.get('doubleSided',True);nodes=m.node_tree.nodes;links=m.node_tree.links
 variants=[{'name':'Base'}]+specs[item]['variants'];color=None;base=None
 for i,var in enumerate(variants):
  tex=next((x['texture'] for x in var.get('textures',[]) if x['source']==source),config.get('texture'))
  n=nodes.new('ShaderNodeTexImage');n.name=var['name'];n.label=var['name'];n.image=img(item,tex);n.location=(-950,i*-220)
  if i==0:base=n;color=n.outputs['Color']
  else:
   mix=nodes.new('ShaderNodeMixRGB');mix.name='Variant '+var['name'];mix.location=(-650+i*125,50);links.new(color,mix.inputs[1]);links.new(n.outputs['Color'],mix.inputs[2]);driver(mix.inputs[0],'default_value',root,'material_variant','1 if v == '+str(i)+' else 0');color=mix.outputs['Color']
 links.new(color,p.inputs['Base Color'])
 if config.get('alphaClip'):
  m.blend_method='CLIP';m.shadow_method='CLIP';m.alpha_threshold=.3;links.new(base.outputs['Alpha'],p.inputs['Alpha']);m['alpha_mask_retained_for_all_variants']=True
 if config.get('normal'):
  n=nodes.new('ShaderNodeTexImage');n.image=img(item,config['normal'],True);n.location=(-450,-1300);nm=nodes.new('ShaderNodeNormalMap');nm.inputs['Strength'].default_value=.6;links.new(n.outputs['Color'],nm.inputs['Color']);links.new(nm.outputs['Normal'],p.inputs['Normal'])
 m['variants']=json.dumps([v['name'] for v in variants]);return m
def configure(o,root,label,item):
 root['equipped']=int(item!='PrimalDress');root.id_properties_ui('equipped').update(min=0,max=1,description='0 снять, 1 надеть')
 root['material_variant']=0;root.id_properties_ui('material_variant').update(min=0,max=len(specs[item]['variants']),description='0 Base; '+', '.join(str(i+1)+' '+v['name'] for i,v in enumerate(specs[item]['variants'])))
 root['variant_names']='Base | '+' | '.join(v['name'] for v in specs[item]['variants']);root['layer']=specs[item]['layer'];root['slots']=', '.join(specs[item]['slots']);root['source_item']=item;root['actor_fit']=label
 for p in ['hide_render','hide_viewport']:
  try:o.driver_remove(p)
  except:pass
  setattr(o,p,item=='PrimalDress');driver(o,p,root,'equipped','not v')
 o['source_item']=item;o['fitted_for']=label;o['runtime_binding']='One skinned mesh, original material submeshes; own hip skeleton; bind by original bone name.'
 mats=[make_material(item,i,root) for i in range(len(specs[item]['materials']))]
 o.data.materials.clear()
 for m in mats:o.data.materials.append(m)
 return mats
def recover_parts(o,data):
 # Preserve already refined topology, weights and positions. Match original submesh UV triangles.
 uvverts=[(u[0],u[1],0) for u in data['uv']];trees=[BVHTree.FromPolygons(uvverts,fs,all_triangles=True) for fs in data['parts']]
 uv=o.data.uv_layers.active.data
 for p in o.data.polygons:
  center=sum((uv[i].uv for i in p.loop_indices),Vector((0,0)))/len(p.loop_indices);q=Vector((center.x,center.y,0));rank=[]
  for pi,t in enumerate(trees):
   near,_,fi,dist=t.find_nearest(q)
   f=data['parts'][pi][fi];sourcecenter=sum((Vector((-data['vertices'][i][0]*100,data['vertices'][i][1]*100,data['vertices'][i][2]*100)) for i in f),Vector())/3
   rank.append((round(dist,5),(sourcecenter-p.center).length,pi))
  p.material_index=min(rank)[2]
def build(label,item):
 data=json.loads((DATA/(item+'_source.json')).read_text());name=label+'_'+item
 report['sources'][item]={'triangles':sum(len(p) for p in data['parts']),'source':data['source']}
 if name in bpy.data.objects:
  o=bpy.data.objects[name];root=bpy.data.objects[name+'_Wear'];configure(o,root,label,item);recover_parts(o,data);gr=bpy.data.objects[name+'_Skeleton']
 else:
  parts=[]
  for pi,faces in enumerate(data['parts']):
   used=sorted({i for f in faces for i in f});remap={v:i for i,v in enumerate(used)};verts=[Vector((-data['vertices'][i][0]*100,data['vertices'][i][1]*100,data['vertices'][i][2]*100)) for i in used]
   if label=='Kshishtof':
    for vi,si in enumerate(used):
     for ji,w in zip(data['joints'][si],data['weights'][si]):
      if w:
       bn=data['bones'][ji];verts[vi]+=(rigs[label].data.bones[bn].head_local-rigs['Marta'].data.bones[bn].head_local)*w
   mesh=bpy.data.meshes.new(name+'_'+str(pi));mesh.from_pydata(verts,[],[(remap[f[2]],remap[f[1]],remap[f[0]]) for f in faces]);mesh.update();o=bpy.data.objects.new(mesh.name,mesh);sc.collection.objects.link(o);o.matrix_world=bodies[label].matrix_world.copy();uv=mesh.uv_layers.new(name='UVMap')
   # Temporary distinct material preserves submesh partition through join.
   mesh.materials.append(bpy.data.materials.new(name+'_Part_'+str(pi)))
   for loop in mesh.loops:uv.data[loop.index].uv=data['uv'][used[loop.vertex_index]]
   for vi,si in enumerate(used):
    for ji,w in zip(data['joints'][si],data['weights'][si]):
     if w>1e-8:
      bn=data['bones'][ji];(o.vertex_groups.get(bn) or o.vertex_groups.new(name=bn)).add([vi],w,'REPLACE')
   bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.0001);bm.to_mesh(mesh);bm.free()
   active(o);mod=o.modifiers.new('Low polygon Primal','DECIMATE');mod.ratio=min(1,budgets[item][pi]/len(faces));mod.use_collapse_triangulate=True;bpy.ops.object.modifier_apply(modifier=mod.name)
   for p in mesh.polygons:p.use_smooth=True
   parts.append(o)
  active(parts[0])
  for part in parts:part.select_set(True)
  bpy.ops.object.join();o=parts[0];o.name=name
  b=bodies[label];b.data.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in b.data.vertices],[t.vertices[:] for t in b.data.loop_triangles],all_triangles=True)
  for v in o.data.vertices:
   p,n,ti,dist=bv.find_nearest(v.co);signed=(v.co-p).dot(n)
   if signed<.8:v.co+=n*(.8-signed)
  active(o);bpy.ops.object.vertex_group_normalize_all(lock_active=False)
  root=bpy.data.objects.new(name+'_Wear',None);sc.collection.objects.link(root);gr=rigs[label].copy();gr.data=rigs[label].data.copy();gr.animation_data_clear();sc.collection.objects.link(gr);gr.name=name+'_Skeleton';gr.parent=root;gr.matrix_world=rigs[label].matrix_world.copy()
  needed={g.name for g in o.vertex_groups}
  for bn in list(needed):
   parent=gr.data.bones[bn].parent
   while parent:needed.add(parent.name);parent=parent.parent
  active(gr);bpy.ops.object.mode_set(mode='EDIT')
  for b in list(gr.data.edit_bones):
   if b.name not in needed:gr.data.edit_bones.remove(b)
  bpy.ops.object.mode_set(mode='OBJECT');world=o.matrix_world.copy();o.parent=gr;o.matrix_world=world;mod=o.modifiers.new('Wear bone binding','ARMATURE');mod.object=gr
  gr.animation_data_create();gr.animation_data.action=rigs[label].animation_data.action.copy();gr.animation_data.action.name=name+'_RunBinding'
  for fc in list(gr.animation_data.action.fcurves):
   m=re.match(r'pose.bones\["([^\"]+)"\]',fc.data_path)
   if m and m.group(1) not in gr.pose.bones:gr.animation_data.action.fcurves.remove(fc)
  indices=[p.material_index for p in o.data.polygons];configure(o,root,label,item)
  for p,mi in zip(o.data.polygons,indices):p.material_index=mi
 gr.hide_set(True);root.empty_display_size=.05
 report['garments'][name]={'triangles':sum(len(p.vertices)-2 for p in o.data.polygons),'vertices':len(o.data.vertices),'bones':len(gr.data.bones),'materials':[m['source_slot'] for m in o.data.materials],'parts':[sum(p.material_index==i for p in o.data.polygons) for i in range(len(o.data.materials))],'variants':root['variant_names']}
 return o
jobs=[(l,g['name']) for l in rigs for g in catalog if not(l=='Kshishtof' and g['name']=='PrimalTop')]
def step():
 if not jobs:
  sc.frame_set(1);bpy.context.view_layer.update();bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'));(D/'build_status.json').write_text(json.dumps({'state':'done',**report},indent=2));return None
 label,item=jobs.pop(0)
 try:
  (D/'build_status.json').write_text(json.dumps({'state':'building','current':label+' '+item,'remaining':len(jobs),**report},indent=2));build(label,item)
 except Exception:
  report['errors'].append({'item':label+' '+item,'error':traceback.format_exc()});(D/'build_status.json').write_text(json.dumps({'state':'error',**report},indent=2));return None
 return .3
bpy.app.timers.register(step,first_interval=.2)
print('WARDROBE_BUILD_STARTED',len(jobs))

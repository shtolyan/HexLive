"""Adapt existing pack geometry inside the single running Blender scene."""
import bpy,bmesh,json,math,re,traceback,hashlib
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
O=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=O/'Backpacks';sc=bpy.context.scene
col=bpy.data.collections.get('05 Рюкзаки Primal')
if not col:col=bpy.data.collections.new('05 Рюкзаки Primal');sc.collection.children.link(col)
control=bpy.data.objects.get('Backpack_Controls')
if not control:control=bpy.data.objects.new('Backpack_Controls',None);col.objects.link(control)
for label in ['Marta','Kshishtof']:
 control[label]=1;control.id_properties_ui(label).update(min=0,max=3,description='0 Снять, 1 Кожаный мешок, 2 Охотничий меховой, 3 Походный')
control.empty_display_size=.02
report=json.loads((D/'build_status.json').read_text()).get('packs',{}) if (D/'build_status.json').exists() else {}
def active(o):
 bpy.ops.object.select_all(action='DESELECT');o.hide_set(False);o.select_set(True);bpy.context.view_layer.objects.active=o

def material(name,color,fur=False):
 m=bpy.data.materials.new('Pack Work '+name);m.use_nodes=True;n=m.node_tree.nodes;n.clear();out=n.new('ShaderNodeOutputMaterial');em=n.new('ShaderNodeEmission');m.node_tree.links.new(em.outputs[0],out.inputs['Surface'])
 if fur:
  tex=n.new('ShaderNodeTexImage');tex.image=bpy.data.images.load('/Volumes/ORICO/HexLive/Assets/ImportedActors/Wear/PrimalDress/Textures/PrDr_fur.jpg',check_existing=True);uv=n.new('ShaderNodeUVMap');uv.uv_map='DesignUV';m.node_tree.links.new(uv.outputs[0],tex.inputs['Vector']);mix=n.new('ShaderNodeMixRGB');mix.blend_type='MULTIPLY';mix.inputs[0].default_value=1;mix.inputs[2].default_value=(*color,1);m.node_tree.links.new(tex.outputs['Color'],mix.inputs[1]);m.node_tree.links.new(mix.outputs[0],em.inputs[0])
 else:
  tex=n.new('ShaderNodeTexNoise');tex.inputs['Scale'].default_value=45;tex.inputs['Detail'].default_value=3;ramp=n.new('ShaderNodeValToRGB');ramp.color_ramp.elements[0].position=.15;ramp.color_ramp.elements[0].color=(*(x*.5 for x in color),1);ramp.color_ramp.elements[1].position=.85;ramp.color_ramp.elements[1].color=(*color,1);m.node_tree.links.new(tex.outputs['Fac'],ramp.inputs[0]);m.node_tree.links.new(ramp.outputs[0],em.inputs[0])
 m.use_backface_culling=False;return m
mats=[material('Cognac hide',(.28,.125,.046)),material('Dark hide',(.083,.035,.018)),material('Rawhide stitches',(.58,.38,.19)),material('Bone toggles',(.72,.59,.39)),material('Primal fur',(.66,.55,.42),True),material('Smoked hide',(.17,.075,.026))]
def clean(o):
 bm=bmesh.new();bm.from_mesh(o.data);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.0001);bm.verts.ensure_lookup_table();seen=set();bad=[]
 for f in bm.faces:
  key=tuple(sorted(v.index for v in f.verts))
  if key in seen:bad.append(f)
  else:seen.add(key)
 if bad:bmesh.ops.delete(bm,geom=bad,context='FACES_ONLY')
 bmesh.ops.delete(bm,geom=[v for v in bm.verts if not v.link_faces],context='VERTS');bm.to_mesh(o.data);bm.free();o.data.update()
def trim(o):
 for v in o.data.vertices:
  ws=sorted([(g.group,g.weight) for g in v.groups if g.weight>1e-7],key=lambda q:q[1],reverse=True)[:4];total=sum(w for _,w in ws)
  for gi in [g.group for g in v.groups]:o.vertex_groups[gi].remove([v.index])
  if total:
   for gi,w in ws:o.vertex_groups[gi].add([v.index],w/total,'REPLACE')
  else:(o.vertex_groups.get('chestLower') or o.vertex_groups.new(name='chestLower')).add([v.index],1,'REPLACE')
def newmesh(name,verts,faces,mi):
 me=bpy.data.meshes.new(name);me.from_pydata(verts,[],faces);me.update();ob=bpy.data.objects.new(name,me);col.objects.link(ob);ob.matrix_world=bpy.data.objects['Marta_LOD0'].matrix_world.copy();me.materials.append(mats[mi]);return ob

def tube(name,points,radius,mi,sides=5):
 vs=[];fs=[]
 for i,p in enumerate(points):
  p=Vector(p);axis=(Vector(points[min(i+1,len(points)-1)])-Vector(points[max(0,i-1)])).normalized();a=axis.cross(Vector((0,0,1)))
  if a.length<.01:a=axis.cross(Vector((0,1,0)))
  a.normalize();b=axis.cross(a).normalized()
  for j in range(sides):vs.append(p+radius*(a*math.cos(j*math.tau/sides)+b*math.sin(j*math.tau/sides)))
 for i in range(len(points)-1):
  for j in range(sides):fs.append((i*sides+j,i*sides+(j+1)%sides,(i+1)*sides+(j+1)%sides,(i+1)*sides+j))
 fs.extend([tuple(reversed(range(sides))),tuple(range((len(points)-1)*sides,len(points)*sides))]);return newmesh(name,vs,fs,mi)
def weight_chest(o):
 for vg in list(o.vertex_groups):o.vertex_groups.remove(vg)
 o.vertex_groups.new(name='chestLower').add(list(range(len(o.data.vertices))),1,'REPLACE')
def import_base(source,style):
 data=json.loads((D/'Data'/(source+'_source.json')).read_text());parts=[]
 for pi,faces in enumerate(data['parts']):
  # Riot's metal eyelets are replaced by rawhide loops below.
  if source=='RiotBackpack' and pi==2:continue
  if source=='OsirisBackpack':faces=[f for f in faces if all(data['vertices'][i][2]*100 < -8 and data['vertices'][i][1]*100 < 147 for i in f)]
  used=sorted({i for f in faces for i in f});remap={v:i for i,v in enumerate(used)};vs=[]
  for i in used:
   x,y,z=data['vertices'][i];v=Vector((-x*100,y*100,z*100))
   if source=='RiotBackpack':v.x*=.92;v.y=128+(v.y-128)*.8+4
   vs.append(v)
  if source=='RiotBackpack':mi=(4 if style==2 else 0) if pi==3 else 1;budget=1000 if pi==3 else 240
  else:mi=5;budget=1350
  o=newmesh(source+'_part'+str(pi),vs,[(remap[f[2]],remap[f[1]],remap[f[0]]) for f in faces],mi)
  for vi,si in enumerate(used):
   for ji,w in zip(data['joints'][si],data['weights'][si]):
    if w>1e-7:
     bn=data['bones'][ji];(o.vertex_groups.get(bn) or o.vertex_groups.new(name=bn)).add([vi],w,'ADD')
  if (source=='RiotBackpack' and pi==3) or source=='OsirisBackpack':weight_chest(o)
  clean(o);active(o);dec=o.modifiers.new('Browser geometry budget','DECIMATE');dec.ratio=min(1,budget/sum(len(p.vertices)-2 for p in o.data.polygons));dec.use_collapse_triangulate=True;bpy.ops.object.modifier_apply(modifier=dec.name);clean(o);parts.append(o)
 return parts,data

def decorate(parts,style):
 # Patches and stitches are actual geometry with no transparent fur shells.
 bodypart=parts[-1];me=bodypart.data;me.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in me.vertices],[t.vertices[:] for t in me.loop_triangles],all_triangles=True)
 def surface(x,y):
  p,n,fi,dist=bv.ray_cast(Vector((x,y,-60)),Vector((0,0,1)),100)
  return (p.z-.4) if p is not None else (-24 if style==3 else -22)
 extras=[]
 if style in [2,3]:
  width=11.4 if style==2 else 13.2;top=139 if style==2 else 146;bottom=125 if style==2 else 132;vs=[];fs=[];nx=10;ny=8
  for row in range(ny+1):
   for j in range(nx+1):
    x=-width+2*width*j/nx;t=row/ny;y=top+(bottom-top)*t-2.6*t*(1-abs(x/width));vs.append((x,y,surface(x,y)-1.0))
  for row in range(ny):
   for j in range(nx):a=row*(nx+1)+j;fs.append((a,a+1,a+nx+2,a+nx+1))
  extras.append(newmesh('Hand cut leather flap',vs,fs,1 if style==2 else 0))
  for j in range(nx):
   a=Vector(vs[ny*(nx+1)+j]);b=Vector(vs[ny*(nx+1)+j+1]);c=(a+b)/2+Vector((0,-1.2-(j%3)*.3,-.08));extras.append(newmesh('Hide fringe',[a,b,c],[(0,1,2)],4 if style==3 else 1))
  cy=bottom+3;cz=surface(0,cy)-1.65;extras.append(tube('Carved bone toggle',[(-2.1,cy,cz),(0,cy+.25,cz-.2),(2.1,cy,cz)],.45,3,6));extras.append(tube('Toggle tie',[(0,cy+3.5,cz+.5),(-.4,cy+.5,cz-.5),(0,cy-1,cz),( .5,cy+.5,cz-.5),(0,cy+3.5,cz+.5)],.17,2))
  for x in [-width*.85,width*.85]:
   for y in [top-2,top-5,top-8]:extras.append(tube('Hand stitching',[(x-.5,y-.45,surface(x-.5,y-.45)-1.3),(x+.5,y+.45,surface(x+.5,y+.45)-1.3)],.13,2,4))
 if style==1:
  # One asymmetric sewn hide patch and visible hand stitching.
  corners=[Vector((-7,127,0)),Vector((3,128,0)),Vector((6,116,0)),Vector((-5,114,0))];vs=[];fs=[]
  for row in range(5):
   a=corners[0].lerp(corners[3],row/4);b=corners[1].lerp(corners[2],row/4)
   for j in range(5):p=a.lerp(b,j/4);p.z=surface(p.x,p.y)-1.0;vs.append(p)
  for row in range(4):
   for j in range(4):a=row*5+j;fs.append((a,a+1,a+6,a+5))
  extras.append(newmesh('Sewn repair patch',vs,fs,5));vs=corners
  for i in range(4):
   a=Vector(vs[i]);b=Vector(vs[(i+1)%4]);tangent=(b-a).normalized();cross=Vector((-tangent.y,tangent.x,0))*.65
   for j in range(1,5):
    p=a.lerp(b,j/5);p.z=surface(p.x,p.y)-1.3;extras.append(tube('Rawhide stitch',[p-cross,p+cross],.15,2,4))
 if style==3:
  # Rolled pelt over the older rectangular shell changes its expedition silhouette.
  extras.append(tube('Rolled Primal pelt',[(-14,149,-20.8),(0,149,-21),(14,149,-20.8)],3.8,4,12))
  for x in [-8,8]:
   points=[(x,149+3.95*math.cos(a*math.tau/12),-20.9+3.95*math.sin(a*math.tau/12)) for a in range(13)];extras.append(tube('Pelt rawhide binding',points,.35,1,5))
 if style in [1,2]:
  for x in [-10.7,10.7]:
   y=111.8;z=surface(x,y)-.5;pts=[(x+.8*math.cos(a*math.tau/8),y+.8*math.sin(a*math.tau/8),z) for a in range(9)];extras.append(tube('Rawhide eyelet',pts,.22,2,4))
 for o in extras:weight_chest(o)
 if style==3:
  for sign in [-1,1]:
   pts=[(sign*9,142,-17),(sign*10,149,-8),(sign*11,154,0),(sign*10,149,8),(sign*10,138,12),(sign*11,126,10),(sign*16,122,1),(sign*14,123,-10),(sign*9,128,-20)];vs=[];fs=[]
   for point in pts:
    p=Vector(point);normal=Vector((p.x*.4,0,p.z)).normalized()
    for width,thick in [(-1,-1),(1,-1),(1,1),(-1,1)]:vs.append(p+Vector((width*1.25,0,0))+normal*(thick*.22))
   for i in range(len(pts)-1):
    for j in range(4):fs.append((i*4+j,i*4+(j+1)%4,(i+1)*4+(j+1)%4,(i+1)*4+j))
   fs.extend([(3,2,1,0),tuple(range((len(pts)-1)*4,len(pts)*4))]);o=newmesh('Hand cut shoulder strap',vs,fs,1)
   lo=o.vertex_groups.new(name='chestLower');hi=o.vertex_groups.new(name='chestUpper')
   for v in o.data.vertices:
    w=max(0,min(1,(v.co.y-131)/15));lo.add([v.index],1-w,'REPLACE');hi.add([v.index],w,'REPLACE')
   extras.append(o)
 parts.extend(extras)

def bind(o,label,name):
 source=bpy.data.objects[label+'_Rig'];gr=source.copy();gr.data=source.data.copy();gr.animation_data_clear();col.objects.link(gr);gr.name=label+'_'+name+'_Skeleton';needed={o.vertex_groups[g.group].name for v in o.data.vertices for g in v.groups}
 for bn in list(needed):
  b=gr.data.bones[bn].parent
  while b:needed.add(b.name);b=b.parent
 active(gr);bpy.ops.object.mode_set(mode='EDIT')
 for b in list(gr.data.edit_bones):
  if b.name not in needed:gr.data.edit_bones.remove(b)
 bpy.ops.object.mode_set(mode='OBJECT');gr.animation_data_create();gr.animation_data.action=source.animation_data.action.copy();gr.animation_data.action.name=label+'_'+name+'_Run'
 for fc in list(gr.animation_data.action.fcurves):
  m=re.match(r'pose.bones\["([^\"]+)"\]',fc.data_path)
  if m and m.group(1) not in gr.data.bones:gr.animation_data.action.fcurves.remove(fc)
 world=o.matrix_world.copy();o.parent=gr;o.matrix_world=world;mod=o.modifiers.new('Original named bones','ARMATURE');mod.object=gr;gr.hide_set(True);return gr

def fit(o,label):
 body=bpy.data.objects[label+'_LOD0'];body.data.calc_loop_triangles();bv=BVHTree.FromPolygons([v.co for v in body.data.vertices],[t.vertices[:] for t in body.data.loop_triangles],all_triangles=True)
 for v in o.data.vertices:
  if label=='Kshishtof':
   for g in v.groups:
    bn=o.vertex_groups[g.group].name;v.co+=(bpy.data.objects[label+'_Rig'].data.bones[bn].head_local-bpy.data.objects['Marta_Rig'].data.bones[bn].head_local)*g.weight
  p,n,fi,dist=bv.find_nearest(v.co)
  if dist<7:
   signed=(v.co-p).dot(n)
   if signed<1.3:v.co=p+n*1.3
 o.data.update()

def bake(o,name):
 uv=o.data.uv_layers.new(name='DesignUV')
 for lp in o.data.loops:
  v=o.data.vertices[lp.vertex_index].co;uv.data[lp.index].uv=((v.x+20)/40,(v.y-103)/50)
 uv=o.data.uv_layers.new(name='PackAtlas');o.data.uv_layers.active=uv;uv.active_render=True;active(o);bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.uv.smart_project(angle_limit=1.15,island_margin=.012);bpy.ops.object.mode_set(mode='OBJECT')
 atlas=bpy.data.images.new(name+'_Atlas512',width=512,height=512,alpha=False)
 for mat in o.data.materials:
  n=mat.node_tree.nodes.new('ShaderNodeTexImage');n.image=atlas;mat.node_tree.nodes.active=n
 sc.render.engine='CYCLES';sc.cycles.samples=1;bpy.ops.object.bake(type='EMIT',use_selected_to_active=False,margin=6,uv_layer='PackAtlas')
 atlas.filepath_raw=str(D/'Textures'/(name+'.png'));atlas.file_format='PNG';atlas.save();atlas.pack();mat=bpy.data.materials.new(name+'_Primal');mat.use_nodes=True;p=mat.node_tree.nodes.get('Principled BSDF');p.inputs['Roughness'].default_value=1;p.inputs['Specular'].default_value=.16;p.inputs['Metallic'].default_value=0;n=mat.node_tree.nodes.new('ShaderNodeTexImage');n.image=atlas;mat.node_tree.links.new(n.outputs['Color'],p.inputs['Base Color']);mat.use_backface_culling=False;o.data.materials.clear();o.data.materials.append(mat)
 for p in o.data.polygons:p.material_index=0;p.use_smooth=True
 o.data.uv_layers.remove(o.data.uv_layers['DesignUV'])

def visibility(o,label,style):
 for field in ['hide_render','hide_viewport']:
  d=o.driver_add(field).driver;v=d.variables.new();v.name='style';v.targets[0].id=control;v.targets[0].data_path='["'+label+'"]';d.expression=f'style != {style}'
 o['pack_style']=style;o['actor_fit']=label;o['runtime_binding']='Separate skinned mesh, own hip hierarchy, original bone names, at most four weights.'

def build(style,name,source):
 parts,data=import_base(source,style);decorate(parts,style);active(parts[0])
 for o in parts:o.select_set(True)
 bpy.ops.object.join();o=parts[0];o.name='Marta_'+name;clean(o);trim(o);fit(o,'Marta');bake(o,name)
 male=o.copy();male.data=o.data.copy();male.name='Kshishtof_'+name;col.objects.link(male);male.matrix_world=bpy.data.objects['Kshishtof_LOD0'].matrix_world.copy();fit(male,'Kshishtof');trim(male)
 for label,ob in [('Marta',o),('Kshishtof',male)]:
  gr=bind(ob,label,name);visibility(ob,label,style);ob['source_mesh']=data['source'];report[ob.name]={'triangles':sum(len(p.vertices)-2 for p in ob.data.polygons),'vertices':len(ob.data.vertices),'bones':len(gr.data.bones),'materials':1,'source':source,'source_triangles':sum(len(p) for p in data['parts'])}
 sc.frame_set(1);bpy.context.view_layer.update()
jobs=[(1,'PrimalGathererPack','RiotBackpack'),(2,'PrimalHunterPack','RiotBackpack'),(3,'PrimalTrailPack','OsirisBackpack')]
def step():
 if not jobs:
  (D/'build_status.json').write_text(json.dumps({'state':'done','packs':report},indent=2));bpy.ops.wm.save_as_mainfile(filepath=str(O/'Primal_Wardrobe.blend'));return None
 style,name,source=jobs.pop(0)
 try:build(style,name,source)
 except:(D/'build_status.json').write_text(json.dumps({'state':'error','name':name,'error':traceback.format_exc(),'packs':report},indent=2));return None
 (D/'build_status.json').write_text(json.dumps({'state':'building','current':name,'remaining':len(jobs),'packs':report},indent=2));return .3
bpy.app.driver_namespace['backpack_builder']=globals();bpy.app.timers.register(step,first_interval=.3)

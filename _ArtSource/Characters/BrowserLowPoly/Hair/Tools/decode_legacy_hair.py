import bpy,json,gzip,re,traceback
from pathlib import Path
from mathutils import Vector
ROOT=Path('/Volumes/ORICO/HexLive');D=ROOT/'_ArtSource/Characters/BrowserLowPoly/Hair/Data';catalog=json.loads((D/'catalog.json').read_text());jobs=[g for g in catalog if g['source'].endswith('.fbx')]
def decode(g):
 sc=bpy.context.scene;timing=(sc.frame_start,sc.frame_end,sc.render.fps,sc.frame_current);before=set(bpy.data.objects);bpy.ops.import_scene.fbx(filepath=str(ROOT/g['source']),use_anim=False);created=set(bpy.data.objects)-before;meshes=[o for o in created if o.type=='MESH'];arms=[o for o in created if o.type=='ARMATURE'];bones=sorted({vg.name for o in meshes for vg in o.vertex_groups});verts=[];uvs=[];weights=[];joints=[];parts=[[] for _ in g['materials']];matlookup={m['name'].lower():i for i,m in enumerate(g['materials'])}
 def slot(mat):
  name=re.sub(r'\.\d+$','',mat.name)
  if name.lower() in matlookup:return matlookup[name.lower()]
  raise ValueError((g['name'],name,list(matlookup)))
 for o in meshes:
  me=o.data;me.calc_loop_triangles();base=len(verts);uvw=[(0,0)]*len(me.vertices)
  for lp in me.loops:uvw[lp.vertex_index]=me.uv_layers.active.data[lp.index].uv[:]
  for v in me.vertices:
   p=o.matrix_world@v.co;verts.append((-p.x,p.z,-p.y));uvs.append(uvw[v.index]);ws=sorted([(bones.index(o.vertex_groups[x.group].name),x.weight) for x in v.groups],key=lambda q:q[1],reverse=True)[:4];total=sum(w for _,w in ws);weights.append([w/total for _,w in ws]+[0]*(4-len(ws)));joints.append([i for i,_ in ws]+[0]*(4-len(ws)))
  for t in me.loop_triangles:parts[slot(me.materials[t.material_index])].append([base+int(v) for v in reversed(t.vertices)])
 bind=[]
 for bn in bones:
  a=next(a for a in arms if bn in a.data.bones);p=a.matrix_world@a.data.bones[bn].head_local;p=Vector((-p.x,p.z,-p.y));m={'e'+str(i)+str(j):int(i==j) for i in range(4) for j in range(4)};m.update(e03=-p.x,e13=-p.y,e23=-p.z);bind.append(m)
 result=dict(g,vertices=verts,uv=uvs,weights=weights,joints=joints,parts=parts,bindposes=bind,bones=bones)
 with gzip.open(D/(g['name']+'.json.gz'),'wt',compresslevel=1) as f:json.dump(result,f,separators=(',',':'))
 for o in created:bpy.data.objects.remove(o,do_unlink=True)
 sc.frame_start,sc.frame_end,sc.render.fps,frame=timing;sc.frame_set(frame)
 print('DECODED',g['name'],len(verts),sum(len(p) for p in parts))
def step():
 if not jobs:(D/'legacy_status.json').write_text(json.dumps({'state':'done'}));return None
 g=jobs.pop(0)
 try:decode(g)
 except:(D/'legacy_status.json').write_text(json.dumps({'state':'error','name':g['name'],'error':traceback.format_exc()}));return None
 (D/'legacy_status.json').write_text(json.dumps({'state':'decoding','name':g['name'],'remaining':len(jobs)}));return .3
bpy.app.timers.register(step,first_interval=.2)

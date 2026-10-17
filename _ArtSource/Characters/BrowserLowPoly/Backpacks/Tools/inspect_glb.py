"""Validate actual exported GLB payloads without starting Blender."""
import json,struct
from pathlib import Path
D=Path(__file__).resolve().parents[1]
rows={}
for p in sorted((D/'Exports').glob('*.glb')):
 raw=p.read_bytes();magic,version,total=struct.unpack_from('<4sII',raw)
 assert magic==b'glTF' and version==2 and total==len(raw)
 n,kind=struct.unpack_from('<II',raw,12);assert kind==0x4e4f534a
 j=json.loads(raw[20:20+n]);prims=[pr for m in j['meshes'] for pr in m['primitives']];triangles=sum(j['accessors'][pr['indices']]['count']//3 for pr in prims);verts=sum(j['accessors'][pr['attributes']['POSITION']]['count'] for pr in prims)
 assert len(j['skins'])==1 and len(prims)==1 and len(j['materials'])==1
 assert all('JOINTS_0' in pr['attributes'] and 'WEIGHTS_0' in pr['attributes'] and 'JOINTS_1' not in pr['attributes'] for pr in prims)
 assert j['materials'][0].get('alphaMode','OPAQUE')=='OPAQUE' and j['materials'][0].get('doubleSided')
 assert len(j['images'])==1 and 'bufferView' in j['images'][0]
 assert all('uri' not in im for im in j['images']) and not j.get('animations')
 rows[p.stem]={'bytes':len(raw),'triangles':triangles,'exported_vertices':verts,'materials':1,'skin_bones':len(j['skins'][0]['joints']),'embedded_atlas':True,'opaque_double_sided':True}
assert len(rows)==6, len(rows)
(D/'export_verification.json').write_text(json.dumps({'glb_count':len(rows),'all_passed':True,'exports':rows},indent=2))
print(json.dumps({'count':len(rows),'size_kib_range':[min(x['bytes'] for x in rows.values())//1024,max(x['bytes'] for x in rows.values())//1024],'triangles_range':[min(x['triangles'] for x in rows.values()),max(x['triangles'] for x in rows.values())]}))

"""Read committed Unity mesh text without running/mutating Unity Editor."""
import json,re,struct,yaml
from pathlib import Path
OUT=Path(__file__).resolve().parent;ROOT=OUT.parents[2];D=OUT/'Clothing_Primal';D.mkdir(exist_ok=True)
def docs(path):
 raw=path.read_text();out={}
 for kind,fid,text in re.findall(r'--- !u!(\d+) &(\d+)\n(.*?)(?=--- !u!|\Z)',raw,re.S):out[int(fid)]=yaml.safe_load(text)
 return out
for item,sid in [('PrimalTop','clothing.top_primal'),('PrimalBriefs','underwear.briefs_primal')]:
 path=ROOT/f'Assets/ImportedActors/Wear/{item}/Meshes/Marta.mesh';mesh=next(iter(docs(path).values()))['Mesh']
 prefab=docs(ROOT/f'Assets/HexLiveContent/Wear/{sid}/{item}.prefab');smr=next(v['SkinnedMeshRenderer'] for v in prefab.values() if 'SkinnedMeshRenderer' in v)
 bones=[prefab[prefab[b['fileID']]['Transform']['m_GameObject']['fileID']]['GameObject']['m_Name'] for b in smr['m_Bones']]
 vd=mesh['m_VertexData'];count=vd['m_VertexCount'];channels=vd['m_Channels'];data=bytes.fromhex(vd['_typelessdata']);offsets={};strides={};start=0
 formats={0:('f',4),10:('I',4)}
 for ch in channels:
  if ch['dimension']:strides[ch['stream']]=max(strides.get(ch['stream'],0),ch['offset']+ch['dimension']*formats[ch['format']][1])
 for stream,stride in sorted(strides.items()):offsets[stream]=start;start=(start+stride*count+15)//16*16
 def read_channel(i):
  ch=channels[i];fmt=formats[ch['format']][0];return [struct.unpack_from('<'+fmt*ch['dimension'],data,offsets[ch['stream']]+v*strides[ch['stream']]+ch['offset']) for v in range(count)]
 code='H' if mesh['m_IndexFormat']==0 else 'I';ib=bytes.fromhex(mesh['m_IndexBuffer']);all_idx=[v[0] for v in struct.iter_unpack('<'+code,ib)];parts=[]
 for sub in mesh['m_SubMeshes']:
  start=sub['firstByte']//struct.calcsize(code);idx=all_idx[start:start+sub['indexCount']];parts.append([idx[i:i+3] for i in range(0,len(idx),3)])
 result=dict(source=str(path.relative_to(ROOT)),vertices=read_channel(0),normals=read_channel(1),uv=read_channel(4),weights=read_channel(12),joints=read_channel(13),bones=bones,parts=parts)
 (D/(item+'_source.json')).write_text(json.dumps(result,separators=(',',':')))
 print(item,count,[len(p) for p in parts],bones)

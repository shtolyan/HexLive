import json,re,struct,gzip,yaml
from pathlib import Path
ROOT=Path('/Volumes/ORICO/HexLive');OUT=ROOT/'_ArtSource/Characters/BrowserLowPoly/Hair/Data'
def docs(path):
 return {int(fid):yaml.load(text,Loader=yaml.CSafeLoader) for _,fid,text in re.findall(r'--- !u!(\d+) &(-?\d+)\n(.*?)(?=--- !u!|\Z)',path.read_text(encoding='utf-8-sig'),re.S)}
guids={}
for m in (ROOT/'Assets/ImportedActors').rglob('*.meta'):
 with m.open() as f:head=f.read(160)
 x=re.search(r'^guid: (\w+)',head,re.M)
 if x:guids[x[1]]=Path(str(m)[:-5])
catalog=[]
names=['AdellHair','AsukaHair','BendineHair','Bob3Hair','ChunkyHair','EilisHair','Hair07','JelikaHair_32434','JenniferHair','LeonyPonytail','LoonaHair','LowPonytail','Neu09Hair','OnyxHair','ShilohHair','TootsieRollHair']
for name in names:
 prefab=ROOT/f'Assets/ImportedActors/Hair/{name}/{name}.prefab';ds=docs(prefab);smr=next(v['SkinnedMeshRenderer'] for v in ds.values() if 'SkinnedMeshRenderer' in v);bones=[ds[ds[b['fileID']]['Transform']['m_GameObject']['fileID']]['GameObject']['m_Name'] for b in smr['m_Bones']];source=guids[smr['m_Mesh']['guid']];mats=[]
 for mr in smr['m_Materials']:
  mp=guids[mr['guid']];mat=next(v['Material'] for v in docs(mp).values() if 'Material' in v);saved=mat['m_SavedProperties'];tex={k:v for a in saved['m_TexEnvs'] for k,v in a.items()};colors={k:v for a in saved['m_Colors'] for k,v in a.items()};floats={k:v for a in saved['m_Floats'] for k,v in a.items()};tp=tex.get('_BaseMap',tex.get('_MainTex',{}));guid=tp.get('m_Texture',{}).get('guid');texture=guids.get(guid);mats.append({'name':mat['m_Name'],'file':str(mp.relative_to(ROOT)),'texture':str(texture.relative_to(ROOT)) if texture else None,'color':colors.get('_BaseColor',colors.get('_Color',{'r':1,'g':1,'b':1,'a':1})),'uv_scale':tp.get('m_Scale',{'x':1,'y':1}),'uv_offset':tp.get('m_Offset',{'x':0,'y':0}),'alpha_clip':floats.get('_AlphaClip',0),'surface':floats.get('_Surface',0)})
 g={'name':name,'prefab':str(prefab.relative_to(ROOT)),'source':str(source.relative_to(ROOT)),'bones':bones,'materials':mats}
 if source.suffix=='.mesh':
  mesh=next(iter(docs(source).values()))['Mesh'];vd=mesh['m_VertexData'];count=vd['m_VertexCount'];channels=vd['m_Channels'];data=bytes.fromhex(vd['_typelessdata']);offsets={};strides={};start=0;formats={0:('f',4),1:('e',2),10:('I',4)}
  for ch in channels:
   if ch['dimension']:strides[ch['stream']]=max(strides.get(ch['stream'],0),ch['offset']+ch['dimension']*formats[ch['format']][1])
  for stream,stride in sorted(strides.items()):offsets[stream]=start;start=(start+stride*count+15)//16*16
  def channel(i):
   ch=channels[i];fmt=formats[ch['format']][0];return [struct.unpack_from('<'+fmt*ch['dimension'],data,offsets[ch['stream']]+v*strides[ch['stream']]+ch['offset']) for v in range(count)]
  code='H' if mesh['m_IndexFormat']==0 else 'I';ib=bytes.fromhex(mesh['m_IndexBuffer']);idx=[v[0] for v in struct.iter_unpack('<'+code,ib)];parts=[]
  for sub in mesh['m_SubMeshes']:
   start=sub['firstByte']//struct.calcsize(code);ii=idx[start:start+sub['indexCount']];parts.append([[v+sub.get('baseVertex',0) for v in ii[i:i+3]] for i in range(0,len(ii),3)])
  result=dict(vertices=channel(0),uv=channel(4),weights=channel(12),joints=channel(13),parts=parts,bindposes=mesh['m_BindPose'],**g)
  with gzip.open(OUT/(name+'.json.gz'),'wt') as f:json.dump(result,f,separators=(',',':'))
  g['vertices']=count;g['triangles']=sum(len(p) for p in parts);g['part_triangles']=[len(p) for p in parts]
 catalog.append(g);print(name,g.get('vertices'),g.get('triangles'),len(mats),flush=True)
(OUT/'catalog.json').write_text(json.dumps(catalog,indent=2))

"""Lossless zero-elision for GLB morph data using core glTF 2.0 sparse accessors.
Spec: https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#sparse-accessors
No quantization, no shape removal, no external decoder extension.
"""
import json,struct,copy
from pathlib import Path

def pack(path):
 raw=path.read_bytes();jl=struct.unpack_from('<I',raw,12)[0];doc=json.loads(raw[20:20+jl]);start=20+jl;bl=struct.unpack_from('<I',raw,start)[0];binary=raw[start+8:start+8+bl]
 oldviews=doc['bufferViews'];morphs={a for m in doc['meshes'] for p in m['primitives'] for t in p.get('targets',[]) for a in t.values()}
 if any('sparse' in doc['accessors'][a] for a in morphs):return {'bytes':len(raw),'already_sparse':True}
 payload=bytearray();newviews=[];oldmap={};saved=0
 def append(data,meta=None):
  payload.extend(b'\x00'*((-len(payload))%4));v=dict(meta or {});v.update(buffer=0,byteOffset=len(payload),byteLength=len(data));newviews.append(v);payload.extend(data);return len(newviews)-1
 def copy_view(i):
  if i not in oldmap:
   v=oldviews[i];off=v.get('byteOffset',0);meta={k:val for k,val in v.items() if k not in ['buffer','byteOffset','byteLength']};oldmap[i]=append(binary[off:off+v['byteLength']],meta)
  return oldmap[i]
 for ai,a in enumerate(doc['accessors']):
  if ai not in morphs:
   if 'bufferView' in a:a['bufferView']=copy_view(a['bufferView'])
   continue
  assert a['componentType']==5126 and a['type']=='VEC3' and 'sparse' not in a
  v=oldviews[a['bufferView']];assert v.get('byteStride',12)==12
  off=v.get('byteOffset',0)+a.get('byteOffset',0);data=binary[off:off+12*a['count']]
  nonzero=[i for i,values in enumerate(struct.iter_unpack('<fff',data)) if any(n!=0 for n in values)]
  code='H' if a['count']<65536 else 'I';component=5123 if code=='H' else 5125
  a.pop('byteOffset',None);a.pop('bufferView',None)
  if len(nonzero)*(12+struct.calcsize(code))>=len(data):a['bufferView']=append(data,{'target':34962});continue
  restored=bytearray(len(data))
  for i in nonzero:restored[i*12:i*12+12]=data[i*12:i*12+12]
  # Numeric equality is exact; signed zero may be canonicalized to positive zero.
  assert list(struct.iter_unpack('<f',restored))==list(struct.iter_unpack('<f',data))
  if nonzero:
   idx=append(struct.pack('<'+code*len(nonzero),*nonzero));values=append(b''.join(data[i*12:i*12+12] for i in nonzero))
   a['sparse']={'count':len(nonzero),'indices':{'bufferView':idx,'componentType':component},'values':{'bufferView':values}}
  saved+=1
 for im in doc.get('images',[]):
  if 'bufferView' in im:im['bufferView']=copy_view(im['bufferView'])
 doc['bufferViews']=newviews;doc['buffers']=[{'byteLength':len(payload)}]
 j=json.dumps(doc,separators=(',',':')).encode();j+=b' '*((-len(j))%4);payload.extend(b'\x00'*((-len(payload))%4))
 result=struct.pack('<III',0x46546C67,2,12+8+len(j)+8+len(payload))+struct.pack('<II',len(j),0x4E4F534A)+j+struct.pack('<II',len(payload),0x004E4942)+payload
 path.write_bytes(result);return {'before':len(raw),'after':len(result),'morph_accessors_sparsified':saved,'values_numerically_identical':True}
if __name__=='__main__':
 import argparse
 parser=argparse.ArgumentParser();parser.add_argument('--directory',default='Rigged');args=parser.parse_args()
 d=Path(__file__).resolve().parent/args.directory;r={p.name:pack(p) for p in sorted(d.glob('*.glb'))};(d/'sparse_packing.json').write_text(json.dumps(r,indent=2));print(json.dumps(r,indent=2))

"""Inspect actual binary FBX output using Blender's parser, without importing it."""
import hashlib
import json
from pathlib import Path
from io_scene_fbx import parse_fbx

root=Path('/Volumes/ORICO/HexLive-webgl/Assets/HexLiveContent/People/Source')
manifest=json.loads((root/'source-manifest.json').read_text())
results=[]

def name(node):
    return node.props[1].split(b'\x00\x01')[0].decode('utf8')

for model in manifest['models']:
    path=root/model['path']
    assert hashlib.sha256(path.read_bytes()).hexdigest()==model['sha256'], path
    tree,version=parse_fbx.parse(str(path))
    objects=next(n for n in tree.elems if n.id==b'Objects').elems
    bones=[name(n) for n in objects if n.id==b'Model' and n.props[-1]==b'LimbNode']
    shapes=[name(n) for n in objects if n.id==b'Deformer' and n.props[-1]==b'BlendShapeChannel']
    meshes=[n for n in objects if n.id==b'Geometry' and n.props[-1]==b'Mesh']
    assert len(bones)==len(set(bones)) and set(bones)==set(model['bones']), model['id']
    expected_shapes=[s for mesh in model['meshes'] for s in mesh['blendShapes']]
    assert sorted(shapes)==sorted(expected_shapes), model['id']
    assert len(meshes)==len(model['meshes']), model['id']
    assert not any(n.id==b'AnimationStack' for n in objects), model['id']
    triangles=0
    for mesh in meshes:
        pvi=next(n for n in mesh.elems if n.id==b'PolygonVertexIndex').props[0]
        polygon_size=0
        for vi in pvi:
            polygon_size+=1
            if vi<0:
                assert polygon_size>=3
                triangles+=polygon_size-2
                polygon_size=0
        assert polygon_size==0
    assert triangles==sum(m['triangles'] for m in model['meshes']), model['id']
    results.append({'file':model['path'],'bones':len(bones),'blendShapes':len(shapes),
                    'triangles':triangles,'passed':True})
for texture in manifest['textures'].values():
    assert hashlib.sha256((root/texture['path']).read_bytes()).hexdigest()==texture['sha256']
report={'allPassed':True,'files':len(results),'checks':['FBX parsed','bone names exact',
        'morph names exact','mesh and triangle counts','no demo animations','SHA256 files and textures'],
        'unityValidated':False,'results':results}
(root/'fbx-verification.json').write_text(json.dumps(report,indent=2))
print(json.dumps({'allPassed':True,'files':len(results),'unityValidated':False}))

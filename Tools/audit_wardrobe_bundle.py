#!/usr/bin/env python3
"""Read the actual UnityFS payload (pip install UnityPy); no Unity/editor mutation.
Compare the old detached-template basis with its authored imported ancestors.
The runtime regression separately executes the production C# factories.
"""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
import UnityPy


def xyz(v):
    return [v.x, v.y, v.z]


def apply(t, p):
    p = [v * s for v, s in zip(p, xyz(t.m_LocalScale))]
    q = t.m_LocalRotation
    u = [q.x, q.y, q.z]
    cross = lambda a, b: [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
    uv = cross(u, p)
    uuv = cross(u, uv)
    return [p[i] + 2*(q.w*uv[i]+uuv[i]) + xyz(t.m_LocalPosition)[i] for i in range(3)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    parser.add_argument('--output', type=Path, default=Path('Docs'))
    args = parser.parse_args()
    env = UnityPy.load(str(args.bundle))
    transforms = {o.read().m_GameObject.path_id: o.read() for o in env.objects if o.type.name == 'Transform'}
    template = next(t for t in transforms.values() if t.m_GameObject.read().m_Name == 'HangerTemplate')
    template_id = template.object_reader.path_id

    def world(t, p, stop=None):
        if t.object_reader.path_id == stop:
            return p
        p = apply(t, p)
        return world(t.m_Father.read(), p, stop) if t.m_Father.path_id else p

    def is_hanger(t):
        if t.object_reader.path_id == template_id:
            return True
        return is_hanger(t.m_Father.read()) if t.m_Father.path_id else False

    before, after, shelf = [], [], []
    for obj in env.objects:
        if obj.type.name != 'MeshFilter':
            continue
        f = obj.read()
        t = transforms[f.m_GameObject.path_id]
        box = f.m_Mesh.read().m_LocalAABB
        points = [[c + e*s for c,e,s in zip(xyz(box.m_Center),xyz(box.m_Extent),signs)] for signs in itertools.product([-1,1],repeat=3)]
        if is_hanger(t):
            before.extend(world(t,p,template_id) for p in points)
            after.extend(world(t,p) for p in points)
        if t.m_GameObject.read().m_Name.startswith('Wardrobe_ShoeShelf_'):
            shelf.extend(world(t,p) for p in points)
    def bounds(points):
        low=[min(p[i] for p in points) for i in range(3)]
        high=[max(p[i] for p in points) for i in range(3)]
        return dict(min=low,max=high,size=[b-a for a,b in zip(low,high)])
    old,new,board=map(bounds,[before,after,shelf])
    assert old['size'][1] < .025 and new['size'][1] > .2
    assert new['size'][2] < .025
    assert abs(board['max'][1] - .265) < 1e-5
    receipt=dict(bundleSha256=hashlib.sha256(args.bundle.read_bytes()).hexdigest(),entry='main',
                 oldDetachedBounds=old,authoredBounds=new,shelfBounds=board,oldShelfSurface=.19,
                 sinkDepth=board['max'][1]-.19)
    args.output.mkdir(parents=True,exist_ok=True)
    (args.output/'WardrobeBundleBasis.json').write_text(json.dumps(receipt,indent=2)+'\n')
    # Side view: Z horizontal, Y vertical; all lengths use one 550 px/metre scale.
    def rect(box,x,y,color):
        return f'<rect x="{x+box["min"][2]*550:.3f}" y="{y-box["max"][1]*550:.3f}" width="{box["size"][2]*550:.3f}" height="{box["size"][1]*550:.3f}" fill="{color}"/>'
    top=board['max'][1]
    svg=f'''<svg xmlns="http://www.w3.org/2000/svg" width="960" height="360" viewBox="0 0 960 360">
<rect width="960" height="360" fill="#20282d"/><g fill="#f1f4f6" font-family="sans-serif" font-size="18">
<text x="24" y="32">WebGL-бандл: вид сбоку (Z →, Y ↑), единый масштаб 550 px/м</text>
<text x="24" y="76">Было: потеря осей родителя</text><text x="335" y="76">Стало: исходные оси FBX</text>
{rect(old,165,125,'#f27665')}{rect(new,470,125,'#99d665')}
<text x="24" y="302">Высота {old['size'][1]:.4f} м</text><text x="335" y="302">Высота {new['size'][1]:.4f} м</text>
<text x="645" y="76">Обувная полка</text>
<rect x="650" y="{290-top*550:.3f}" width="270" height="{board['size'][1]*550:.3f}" fill="#af8b58"/>
<path d="M650,{290-.19*550:.3f} H920" stroke="#f27665" stroke-width="3"/>
<path d="M650,{290-top*550:.3f} H920" stroke="#99d665" stroke-width="3"/>
<text x="645" y="270">Поверхность: {top:.3f} м</text><text x="645" y="302">Было 0.190: ниже на {(top-.19)*100:.1f} см</text>
<text x="24" y="340" font-size="12">Границы измерены из опубликованного payload; поворот не добавляется вручную.</text></g></svg>'''
    (args.output/'WardrobeBundleBasis.svg').write_text(svg)
    print(json.dumps(receipt,indent=2))


if __name__ == '__main__':
    main()

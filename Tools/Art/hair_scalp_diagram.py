"""Draw real sagittal sections of the fitted Bob3 scalp and both body meshes.

blender -b --python-exit-code 1 -P Tools/Art/hair_scalp_diagram.py -- --output <svg>
No fabricated radii: coordinates come from the committed, skinned FBX vertices.
"""
import argparse
import json
import sys
from pathlib import Path
import bpy
from mathutils import Vector
sys.path.insert(0, str(Path(__file__).resolve().parent))
from fit_hair_scalps import SOURCE, load, bones, rig_of, weights_at, skin_matrix


def segments(points, triangles):
    result = []
    for triangle in triangles:
        hits = []
        for a, b in zip(triangle, triangle[1:] + triangle[:1]):
            p, q = points[a], points[b]
            if (p.x > 0) == (q.x > 0) or abs(p.x - q.x) < 1e-8: continue
            point = p.lerp(q, -p.x / (q.x - p.x))
            if not any((point - hit).length < 1e-7 for hit in hits): hits.append(point)
        if len(hits) == 2: result.append(hits)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    manifest = json.loads((SOURCE / 'source-manifest.json').read_text())
    base = next(r for r in manifest['models'] if r['id'] == 'Bob3Hair_LOD0')
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="800" height="480" viewBox="0 0 800 480">',
        '<rect width="800" height="480" fill="#10191f"/>',
        '<g font-family="sans-serif" fill="#e6edf3"><text x="24" y="28" font-size="19">Посадка подложки волос: реальный срез FBX, x = 0</text>']
    for panel, actor in enumerate(('Marta', 'Kshishtof')):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        objects = load(SOURCE / 'Models' / (actor + '.fbx'))
        body = next(o for o in objects if o.type == 'MESH' and '_LOD0' in o.name)
        target = bones(rig_of(body)); head = target['head'].translation
        body.data.calc_loop_triangles()
        outline = segments([body.matrix_world @ v.co for v in body.data.vertices],
            [list(t.vertices) for t in body.data.loop_triangles])
        row = base if actor == 'Marta' else base['bodyFits'][actor]
        objects = load(SOURCE / row['path'])
        hair = next(o for o in objects if o.type == 'MESH'); source = bones(rig_of(hair))
        transforms = {b: target[b] @ source[b].inverted() @ hair.matrix_world for b in source if b in target}
        points = [skin_matrix(ws, transforms) @ v.co for ws, v in zip(weights_at(hair), hair.data.vertices)]
        cap_slots = {i for i, key in enumerate(row['meshes'][0]['materials'])
            if manifest['materials'][key]['sourceSlot'].lower() in ('scalp', 'base', 'cap')}
        hair.data.calc_loop_triangles()
        cap = segments(points, [list(t.vertices) for t in hair.data.loop_triangles if t.material_index in cap_slots])
        x0 = panel * 400
        def xy(p): return x0 + 210 + (p.y - head.y) * 1450, 415 - (p.z - head.z) * 1450
        svg.append(f'<text x="{x0 + 24}" y="60" font-size="16">{actor} · Bob3 · LOD0</text>')
        for y in range(85, 431, 29): svg.append(f'<path d="M{x0+24},{y} H{x0+375}" stroke="#263640" stroke-width=".5"/>')
        for x in range(x0+24, x0+376, 29): svg.append(f'<path d="M{x},85 V430" stroke="#263640" stroke-width=".5"/>')
        for lines, colour, width in ((outline, '#efa356', 2.2), (cap, '#61caff', 1.4)):
            for p, q in lines:
                if min(p.z, q.z) < head.z - .005: continue
                a, b = xy(p), xy(q)
                svg.append(f'<path d="M{a[0]:.3f},{a[1]:.3f} L{b[0]:.3f},{b[1]:.3f}" fill="none" stroke="{colour}" stroke-width="{width}"/>')
    svg += ['<text x="24" y="454" font-size="13">Оранжевый — голова · голубой — подложка волос · сетка 20 мм</text>',
        '<text x="24" y="473" font-size="12">Цель: 1.5 мм; после FBX проверяется зазор ≥ 0.5 мм по всей 3D-подложке, не только на срезе.</text>', '</g></svg>']
    args.output.resolve().write_text('\n'.join(svg), encoding='utf-8')


if __name__ == '__main__': main()

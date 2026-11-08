"""Read-only FBX round-trip check of scalp fits, bindings, UV and materials.

blender -b --factory-startup -P Tools/Art/verify_hair_scalps.py -- --report <json>
Does not launch Unity or build content. Complements, never replaces, live QA.
"""
import argparse
import json
import re
import sys
from pathlib import Path

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

sys.path.insert(0, str(Path(__file__).resolve().parent))
from fit_hair_scalps import SOURCE, load, bones, rig_of, weights_at, skin_matrix, sha, MINIMUM, signed_distance


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    manifest = json.loads((SOURCE / 'source-manifest.json').read_text())
    bpy.ops.wm.read_factory_settings(use_empty=True)
    targets = {}
    for actor in ('Marta', 'Kshishtof'):
        objects = load(SOURCE / 'Models' / (actor + '.fbx'))
        mesh = next(o for o in objects if o.type == 'MESH' and '_LOD0' in o.name)
        mesh.data.calc_loop_triangles()
        tree = BVHTree.FromPolygons([mesh.matrix_world @ v.co for v in mesh.data.vertices],
            [list(t.vertices) for t in mesh.data.loop_triangles], all_triangles=True)
        targets[actor] = tree, bones(rig_of(mesh))
    reports = []; errors = []
    base_rows = [row for row in manifest['models'] if row['kind'] == 'hair']
    rows = base_rows + [fit for row in base_rows for fit in row.get('bodyFits', {}).values()]
    assert len(base_rows) == 32 and len(rows) == 64
    for row in rows:
        path = SOURCE / row['path']
        assert sha(path) == row['sha256'], row['id']
        bindings = json.loads((SOURCE / row['authoredBindings']).read_text())
        assert bindings['fbxSha256'] == row['sha256']
        objects = load(path)
        mesh = next(o for o in objects if o.type == 'MESH')
        assert mesh.name == bindings['model']
        expected_materials = row['meshes'][0]['materials']
        assert [re.sub(r'\.\d{3}$', '', m.name) for m in mesh.data.materials] == expected_materials
        authored = [dict(ws) for ws in bindings['weights']]
        tree = KDTree(len(bindings['positions']))
        for i, (x, y, z) in enumerate(bindings['positions']): tree.insert(Vector((-x, -z, y)), i)
        tree.balance()
        uv = [set() for _ in mesh.data.vertices]
        for loop in mesh.data.loops: uv[loop.vertex_index].add(tuple(mesh.data.uv_layers.active.data[loop.index].uv))
        weights = weights_at(mesh)
        max_error = 0
        for i, vertex in enumerate(mesh.data.vertices):
            candidates = tree.find_range(mesh.matrix_world @ vertex.co, .00002)
            binding_errors = []
            for _, index, _ in candidates:
                if not all(any((Vector(v) - Vector(w)).length < .00002 for w in bindings['uv'][index]) for v in uv[i]): continue
                wanted = authored[index]; actual = weights[i]
                binding_errors.append(max(abs(wanted.get(b, 0) - actual.get(b, 0)) for b in wanted.keys() | actual.keys()))
            assert binding_errors and min(binding_errors) < .0001, (row['id'], i, binding_errors)
            max_error = max(max_error, min(binding_errors))
        body_tree, target = targets[row['actor']]
        source = bones(rig_of(mesh))
        transforms = {b: target[b] @ source[b].inverted() @ mesh.matrix_world for b in source if b in target}
        points = [skin_matrix(ws, transforms) @ v.co for ws, v in zip(weights, mesh.data.vertices)]
        cap_slots = {i for i, key in enumerate(expected_materials)
            if manifest['materials'][key]['sourceSlot'].lower() in ('scalp', 'cap', 'base')}
        mesh.data.calc_loop_triangles()
        minimum = float('inf'); samples = 0
        for tri in mesh.data.loop_triangles:
            if tri.material_index not in cap_slots: continue
            a, b, c = [points[i] for i in tri.vertices]
            for point in (a, b, c, (a+b)*.5, (b+c)*.5, (c+a)*.5, (a+b+c)/3):
                signed, _, _ = signed_distance(body_tree, point)
                minimum = min(minimum, signed); samples += 1
        if minimum < MINIMUM - .00003: errors.append({'model': row['id'], 'minimumClearanceM': minimum})
        reports.append({'model': row['id'], 'actor': row['actor'], 'sha256': row['sha256'],
            'minimumClearanceM': minimum, 'maxBindingError': max_error,
            'samples': samples, 'importedTriangles': len(mesh.data.loop_triangles),
            'materialsVerified': len(expected_materials)})
        for obj in objects: bpy.data.objects.remove(obj, do_unlink=True)
        print('VERIFIED', row['id'], minimum, flush=True)
    args.report.resolve().write_text(json.dumps({'modelsVerified': len(reports), 'errors': errors,
        'unityValidated': False, 'publishedWebGLValidated': False, 'models': reports}, indent=2))
    assert not errors, errors


if __name__ == '__main__': main()

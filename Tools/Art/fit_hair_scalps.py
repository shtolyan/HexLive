"""Fit authored scalp shells against both People bodies/LODs; staging only.

blender -b --factory-startup -P Tools/Art/fit_hair_scalps.py -- --output <stage>
Strands, materials, UVs and the source files remain untouched. The exported
hash-pinned bindings follow the same Unity basis as export_hair_candidates.py.
No Editor, bundles or publication. Install only after inspecting the report.
"""
import argparse
import copy
import hashlib
import json
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'Assets/HexLiveContent/People/Source'
MARGIN = .0015
MINIMUM = .0005


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(path), use_anim=False)
    return set(bpy.data.objects) - before


def rig_of(mesh):
    return next(m.object for m in mesh.modifiers if m.type == 'ARMATURE')


def bones(rig):
    return {b.name: rig.matrix_world @ b.matrix_local for b in rig.data.bones}


def weights_at(mesh):
    return [{mesh.vertex_groups[g.group].name: g.weight for g in v.groups if g.weight > 0}
            for v in mesh.data.vertices]


def mix_weights(values):
    result = {}
    for weights in values:
        for name, weight in weights.items():
            result[name] = result.get(name, 0) + weight / len(values)
    result = dict(sorted(result.items(), key=lambda p: (-p[1], p[0]))[:4])
    total = sum(result.values())
    return {name: weight / total for name, weight in result.items()}


def skin_matrix(weights, transforms):
    result = Matrix(((0, 0, 0, 0),) * 4)
    for name, weight in weights.items():
        result += transforms[name] * weight
    return result


def signed_distance(tree, point):
    near, normal, _, distance = tree.find_nearest(point)
    # A nearest face normal is ambiguous at a faceted vertex/edge. Determine
    # volume membership by ray parity instead of treating that normal as a sign.
    votes = []
    for direction in (Vector((1, .137, .071)), Vector((-.219, 1, .093)), Vector((.163, .119, 1))):
        direction.normalize(); origin = point.copy(); count = 0
        for _ in range(128):
            hit, _, _, _ = tree.ray_cast(origin, direction, 10)
            if hit is None: break
            origin = hit + direction * .00005; count += 1
        else: raise RuntimeError('Body ray parity did not terminate')
        votes.append(count % 2)
        if len(votes) == 2 and votes[0] == votes[1]: break
    inside = sum(votes) > len(votes) / 2
    return (-distance if inside else distance), near, normal


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--styles', nargs='*')
    parser.add_argument('--source', type=Path, default=SOURCE)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    source = args.source.resolve()
    stage = args.output.resolve()
    assert not stage.is_relative_to(SOURCE), 'Staging must be outside the source tree'
    (stage / 'Models').mkdir(parents=True, exist_ok=True)
    manifest = json.loads((source / 'source-manifest.json').read_text())
    bpy.ops.wm.read_factory_settings(use_empty=True)
    targets = []
    for actor in ('Marta', 'Kshishtof'):
        row = next(m for m in manifest['models'] if m['kind'] == 'actor' and m['id'] == actor)
        path = source / row['path']
        assert sha(path) == row['sha256']
        objects = load(path)
        for mesh in sorted((o for o in objects if o.type == 'MESH' and '_LOD' in o.name), key=lambda o: o.name):
            mesh.data.calc_loop_triangles()
            tree = BVHTree.FromPolygons([mesh.matrix_world @ v.co for v in mesh.data.vertices],
                [list(t.vertices) for t in mesh.data.loop_triangles], all_triangles=True)
            targets.append((mesh.name, tree, bones(rig_of(mesh))))
    assert len(targets) == 2, [t[0] for t in targets]
    export = {'schemaVersion': 1, 'sourceBlendSha256': manifest['hairSource']['sourceBlendSha256'],
        'models': [], 'materials': manifest['materials'], 'textures': {},
        'unityValidated': False, 'bundleBuildAuthorized': False}
    reports = []
    jobs = [(row, actor) for row in manifest['models'] if row['kind'] == 'hair'
            for actor in ('Marta', 'Kshishtof')]
    fitted_models = {}
    for row, actor in jobs:
        if row['kind'] != 'hair' or (args.styles and row['id'].rsplit('_LOD', 1)[0] not in args.styles):
            continue
        path = source / row['path']
        assert sha(path) == row['sha256']
        objects = load(path)
        mesh = next(o for o in objects if o.type == 'MESH')
        model_id = row['id'] if actor == 'Marta' else row['id'].replace('_LOD', '_Kshishtof_LOD')
        mesh.name = model_id
        rig = rig_of(mesh)
        old = mesh.data
        old.calc_loop_triangles()
        positions = [v.co.copy() for v in old.vertices]
        original_positions = positions.copy()
        weights = weights_at(mesh)
        faces = []
        scalp_slots = {i for i, key in enumerate(row['meshes'][0]['materials'])
            if manifest['materials'][key]['sourceSlot'].lower() in ('scalp', 'base', 'cap')}
        assert scalp_slots, row['id']
        for tri in old.loop_triangles:
            faces.append((tuple(tri.vertices),
                tuple(old.uv_layers.active.data[i].uv.copy() for i in tri.loops),
                tri.material_index,
                tuple(old.corner_normals[i].vector.copy() for i in tri.loops)))
        cap_before = sum(f[2] in scalp_slots for f in faces)
        # Subdivide only scalp triangles. Preserve each corner's original UV
        # interpolation, including seams; the authored hairline is not enlarged.
        for _ in range(2):
            edges = {}
            def midpoint(a, b):
                key = tuple(sorted((a, b)))
                if key not in edges:
                    edges[key] = len(positions)
                    positions.append((positions[a] + positions[b]) * .5)
                    original_positions.append((original_positions[a] + original_positions[b]) * .5)
                    weights.append(mix_weights([weights[a], weights[b]]))
                return edges[key]
            divided = []
            for indices, uv, slot, normals in faces:
                if slot not in scalp_slots:
                    divided.append((indices, uv, slot, normals)); continue
                a, b, c = indices
                ab, bc, ca = midpoint(a, b), midpoint(b, c), midpoint(c, a)
                u, v, w = uv; uvab, uvbc, uvca = (u + v) * .5, (v + w) * .5, (w + u) * .5
                for ids, coords in [((a, ab, ca), (u, uvab, uvca)), ((ab, b, bc), (uvab, v, uvbc)),
                                    ((ca, bc, c), (uvca, uvbc, w)), ((ab, bc, ca), (uvab, uvbc, uvca))]:
                    divided.append((ids, coords, slot, None))
            faces = divided
        cap_faces = [f for f in faces if f[2] in scalp_slots]
        cap_vertices = sorted({i for f in cap_faces for i in f[0]})
        source_bones = bones(rig)
        mappings = [(name, tree, {b: target[b] @ source_bones[b].inverted() @ mesh.matrix_world
                    for b in source_bones if b in target}) for name, tree, target in targets
                    if name.startswith(actor + '_')]
        # Include triangle interiors, not just vertices: a coarse shell can
        # have every vertex outside yet cut through a convex head between them.
        samples = [(i,) for i in cap_vertices]
        for f in cap_faces:
            ids = f[0]
            for group in (ids, (ids[0], ids[1]), (ids[1], ids[2]), (ids[2], ids[0])):
                samples.append(group)
        before_min = {}
        sampled_mappings = []
        for name, tree, transforms in mappings:
            matrices = {i: skin_matrix(weights[i], transforms) for i in cap_vertices}
            inverses = {i: m.to_3x3().inverted() for i, m in matrices.items()}
            sampled_mappings.append((name, tree, matrices, inverses))
            minimum = float('inf')
            for ids in samples:
                point = sum((matrices[i] @ positions[i] for i in ids), Vector()) / len(ids)
                signed, _, _ = signed_distance(tree, point)
                minimum = min(minimum, signed)
            before_min[name] = minimum
        for iteration in range(24):
            adjusted = 0
            for name, tree, matrices, inverses in sampled_mappings:
                for ids in samples:
                    point = sum((matrices[i] @ positions[i] for i in ids), Vector()) / len(ids)
                    signed, near, normal = signed_distance(tree, point)
                    if signed >= MINIMUM: continue
                    desired = near + normal * MARGIN
                    delta = desired - point
                    for i in ids: positions[i] += inverses[i] @ delta
                    adjusted += 1
            if adjusted == 0: break
        else: raise RuntimeError(row['id'] + ': scalp fitting did not converge')
        maximum = max((mesh.matrix_world.to_3x3() @ (p - q)).length
                      for p, q in zip(positions, original_positions))
        assert maximum < .03, (row['id'], 'displacement exceeds 3 cm', maximum)
        after_min = {}
        for name, tree, matrices, _ in sampled_mappings:
            minimum = float('inf')
            for ids in samples:
                p = sum((matrices[i] @ positions[i] for i in ids), Vector()) / len(ids)
                signed, _, _ = signed_distance(tree, p)
                minimum = min(minimum, signed)
            assert minimum >= MINIMUM - 1e-7, (row['id'], name, minimum)
            after_min[name] = minimum
        fitted = bpy.data.meshes.new(old.name + '_ScalpFit')
        fitted.from_pydata(positions, [], [f[0] for f in faces]); fitted.update()
        uv = fitted.uv_layers.new(name='SourceUV')
        for material in old.materials: fitted.materials.append(material)
        for poly, (_, coords, slot, _) in zip(fitted.polygons, faces):
            poly.material_index = slot; poly.use_smooth = True
            for i, coord in zip(poly.loop_indices, coords): uv.data[i].uv = coord
        fitted.update()
        normals = [fitted.corner_normals[i].vector.copy() for i in range(len(fitted.loops))]
        for poly, face in zip(fitted.polygons, faces):
            if face[3] is not None:
                for i, normal in zip(poly.loop_indices, face[3]): normals[i] = normal
        fitted.normals_split_custom_set(normals)
        mesh.data = fitted
        for name in {name for ws in weights for name in ws}:
            if mesh.vertex_groups.get(name) is None: mesh.vertex_groups.new(name=name)
        for i, ws in enumerate(weights):
            for name, weight in ws.items(): mesh.vertex_groups[name].add([i], weight, 'REPLACE')
        bpy.ops.object.select_all(action='DESELECT')
        rig.select_set(True); mesh.select_set(True); bpy.context.view_layer.objects.active = rig
        target = stage / 'Models' / (model_id + '.fbx')
        bpy.ops.export_scene.fbx(filepath=str(target), use_selection=True, object_types={'MESH','ARMATURE'},
            global_scale=1, apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
            use_space_transform=True, bake_space_transform=False, use_mesh_modifiers=False,
            mesh_smooth_type='OFF', add_leaf_bones=False, use_armature_deform_only=False,
            armature_nodetype='NULL', bake_anim=False, path_mode='STRIP', axis_forward='-Z', axis_up='Y')
        uvs = [set() for _ in positions]
        for ids, coords, _, _ in faces:
            for i, coord in zip(ids, coords): uvs[i].add(tuple(coord))
        bindings = {'schemaVersion': 1, 'model': model_id, 'fbxSha256': sha(target),
            'scalpFitSourceSha256': row['sha256'],
            'positions': [[-p.x, p.z, -p.y] for v in positions for p in [mesh.matrix_world @ v]],
            'uv': [sorted(values) for values in uvs], 'weights': [sorted(ws.items()) for ws in weights]}
        target.with_suffix('.hair-bindings.json').write_text(json.dumps(bindings, separators=(',', ':')))
        result = copy.deepcopy(row)
        result.update(id=model_id, actor=actor, path='Models/' + target.name,
            authoredBindings='Models/' + target.with_suffix('.hair-bindings.json').name,
            bytes=target.stat().st_size, sha256=sha(target), scalpFitSourceSha256=row['sha256'])
        result['meshes'][0].update(name=model_id, vertices=len(positions), triangles=len(faces))
        if actor == 'Marta':
            fitted_models[row['id']] = result
        else:
            fitted_models[row['id']]['bodyFits'] = {actor: result}
        report = {'style': row['id'], 'actor': actor, 'sourceSha256': row['sha256'], 'outputSha256': sha(target),
            'capTrianglesBefore': cap_before, 'capTrianglesAfter': len(cap_faces),
            'trianglesBefore': len(old.polygons), 'trianglesAfter': len(faces),
            'maxDisplacementM': maximum, 'minimumBeforeM': before_min, 'minimumAfterM': after_min}
        reports.append(report); print(json.dumps(report), flush=True)
        for obj in objects: bpy.data.objects.remove(obj, do_unlink=True)
    export['models'] = list(fitted_models.values())
    (stage / 'hair-source-manifest.json').write_text(json.dumps(export, indent=2))
    (stage / 'scalp-fit-report.json').write_text(json.dumps({'marginM': MARGIN, 'minimumM': MINIMUM,
        'unityValidated': False, 'models': reports}, indent=2))


if __name__ == '__main__': main()

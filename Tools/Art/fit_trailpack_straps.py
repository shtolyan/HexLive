"""Fit only PrimalTrailPack straps to the shipped body FBXs (§169).

Run with Blender --background --factory-startup --python THIS --
    --source-dir Assets/HexLiveContent/People/Source/Models --output-dir STAGING
    --receipt STAGING/trailpack-fit.json
Never saves the reference blend, changes bodies/materials or builds bundles.
Use the original pre-fit exports as input; re-subdivision of a fitted mesh fails.
"""
import argparse
import hashlib
import json
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def components(mesh):
    links = [[] for _ in mesh.vertices]
    for edge in mesh.edges:
        a, b = edge.vertices
        links[a].append(b)
        links[b].append(a)
    seen, result = set(), []
    for vertex in mesh.vertices:
        if vertex.index in seen:
            continue
        pending, ids = [vertex.index], []
        seen.add(vertex.index)
        while pending:
            i = pending.pop()
            ids.append(i)
            for j in links[i]:
                if j not in seen:
                    seen.add(j)
                    pending.append(j)
        result.append(ids)
    return result


def straps(obj):
    points = [obj.matrix_world @ v.co for v in obj.data.vertices]
    groups = []
    for ids in components(obj.data):
        bounds = [(min(points[i][k] for i in ids), max(points[i][k] for i in ids)) for k in range(3)]
        # The two shoulder ribbons extend from the front waist over the shoulder.
        if bounds[1][0] < -.03 and bounds[2][1] - bounds[2][0] > .25:
            groups.append(ids)
    assert len(groups) == 2, "Expected exactly the two separate shoulder ribbons"
    return set(i for group in groups for i in group)


def import_model(path):
    previous = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(path), use_anim=False)
    objects = set(bpy.data.objects) - previous
    mesh = max((o for o in objects if o.type == 'MESH'), key=lambda o: len(o.data.vertices))
    rig = next(m.object for m in mesh.modifiers if m.type == 'ARMATURE')
    rig.data.pose_position = 'REST'
    return mesh, rig


def weights_at(points, vertex_weights, triangle, point):
    a, b, c = [points[i] for i in triangle]
    v0, v1, v2 = b-a, c-a, point-a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00*d11-d01*d01
    if abs(den) < 1e-14:
        factors = [1, 0, 0]
    else:
        v, w = (d11*d20-d01*d21)/den, (d00*d21-d01*d20)/den
        factors = [max(0, 1-v-w), max(0, v), max(0, w)]
    result = {}
    for i, factor in zip(triangle, factors):
        for name, value in vertex_weights[i].items():
            result[name] = result.get(name, 0) + value*factor
    result = {name: value for name, value in result.items() if value > 1e-5}
    total = sum(result.values())
    assert total > 0
    return {name: value/total for name, value in result.items()}


def add_matching_bones(rig, body_rig, needed):
    required = set(needed)
    for name in list(required):
        bone = body_rig.data.bones.get(name)
        assert bone is not None, name
        while bone.parent:
            bone = bone.parent
            required.add(bone.name)
    missing = required-set(rig.data.bones.keys())
    bpy.ops.object.select_all(action='DESELECT')
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='EDIT')
    basis = rig.matrix_world.inverted() @ body_rig.matrix_world
    try:
        for name in sorted(missing):
            source = body_rig.data.bones[name]
            bone = rig.data.edit_bones.new(name)
            bone.head = basis @ source.head_local
            bone.tail = basis @ source.tail_local
            bone.align_roll((basis.to_3x3() @ source.matrix_local.to_3x3().col[2]).normalized())
            bone.use_deform = source.use_deform
        for name in missing:
            source = body_rig.data.bones[name]
            if source.parent:
                rig.data.edit_bones[name].parent = rig.data.edit_bones[source.parent.name]
    finally:
        bpy.ops.object.mode_set(mode='OBJECT')
    return sorted(missing)


def fit(actor, args):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    body_path = (args.body_dir or args.source_dir)/(actor+'.fbx')
    pack_path = args.source_dir/(actor+'_PrimalTrailPack.fbx')
    body, body_rig = import_model(body_path)
    pack, rig = import_model(pack_path)
    assert not any(g.name in ('lPectoral', 'rPectoral') for g in pack.vertex_groups), "Use an original pre-fit export"
    ids = straps(pack)
    assert len(ids) == 72, "Unexpected original strap topology"
    original_vertices = len(pack.data.vertices)
    untouched = [tuple(v.co) for v in pack.data.vertices if v.index not in ids]
    body.data.calc_loop_triangles()
    triangles = [tuple(t.vertices) for t in body.data.loop_triangles]
    points = [body.matrix_world @ v.co for v in body.data.vertices]
    vertex_weights = [{body.vertex_groups[g.group].name:g.weight for g in v.groups} for v in body.data.vertices]
    tree = BVHTree.FromPolygons(points, triangles, all_triangles=True)
    bm = bmesh.new()
    try:
        bm.from_mesh(pack.data)
        bm.verts.ensure_lookup_table()
        edges = [e for e in bm.edges if all(v.index in ids for v in e.verts)]
        bmesh.ops.subdivide_edges(bm, edges=edges, cuts=3, use_grid_fill=True)
        bm.to_mesh(pack.data)
    finally:
        bm.free()
    ids = straps(pack)
    assert [tuple(v.co) for v in pack.data.vertices if v.index not in ids] == untouched
    inverse = pack.matrix_world.inverted()
    fitted, weights, max_shift = 0, {}, 0
    for i in sorted(ids):
        vertex = pack.data.vertices[i]
        point = pack.matrix_world @ vertex.co
        surface, normal, face, distance = tree.find_nearest(point)
        assert surface is not None
        signed = (point-surface).dot(normal)
        # Fit front/shoulder contact, keep the existing rear anchors and bag silhouette.
        if point.y < .02 and signed < args.clearance:
            target = surface + normal*args.clearance
            shift = (target-point).length
            assert shift < .08, (actor, i, shift)
            max_shift = max(max_shift, shift)
            fitted += 1
        else:
            target = point
        weights[i] = weights_at(points, vertex_weights, triangles[face], surface)
        vertex.co = inverse @ target
    added = add_matching_bones(rig, body_rig, {name for row in weights.values() for name in row})
    for name in {name for row in weights.values() for name in row}:
        if pack.vertex_groups.get(name) is None:
            pack.vertex_groups.new(name=name)
    for group in pack.vertex_groups:
        group.remove(sorted(ids))
    for i, row in weights.items():
        for name, value in row.items():
            pack.vertex_groups[name].add([i], value, 'REPLACE')
    bpy.ops.object.select_all(action='DESELECT')
    rig.select_set(True)
    pack.select_set(True)
    bpy.context.view_layer.objects.active = rig
    args.output_dir.mkdir(parents=True, exist_ok=True)
    output = args.output_dir/pack_path.name
    bpy.ops.export_scene.fbx(filepath=str(output), use_selection=True,
        object_types={'MESH','ARMATURE'}, global_scale=1, apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_UNITS', use_space_transform=True,
        bake_space_transform=False, use_mesh_modifiers=False, mesh_smooth_type='OFF',
        add_leaf_bones=False, use_armature_deform_only=False, armature_nodetype='NULL',
        bake_anim=False, path_mode='STRIP', axis_forward='-Z', axis_up='Y')
    pack.data.calc_loop_triangles()
    return {'actor':actor, 'inputSha256':sha(pack_path), 'bodySha256':sha(body_path),
        'output':output.name, 'outputSha256':sha(output), 'bytes':output.stat().st_size,
        'originalVertices':original_vertices, 'vertices':len(pack.data.vertices),
        'triangles':len(pack.data.loop_triangles), 'strapVertices':len(ids),
        'fittedVertices':fitted, 'maxShiftMetres':max_shift, 'clearanceMetres':args.clearance,
        'bodyUnchanged':True, 'bagVerticesUnchanged':True, 'addedBones':added,
        'bones':list(rig.data.bones.keys()), 'maxBoneWeights':max(len(v.groups) for v in pack.data.vertices)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-dir', type=Path, required=True)
    parser.add_argument('--body-dir', type=Path, help='Use current bodies when pre-fit packs are staged separately')
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--receipt', type=Path, required=True)
    parser.add_argument('--clearance', type=float, default=.020)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    assert .005 <= args.clearance <= .025
    assert args.source_dir.resolve() != args.output_dir.resolve(), "Stage before import/verification"
    rows = [fit(actor,args) for actor in ['Marta','Kshishtof']]
    args.receipt.write_text(json.dumps({'schemaVersion':1,'tool':'Tools/Art/fit_trailpack_straps.py','models':rows},indent=2)+'\n',encoding='utf-8')
    print(json.dumps(rows,indent=2))


if __name__ == '__main__':
    main()

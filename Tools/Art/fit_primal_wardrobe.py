"""#476: stage two genuinely paired sleeves and a male chest wrap from shipped FBXs.

Blender --background --factory-startup --python-exit-code 1 --python THIS -- --source-dir DIR
    --output-dir STAGING --receipt STAGING/fit.json
No blend, body, texture or runtime transform is modified. Use original single sleeves.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

sys.path.insert(0, str(Path(__file__).resolve().parent))
from fit_trailpack_straps import add_matching_bones, import_model


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def export(mesh, rig, path):
    bpy.ops.object.select_all(action='DESELECT')
    for obj in [mesh, rig]:
        obj.hide_set(False)
        obj.select_set(True)
        obj.animation_data_clear()
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(filepath=str(path), use_selection=True,
        object_types={'MESH', 'ARMATURE'}, global_scale=1, apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_UNITS', use_space_transform=True,
        bake_space_transform=False, use_mesh_modifiers=False, mesh_smooth_type='OFF',
        add_leaf_bones=False, use_armature_deform_only=False, armature_nodetype='NULL',
        bake_anim=False, path_mode='STRIP', axis_forward='-Z', axis_up='Y')
    mesh.data.calc_loop_triangles()
    return dict(output=path.name, sha256=digest(path), bytes=path.stat().st_size,
                vertices=len(mesh.data.vertices), triangles=len(mesh.data.loop_triangles),
                bones=list(rig.data.bones.keys()),
                maxBoneWeights=max(len(v.groups) for v in mesh.data.vertices))


def pair_sleeves(actor, args):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    body, body_rig = import_model(args.source_dir / (actor + '.fbx'))
    path = args.source_dir / (actor + '_PrimalArmWraps.fbx')
    mesh, rig = import_model(path)
    old = mesh.data
    points = [mesh.matrix_world @ v.co for v in old.vertices]
    assert min(p.x for p in points) > .25, 'Use original one-sided source, not an already paired export'
    weights = [{mesh.vertex_groups[g.group].name:g.weight for g in v.groups} for v in old.vertices]
    mirror_name = lambda n: 'r' + n[1:] if n.startswith('l') else 'l' + n[1:] if n.startswith('r') else n
    required = {mirror_name(n) for w in weights for n in w}
    add_matching_bones(rig, body_rig, required)
    mirror = Matrix.Diagonal((-1, 1, 1, 1))
    reflected = []
    for point, skin in zip(points, weights):
        adjustment = Vector()
        for name, weight in skin.items():
            left = body_rig.matrix_world @ body_rig.data.bones[name].head_local
            right = body_rig.matrix_world @ body_rig.data.bones[mirror_name(name)].head_local
            adjustment += (right - mirror @ left) * weight
        reflected.append(mesh.matrix_world.inverted() @ (mirror @ point + adjustment))
    count = len(old.vertices)
    faces = [list(p.vertices) for p in old.polygons]
    paired = bpy.data.meshes.new(actor + '_PrimalArmWraps')
    paired.from_pydata([tuple(v.co) for v in old.vertices] + reflected, [],
                      faces + [[count+i for i in reversed(f)] for f in faces])
    for material in old.materials:
        paired.materials.append(material)
    uv = paired.uv_layers.new(name='UVMap')
    for i, face in enumerate(old.polygons):
        values = [old.uv_layers.active.data[k].uv.copy() for k in face.loop_indices]
        for target, coords in [(paired.polygons[i], values),
                               (paired.polygons[i+len(faces)], list(reversed(values)))]:
            target.material_index = face.material_index
            target.use_smooth = face.use_smooth
            for k, value in zip(target.loop_indices, coords):
                uv.data[k].uv = value
    mesh.data = paired
    for i, skin in enumerate(weights):
        for name, value in skin.items():
            original = mesh.vertex_groups.get(name) or mesh.vertex_groups.new(name=name)
            original.add([i], value, 'REPLACE')
            opposite = mirror_name(name)
            group = mesh.vertex_groups.get(opposite) or mesh.vertex_groups.new(name=opposite)
            group.add([count+i], value, 'REPLACE')
    paired.update()
    return dict(actor=actor, id='PrimalArmWraps', inputSha256=digest(path),
                originalVertices=count, **export(mesh, rig, args.output_dir/path.name))


def male_top(args):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    female, female_rig = import_model(args.source_dir/'Marta.fbx')
    male, male_rig = import_model(args.source_dir/'Kshishtof.fbx')
    top, source_rig = import_model(args.source_dir/'Marta_PrimalTop.fbx')
    male.data.calc_loop_triangles()
    tree = BVHTree.FromPolygons([male.matrix_world@v.co for v in male.data.vertices],
                               [tuple(t.vertices) for t in male.data.loop_triangles], all_triangles=True)
    names = {top.vertex_groups[g.group].name for v in top.data.vertices for g in v.groups}
    rig = male_rig.copy()
    rig.data = male_rig.data.copy()
    rig.animation_data_clear()
    bpy.context.collection.objects.link(rig)
    required = set(names)
    for name in names:
        parent = rig.data.bones[name].parent
        while parent:
            required.add(parent.name)
            parent = parent.parent
    bpy.ops.object.select_all(action='DESELECT')
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='EDIT')
    for bone in list(rig.data.edit_bones):
        if bone.name not in required:
            rig.data.edit_bones.remove(bone)
    bpy.ops.object.mode_set(mode='OBJECT')
    remap = {name:(male_rig.matrix_world @ male_rig.data.bones[name].matrix_local @
                   (female_rig.matrix_world @ female_rig.data.bones[name].matrix_local).inverted()) for name in names}
    inverse = top.matrix_world.inverted()
    for vertex in top.data.vertices:
        source = top.matrix_world @ vertex.co
        point = sum((remap[top.vertex_groups[g.group].name] @ source * g.weight for g in vertex.groups), Vector())
        surface, normal, _, distance = tree.find_nearest(point)
        # Flatten the chest wrap to the male torso while preserving loose ties/fringe.
        if distance < .10:
            point = surface + normal * .006
        elif (point-surface).dot(normal) < .006:
            point += normal * (.006-(point-surface).dot(normal))
        vertex.co = inverse @ point
    world = top.matrix_world.copy()
    top.parent = rig
    top.matrix_world = world
    for modifier in top.modifiers:
        if modifier.type == 'ARMATURE': modifier.object = rig
    top.name = top.data.name = 'Kshishtof_PrimalTop'
    return dict(actor='Kshishtof', id='PrimalTop',
                inputSha256=digest(args.source_dir/'Marta_PrimalTop.fbx'),
                **export(top, rig, args.output_dir/'Kshishtof_PrimalTop.fbx'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-dir', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--receipt', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    assert args.source_dir.resolve() != args.output_dir.resolve()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    bodies = {name:digest(args.source_dir/(name+'.fbx')) for name in ['Marta', 'Kshishtof']}
    rows = [pair_sleeves(actor, args) for actor in bodies] + [male_top(args)]
    assert bodies == {name:digest(args.source_dir/(name+'.fbx')) for name in bodies}
    args.receipt.write_text(json.dumps(dict(models=rows, bodiesUnchanged=True), indent=2)+'\n')
    print('PAIRED_WARDROBE_STAGED', len(rows))


if __name__ == '__main__':
    main()

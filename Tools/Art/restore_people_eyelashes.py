"""Extract authored lash cards, original UVs, weights and nonzero morphs.

Blender CLI --python-exit-code 1 --python this.py -- --output <staging>
No body geometry or artist source is edited. Unity binds the output to its
existing skeleton; the original grayscale opacity map is prepared separately.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import bpy

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    options = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    rows = []
    for actor, relative in [('Marta', 'Martanaked/Martanaked.fbx'),
                            ('Kshishtof', 'Kshishtof/Kshishtof.fbx')]:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        path = ROOT / 'Assets/ImportedActors/Daz3D' / relative
        bpy.ops.import_scene.fbx(filepath=str(path), use_anim=False, use_image_search=False)
        body = max((o for o in bpy.data.objects if o.type == 'MESH'), key=lambda o: len(o.data.vertices))
        slot = next(i for i, s in enumerate(body.material_slots) if s.material.name.split('.')[0] == 'Eyelashes')
        faces = [p for p in body.data.polygons if p.material_index == slot]
        assert len(faces) == 252, 'Unexpected source lash topology'
        ids = sorted({v for p in faces for v in p.vertices})
        mapping = {old: new for new, old in enumerate(ids)}
        mesh = bpy.data.meshes.new(actor + '_Eyelashes')
        mesh.from_pydata([body.data.vertices[i].co[:] for i in ids], [],
                         [[mapping[i] for i in p.vertices] for p in faces])
        uv = mesh.uv_layers.new(name='SourceUV')
        for face, old in zip(mesh.polygons, faces):
            face.use_smooth = old.use_smooth
            for loop, old_loop in zip(face.loop_indices, old.loop_indices):
                uv.data[loop].uv = body.data.uv_layers.active.data[old_loop].uv
        mesh.normals_split_custom_set([body.data.corner_normals[loop].vector[:]
                                       for face in faces for loop in face.loop_indices])
        lashes = bpy.data.objects.new(actor + '_Eyelashes', mesh)
        bpy.context.collection.objects.link(lashes)
        # Keep the source armature parent: an unparented skinned FBX mesh folds
        # the centimetre basis into its vertices and no longer matches the body.
        lashes.parent = body.parent
        lashes.matrix_world = body.matrix_world.copy()
        for group in body.vertex_groups:
            new = lashes.vertex_groups.new(name=group.name)
            for old in ids:
                weights = [w.weight for w in body.data.vertices[old].groups if w.group == group.index]
                if weights: new.add([mapping[old]], weights[0], 'REPLACE')
        lashes.shape_key_add(name='Basis')
        basis = body.data.shape_keys.key_blocks[0]
        for source in list(body.data.shape_keys.key_blocks)[1:]:
            if max((source.data[i].co - basis.data[i].co).length for i in ids) < 1e-7:
                continue
            key = lashes.shape_key_add(name=source.name)
            for old in ids: key.data[mapping[old]].co = source.data[old].co
        rig = body.modifiers[0].object
        mod = lashes.modifiers.new('Authored skin weights', 'ARMATURE'); mod.object = rig
        bpy.ops.object.select_all(action='DESELECT')
        lashes.select_set(True); rig.select_set(True); bpy.context.view_layer.objects.active = lashes
        target = output / (actor + '_Eyelashes.fbx')
        bpy.ops.export_scene.fbx(filepath=str(target), use_selection=True, object_types={'MESH', 'ARMATURE'},
                                 add_leaf_bones=False, bake_anim=False, axis_forward='-Z', axis_up='Y',
                                 use_mesh_modifiers=False)
        rows.append({'actor': actor, 'source': path.relative_to(ROOT).as_posix(),
                     'sourceSha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                     'file': target.name, 'sha256': hashlib.sha256(target.read_bytes()).hexdigest(),
                     'vertices': len(ids), 'faces': len(faces), 'morphs': len(mesh.shape_keys.key_blocks)-1})
    (output / 'eyelashes.json').write_text(json.dumps(rows, indent=2) + '\n')
    print(json.dumps(rows, indent=2), flush=True)


if __name__ == '__main__':
    main()

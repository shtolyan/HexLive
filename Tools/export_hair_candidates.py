"""Blender CLI exporter for reviewed source-UV hair candidates; never builds content.

Run with --background --disable-autoexec <verified blend> --python this.py --
--inputs <selected-integration-inputs.json> --project <checkout> --output <staging>.
The input manifest pins geometry and original textures. Existing scene data is
read only; the process creates temporary rigs/meshes and does not save the blend.
"""
import argparse
import hashlib
import json
import re
import shutil
import sys
from pathlib import Path

import bpy
from mathutils import Matrix
sys.path.insert(0, str(Path(__file__).resolve().parent))
from hair_opacity_sources import add_opacity_source, apply_source_cutoff


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inputs', type=Path, required=True)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    manifest = json.loads(args.inputs.read_text())
    assert digest(Path(bpy.data.filepath)) == manifest['sourceBlendSha256'], 'Source blend changed'
    assert len(manifest['models']) == 32
    output = args.output.resolve()
    (output/'Models').mkdir(parents=True, exist_ok=True)
    (output/'Textures').mkdir(exist_ok=True)
    source_rig = bpy.data.objects['Marta_Rig']
    collection = bpy.data.collections.new('HairSourceUVExport')
    bpy.context.scene.collection.children.link(collection)
    result = {'schemaVersion': 1, 'state': 'source-exported-awaiting-unity-validation',
              'unityValidated': False, 'bundleBuildAuthorized': False,
              'sourceBlendSha256': manifest['sourceBlendSha256'],
              'models': [], 'materials': {}, 'textures': {}, 'errors': []}
    for style in sorted({row['style'] for row in manifest['models']}):
        rows = sorted((r for r in manifest['models'] if r['style'] == style), key=lambda r:r['lod'])
        assert [r['lod'] for r in rows] == [0, 1]
        inputs = []
        for row in rows:
            assert re.fullmatch(r'[A-Za-z0-9_]+', row['candidate'])
            path = args.inputs.parent/(row['candidate']+'.json')
            assert digest(path) == row['sha256'], path
            inputs.append(json.loads(path.read_text()))
            for tex in row['textures']:
                assert digest(args.project/tex['path']) == tex['sha256'], tex['path']
        used = {name for data in inputs for weights in data['weights'] for name, weight in weights if weight > 0}
        assert used <= set(source_rig.data.bones.keys()), used - set(source_rig.data.bones.keys())
        keep = set(used)
        for name in used:
            bone = source_rig.data.bones[name].parent
            while bone:
                keep.add(bone.name)
                bone = bone.parent
        rig_name = style+'_HairSkeleton'
        if rig_name in bpy.data.objects:
            bpy.data.objects[rig_name].name = 'Reference_'+rig_name
        rig = source_rig.copy()
        rig.data = source_rig.data.copy()
        rig.name = rig_name
        rig.animation_data_clear()
        collection.objects.link(rig)
        rig.hide_viewport = False
        rig.hide_select = False
        rig.hide_set(False)
        rig.data.pose_position = 'REST'
        world = source_rig.matrix_world.copy()
        world.translation.x = 0
        rig.matrix_world = world
        bpy.ops.object.select_all(action='DESELECT')
        rig.select_set(True)
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        for bone in list(rig.data.edit_bones):
            if bone.name not in keep:
                rig.data.edit_bones.remove(bone)
        bpy.ops.object.mode_set(mode='OBJECT')
        for row, data in zip(rows, inputs):
            name = style+'_LOD'+str(row['lod'])
            if name in bpy.data.objects:
                bpy.data.objects[name].name = 'Reference_'+name
            mesh = bpy.data.meshes.new(name)
            # FBX consumers can enumerate materials by first polygon use. Keep
            # that order identical to the declared slots and source manifest.
            faces = sorted(data['faces'], key=lambda face: face[3])
            used_slots = sorted({face[3] for face in data['faces']})
            slot_remap = {old: new for new, old in enumerate(used_slots)}
            mesh.from_pydata(data['vertices'], [], [face[0] for face in faces])
            mesh.update()
            uv = mesh.uv_layers.new(name='SourceUV')
            normals = []
            for polygon, face in zip(mesh.polygons, faces):
                polygon.material_index = slot_remap[face[3]]
                polygon.use_smooth = True
                for loop, value, normal in zip(polygon.loop_indices, face[1], face[2]):
                    uv.data[loop].uv = value
                    normals.append(normal)
            mesh.normals_split_custom_set(normals)
            material_names = []
            for slot, config in enumerate(data['materials']):
                if slot not in slot_remap:
                    continue
                key = style+'_SourceUV_'+str(slot)
                material_names.append(key)
                mat = bpy.data.materials.get(key) or bpy.data.materials.new(key)
                mesh.materials.append(mat)
                images = []
                if config.get('texture'):
                    source = args.project/config['texture']
                    sha = digest(source)
                    relative = 'Textures/hair_'+sha[:20]+source.suffix.lower()
                    target = output/relative
                    if not target.exists():
                        shutil.copyfile(source, target)
                    assert digest(target) == sha
                    result['textures'][key] = {'path': relative, 'sha256': sha, 'sourcePath': config['texture'], 'colorSpace': 'sRGB'}
                    images = [{'node': 'Base', 'image': relative,
                        'links': [{'output':'Color', 'toNode':'Principled', 'toSocket':'Base Color'},
                                  {'output':'Alpha', 'toNode':'Principled', 'toSocket':'Alpha'}]}]
                result['materials'][key] = {'name':key, 'sourceSlot':config['name'],
                    'authoredHair': True, 'doubleSided':True, 'alphaMode':'CLIP',
                    'alphaCutoff': config.get('alphaCutoff', .12 if style=='BendineHair' else .08 if style=='ChunkyHair' else .42),
                    'baseColor':[config['color'][k] for k in ('r','g','b')]+[1],
                    'uvScale':[config['uv_scale'][k] for k in ('x','y')],
                    'uvOffset':[config['uv_offset'][k] for k in ('x','y')],
                    'roughness':.92, 'metallic':0, 'variants':['Base'], 'images':images}
                apply_source_cutoff(args.project.resolve(), config, result['materials'][key])
                add_opacity_source(args.project.resolve(), output, config, result['materials'][key], result['textures'], key)
            ob = bpy.data.objects.new(name, mesh)
            collection.objects.link(ob)
            ob.parent = rig
            ob.matrix_parent_inverse = Matrix.Identity(4)
            ob.matrix_basis = Matrix.Identity(4)
            for vertex, weights in enumerate(data['weights']):
                assert 0 < len(weights) <= 4 and abs(sum(w for _,w in weights)-1) < 1e-4
                for bone, weight in weights:
                    group = ob.vertex_groups.get(bone) or ob.vertex_groups.new(name=bone)
                    group.add([vertex], weight, 'REPLACE')
            ob.modifiers.new('Armature', 'ARMATURE').object = rig
            bpy.ops.object.select_all(action='DESELECT')
            rig.select_set(True)
            ob.select_set(True)
            bpy.context.view_layer.objects.active = rig
            bpy.context.view_layer.update()
            target = output/'Models'/(name+'.fbx')
            bpy.ops.export_scene.fbx(filepath=str(target), use_selection=True,
                object_types={'MESH','ARMATURE'}, global_scale=1,
                apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
                use_space_transform=True, bake_space_transform=False,
                use_mesh_modifiers=False, mesh_smooth_type='OFF', add_leaf_bones=False,
                use_armature_deform_only=False, armature_nodetype='NULL', bake_anim=False,
                path_mode='STRIP', axis_forward='-Z', axis_up='Y')
            result['models'].append({'kind':'hair', 'id':name, 'actor':'Marta',
                'path':'Models/'+target.name, 'bytes':target.stat().st_size,
                'sha256':digest(target), 'candidateSha256':row['sha256'],
                'bones':[b.name for b in rig.data.bones], 'meshes':[{
                    'name':name, 'vertices':len(mesh.vertices), 'triangles':len(mesh.polygons),
                    'blendShapes':[], 'materials':material_names, 'uvLayers':['SourceUV'],
                    'maxBoneWeights':max(map(len,data['weights']))}]})
            # Unity's FBX importer can discard small influences even at a zero
            # minimum. Persist authored bindings for the hair-only postprocessor.
            vertex_uvs = [set() for _ in data['vertices']]
            for face in data['faces']:
                for vertex, value in zip(face[0], face[1]):
                    vertex_uvs[vertex].add(tuple(value))
            bindings = {'schemaVersion': 1, 'model': name,
                'fbxSha256': digest(target), 'candidateSha256': row['sha256'],
                'positions': [[-x*.01,y*.01,z*.01] for x,y,z in data['vertices']],
                'uv': [sorted(values) for values in vertex_uvs], 'weights': data['weights']}
            bindings_path = target.with_suffix('.hair-bindings.json')
            bindings_path.write_text(json.dumps(bindings,separators=(',',':')))
            result['models'][-1]['authoredBindings'] = 'Models/'+bindings_path.name
            print('HAIR_SOURCE_EXPORTED',name,len(mesh.polygons),flush=True)
            bpy.data.objects.remove(ob, do_unlink=True)
            bpy.data.meshes.remove(mesh)
        bpy.data.objects.remove(rig, do_unlink=True)
    assert len(result['models']) == 32
    (output/'hair-source-manifest.json').write_text(json.dumps(result,indent=2))


if __name__ == '__main__':
    main()

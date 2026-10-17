"""Export the existing single Blender scene for Unity; never build bundles.
Run inside the open Primal_Wardrobe.blend. Restores scene state after every job.
Materials are described explicitly: Blender variant drivers are not game assets.
"""
import bpy
import hashlib
import json
import re
import traceback
from pathlib import Path

SOURCE = Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly')
OUTPUT = Path('/Volumes/ORICO/HexLive-webgl/Assets/HexLiveContent/People/Source')
STATUS = SOURCE / 'UnityExport/status.json'
assert Path(bpy.data.filepath) == SOURCE / 'Primal_Wardrobe.blend'
OUTPUT.mkdir(parents=True, exist_ok=True)
(OUTPUT / 'Models').mkdir(exist_ok=True)
(OUTPUT / 'Textures').mkdir(exist_ok=True)
scene = bpy.context.scene
initial_frame = scene.frame_current
initial_active = bpy.context.view_layer.objects.active
initial_selected = list(bpy.context.selected_objects)
playing = bpy.context.screen is not None and bpy.context.screen.is_animation_playing
if playing:
    bpy.ops.screen.animation_cancel(restore_frame=False)

manifest = {'schemaVersion': 1, 'state': 'exporting', 'unityValidated': False,
            'bundleBuildAuthorized': False, 'models': [], 'materials': {},
            'textures': {}, 'errors': []}
jobs = []
for actor in ['Marta', 'Kshishtof']:
    meshes = [actor + '_LOD0']
    if actor == 'Kshishtof': meshes.append('Kshishtof_Genitals')
    jobs.append(('actor', actor, actor, actor + '_Rig', meshes))
for item in json.loads((SOURCE / 'Wardrobe/Data/catalog.json').read_text()):
    for actor in ['Marta', 'Kshishtof']:
        name = actor + '_' + item['name']
        if name in bpy.data.objects:
            jobs.append(('wear', item['name'], actor, name + '_Skeleton', [name]))
for item in json.loads((SOURCE / 'Hair/Data/catalog.json').read_text()):
    for lod in [0, 1]:
        name = item['name'] + '_LOD' + str(lod)
        jobs.append(('hair', name, 'Marta', item['name'] + '_HairSkeleton', [name]))
for actor in ['Marta', 'Kshishtof']:
    for item in ['PrimalGathererPack', 'PrimalHunterPack', 'PrimalTrailPack']:
        name = actor + '_' + item
        jobs.append(('backpack', item, actor, name + '_Skeleton', [name]))
assert len(jobs) == 55, len(jobs)


def body_hashes():
    result = {}
    for name in ['Marta_LOD0', 'Kshishtof_LOD0']:
        import array
        mesh = bpy.data.objects[name].data
        digest = hashlib.sha256()
        for block in mesh.shape_keys.key_blocks:
            values = array.array('f', [0]) * (len(block.data) * 3)
            block.data.foreach_get('co', values)
            digest.update(block.name.encode())
            digest.update(values.tobytes())
        result[name] = digest.hexdigest()
    return result

original_body_hashes = body_hashes()


def safe_name(value):
    return re.sub(r'[^A-Za-z0-9_.-]', '_', value)


def image_entry(image):
    if image.name in manifest['textures']:
        return manifest['textures'][image.name]['path']
    if not image.packed_file:
        raise ValueError('Image is not packed: ' + image.name)
    data = bytes(image.packed_file.data)
    if data.startswith(b'\x89PNG'): ext = '.png'
    elif data.startswith(b'\xff\xd8'): ext = '.jpg'
    else: raise ValueError('Unsupported packed image encoding: ' + image.name)
    digest = hashlib.sha256(data).hexdigest()
    filename = safe_name(Path(image.name).stem)[:80] + '_' + digest[:12] + ext
    relative = next((t['path'] for t in manifest['textures'].values()
                     if t['sha256'] == digest), 'Textures/' + filename)
    (OUTPUT / relative).write_bytes(data)
    # Resizing a Blender image does not rewrite its packed original bytes.
    # Record the dimensions of the delivered file, not the current canvas.
    probe = bpy.data.images.load(str(OUTPUT / relative), check_existing=False)
    width, height = probe.size[:]
    bpy.data.images.remove(probe)
    manifest['textures'][image.name] = {'path': relative, 'sha256': digest,
        'width': width, 'height': height, 'sourceCanvasWidth': image.size[0],
        'sourceCanvasHeight': image.size[1], 'colorSpace': image.colorspace_settings.name}
    return relative


def material_entry(material):
    if material.name in manifest['materials']: return material.name
    nodes = material.node_tree.nodes
    principled = next((n for n in nodes if n.type == 'BSDF_PRINCIPLED'), None)
    entry = {'name': material.name, 'sourceSlot': material.get('source_slot', material.name),
             'doubleSided': not material.use_backface_culling,
             'alphaMode': material.blend_method, 'alphaCutoff': material.alpha_threshold,
             'images': []}
    if principled:
        entry.update(baseColor=list(principled.inputs['Base Color'].default_value),
                     roughness=principled.inputs['Roughness'].default_value,
                     metallic=principled.inputs['Metallic'].default_value)
    for node in nodes:
        if node.type == 'TEX_IMAGE' and node.image:
            entry['images'].append({'node': node.name, 'image': image_entry(node.image),
                'links': [{'output': link.from_socket.name, 'toNode': link.to_node.name,
                           'toSocket': link.to_socket.name} for output in node.outputs for link in output.links]})
    if 'variants' in material: entry['variants'] = json.loads(material['variants'])
    manifest['materials'][material.name] = entry
    return material.name


def export_job(job):
    kind, logical_id, actor, rig_name, names = job
    rig = bpy.data.objects[rig_name]
    meshes = [bpy.data.objects[name] for name in names]
    targets = [rig] + meshes
    old_world = rig.matrix_world.copy()
    old_pose = rig.data.pose_position
    old_action = rig.animation_data.action if rig.animation_data else None
    hidden = [(obj, obj.hide_get(), obj.hide_viewport, obj.hide_select) for obj in targets]
    drivers = [(driver, driver.mute) for obj in targets if obj.animation_data
               for driver in obj.animation_data.drivers if driver.data_path in ('hide_viewport','hide_render')]
    values = [(block, block.value) for obj in meshes if obj.data.shape_keys
              for block in obj.data.shape_keys.key_blocks]
    filename = (actor + '_' if kind in ('wear','backpack') else '') + logical_id + '.fbx'
    try:
        for driver, mute in drivers: driver.mute = True
        bpy.ops.object.select_all(action='DESELECT')
        for obj, _, _, _ in hidden:
            obj.hide_select = False
            obj.hide_viewport = False
            obj.hide_set(False)
            obj.select_set(True)
        for block, value in values: block.value = 0
        rig.data.pose_position = 'REST'
        if rig.animation_data: rig.animation_data.action = None
        world = old_world.copy()
        world.translation.x = 0
        rig.matrix_world = world
        bpy.context.view_layer.objects.active = rig
        bpy.context.view_layer.update()
        mesh_entries = []
        for obj in meshes:
            mesh = obj.data
            names = [block.name for block in mesh.shape_keys.key_blocks][1:] if mesh.shape_keys else []
            if kind == 'actor' and obj.name.endswith('_LOD0'): assert len(names) == 109
            mesh_entries.append({'name': obj.name, 'vertices': len(mesh.vertices),
                'triangles': sum(len(poly.vertices)-2 for poly in mesh.polygons),
                'blendShapes': names, 'materials': [material_entry(m) for m in mesh.materials],
                'uvLayers': [layer.name for layer in mesh.uv_layers],
                'maxBoneWeights': max((len(v.groups) for v in mesh.vertices), default=0)})
        bpy.ops.export_scene.fbx(filepath=str(OUTPUT / 'Models' / filename),
            use_selection=True, object_types={'MESH','ARMATURE'}, global_scale=1,
            apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
            use_space_transform=True, bake_space_transform=False,
            use_mesh_modifiers=False, mesh_smooth_type='OFF',
            add_leaf_bones=False, use_armature_deform_only=False,
            armature_nodetype='NULL', bake_anim=False, path_mode='STRIP',
            axis_forward='-Z', axis_up='Y')
        path = OUTPUT / 'Models' / filename
        manifest['models'].append({'kind': kind, 'id': logical_id, 'actor': actor,
            'path': 'Models/' + filename, 'bytes': path.stat().st_size,
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'bones': [bone.name for bone in rig.data.bones], 'meshes': mesh_entries})
    finally:
        rig.matrix_world = old_world
        rig.data.pose_position = old_pose
        if rig.animation_data: rig.animation_data.action = old_action
        for block, value in values: block.value = value
        for obj, hide, viewport, select in hidden:
            obj.hide_viewport = viewport
            obj.hide_set(hide)
            obj.hide_select = select
        for driver, mute in drivers: driver.mute = mute
        bpy.context.view_layer.update()


def finish():
    scene.frame_set(initial_frame)
    bpy.ops.object.select_all(action='DESELECT')
    for obj in initial_selected:
        if obj.name in bpy.data.objects: obj.select_set(True)
    bpy.context.view_layer.objects.active = initial_active
    manifest['bodyMorphsUnchanged'] = body_hashes() == original_body_hashes
    assert manifest['bodyMorphsUnchanged']
    (OUTPUT / 'source-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
    STATUS.write_text(json.dumps({'state': manifest['state'], 'exported': len(manifest['models']),
                                 'errors': manifest['errors']}, indent=2))
    if playing and bpy.context.screen and not bpy.context.screen.is_animation_playing:
        bpy.ops.screen.animation_play()


def step():
    if not jobs:
        manifest['state'] = 'source-exported-awaiting-unity-validation'
        finish()
        return None
    job = jobs.pop(0)
    try:
        export_job(job)
    except Exception:
        manifest['state'] = 'error'
        manifest['errors'].append({'job': job, 'error': traceback.format_exc()})
        finish()
        return None
    STATUS.write_text(json.dumps({'state':'exporting','current':job[1],
                                 'exported':len(manifest['models']),'remaining':len(jobs)}))
    return .2

bpy.app.timers.register(step, first_interval=.3)

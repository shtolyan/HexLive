"""Transfer authored Genesis bandages onto the reviewed browser body UV atlases.

Run with native Blender, never through an interactive editor session:
  blender --background --disable-autoexec --python-exit-code 1 \
    --python Tools/bake_bandage_atlases.py -- --source-blend /path/Primal_Wardrobe.blend \
    --output Build/BandageAtlases --supersample

The source blend is SHA-checked, loaded without scripts, and never saved.
Outputs require Unity visual/runtime verification before integration.
"""
import bpy, json, sys, numpy as np
from pathlib import Path

import argparse
import hashlib

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source-blend', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--torso-only', action='store_true', help='Audit only; do not publish a partial atlas set.')
parser.add_argument('--size', type=int, choices=(1024, 2048), default=2048)
parser.add_argument('--supersample', action='store_true', help='Bake at twice output resolution and filter premultiplied alpha.')
args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
ROOT = Path(__file__).resolve().parents[1]
BLEND = args.source_blend.resolve(strict=True)
OUT = args.output.resolve()
SOURCE_SHA256 = '568b871a7c839e140de973da1e670fe1ab7e00cceabab8083e0267b6bd7b5661'
if hashlib.sha256(BLEND.read_bytes()).hexdigest() != SOURCE_SHA256:
    raise SystemExit('Source blend differs from the reviewed body atlas: audit the new geometry before baking.')
OUT.mkdir(parents=True, exist_ok=True)
ZONES = {
    'Torso': ('Torso', (.5, .53467, .71289, .12402)),
    'Pelvis': ('Torso', (.5, .24072, .85156, .17871)),
    'ArmL': ('Arms', (.31836, .76855, .26953, .37305)),
    'ArmR': ('Arms', (.68701, .27930, .26855, .37305)),
    'LegL': ('Legs', (.74805, .65674, .41016, .35645)),
    'LegR': ('Legs', (.25195, .65674, .41016, .35645)),
}
reports = []
for actor, fbx in [('Marta', 'Martanaked/Martanaked.fbx'), ('Kshishtof', 'Kshishtof/Kshishtof.fbx')]:
    bpy.ops.wm.open_mainfile(filepath=str(BLEND), use_scripts=False)
    target = bpy.data.objects[actor + '_LOD0']
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(ROOT / 'Assets/ImportedActors/Daz3D' / fbx), use_anim=False, use_image_search=False)
    source = max((o for o in set(bpy.data.objects) - before if o.type == 'MESH'), key=lambda o: len(o.data.vertices))
    source.parent = None
    source.matrix_world = target.matrix_world.copy()
    slots = [s.material.name.split('.')[0] for s in source.material_slots]
    for ob in [source, target]:
        ob.animation_data_clear()
        for mod in list(ob.modifiers): ob.modifiers.remove(mod)
        if ob.data.shape_keys:
            ob.data.shape_keys.animation_data_clear()
            for key in ob.data.shape_keys.key_blocks: key.value = 0
    for ob in bpy.data.objects:
        ob.hide_render = ob not in [source, target]
        if ob in [source, target]: ob.hide_set(False); ob.hide_viewport = False
    # Bake ray distances operate in local mesh units. Delivered meshes use cm
    # with a .01 parent scale; normalize only these temporary objects to metres.
    bpy.ops.object.select_all(action='DESELECT')
    for ob in [source, target]:
        world = ob.matrix_world.copy(); ob.parent = None; ob.matrix_world = world
        ob.select_set(True)
    bpy.context.view_layer.objects.active = target
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    assert all(abs(v-1) < 1e-5 for ob in [source, target] for v in ob.scale)
    size = args.size * (2 if args.supersample else 1)
    image = bpy.data.images.new('Bandage_bake', width=size, height=size, alpha=True, float_buffer=True)
    image.colorspace_settings.name = 'Non-Color'
    mat = bpy.data.materials.new('Bandage_target'); mat.use_nodes = True
    dest = mat.node_tree.nodes.new('ShaderNodeTexImage'); dest.image = image; mat.node_tree.nodes.active = dest
    target.data.materials.clear(); target.data.materials.append(mat)
    for p in target.data.polygons: p.material_index = 0
    target.data.uv_layers.active.active_render = True
    scene = bpy.context.scene; scene.render.engine = 'CYCLES'; scene.cycles.device = 'CPU'; scene.cycles.samples = 1
    scene.render.threads_mode = 'FIXED'; scene.render.threads = 2
    for zone, (slot_name, rect) in ZONES.items():
        if args.torso_only and zone != 'Torso': continue
        emit = tex = None
        for slot, oldname in zip(source.material_slots, slots):
            mat = bpy.data.materials.new('Bandage_' + zone + '_' + oldname); mat.use_nodes = True; slot.material = mat
            nodes = mat.node_tree.nodes; nodes.clear(); links = mat.node_tree.links
            output = nodes.new('ShaderNodeOutputMaterial'); em = nodes.new('ShaderNodeEmission')
            em.inputs['Color'].default_value = (0, 0, 0, 1); links.new(em.outputs[0], output.inputs['Surface'])
            if oldname != slot_name: continue
            emit = em; tex = nodes.new('ShaderNodeTexImage')
            tex.image = bpy.data.images.load(str(ROOT / 'Assets/Resources/HexLive/Decals' / ('bandage_wrap_' + zone + '.png')), check_existing=True)
            tex.extension = 'CLIP'
            uv = nodes.new('ShaderNodeUVMap'); uv.uv_map = source.data.uv_layers.active.name
            frac = nodes.new('ShaderNodeVectorMath'); frac.operation = 'FRACTION'; links.new(uv.outputs[0], frac.inputs[0])
            sub = nodes.new('ShaderNodeVectorMath'); sub.operation = 'SUBTRACT'
            cx, cy, w, h = rect; sub.inputs[1].default_value = (cx-w/2, cy-h/2, 0); links.new(frac.outputs[0], sub.inputs[0])
            div = nodes.new('ShaderNodeVectorMath'); div.operation = 'DIVIDE'; div.inputs[1].default_value = (w, h, 1)
            links.new(sub.outputs[0], div.inputs[0])
            sample_uv = div.outputs[0]
            if zone == 'Torso':
                # The female crop ends at U=.856445; the male spine reaches
                # U=.87 inside the band. Repeat fabric horizontally to close
                # the circumference, retaining the authored vertical alpha.
                separate = nodes.new('ShaderNodeSeparateXYZ'); links.new(sample_uv, separate.inputs[0])
                repeat = nodes.new('ShaderNodeMath'); repeat.operation = 'FRACT'; links.new(separate.outputs['X'], repeat.inputs[0])
                # Exclude the cropped image's transparent horizontal border:
                # it must not introduce a fade seam in a closed wrap.
                inset = nodes.new('ShaderNodeMath'); inset.operation = 'MULTIPLY_ADD'; inset.inputs[1].default_value = .96; inset.inputs[2].default_value = .02
                links.new(repeat.outputs[0], inset.inputs[0])
                combine = nodes.new('ShaderNodeCombineXYZ'); links.new(inset.outputs[0], combine.inputs['X']); links.new(separate.outputs['Y'], combine.inputs['Y'])
                sample_uv = combine.outputs[0]
            links.new(sample_uv, tex.inputs['Vector'])
        assert emit is not None, (actor, zone, slots)
        bpy.ops.object.select_all(action='DESELECT'); source.select_set(True); target.select_set(True); bpy.context.view_layer.objects.active = target
        channels = []
        for channel in ['Color', 'Alpha']:
            links = emit.id_data.links
            for link in list(emit.inputs['Color'].links): links.remove(link)
            links.new(tex.outputs[channel], emit.inputs['Color'])
            bpy.ops.object.bake(type='EMIT', use_selected_to_active=True, uv_layer=target.data.uv_layers.active.name, use_clear=True, use_cage=False, margin=2, cage_extrusion=.01, max_ray_distance=.025)
            buf = np.empty(size*size*4, dtype=np.float32); image.pixels.foreach_get(buf); channels.append(buf)
        rgba = channels[0]; rgba[3::4] = np.clip(channels[1][0::4], 0, 1)
        count = int(np.count_nonzero(rgba[3::4] > .5))
        assert count > 100, (actor, zone, count)
        def filtered(data, normal=False):
            if not args.supersample: return data, size
            values = data.reshape(size//2, 2, size//2, 2, 4)
            alpha = values[..., 3:4]
            rgb = values[..., :3] * 2 - 1 if normal else values[..., :3]
            weight = alpha.sum(axis=(1, 3))
            result_rgb = (rgb * alpha).sum(axis=(1, 3)) / np.maximum(weight, 1e-8)
            if normal:
                result_rgb /= np.maximum(np.linalg.norm(result_rgb, axis=-1, keepdims=True), 1e-8)
                result_rgb = result_rgb * .5 + .5
            return np.concatenate([result_rgb, weight*.25], axis=-1).astype(np.float32).reshape(-1), size//2
        output_rgba, output_size = filtered(rgba)
        # EMIT float bake above contains linear RGB. A byte Image.save writes
        # assigned values unchanged even when its colorspace says sRGB.
        # Encode exactly once; alpha and tangent-normal data stay linear.
        rgb = output_rgba.reshape(-1, 4)[:, :3]
        rgb[:] = np.where(rgb <= .0031308, rgb * 12.92, 1.055 * np.power(np.maximum(rgb, 0), 1/2.4) - .055)
        packed = bpy.data.images.new('Bandage_' + actor + '_' + zone, width=output_size, height=output_size, alpha=True)
        packed.pixels.foreach_set(output_rgba); packed.filepath_raw = str(OUT / (actor + '_' + zone + '.png')); packed.file_format = 'PNG'; packed.save()
        # Re-express the authored source normal in the target atlas tangent basis.
        # Emission-copying RGB would rotate relief incorrectly on smart_project islands.
        for slot, oldname in zip(source.material_slots, slots):
            nodes = slot.material.node_tree.nodes; links = slot.material.node_tree.links
            output = next(n for n in nodes if n.type == 'OUTPUT_MATERIAL')
            for link in list(output.inputs['Surface'].links): links.remove(link)
            shader = nodes.new('ShaderNodeBsdfPrincipled'); links.new(shader.outputs[0], output.inputs['Surface'])
            if oldname != slot_name: continue
            normal_tex = nodes.new('ShaderNodeTexImage')
            normal_tex.image = bpy.data.images.load(str(ROOT / 'Assets/Resources/HexLive/Decals' / ('bandage_wrap_' + zone + '_n.png')), check_existing=True)
            normal_tex.image.colorspace_settings.name = 'Non-Color'; normal_tex.extension = 'EXTEND'
            links.new(tex.inputs['Vector'].links[0].from_socket, normal_tex.inputs['Vector'])
            normal = nodes.new('ShaderNodeNormalMap'); normal.space = 'TANGENT'; normal.uv_map = source.data.uv_layers.active.name
            links.new(normal_tex.outputs['Color'], normal.inputs['Color']); links.new(normal.outputs['Normal'], shader.inputs['Normal'])
        bpy.ops.object.bake(type='NORMAL', normal_space='TANGENT', normal_r='POS_X', normal_g='POS_Y', normal_b='POS_Z', use_selected_to_active=True, uv_layer=target.data.uv_layers.active.name, use_clear=True, use_cage=False, margin=2, cage_extrusion=.01, max_ray_distance=.025)
        normal_rgba = np.empty(size*size*4, dtype=np.float32); image.pixels.foreach_get(normal_rgba)
        normal_rgba[3::4] = rgba[3::4]
        output_normal, output_size = filtered(normal_rgba, normal=True)
        normal_image = bpy.data.images.new('Bandage_' + actor + '_' + zone + '_n', width=output_size, height=output_size, alpha=True)
        normal_image.colorspace_settings.name = 'Non-Color'; normal_image.pixels.foreach_set(output_normal)
        normal_image.filepath_raw = str(OUT / (actor + '_' + zone + '_n.png')); normal_image.file_format = 'PNG'; normal_image.save()
        report = dict(actor=actor, zone=zone, sourceMesh=source.name, sourceVertices=len(source.data.vertices), targetVertices=len(target.data.vertices), sourceRect=rect, bakeSize=size, outputSize=output_size, alphaAboveHalf=count, sourceBlendSha256=SOURCE_SHA256, status='awaiting-Unity-visual-check')
        reports.append(report); print(json.dumps(report), flush=True)
        (OUT / ('projection-torso.json' if args.torso_only else 'projection.json')).write_text(json.dumps(reports, indent=2))
        bpy.data.images.remove(packed); bpy.data.images.remove(normal_image)
        del channels, rgba, normal_rgba, output_rgba, output_normal, buf
print(f'Completed {len(reports)} color/alpha and tangent normal projections; runtime and normal rendering not validated.', flush=True)

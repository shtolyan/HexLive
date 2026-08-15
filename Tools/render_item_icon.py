"""Headless-Blender renderer for inventory item icons (ICON_GENERATION_SPEC.md).

    /Applications/Blender.app/Contents/MacOS/Blender -b -P Tools/render_item_icon.py -- \
        <model.obj|model.glb|model.fbx> <out.png> [--install <itemId>] [--fit 1.25] \
        [--tilt 22] [--samples 64]

Takes a garment OBJ (from Tools/unity_mesh_to_obj.py) or a prop GLB/FBX (the ones
in Assets/Resources/HexLive/Objects) and renders the 512x512 RGBA icon the
inventory expects. The numbers below are NOT free parameters — they reproduce
the framing of the icons that shipped with the game (compare
`Assets/Resources/HexLive/UI/Items/Shorts_10_14636.png`). Change them and the
new icon will not sit in the same row as the old ones.

`--tilt <deg>` rolls the model about its long axis before framing. A FLAT prop
(a board, a hide) presents its whole face square-on to the key sun and burns out
to white; a roll of ~20 degrees takes the face off that normal and shows the
edge and the end grain. It is a pose, not a rig change — camera, lights and
framing stay the ones the whole icon set shares.

`--install <itemId>` also copies the PNG to `Assets/HexLiveContent/Icons/`,
writes a sprite `.meta`, and registers the Addressables entry `icon/<itemId>` in
`HexLive.Icons`, so Unity imports it as a Sprite on next launch without an
editor session.

⚠️ It used to install into `Assets/Resources/HexLive/UI/Items`, and that folder
is DEAD: `Wearing/Garments/ItemIcons` is the only door to item icons and it asks
Addressables for `icon/<id>`, nothing else. An icon installed the old way copied
cleanly, imported cleanly — and never appeared anywhere in the game. A PNG
without its Addressables entry is invisible in exactly the same silent way.
"""
import math
import os
import re
import shutil
import sys
import uuid

import bpy
from mathutils import Matrix, Vector

# --- the shipped looks (do not tune casually) ----------------------------
# Two presets, because two batches of icons shipped: the cloth one matches the
# Molly garment icons (Shorts_10_14636.png), the tool one matches the AI tool
# icons (tool.axe_stone.png, tool.lighter.png). Both are 3/4 from the same
# side; the tool preset sits a little wider and harder.
STYLES = {
    #        camera direction          fit   roughness  spec  sheen  flat
    "cloth": (Vector((0.55, -1.0, 0.30)), 1.25, 0.62, 0.15, 0.15, False),
    "tool":  (Vector((0.75, -1.0, 0.30)), 1.35, 0.90, 0.00, 0.00, True),
}
SAMPLES = 64        # Cycles + denoise
RESOLUTION = 512
SUNS = (  # direction, energy: key / fill / rim / top
    ((0.7, -1.0, 0.9), 4.0),
    ((-1.0, -0.6, 0.25), 1.8),
    ((-0.2, 1.0, 0.6), 2.2),
    ((0.0, -0.1, 1.0), 1.2),
)

ICON_DIR = "Assets/HexLiveContent/Icons"
ICON_GROUP = "Assets/AddressableAssetsData/AssetGroups/HexLive.Icons.asset"
# Шаблон .meta встроен НАМЕРЕННО. Раньше он копировался с соседней
# иконки — и когда ту вещь снесли вместе со старым гардеробом,
# установка иконок сломалась бы на пустом месте. Плюс режим спрайта
# тут зафиксирован: Single. Multiple с пустой нарезкой даёт текстуру,
# на которой Resources.Load<Sprite> возвращает null, — иконки просто
# не появляются, без единой ошибки (так пропали 673 штуки).
META_TEMPLATE_TEXT = """fileFormatVersion: 2
guid: __GUID__
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 1
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {x: 0.5, y: 0.5}
  spritePixelsToUnits: 100
  spriteBorder: {x: 0, y: 0, z: 0, w: 0}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationDetail: -1
  textureType: 8
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: Standalone
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData: 
    physicsShape: []
    bones: []
    spriteID: __SPRITEID__
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {}
  mipmapLimitGroupName: 
  pSDRemoveMatte: 0
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]
    # OBJ comes from a garment mesh, GLB/FBX from a prop — override with --style
    style = "tool" if src.lower().endswith((".glb", ".gltf", ".fbx")) else "cloth"
    if "--style" in argv:
        style = argv[argv.index("--style") + 1]
    opts = {"install": None, "style": style, "fit": None, "tilt": 0.0,
            "samples": SAMPLES}
    if "--install" in argv:
        opts["install"] = argv[argv.index("--install") + 1]
    if "--fit" in argv:
        opts["fit"] = float(argv[argv.index("--fit") + 1])
    if "--tilt" in argv:
        opts["tilt"] = float(argv[argv.index("--tilt") + 1])
    if "--samples" in argv:
        opts["samples"] = int(argv[argv.index("--samples") + 1])
    return src, dst, opts


def load(src):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if src.lower().endswith(".glb") or src.lower().endswith(".gltf"):
        bpy.ops.import_scene.gltf(filepath=src)
    elif src.lower().endswith(".fbx"):
        bpy.ops.import_scene.fbx(filepath=src)
    else:
        # Unity Y-up / +Z-forward -> Blender Z-up, model front at -Y.
        # The legacy operator was REMOVED in Blender 4.0, so pick whichever this
        # build has: the icons must keep landing in the same orientation whether
        # they are rendered on the 3.2.2 the spec was written against or on a
        # current build. The axis pair is the same, only spelled differently.
        if hasattr(bpy.ops.wm, "obj_import"):
            bpy.ops.wm.obj_import(filepath=src, forward_axis='NEGATIVE_Z', up_axis='Y')
        else:
            bpy.ops.import_scene.obj(filepath=src, axis_forward='-Z', axis_up='Y')
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    assert meshes, f"no mesh in {src}"
    return meshes


def tilt(meshes, degrees):
    """Крен вокруг длинной оси модели — поза, а не правка рига.

    ⚠️ Поворот кладётся В ВЕРШИНЫ. Присваивать объекту `rotation_euler` нельзя:
    импортёры FBX/glTF уже держат там поворот осевой конверсии, и присваивание
    его затирает — модель молча возвращается в исходную позу.
    """
    if abs(degrees) < 1e-6:
        return
    lo, hi = world_bbox(meshes)
    size = hi - lo
    axis = 'XYZ'[max(range(3), key=lambda i: size[i])]
    rot = Matrix.Rotation(math.radians(degrees), 4, axis)
    for ob in meshes:
        ob.data.transform(ob.matrix_world.inverted() @ rot @ ob.matrix_world)
        ob.data.update()


def world_bbox(meshes):
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for ob in meshes:
        for corner in ob.bound_box:
            w = ob.matrix_world @ Vector(corner)
            for i in range(3):
                lo[i], hi[i] = min(lo[i], w[i]), max(hi[i], w[i])
    return lo, hi


def _set_input(bsdf, value, *names):
    """Set the first Principled input that exists under any of `names`."""
    for name in names:
        if name in bsdf.inputs:
            bsdf.inputs[name].default_value = value
            return


def setup_materials(meshes, style):
    _, _, roughness, specular, sheen, flat = STYLES[style]
    if flat:
        bpy.ops.object.select_all(action='DESELECT')
        for ob in meshes:
            ob.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        bpy.ops.object.shade_flat()
    for ob in meshes:
        for slot in ob.material_slots:
            mat = slot.material
            if mat is None:
                continue
            mat.use_nodes = True
            bsdf = next((n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
            if bsdf is None:
                continue
            # Native FBX mirrors can retain Unity-only texture references that
            # Blender cannot resolve headlessly. Never ship the resulting
            # magenta missing-texture icon: detach the unavailable image and
            # render the real mesh as neutral warm-white material instead.
            missing_image = any(
                node.type == 'TEX_IMAGE' and
                (node.image is None or not os.path.isfile(bpy.path.abspath(node.image.filepath)))
                for node in mat.node_tree.nodes)
            if missing_image and 'Base Color' in bsdf.inputs:
                for link in list(bsdf.inputs['Base Color'].links):
                    mat.node_tree.links.remove(link)
                bsdf.inputs['Base Color'].default_value = (0.82, 0.76, 0.66, 1.0)
            # Blender 4.x renamed several Principled inputs, so address them by
            # whichever name this build knows. Missing ones are skipped rather
            # than fatal: an icon without sheen still matches the shipped set far
            # better than no icon at all.
            _set_input(bsdf, specular, 'Specular', 'Specular IOR Level')
            _set_input(bsdf, roughness, 'Roughness')
            _set_input(bsdf, sheen, 'Sheen', 'Sheen Weight')
            # icons are always opaque: a see-through prop (the plastic bottle)
            # would otherwise render as a ghost on the transparent film
            if 'Alpha' in bsdf.inputs and not bsdf.inputs['Alpha'].links:
                bsdf.inputs['Alpha'].default_value = 1.0
            mat.blend_method = 'OPAQUE'


def render(dst, meshes, style, fit, samples):
    cam_dir, default_fit = STYLES[style][0], STYLES[style][1]
    fit = default_fit if fit is None else fit
    scene = bpy.context.scene
    lo, hi = world_bbox(meshes)
    centre, maxdim = (lo + hi) / 2, max(hi - lo)

    cam_data = bpy.data.cameras.new("Cam")
    cam_data.type = 'ORTHO'
    cam_data.ortho_scale = maxdim * fit
    cam = bpy.data.objects.new("Cam", cam_data)
    scene.collection.objects.link(cam)
    d = cam_dir.normalized()
    cam.location = centre + d * (maxdim * 4.0)
    cam.rotation_mode = 'QUATERNION'
    cam.rotation_quaternion = d.to_track_quat('Z', 'Y')
    scene.camera = cam

    for i, (direction, energy) in enumerate(SUNS):
        data = bpy.data.lights.new(f"sun{i}", type='SUN')
        data.energy, data.angle = energy, math.radians(25)
        ob = bpy.data.objects.new(f"sun{i}", data)
        scene.collection.objects.link(ob)
        ob.rotation_mode = 'QUATERNION'
        ob.rotation_quaternion = Vector(direction).normalized().to_track_quat('Z', 'Y')

    scene.render.engine = 'CYCLES'
    try:
        scene.cycles.device = 'CPU'
    except Exception:
        pass
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = RESOLUTION
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.view_settings.view_transform = 'Standard'
    scene.render.filepath = dst
    bpy.ops.render.render(write_still=True)


def register_addressable(guid, item_id):
    """Прописать `icon/<id>` в группу HexLive.Icons — БЕЗ этого иконки нет.

    Игра берёт иконки только через Addressables (`ItemIcons.Address`), поэтому
    PNG без записи в группе — это молчаливая пустота: файл на месте, импорт
    чистый, в игре ничего. Записи в группе отсортированы по GUID, повторный
    прогон переписывает свою и не плодит дублей.
    """
    if not os.path.isfile(ICON_GROUP):
        raise SystemExit(f"run from the repo root — {ICON_GROUP} not found")
    text = open(ICON_GROUP, encoding="utf-8").read()
    address = f"icon/{item_id}"
    entry = (r"  - m_GUID: (\w+)\n    m_Address: (.+)\n    m_ReadOnly: 0\n"
             r"    m_SerializedLabels: \[\]\n"
             r"    FlaggedDuringContentUpdateRestriction: 0\n")
    block_at = re.search(r"  m_SerializeEntries:\n((?:" + entry + r")+)", text)
    if block_at is None:
        raise SystemExit(f"не разобрал m_SerializeEntries в {ICON_GROUP}")
    kept = [(g, a) for g, a in re.findall(entry, block_at.group(1))
            if a != address and g != guid]
    kept.append((guid, address))
    rebuilt = "".join(
        f"  - m_GUID: {g}\n    m_Address: {a}\n    m_ReadOnly: 0\n"
        f"    m_SerializedLabels: []\n    FlaggedDuringContentUpdateRestriction: 0\n"
        for g, a in sorted(kept))
    open(ICON_GROUP, "w", encoding="utf-8").write(
        text[:block_at.start(1)] + rebuilt + text[block_at.end(1):])
    print("ADDRESSABLE", address, guid, f"({len(kept)} entries)")


def install(png, item_id):
    """Положить иконку туда, откуда игра её действительно читает."""
    if not os.path.isdir(ICON_DIR):
        raise SystemExit(f"run from the repo root — {ICON_DIR} not found")
    target = os.path.join(ICON_DIR, item_id + ".png")
    shutil.copyfile(png, target)
    # GUID переживает переустановку: иначе каждый повторный рендер иконки
    # рвал бы ссылку на неё из группы Addressables.
    guid = uuid.uuid4().hex
    if os.path.isfile(target + ".meta"):
        found = re.search(r"^guid: (\w+)", open(target + ".meta", encoding="utf-8").read(),
                          re.M)
        if found:
            guid = found.group(1)
    meta = (META_TEMPLATE_TEXT
            .replace("__GUID__", guid)
            .replace("__SPRITEID__", uuid.uuid4().hex))
    open(target + ".meta", "w", encoding="utf-8").write(meta)
    print("INSTALLED", target)
    register_addressable(guid, item_id)


def main():
    src, dst, opts = parse_args()
    meshes = load(src)
    tilt(meshes, opts["tilt"])
    setup_materials(meshes, opts["style"])
    render(dst, meshes, opts["style"], opts["fit"], opts["samples"])
    print(f"WROTE {dst} (style={opts['style']}, tilt={opts['tilt']})")
    if opts["install"]:
        install(dst, opts["install"])


main()

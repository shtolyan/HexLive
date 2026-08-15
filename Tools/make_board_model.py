"""Сгенерировать модель доски `resource.board` — headless Blender.

    /Applications/Blender.app/Contents/MacOS/Blender -b -P Tools/make_board_model.py

Почему скрипт, а не .blend: доска — это коробка с фасками, её дешевле
описать, чем хранить; правка цвета или толщины здесь — одна строка, и модель
пересобирается детерминированно (seed зашит).

⭐ Палитра берётся ИЗ `Assets/HexLiveContent/source.blend` — те же материалы
`Sapwood` и `Heartwood`, которыми покрашен срез бревна (`log_final_native`).
Доска — это распиленное бревно, поэтому её цвет обязан совпадать со срезом, а
не жить своей жизнью. Старая доска была в палитре протезов (Prosthetic_Wood*,
0.55/0.25 линейных = средне-коричневый) и читалась как обугленная палка.

Габариты 1:1 повторяют прежний экспорт (0.9744 × 0.1787 × 0.0547), чтобы
подмена была бесшовной: `ObjectFit` нормирует по максимальному измерению, а
верстак (`station.workbench.fbx`) собран из досок этого размера.
"""

import math
import os
import random
import sys

import bpy
import bmesh
from mathutils import Vector

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_BLEND = os.path.join(REPO, "Assets/HexLiveContent/source.blend")
OUT_FBX = os.path.join(REPO, "Assets/Resources/HexLive/Objects/resource.board.fbx")

# Прежний bbox, повторён точно.
LENGTH, WIDTH, THICK = 0.9744, 0.1787, 0.0547
SEGMENTS = 6          # долей по длине — на них живёт «пиленая» неровность
LANES = 6             # дорожек по ширине — по ним пущено волокно
GRAIN_LANES = (1, 4)  # какие дорожки темнее: две тонкие полосы вдоль доски
BEVEL = 0.0055        # фаска: она и делает силуэт гранёным
SEED = 1487

# ⭐ Палитра доски — ЕДИНСТВЕННОЕ место, где правится её цвет.
#
# Опорная точка — `Sapwood` из source.blend (0.82 0.64 0.42), краска среза
# бревна: доска и есть распущенное бревно. Но целиком в Sapwood она не живёт.
# Замер по штатному рендеру иконок (`Tools/render_item_icon.py`, четыре солнца)
# показал: горизонтальная пласть получает ~2.2 к альбедо, всё выше ~0.46
# упирается в белый потолок. При 0.82 в белое уходило 29 % пикселей иконки —
# волокно, фаски и торец исчезали, доска читалась бруском мыла. Поза не
# спасает: наклон подставляет пласть ключевому солнцу, и доля пересвета росла
# до 52 %. Поэтому пласть взята тем же оттенком, но приглушённой до потолка
# рига — светло-бежевой, а не белой.
FACE_RGB = (0.455, 0.350, 0.235)   # пласть и боковые кромки
GRAIN_RGB = (0.300, 0.212, 0.125)  # две дорожки волокна вдоль пласти
SHADE_RGB = (0.360, 0.265, 0.168)  # испод и фаски
END_RGB = (0.520, 0.408, 0.280)    # торец — свежий поперечный спил, светлее всех


def palette():
    """Материалы из source.blend + два оттенка, выведенных из Sapwood."""
    with bpy.data.libraries.load(SOURCE_BLEND, link=False) as (src, dst):
        wanted = [n for n in src.materials if n in ("Sapwood", "Heartwood")]
        assert len(wanted) == 2, f"в source.blend нет Sapwood/Heartwood: {wanted}"
        dst.materials = wanted

    sap = bpy.data.materials["Sapwood"]        # 0.82 0.64 0.42 — сам срез
    heart = bpy.data.materials["Heartwood"]    # 0.88 0.55 0.26 — ядро, тёплое

    def shade(name, rgb, rough):
        mat = sap.copy()
        mat.name = name
        bsdf = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
        bsdf.inputs['Base Color'].default_value = (*rgb, 1.0)
        bsdf.inputs['Roughness'].default_value = rough
        return mat

    # Sapwood/Heartwood остаются в файле как опора палитры: от них выведены
    # оттенки ниже, и по ним видно родство доски со срезом бревна.
    heart.name = "Board_Heartwood"
    face = shade("Board_Sapwood", FACE_RGB, 0.90)
    grain = shade("Board_Grain", GRAIN_RGB, 0.94)
    dark = shade("Board_SapwoodShade", SHADE_RGB, 0.92)
    end = shade("Board_EndGrain", END_RGB, 0.88)
    bpy.data.materials.remove(sap)
    return [face, grain, dark, end]      # 0 пласть, 1 волокно, 2 испод/фаска, 3 торец


SAP, GRAIN, SHADE, END = 0, 1, 2, 3


def build_mesh():
    rng = random.Random(SEED)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(LENGTH, WIDTH, THICK), verts=bm.verts)

    def subdivide(axis, cuts):
        edges = [e for e in bm.edges
                 if abs((e.verts[0].co - e.verts[1].co).normalized()[axis]) > 0.9]
        bmesh.ops.subdivide_edges(bm, edges=edges, cuts=cuts, use_grid_fill=True)

    subdivide(0, SEGMENTS - 1)   # доли по длине
    subdivide(1, LANES - 1)      # дорожки по ширине — по ним пойдёт волокно

    # Пиленая вручную доска не идеальный брусок: каждая доля чуть своей толщины
    # и ширины, а поперечные пропилы чуть гуляют по длине. Отклонения мелкие —
    # это огранка силуэта, а не кривизна.
    slices = {}
    for v in bm.verts:
        slices.setdefault(round(v.co.x, 5), []).append(v)
    for i, (x, verts) in enumerate(sorted(slices.items())):
        tz = 1.0 + rng.uniform(-0.12, 0.08)
        ty = 1.0 + rng.uniform(-0.05, 0.05)
        dx = 0.0 if i in (0, len(slices) - 1) else rng.uniform(-0.02, 0.02) * LENGTH
        # Доску чуть ведёт по длине — но именно чуть: на 0.16 толщины она
        # выгибалась бананом и переставала быть доской.
        dz = math.sin(i / max(1, len(slices) - 1) * math.pi) * THICK * 0.05
        for v in verts:
            v.co.z = v.co.z * tz + dz
            v.co.y *= ty
            v.co.x += dx

    # ⚠️ Фаска ТОЛЬКО по настоящим рёбрам корпуса. Если отдать bevel все рёбра
    # подряд, он снимет и внутренние рёбра сетки подразделения — пласть
    # покроется гребнями, и доска в рендере превращается в стиральную доску,
    # а «волокно» читается белыми полосами бликов.
    sharp = [e for e in bm.edges
             if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > math.radians(25.0)]
    bmesh.ops.bevel(
        bm, geom=sharp,
        offset=BEVEL, offset_type='OFFSET', segments=1, profile=0.5,
        affect='EDGES', clamp_overlap=True, loop_slide=True)

    def lane_of(y):
        i = int((y / WIDTH + 0.5) * LANES)
        return min(LANES - 1, max(0, i))

    for f in bm.faces:
        n, c = f.normal, f.calc_center_median()
        if abs(n.x) > 0.75:
            f.material_index = END                       # торцы — торцевой спил
        elif n.z > 0.75:
            f.material_index = GRAIN if lane_of(c.y) in GRAIN_LANES else SAP
        elif n.z < -0.75:
            f.material_index = SHADE                     # испод в тени
        elif abs(n.y) > 0.75:
            f.material_index = SAP                       # боковые кромки
        else:
            f.material_index = SHADE                     # фаски

    mesh = bpy.data.meshes.new("resource.board")
    bm.to_mesh(mesh)
    bm.free()
    for p in mesh.polygons:
        p.use_smooth = False                             # плоская огранка — это стиль
    mesh.calc_normals()
    return mesh


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mats = palette()
    mesh = build_mesh()
    for m in mats:
        mesh.materials.append(m)

    obj = bpy.data.objects.new("resource.board", mesh)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)

    bpy.ops.export_scene.fbx(
        filepath=OUT_FBX, use_selection=True, apply_unit_scale=True,
        object_types={'MESH'}, mesh_smooth_type='FACE', use_mesh_modifiers=False,
        add_leaf_bones=False, bake_anim=False, path_mode='STRIP',
        axis_forward='-Z', axis_up='Y')

    print("BOARD dims=", tuple(round(v, 4) for v in obj.dimensions),
          "verts=", len(mesh.vertices), "polys=", len(mesh.polygons),
          "tris=", sum(len(p.vertices) - 2 for p in mesh.polygons))
    print("WROTE", OUT_FBX)


main()

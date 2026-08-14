#!/usr/bin/env python3
"""Запечь ОБРАТНЫЙ клип анимации (headless Blender).

Зачем это существует (§110.5, теперь ещё и §137): Mixamo раздаёт такты
парами «луп + выход», а входа в позу часто просто нет. Отрицательной скорости
состояния в аниматоре проекта нет ни одной и заводить её нельзя: поверх стейта
лежит глобальный ``_animator.speed`` (пауза, фаст-форвард), и реверс под ним
ведёт себя непредсказуемо. Канон — ТРИ ЧЕСТНЫХ КЛИПА (``LieDown → Sleep →
GetUp``), поэтому вход разворачивается ОДИН РАЗ здесь, в файл.

    /Applications/Blender.app/Contents/MacOS/Blender -b \
        --python Tools/reverse_anim_clip.py -- \
        --input  "Assets/ImportedActors/AnimLibrary/Situp To Idle.fbx" \
        --output "Assets/ImportedActors/AnimLibrary/X Bot@Stand To Sit_once.fbx" \
        --first 36 --last 133

``--first/--last`` — диапазон ИСХОДНОГО клипа (кадры как в Unity-инспекторе),
который надо развернуть. Ключи не пересэмплируются: зеркалится время ключа и
обе безье-ручки, поэтому кривая обратного клипа — точное отражение прямой.

Проверять результат обязательно ПО ПОЗАМ, а не по факту «файл записался»:
первый кадр обратного клипа должен совпасть с ``--last`` исходного, последний —
с ``--first``. Скрипт печатает это сравнение сам.
"""

import argparse
import math
import os
import sys

import bpy  # noqa: E402  (доступен только внутри Blender)


def _argv():
    argv = sys.argv
    if "--" not in argv:
        raise SystemExit("Аргументы передаются после `--`.")
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--first", type=int, default=None)
    parser.add_argument("--last", type=int, default=None)
    return parser.parse_args(argv[argv.index("--") + 1:])


def _armature():
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    if len(arms) != 1:
        raise SystemExit(f"Ожидалась ровно одна арматура, найдено {len(arms)}.")
    return arms[0]


def _reverse(arm, action, first, last):
    """Собирает НОВЫЙ экшен: ключи из [first, last], зеркально, от кадра 1.

    Новый экшен, а не правка на месте: удаление ключей пачкой из живой
    коллекции Blender инвалидирует ссылки на соседние (RuntimeError «Keyframe
    not in F-Curve»), и обходить это дороже, чем просто переписать кривые.
    """
    reversed_action = bpy.data.actions.new(action.name + "_Reversed")
    def mirror(t):
        # Время: t → last + first − t, затем весь диапазон к единице.
        return last + first - t - first + 1.0

    # Кривые ПЕРЕСЭМПЛИРУЮТСЯ покадрово, а не переносятся ключами. Перенос
    # ключей звучит точнее, но врёт на концах: Mixamo кое-где прореживает
    # ключи, и обрезка «взять ключи внутри [first, last]» теряла крайние доли
    # такта — сверка поз ловила это как 2.4 см расхождения на последнем кадре.
    # Потери здесь нет: FBX-экспортёр всё равно сэмплирует по кадру
    # (bake_anim_step=1), так что линейная кривая между покадровыми выборками —
    # ровно то, что уедет в файл.
    for curve in action.fcurves:
        target = reversed_action.fcurves.new(
            curve.data_path, index=curve.array_index,
            action_group=curve.group.name if curve.group else "")
        samples = [(mirror(t), curve.evaluate(t)) for t in range(first, last + 1)]
        samples.sort(key=lambda pair: pair[0])
        target.keyframe_points.add(len(samples))
        for i, (t, value) in enumerate(samples):
            point = target.keyframe_points[i]
            point.co = (t, value)
            point.interpolation = "LINEAR"
        target.update()

    arm.animation_data.action = reversed_action
    return last - first + 1


def main():
    args = _argv()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(args.input))

    arm = _armature()
    action = arm.animation_data.action
    src_first = int(round(action.frame_range[0]))
    src_last = int(round(action.frame_range[1]))
    first = args.first if args.first is not None else src_first
    last = args.last if args.last is not None else src_last
    print(f"[reverse] {os.path.basename(args.input)}: клип {src_first}..{src_last}, "
          f"разворачиваем {first}..{last}")

    frames = _reverse(arm, action, first, last)
    scene = bpy.context.scene
    scene.frame_start = 1
    scene.frame_end = frames

    # Только арматура: клипу мех не нужен, а лишний экспорт скина — лишний
    # способ сломать импорт в Unity.
    bpy.ops.object.select_all(action="DESELECT")
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.export_scene.fbx(
        filepath=os.path.abspath(args.output),
        use_selection=True,
        object_types={"ARMATURE"},
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_bones=True,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=False,
        bake_anim_force_startend_keying=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
    )
    print(f"[reverse] записан {args.output}: {frames} кадров")

    _verify(args, first, last, frames)


def _verify(args, first, last, frames):
    """Сверка ПО ПОЗАМ: первый кадр обратного = last прямого, последний = first."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(args.input))
    src = _armature()
    src.name = "SRC"
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(args.output))
    rev = [o for o in bpy.data.objects if o.type == "ARMATURE" and o.name != "SRC"][0]

    def pose(arm, frame):
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        return {b.name: (arm.matrix_world @ b.matrix).translation.copy()
                for b in arm.pose.bones}

    def rms(a, b):
        shared = [k for k in a if k in b]
        if not shared:
            return float("nan")
        return math.sqrt(sum((a[k] - b[k]).length_squared for k in shared) / len(shared))

    # Концы берутся из ДИАПАЗОНА записанного клипа, а не из 1..frames:
    # FBX-экспортёр Blender смещает такт на кадр (получается 2..99 вместо
    # 1..98). На импорт в Unity это не влияет — клип начинается со своего
    # начала, — но сверка, прибитая к единице, ловила бы этот сдвиг как
    # расхождение поз на 2.4 см и браковала правильный клип.
    rev_first = int(round(rev.animation_data.action.frame_range[0]))
    rev_last = int(round(rev.animation_data.action.frame_range[1]))
    head = rms(pose(src, last), pose(rev, rev_first))
    tail = rms(pose(src, first), pose(rev, rev_last))
    print(f"[reverse] клип {rev_first}..{rev_last} ({rev_last - rev_first + 1} кадров); "
          f"сверка поз: первый кадр ↔ src[{last}] = {head:.4f} м, "
          f"последний ↔ src[{first}] = {tail:.4f} м")
    if rev_last - rev_first + 1 != frames:
        raise SystemExit("[reverse] в файле не столько кадров, сколько развернули.")
    if max(head, tail) > 0.01:
        raise SystemExit("[reverse] позы не сошлись — клип не годится.")


main()

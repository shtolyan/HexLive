#!/usr/bin/env python3
"""⭐ КОГДА В КЛИПЕ УДАРА КУЛАК/КЛИНОК КАСАЕТСЯ ЦЕЛИ (spec §104.8).

`hitDelaySeconds` в листе снаряжения обещает ровно одно: через сколько секунд от
НАЧАЛА анимации падает урон, а с ним кровь, флинч и звук. Число это до §104.8
было поставлено на глаз («~75% замаха») и не совпадало НИ С ОДНИМ клипом:
настоящий контакт живёт на 24–40% клипа, то есть брызга опаздывала за видимым
ударом на полсекунды и больше. Глазами это ловится за круг отладки, скриптом —
за минуту, поэтому число теперь МЕРЯЕТСЯ.

Правило замера — одно на все клипы: бьющая конечность выносится ВПЕРЁД, значит
кадр контакта = кадр, где кисть или стопа дальше всего по +Z в системе таза.
Максимум ВЫНОСА (расстояния от таза) для этого не годится: у горизонтального
замаха он приходится на отведение оружия НАЗАД, и первая версия этого скрипта
именно так и промахнулась — на клипе тесака показала 21% вместо 40%.

Саму бьющую конечность выбирает «выступ» пика над впадинами до и после него:
удар ВЫСТРЕЛИВАЕТ и тут же УБИРАЕТСЯ. Ни абсолютный максимум, ни превышение над
медианой не годятся — в стойке рука и так висит впереди, а шаг опорной ноги
уезжает вперёд и ТАМ И ОСТАЁТСЯ, и по обеим меркам он побеждал настоящий удар
(Punch A читался как удар левой стопой).

Запуск (Blender нужен только как импортёр FBX; -b здесь законен — это обычный
скрипт, а не MCP-мост):

    /Applications/Blender.app/Contents/MacOS/Blender -b \
        -P Tools/measure_strike_contact.py -- [--write]

Без `--write` печатает таблицу, с ним обновляет `Tools/strike_contacts.json` —
файл, по которому гейт `StrikeContactGate` сверяет ассеты снаряжения.
"""
import json
import os
import re
import sys

import bpy

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GEAR_DIR = os.path.join(REPO, "Assets", "Resources", "HexLive", "Gear")
ASSETS = os.path.join(REPO, "Assets")
OUT = os.path.join(REPO, "Tools", "strike_contacts.json")

EFFECTORS = ["mixamorig:LeftHand", "mixamorig:RightHand",
             "mixamorig:LeftFoot", "mixamorig:RightFoot"]
FPS = 30.0


def guid_index():
    """guid → путь к ассету (по .meta)."""
    index = {}
    for root, _dirs, files in os.walk(ASSETS):
        for name in files:
            if not name.endswith(".meta"):
                continue
            path = os.path.join(root, name)
            try:
                with open(path, "r", errors="ignore") as fh:
                    head = fh.read(400)
            except OSError:
                continue
            m = re.search(r"^guid: ([0-9a-f]{32})", head, re.M)
            if m:
                index[m.group(1)] = path[:-len(".meta")]
    return index


def attack_clip_guids():
    """Все клипы удара, объявленные в листах снаряжения: guid → метки."""
    wanted = {}
    for name in sorted(os.listdir(GEAR_DIR)):
        if not name.endswith(".asset"):
            continue
        gear = name[: -len(".asset")]
        text = open(os.path.join(GEAR_DIR, name), errors="ignore").read()

        block = re.search(r"\n  strikes:\n(.*?)(?=\n  [a-zA-Z])", text, re.S)
        if block:
            for i, m in enumerate(re.finditer(r"clip: \{fileID: [-\d]+, guid: ([0-9a-f]{32})",
                                              block.group(1))):
                wanted.setdefault(m.group(1), []).append(f"{gear}.strike[{i}]")

        block = re.search(r"\n  attackClips:\n(.*?)(?=\n  [a-zA-Z])", text, re.S)
        if block:
            for i, m in enumerate(re.finditer(r"guid: ([0-9a-f]{32})", block.group(1))):
                wanted.setdefault(m.group(1), []).append(f"{gear}.attack[{i}]")
    return wanted


def contact_of(path):
    """(кадров, кадр контакта, конечность) для одного FBX."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path)
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    action = arm.animation_data.action
    first, last = (int(round(v)) for v in action.frame_range)
    scene = bpy.context.scene
    hips = arm.pose.bones["mixamorig:Hips"]

    forward = {b: [] for b in EFFECTORS if b in arm.pose.bones}
    for f in range(first, last + 1):
        scene.frame_set(f)
        inv = hips.matrix.inverted()
        for b in forward:
            forward[b].append((inv @ arm.pose.bones[b].matrix).translation.z)

    # Бьющая конечность = та, чей вынос вперёд ВЫСТРЕЛИВАЕТ и тут же
    # УБИРАЕТСЯ: считаем «выступ» пика над впадинами до и после него в окне
    # трети клипа. Ни абсолютный максимум, ни превышение над медианой не
    # годятся: в стойке рука и так висит впереди, а шаг опорной ноги уезжает
    # вперёд и ТАМ И ОСТАЁТСЯ — по обеим меркам он побеждал настоящий удар.
    span = max(4, int(len(next(iter(forward.values()))) * 0.35))

    def prominence(bone):
        d = forward[bone]
        top = max(d)
        at = d.index(top)
        before = d[max(0, at - span):at + 1]
        after = d[at:at + span + 1]
        return min(top - min(before), top - min(after))

    limb = max(forward, key=prominence)
    track = forward[limb]
    return last - first, track.index(max(track)), limb


def main():
    write = "--write" in sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else False
    index = guid_index()
    rows = {}
    for guid, labels in sorted(attack_clip_guids().items(), key=lambda kv: kv[1][0]):
        fbx = index.get(guid)
        if fbx is None or not fbx.endswith(".fbx"):
            print(f"!! клип не найден по guid {guid} ({', '.join(labels)})")
            continue
        clip = os.path.basename(fbx)[: -len(".fbx")]
        if clip in rows:
            rows[clip]["usedBy"] = sorted(set(rows[clip]["usedBy"]) | set(labels))
            continue
        frames, contact, limb = contact_of(fbx)
        rows[clip] = {
            "clip": clip,
            "frames": frames,
            "fps": FPS,
            "seconds": round(frames / FPS, 4),
            "contactFrame": contact,
            "contactFraction": round(contact / frames, 4),
            "strikingLimb": limb,
            "usedBy": sorted(labels),
        }

    print(f"\n{'клип':40s} {'кадров':>7s} {'контакт':>8s} {'доля':>7s}  конечность")
    for r in sorted(rows.values(), key=lambda r: r["clip"]):
        print(f"{r['clip']:40s} {r['frames']:7d} {r['contactFrame']:8d} "
              f"{r['contactFraction']:6.1%}  {r['strikingLimb']}  <- {', '.join(r['usedBy'])}")

    if write:
        payload = {
            "_comment": "Кадр контакта в клипах удара — мерено Tools/measure_strike_contact.py "
                        "(spec §104.8). Правится ТОЛЬКО перезапуском скрипта.",
            "clips": [rows[k] for k in sorted(rows)],
        }
        with open(OUT, "w") as fh:
            json.dump(payload, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        print(f"\nзаписано: {OUT}")


main()

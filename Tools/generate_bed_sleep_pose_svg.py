#!/usr/bin/env python3
"""Draw bug #351 measurements from the real Unity regression XML files."""
import argparse
import html
import re
import xml.etree.ElementTree as ET
from pathlib import Path


def measurements(path):
    root = ET.parse(path).getroot()
    tests = [test for test in root.iter("test-case")
             if "BedSleepPoseRuntimeTests.BothSleepVariants" in test.get("fullname", "")]
    if len(tests) != 1:
        raise ValueError(f"Expected exactly one bed-pose regression in {path}")
    rows = {}
    for line in tests[0].findtext("output", "").splitlines():
        if not (line.startswith("NPC=") or line.startswith("SUPPORT NPC=")):
            continue
        values = dict(re.findall(r"(\w+)=(\([^)]*\)|[^,]+)", line))
        key = (int(values["NPC"]), int(values.get("simYaw", values.get("yaw"))), float(values["phase"]))
        for name, value in values.items():
            if value.startswith("("):
                values[name] = tuple(float(part) for part in value[1:-1].split(","))
        rows.setdefault(key, {}).update(values)
    return rows


def generate(before, after):
    old = measurements(before)
    new = measurements(after)
    if len(new) != 48 or any("mattressMin" not in row for row in new.values()):
        raise ValueError("The corrected set must contain all 48 measured poses and mattress volumes")
    panels = [
        ("До: широкий Bed-клип", old[(1, 0, 0.25)], "Голова и плечи выходят за край на PNG"),
        ("После: Sleep Mirrored", new[(1, 0, 0.25)], "NPC 1 · зеркальный спокойный вариант"),
        ("После: Sleep", new[(2, 0, 0.25)], "NPC 2 · исходный спокойный вариант"),
    ]
    # The bed bundle was unchanged in the local candidate registry. Use its
    # actual mesh bounds; never infer bed placement from the camera image.
    minimum = new[(1, 0, 0.25)]["mattressMin"]
    maximum = new[(1, 0, 0.25)]["mattressMax"]
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="1080" height="650" viewBox="0 0 1080 650">',
           '<rect width="1080" height="650" fill="#f7f5ef"/>',
           '<g font-family="Arial,sans-serif" fill="#20313b">',
           '<text x="30" y="34" font-size="23">Баг #351 — кровать и фактическая ось тела</text>',
           '<text x="30" y="60" font-size="14">Одна фаза 0.25, yaw 0°. Координаты костей относительно production sleep point; масштаб 240 px/wu.</text>']
    for index, (title, row, note) in enumerate(panels):
        cx, cy, scale = 180 + index * 360, 295, 240
        def point(value):
            return cx + value[0] * scale, cy - value[2] * scale
        def rectangle(low, high, colour, opacity):
            x, y = point((low[0], 0, high[2]))
            return (f'<rect x="{x:.2f}" y="{y:.2f}" width="{(high[0]-low[0])*scale:.2f}" '
                    f'height="{(high[2]-low[2])*scale:.2f}" fill="{colour}" fill-opacity="{opacity}" '
                    f'stroke="{colour}" stroke-width="1.5"/>')
        svg.append(f'<text x="{cx}" y="92" text-anchor="middle" font-size="18">{html.escape(title)}</text>')
        svg.append(rectangle(minimum, maximum, "#78946b", ".18"))
        svg.append(f'<line x1="{cx}" y1="105" x2="{cx}" y2="485" stroke="#78946b" stroke-dasharray="5 4"/>')
        if "headMin" in row:
            svg.append(rectangle(row["headMin"], row["headMax"], "#317cac", ".15"))
            svg.append(rectangle(row["torsoMin"], row["torsoMax"], "#317cac", ".15"))
        head, hips, feet = (point(row[name]) for name in ("headLocal", "hipsLocal", "feetLocal"))
        svg.append(f'<line x1="{feet[0]:.2f}" y1="{feet[1]:.2f}" x2="{head[0]:.2f}" y2="{head[1]:.2f}" stroke="#bd573b" stroke-width="2" stroke-dasharray="5 3"/>')
        svg.append(f'<line x1="{hips[0]:.2f}" y1="{hips[1]:.2f}" x2="{head[0]:.2f}" y2="{head[1]:.2f}" stroke="#bd573b" stroke-width="5"/>')
        for x, y in (head, hips, feet):
            svg.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="4" fill="#bd573b"/>')
        svg.append(f'<text x="{cx}" y="515" text-anchor="middle" font-size="17">Голова–таз: {row["torsoAngle"]}°</text>')
        svg.append(f'<text x="{cx}" y="539" text-anchor="middle" font-size="14">Голова–стопы: {row["bedAngle"]}°</text>')
        svg.append(f'<text x="{cx}" y="563" text-anchor="middle" font-size="12">{html.escape(note)}</text>')
    svg.extend([
        '<text x="30" y="597" font-size="13">Зелёный: реальный leaf-mattress. Красный: центры костей. Синий: объёмы головы/торса по BakeMesh и skin weights.</text>',
        '<text x="30" y="618" font-size="13">Это схема измерений, не контур тела; визуальный дефект до исправления подтверждён отдельными URP PNG.</text>',
        '<text x="30" y="639" font-size="12">После: 2 варианта × 6 yaw × 4 фазы = 48 проверок поддержки и высоты. Выбор id % 2 сохранён.</text>',
        '</g></svg>'])
    return "\n".join(svg) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", required=True, type=Path)
    parser.add_argument("--after", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.write_text(generate(args.before, args.after), encoding="utf-8")

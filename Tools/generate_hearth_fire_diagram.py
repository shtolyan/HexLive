"""#491: measured old Unity fire point versus the committed authored anchor."""
import json
import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spatial = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text()
layout = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text()
radius = float(re.search(r'const float HexRadius = ([\d.]+)f', spatial)[1])
ring = int(re.search(r'const int InteriorRadius = (\d+)', layout)[1])
boundary = int(re.search(r'const int BoundaryRadius = (\d+)', layout)[1])
pose = next(d for d in json.loads((ROOT / 'Tools/construction_stage_manifest.json').read_text())['definitions']
            if d['id'] == 'furniture.hearth')['requiredMarkerPoses'][0]
# Production-factory measurement at source 5eead7e53, before the FBX correction.
old_x = 16.0
spacing = radius * math.sqrt(3)
parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="440" viewBox="0 0 1200 440">',
         '<rect width="1200" height="440" fill="#172027"/>',
         '<g font-family="sans-serif" fill="#ecf2f5">',
         '<text x="28" y="36" font-size="23">#491 · Огонь домашнего очага: измерение и исправление</text>',
         '<text x="28" y="72" font-size="17">До: вид сверху, Unity X/Z · масштаб 22 px/wu</text>']
scale, ox, oy = 22, 90, 198
for r in range(-1, 2):
    for q in range(7):
        x, z = spacing * (q + r / 2), radius * 1.5 * r
        points = ' '.join(f'{ox + (x + radius*math.cos(math.radians(30+k*60)))*scale:.2f},{oy + (z + radius*math.sin(math.radians(30+k*60)))*scale:.2f}' for k in range(6))
        parts.append(f'<polygon points="{points}" fill="none" stroke="#42525d"/>')
parts += [f'<circle cx="{ox}" cy="{oy}" r="8" fill="#7bc86d"/>',
          f'<circle cx="{ox+old_x*scale}" cy="{oy}" r="8" fill="#f0665e"/>',
          f'<path d="M{ox} {oy+18}H{ox+old_x*scale}" stroke="#f0665e" stroke-width="2"/>',
          '<text x="35" y="306" font-size="16" fill="#7bc86d">Топка: X = 0</text>',
          f'<text x="285" y="306" font-size="16" fill="#f0665e">Старый огонь: X = +{old_x:g} wu</text>',
          f'<text x="35" y="344" font-size="16">Смещение {old_x:g} wu / шаг центров {spacing:.4f} wu = {old_x/spacing:.2f}</text>',
          '<text x="660" y="72" font-size="17">После: топка и огонь на одном junction</text>']
cx, cy, detail = 900, 198, 100
for r in range(-ring, ring+1):
    for q in range(max(-ring,-r-ring), min(ring,-r+ring)+1):
        grid_radius = radius/(boundary*math.sqrt(3))
        x = grid_radius*1.5*q
        z = grid_radius*math.sqrt(3)*(r+q*.5)
        parts.append(f'<circle cx="{cx+x*detail:.2f}" cy="{cy+z*detail:.2f}" r="3" fill="#536470"/>')
parts += [f'<circle cx="{cx}" cy="{cy}" r="{.32*detail}" fill="none" stroke="#7bc86d" stroke-width="3"/>',
          f'<circle cx="{cx}" cy="{cy}" r="6" fill="#e8b23c"/>',
          f'<text x="660" y="344" font-size="16">{1+3*ring*(ring+1)} узлов · шаг {radius/boundary:g} wu · R гекса = {radius:g} wu</text>',
          f'<text x="660" y="375" font-size="16">fire_point Unity: {pose["unityPosition"]}; +Y вверх</text>',
          '<text x="28" y="412" font-size="15" fill="#b4c4cc">Зелёный — топка; красный — старый маркер; янтарный — исправленный огонь. Точки — мебельная сетка.</text>',
          '</g></svg>']
out = ROOT / 'Docs/Architecture/hearth-fire-anchor.svg'
out.write_text('\n'.join(parts)+'\n', encoding='utf-8')
print(out)

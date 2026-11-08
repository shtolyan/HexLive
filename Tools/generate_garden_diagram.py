"""Draw §173.8 from the committed garden and hex constants."""
import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spatial = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text(encoding='utf-8')
garden = (ROOT / 'Assets/HexLive/Simulation/Runtime/Helpers/GardenMath.cs').read_text(encoding='utf-8')
radius = float(re.search(r'HexRadius = ([\d.]+)f', spatial)[1])
outer = int(re.search(r'const int RadiusTiles = (\d+)', garden)[1])
inner = int(re.search(r'const int MinRadiusTiles = (\d+)', garden)[1])
spacing = radius * math.sqrt(3)
scale, cx, cy = 25, 360, 350
parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1040" height="720" viewBox="0 0 1040 720">',
         '<rect width="1040" height="720" fill="#18232b"/>',
         '<g font-family="sans-serif" fill="#e9eff3">',
         '<text x="28" y="38" font-size="24">Как станет: грядки возле своего лагеря</text>']
tiles = []
for q in range(-outer, outer + 1):
    for r in range(max(-outer, -q - outer), min(outer, -q + outer) + 1):
        d = max(abs(q), abs(r), abs(q+r))
        x, y = cx + spacing * (q + r/2)*scale, cy + radius*1.5*r*scale
        pts = ' '.join(f'{x+radius*math.cos(math.radians(30+k*60))*scale:.2f},{y+radius*math.sin(math.radians(30+k*60))*scale:.2f}' for k in range(6))
        colour = '#32647f' if d >= inner else '#775331'
        parts.append(f'<polygon points="{pts}" fill="{colour}" fill-opacity=".3" stroke="{colour}"/>')
        parts.append(f'<circle cx="{x}" cy="{y}" r="2" fill="#647580"/>')
        if d >= inner: tiles.append((q,r,x,y))
parts.append(f'<circle cx="{cx}" cy="{cy}" r="12" fill="#edbb5d"/>')
parts.append(f'<text x="{cx-30}" y="{cy+33}" font-size="15">Лагерь</text>')
# The examples use actual tile centres, all of which are real junctions.
yucca = sorted([t for t in tiles if t[0] >= 1], key=lambda t:(max(abs(t[0]),abs(t[1]),abs(t[0]+t[1])),t[1],t[0]))[:10]
herbs = sorted([t for t in tiles if t[0] <= -1], key=lambda t:(max(abs(t[0]),abs(t[1]),abs(t[0]+t[1])),t[1],t[0]))[:10]
for group, colour in [(yucca, '#a4d868'), (herbs, '#69cfba')]:
    for _,_,x,y in group:
        parts.append(f'<circle cx="{x}" cy="{y}" r="7" fill="{colour}"/>')
parts += ['<text x="705" y="130" font-size="19" fill="#a4d868">10 кустов юкки</text>',
          '<text x="705" y="165" font-size="19" fill="#69cfba">10 целебных растений</text>',
          '<text x="705" y="225" font-size="16">Синий: кольца посадки 2–4</text>',
          '<text x="705" y="255" font-size="16">Оранжевый: центр лагеря</text>',
          '<text x="705" y="315" font-size="16">Вода, пол и препятствия</text>',
          '<text x="705" y="340" font-size="16">исключаются при выборе.</text>',
          '<text x="705" y="390" font-size="16">Ростки и начатые посадки</text>',
          '<text x="705" y="415" font-size="16">уже входят в цель 10.</text>',
          f'<text x="28" y="660" font-size="17">R = {radius:g} wu · шаг центров = {spacing:.3f} wu · минимум между растениями = {radius*.5:g} wu</text>',
          '<text x="28" y="691" font-size="15">Пример допустимых точек. ИИ выбирает реальные свободные и безопасные узлы по топологии мира.</text>',
          '</g></svg>']
out = ROOT / 'Docs/GardenDiagram.svg'
out.write_text('\n'.join(parts)+'\n', encoding='utf-8')
print(out)

"""Draw §133.12 from the actual theft radius and hex layout constants."""
from pathlib import Path
import math
import re

ROOT = Path(__file__).resolve().parents[1]
theft = (ROOT / 'Assets/HexLive/Simulation/Runtime/Helpers/TheftMath.cs').read_text(encoding='utf-8')
layout = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text(encoding='utf-8')
radius = int(re.search(r'PrivateGroundRadiusTiles = (\d+)', theft)[1])
size = float(re.search(r'HexRadius = ([\d.]+)f', layout)[1])
width = float(re.search(r'Sqrt3 = ([\d.]+)f', layout)[1])
row = float(re.search(r'HexRowStepFactor = ([\d.]+)f', layout)[1])
scale = 17
def point(q, r):
    return 265 + size * width * (q + r / 2) * scale, 275 + size * row * r * scale

out = ['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="545" viewBox="0 0 1000 545">',
       '<rect width="1000" height="545" fill="#131b29"/>',
       '<g font-family="Arial,sans-serif" fill="#e2e8f0">',
       '<text x="24" y="30" font-size="21">AI в нейтральном чужом лагере — после исправления #463</text>']
cells = [(q, r) for q in range(-radius, radius+1) for r in range(-radius, radius+1)
         if max(abs(q), abs(r), abs(q+r)) <= radius]
for q, r in cells:
    x, y = point(q, r)
    points = ' '.join(f'{x+size*scale*math.cos(math.radians(30+60*i)):.2f},{y+size*scale*math.sin(math.radians(30+60*i)):.2f}' for i in range(6))
    out.append(f'<polygon points="{points}" fill="#532b3a" stroke="#775268" stroke-width="0.6"/>')
    out.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="1.6" fill="#a28e9f"/>')
x,y=point(0,0)
out += [f'<circle cx="{x}" cy="{y}" r="11" fill="#ffbc68"/>',
        f'<text x="{x+18}" y="{y+5}" font-size="15">Якорь лагеря</text>']
for q,r,label in [(2,0,'Сушилка: не использовать'), (-3,1,'Припасы: не подбирать')]:
    x,y=point(q,r)
    out += [f'<circle cx="{x:.2f}" cy="{y:.2f}" r="7" fill="#ff747f"/>',
            f'<text x="{x-60:.2f}" y="{y-16:.2f}" font-size="13">{label}</text>']
x,y=point(1,-3)
out += [f'<circle cx="{x:.2f}" cy="{y:.2f}" r="7" fill="#70d6a7"/>',
        f'<text x="{x-70:.2f}" y="{y-15:.2f}" font-size="13">Свою вещь можно вернуть</text>']
lines = [f'Защищённая зона: {radius} гексов от якоря',
         f'HexRadius = {size:g} wu; {len(cells)} центров тайлов',
         'Это запрет бытовых действий, не прохода.', '',
         '• Не брать нейтральные чужие припасы',
         '• Не пользоваться чужой сушилкой / станцией',
         '• Не выкладывать вещи на чужую землю', '',
         '• Свои вещи можно вернуть',
         '• Свой лагерь: обычная сушка и уборка',
         '• Явный приказ игрока: прежние правила', '',
         'Проверка перед выбором и исполнением.',
         'Снятые вещи и карманы сохраняются у NPC.',
         'Кража по приказу всё ещё ухудшает отношения.']
for i,line in enumerate(lines):
    out.append(f'<text x="545" y="{92+i*26}" font-size="16">{line}</text>')
out += ['<text x="24" y="526" font-size="13">Расчёт: TheftMath.PrivateGroundRadiusTiles; HexSpatialMath.TileToWorld; hex-distance ≤ radius.</text>', '</g></svg>']
(ROOT/'Docs/ForeignCampProperty.svg').write_text('\n'.join(out)+'\n',encoding='utf-8')
print(f'ForeignCampProperty.svg: radius={radius}, HexRadius={size}, tiles={len(cells)}')

"""#638: exact point planting, calculated from canonical hex constants."""
import math
import re
from pathlib import Path

root = Path(__file__).resolve().parents[1]
spatial = (root / 'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text(encoding='utf-8')
layout = (root / 'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text(encoding='utf-8')
radius = float(re.search(r'HexRadius = ([\d.]+)f', spatial)[1])
interior = int(re.search(r'InteriorRadius = (\d+)', layout)[1])
boundary = int(re.search(r'BoundaryRadius = (\d+)', layout)[1])
grid_radius = radius / (boundary * math.sqrt(3))
points = [(q, r) for q in range(-boundary, boundary+1) for r in range(-boundary, boundary+1)
          if max(abs(q), abs(r), abs(q+r)) <= boundary]
inner_count = sum(max(abs(q), abs(r), abs(q+r)) <= interior for q, r in points)
assert inner_count == 37 and len(points)-inner_count == 24
parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="520" viewBox="0 0 1000 520">',
         '<rect width="1000" height="520" fill="#14242c"/>',
         '<style>text{fill:#edf5f6;font:17px sans-serif}.small{font-size:14px}</style>',
         '<defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0L0,6L8,3z" fill="#67bded"/></marker></defs>',
         '<text x="24" y="34">#638 — посадки выбирают точку, мебель сохраняет свой гекс</text>']
for cx, title, old in [(250, 'Было: одна посадка в центре', True), (750, 'Стало: несколько точных посадок', False)]:
    cy, scale = 265, 105
    parts.append(f'<text x="{cx-195}" y="78">{title}</text>')
    outline = ' '.join(f'{cx+radius*math.cos(math.radians(30+k*60))*scale:.2f},{cy+radius*math.sin(math.radians(30+k*60))*scale:.2f}' for k in range(6))
    parts.append(f'<polygon points="{outline}" fill="#235a77" fill-opacity=".22" stroke="#67bded"/>')
    for q, r in points:
        x, y = cx+grid_radius*1.5*q*scale, cy+grid_radius*math.sqrt(3)*(r+q*.5)*scale
        rim = max(abs(q),abs(r),abs(q+r)) == boundary
        planted = (q,r) == (0,0) if old else (q,r) in [(-2,0),(0,1),(2,-1)]
        blocked = not old and (q,r) == (0,-2)
        colour = '#e98770' if blocked else '#eeb35a' if planted else '#4f7787' if not rim else '#ac8153'
        parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{8 if planted else 4}" fill="{colour}"/>')
    if not old:
        x, y = cx, cy+grid_radius*math.sqrt(3)*scale
        parts.append(f'<path d="M{cx+115},{cy+118} L{x+4},{y+6}" stroke="#67bded" stroke-width="3" marker-end="url(#arrow)"/>')
        parts.append(f'<text x="{cx+36}" y="{cy+143}" class="small">Курсор → ближайший узел</text>')
parts += [f'<text x="24" y="450">R={radius:g} wu · шаг={radius/boundary:g} wu · {inner_count} внутренних + {len(points)-inner_count} граничных узла</text>',
          '<text x="24" y="480" class="small">Синий: сухой гекс · Оранжевый: занятая точка/общая граница · Красный: блокировка</text>',
          '<text x="24" y="507" class="small">Посадки не исключают целый гекс. Занятую точку, воду, чужой участок и препятствие сервер отклонит.</text>', '</svg>']
output = root / 'Docs/PointPlanting638.svg'
output.write_text('\n'.join(parts)+'\n', encoding='utf-8')
print(output)

"""#643: render actual canonical test-world portal/obstacles/path, never a guessed hut."""
import json
import re
import sys
from pathlib import Path
root=Path(__file__).resolve().parents[1]
source=Path(sys.argv[1]) if len(sys.argv)>1 else root/'Build/Diagnostics/wave2-colony/643-spatial.json'
data=json.loads(source.read_text())
points=data['points']; route=data['route']
layout=(root/'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text(encoding='utf-8')
interior=int(re.search(r'InteriorRadius = (\d+)',layout)[1]); boundary=int(re.search(r'BoundaryRadius = (\d+)',layout)[1])
assert len(points)==1+3*boundary*(boundary+1)==61
assert interior==3 and boundary==4
queries=(root/'Assets/HexLive/Simulation/Spatial/SpatialQueries.cs').read_text(encoding='utf-8')
reach=float(re.search(r'StandStepAllowance = ([\d.]+)f',queries)[1])
rules=(root/'Assets/HexLive/Simulation/Runtime/Helpers/DoorInteractionMath.cs').read_text(encoding='utf-8')
knock=int(re.search(r'KnockTicks = (\d+)',rules)[1]); hearing=int(re.search(r'HearingRadiusTiles = (\d+)',rules)[1])
portal=next(p for p in points if p['portal'])
xs=[p['x'] for p in points+route]; ys=[p['y'] for p in points+route]
cx=(max(xs)+min(xs))/2; cy=(max(ys)+min(ys))/2
scale=100
xy=lambda p:(350+(p['x']-cx)*scale,280-(p['y']-cy)*scale)
parts=['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="560" viewBox="0 0 1000 560">',
'<rect width="1000" height="560" fill="#14242c"/>',
'<style>text{fill:#edf5f6;font:18px sans-serif}.small{font-size:15px}</style>',
'<defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0L0,6L8,3z" fill="#67bded"/></marker></defs>',
'<text x="22" y="32">#643 — подход к настоящей двери без прохода сквозь закрытую створку</text>']
x,y=xy(portal)
parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{reach*scale:.2f}" fill="#67bded" fill-opacity=".10" stroke="#67bded" stroke-dasharray="5 5"/>')
for p in points:
 x,y=xy(p); colour='#eeb35a' if p['portal'] else '#e98770' if p['blocked'] else '#7dabb3'
 parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{9 if p["portal"] else 5}" fill="{colour}"/>')
if len(route)>1:
 coordinates=' '.join(f'{xy(p)[0]:.2f},{xy(p)[1]:.2f}' for p in route)
 parts.append(f'<polyline points="{coordinates}" fill="none" stroke="#67bded" stroke-width="4" marker-end="url(#arrow)"/>')
parts+=['<text x="620" y="125">Оранжевый: закрытый портал</text>',
'<text x="620" y="158">Красный: реальные блокировки</text>',
'<text x="620" y="191">Синий: маршрут до обода</text>',
f'<text x="620" y="224">Обод: не дальше {reach:.2f} wu</text>',
f'<text x="620" y="270">Стук: {knock} тиков</text>',
f'<text x="620" y="303">Ответ слышен в радиусе {hearing} гекса</text>',
'<text x="620" y="346">Взлом: работа четырёх пальм</text>',
'<text x="620" y="379">Урон и стресс — уже с попытки</text>',
f'<text x="22" y="494">R={data["radius"]:g} wu · шаг={data["radius"]/boundary:g} wu · 37 внутренних + 24 граничных узла</text>',
'<text x="22" y="530" class="small">Координаты и маршрут: DoorInteractionTests → WorldStateFactory → BuildingRules → HexPathfinder.</text>','</svg>']
(root/'Docs/DoorInteraction643.svg').write_text('\n'.join(parts)+'\n',encoding='utf-8')

"""#467: the fixed one-tile-radius gather region, from real hex constants."""
from pathlib import Path
import math
import re

ROOT = Path(__file__).resolve().parents[2]
spatial = (ROOT/'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text()
layout = (ROOT/'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text()
def number(text, name):
    return float(re.search(r'const (?:float|int) '+name+r' = ([\d.]+)f?;', text)[1])
radius=number(spatial,'HexRadius'); sqrt3=number(spatial,'Sqrt3')
boundary=int(number(layout,'BoundaryRadius')); interior=int(number(layout,'InteriorRadius'))
scale=42; cx=400; cy=350
def center(q,r): return radius*sqrt3*(q+r*.5),radius*1.5*r
def screen(x,z): return cx+x*scale,cy-z*scale
nodes=[(radius/(boundary*sqrt3)*1.5*q,radius/boundary*(r+q*.5))
       for r in range(-boundary,boundary+1)
       for q in range(max(-boundary,-r-boundary),min(boundary,-r+boundary)+1)]
svg=['<svg xmlns="http://www.w3.org/2000/svg" width="810" height="760" viewBox="0 0 810 760">',
     '<rect width="810" height="760" fill="#15211e"/><g font-family="Arial" fill="#f1f6ed">',
     '<text x="25" y="37" font-size="23">«Собрать всё»: исходный гекс + 6 соседей</text>',
     '<text x="25" y="66" font-size="16">После каждого сбора — новая ближайшая цель внутри этой области.</text>']
for r in range(-2,3):
    for q in range(max(-2,-r-2),min(2,-r+2)+1):
        ring=max(abs(q),abs(r),abs(q+r)); x,z=center(q,r); selected=ring<=1
        polygon=' '.join(f'{screen(x+radius*math.cos(math.radians(30+60*i)),z+radius*math.sin(math.radians(30+60*i)))[0]:.3f},{screen(x+radius*math.cos(math.radians(30+60*i)),z+radius*math.sin(math.radians(30+60*i)))[1]:.3f}' for i in range(6))
        svg.append(f'<polygon points="{polygon}" fill="{"#22445a" if ring==0 else "#29432a" if selected else "#1b2923"}" stroke="{"#69b3d8" if ring==0 else "#95c574" if selected else "#be9563"}" stroke-width="1.5"/>')
        if selected:
            for nx,nz in nodes:
                px,py=screen(x+nx,z+nz)
                svg.append(f'<circle cx="{px:.3f}" cy="{py:.3f}" r="1.5" fill="#557660"/>')
        px,py=screen(x,z)
        svg.append(f'<text x="{px:.3f}" y="{py+6:.3f}" text-anchor="middle" font-size="15">{"Центр" if ring==0 else "Сбор" if selected else "Вне сбора"}</text>')
svg.extend([
    '<text x="25" y="657" font-size="16">Синий: гекс кликнутого ресурса. Зелёный: шесть соседних.</text>',
    '<text x="25" y="683" font-size="16">Оранжевая граница: второй круг, не собираем. Центр не следует за NPC.</text>',
    f'<text x="25" y="712" font-size="16">R={radius:g} wu, шаг {radius/boundary:g} wu; {1+3*interior*(interior+1)} внутренних + {6*boundary} граничных узла.</text>',
    '<text x="25" y="740" font-size="14">Схема области без препятствий; реальные занятые и недоступные цели проверяет исполнение.</text>',
    '</g></svg>'])
(ROOT/'Docs/GatherFlower467.svg').write_text('\n'.join(svg),encoding='utf-8')
print('Seven fixed tiles; outer ring excluded; nodes per tile:',len(nodes))

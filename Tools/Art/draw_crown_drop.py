"""#468: measured crown bounds, old 20-slot reserve and the actual single drop."""
from pathlib import Path
import math
import re

ROOT=Path(__file__).resolve().parents[2]
catalog=(ROOT/'Assets/HexLive/Simulation/Content/GroundPileCatalog.cs').read_text()
spatial=(ROOT/'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text()
layout=(ROOT/'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text()
def number(text,name):
    return float(re.search(r'const (?:float|int) '+name+r' = ([\d.]+)f?;',text)[1])
radius=number(spatial,'HexRadius');sqrt3=number(spatial,'Sqrt3');boundary=int(number(layout,'BoundaryRadius'))
values=re.search(r'Profiles\["resource.palm_crown"\]=new\("resource.palm_crown",new\(([^)]+)\)',catalog)[1]
single=[float(v.rstrip('f')) for v in values.split(',')]
assert re.search(r'"resource.palm_crown" or "resource.palm_crown_small" => 1',catalog)
def bounds(count):
    columns=math.ceil(count**(1/3));pitchx=single[3]-single[0]+.01;pitchz=single[5]-single[2]+.01
    slots=[((i%columns-(columns-1)*.5)*pitchx,(i//columns%columns-(columns-1)*.5)*pitchz) for i in range(count)]
    return min(x for x,z in slots)+single[0],min(z for x,z in slots)+single[2],max(x for x,z in slots)+single[3],max(z for x,z in slots)+single[5]
old=bounds(20);new=bounds(1)
svg=['<svg xmlns="http://www.w3.org/2000/svg" width="1060" height="600" viewBox="0 0 1060 600">',
     '<rect width="1060" height="600" fill="#15211e"/><g font-family="Arial" fill="#f1f6ed">',
     '<text x="25" y="36" font-size="24">Выбросить одну крону: резервировать её реальный размер</text>']
scale=31
for cx,box,title,color in [(265,old,'Было: место под 20 крон','#ff8d83'),(795,new,'После: место под одну крону','#a9e36f')]:
    cy=285
    svg.append(f'<text x="{cx-230}" y="78" font-size="20">{title}</text>')
    for r in range(-1,2):
        for q in range(max(-1,-r-1),min(1,-r+1)+1):
            x=radius*sqrt3*(q+r*.5);z=radius*1.5*r
            polygon=' '.join(f'{cx+(x+radius*math.cos(math.radians(30+60*i)))*scale:.3f},{cy-(z+radius*math.sin(math.radians(30+60*i)))*scale:.3f}' for i in range(6))
            svg.append(f'<polygon points="{polygon}" fill="none" stroke="#577466" stroke-width="1"/>')
            for sr in range(-boundary,boundary+1):
                for sq in range(max(-boundary,-sr-boundary),min(boundary,-sr+boundary)+1):
                    nx=radius/(boundary*sqrt3)*1.5*sq;nz=radius/boundary*(sr+sq*.5)
                    svg.append(f'<circle cx="{cx+(x+nx)*scale:.3f}" cy="{cy-(z+nz)*scale:.3f}" r="1.1" fill="#486452"/>')
    svg.append(f'<rect x="{cx+box[0]*scale:.3f}" y="{cy-box[3]*scale:.3f}" width="{(box[2]-box[0])*scale:.3f}" height="{(box[3]-box[1])*scale:.3f}" fill="{color}" fill-opacity=".12" stroke="{color}" stroke-width="2"/>')
    svg.append(f'<circle cx="{cx}" cy="{cy}" r="5" fill="#69b3d8"/><text x="{cx-10}" y="{cy-13}" text-anchor="end" font-size="14">Свой NPC / якорь</text>')
    # A real lattice offset nearest (3.5,0); the regression places a coconut here.
    nx=radius/(boundary*sqrt3)*1.5*11;nz=radius/boundary*(-5+11*.5)
    svg.append(f'<circle cx="{cx+nx*scale:.3f}" cy="{cy-nz*scale:.3f}" r="4" fill="#e8b56e"/><text x="{cx+nx*scale:.3f}" y="{cy-nz*scale+24:.3f}" text-anchor="middle" font-size="13">Кокос ~3,5 wu</text>')
    svg.append(f'<text x="{cx-225}" y="493" font-size="16">{box[2]-box[0]:.3f} × {box[3]-box[1]:.3f} wu</text>')
svg.extend([
    '<text x="25" y="531" font-size="16">Свой якорь разрешён; чужая занятая точка и реальное пересечение по-прежнему запрещены.</text>',
    '<text x="25" y="557" font-size="16">Крона не уменьшается; инвентарная стопка остаётся прежней. Серые точки: штатная сетка.</text>',
    f'<text x="25" y="584" font-size="15">R={radius:g} wu, шаг {radius/boundary:g} wu; 37 внутренних + 24 граничных узла. Геометрическая регрессия, не live кадр.</text>',
    '</g></svg>'])
(ROOT/'Docs/CrownDrop468.svg').write_text('\n'.join(svg),encoding='utf-8')
print(f'Old reserve {old[2]-old[0]:.6f}x{old[3]-old[1]:.6f}; single {new[2]-new[0]:.6f}x{new[3]-new[1]:.6f} wu')

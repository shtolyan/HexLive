"""#653: invitation range and scoped control, from canonical spatial constants."""
import math
import re
from pathlib import Path

root=Path(__file__).resolve().parents[1]
spatial=(root/'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text(encoding='utf-8')
layout=(root/'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text(encoding='utf-8')
reach=(root/'Assets/HexLive/Simulation/Runtime/Helpers/InteractionReach.cs').read_text(encoding='utf-8')
radius=float(re.search(r'HexRadius = ([\d.]+)f',spatial)[1])
ring=int(re.search(r'BoundaryRadius = (\d+)',layout)[1])
inner=int(re.search(r'InteriorRadius = (\d+)',layout)[1])
talk=radius*float(re.search(r'Talk => HexSpatialMath.HexRadius \* ([\d.]+)f',reach)[1])
scale,cx,cy=65,250,260
parts=['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="530" viewBox="0 0 1000 530">',
       '<rect width="1000" height="530" fill="#14242c"/>',
       '<style>text{fill:#edf5f6;font:17px sans-serif}.small{font-size:14px}</style>',
       '<defs><marker id="a" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0L0,6L8,3z" fill="#67bded"/></marker></defs>',
       '<text x="22" y="35">#653 — временный отряд по согласию</text>',
       f'<circle cx="{cx}" cy="{cy}" r="{talk*scale}" fill="#235a77" fill-opacity=".35" stroke="#67bded"/>']
for q in range(-ring,ring+1):
    for r in range(max(-ring,-q-ring),min(ring,-q+ring)+1):
        x=cx+radius/(ring*math.sqrt(3))*1.5*q*scale
        y=cy+radius/ring*(r+q*.5)*scale
        colour='#a17b52' if max(abs(q),abs(r),abs(q+r))>inner else '#5c7786'
        parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="3" fill="{colour}"/>')
mx,my=cx+radius/(ring*math.sqrt(3))*3*scale,cy
parts += [f'<circle cx="{cx}" cy="{cy}" r="9" fill="#edb258"/>',
          f'<text x="{cx-35}" y="{cy+35}">Лидер</text>',
          f'<circle cx="{mx}" cy="{my}" r="9" fill="#4b9fff"/>',
          f'<path d="M{mx},{my-22} L{mx+48},{my-75}" stroke="#67bded" stroke-width="2" marker-end="url(#a)"/>',
          f'<text x="{mx+27}" y="{my-85}">Спутник</text>',
          '<text x="500" y="115">Доверие спутника к лидеру &gt;60%</text>',
          f'<text x="500" y="153">Приглашение рядом: 2R = {talk:g} wu</text>',
          '<text x="500" y="205">Синий: радиус разговора и свой grant</text>',
          '<text x="500" y="240">Оранжевый: занятый узел / граница</text>',
          '<circle cx="515" cy="280" r="7" fill="#e98770"/>',
          '<text x="535" y="286">Чужой игрок — доступ запрещён</text>',
          '<text x="500" y="340">Групповые приказы → обычный pipeline</text>',
          '<text x="500" y="380">ИИ / отпуск / disconnect / timeout:</text>',
          '<text x="500" y="412">grant закрывается, спутник живёт сам</text>',
          f'<text x="22" y="489">R={radius:g} wu · шаг={radius/ring:g} wu · 37 внутренних + 24 граничных узла</text>',
          '<text x="22" y="518" class="small">Владение лагерем, вещами и постоянные назначения не меняются.</text>','</svg>']
(root/'Docs/TravelParty653.svg').write_text('\n'.join(parts)+'\n',encoding='utf-8')

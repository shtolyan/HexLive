# Draw from HexPointLayout/HexSpatialMath and the pre-fix GroundPileProfile slot formula.
from pathlib import Path
import math
pitch=math.sqrt(3)*1.05+.01
scale=68
svg=['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="460" viewBox="0 0 1000 460"><rect width="1000" height="460" fill="#14211d"/><g font-family="Arial" fill="#eef5ed"><text x="35" y="40" font-size="24">Рубка пальмы: опора каждого предмета на земле</text>']
for origin,label in [(250,'Было: первые 3 слота стопки'),(745,'Стало: отдельный предмет на каждом узле')]:
 svg.append(f'<text x="{origin-215}" y="80" font-size="17">{label}</text>')
 # HexPointLayout: interior axial radius 3 (37), boundary radius 4 (24).
 for q in range(-4,5):
  for r in range(max(-4,-q-4),min(4,-q+4)+1):
   x=1.5/(4*math.sqrt(3))*1.5*q
   z=1.5/4*(r+q*.5)
   svg.append(f'<circle cx="{origin+x*scale:.2f}" cy="{260+z*scale:.2f}" r="2" fill="#729780"/>')
 points=' '.join(f'{origin+1.5*scale*math.cos(math.radians(30+60*i)):.2f},{260+1.5*scale*math.sin(math.radians(30+60*i)):.2f}' for i in range(6))
 svg.append(f'<polygon points="{points}" fill="none" stroke="#729780" stroke-width="2"/>')
 svg.append(f'<circle cx="{origin}" cy="260" r="5" fill="#97ed5d"/>')
for i in range(3):
 x=250+(i-1)*pitch*scale;y=260-pitch*scale
 svg.append(f'<path d="M250 260L{x:.2f} {y:.2f}" stroke="#ed9569" stroke-dasharray="4 5"/><rect x="{x-1.05*scale/2:.2f}" y="{y-9:.2f}" width="{1.05*scale:.2f}" height="18" rx="7" fill="#b18b58"/>')
yaw=((123*2654435761 & 0xffffffff)*1664525+1013904223)&0xffffffff
yaw=(yaw>>8)/16777216*360
svg.append(f'<g transform="rotate({yaw:.3f} 745 260)"><rect x="{745-1.05*scale/2:.2f}" y="251" width="{1.05*scale:.2f}" height="18" rx="7" fill="#b18b58"/></g>')
svg.append('<text x="35" y="405" font-size="16">37 внутренних + 24 граничных узла; шаг 0,375 wu; радиус гекса 1,5 wu; бревно 1,05 wu.</text>')
svg.append(f'<text x="35" y="434" font-size="16">Сдвиг старого слота: {pitch:.5f} wu по оси; новый: 0. Поворот стабилен по ObjectId.</text></g></svg>')
Path('Docs/PalmHarvest429.svg').write_text(''.join(svg))

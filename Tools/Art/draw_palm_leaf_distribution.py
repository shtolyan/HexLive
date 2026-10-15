"""Source-only #429 diagram; no Unity, asset build or publication.

Reads the real hex constants and measured palm-leaf profile. The full yield
uses the same least-count / farthest-point rule as GroundItemPlacement.
Node ties in this illustration use HexPointLayout template order.
"""
from pathlib import Path
import math
import re

ROOT = Path(__file__).resolve().parents[2]
spatial = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text()
layout = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text()
catalog = (ROOT / 'Assets/HexLive/Simulation/Content/GroundPileCatalog.cs').read_text()


def constant(text, name):
    return float(re.search(r'const (?:float|int) ' + name + r' = ([\d.]+)f?;', text)[1])


radius = constant(spatial, 'HexRadius')
sqrt3 = constant(spatial, 'Sqrt3')
interior = int(constant(layout, 'InteriorRadius'))
boundary = int(constant(layout, 'BoundaryRadius'))
single = [float(v.rstrip('f')) for v in re.search(
    r'Profiles\[ContentIds.PalmLeaf\]=new\(ContentIds.PalmLeaf,new\(([^)]+)\)', catalog)[1].split(',')]
leaf_radius = math.hypot(max(abs(single[0]), abs(single[3])), max(abs(single[2]), abs(single[5])))
limit = radius * sqrt3 / 2 - leaf_radius - .01
nodes = []
for r in range(-boundary, boundary + 1):
    for q in range(max(-boundary, -r-boundary), min(boundary, -r+boundary)+1):
        ring = max(abs(q), abs(r), abs(q+r))
        x = radius / (boundary * sqrt3) * 1.5 * q
        z = radius / boundary * (r + q * .5)
        fits = max(abs(x), abs(.5*x+sqrt3*.5*z), abs(.5*x-sqrt3*.5*z)) <= limit
        nodes.append((x, z, ring <= interior, fits))
allowed = [(x, z) for x, z, _, fits in nodes if fits]
counts = [0] * len(allowed)
for _ in range(42):
    def score(i):
        x, z = allowed[i]
        separation = min(((x-a)**2+(z-b)**2 for (a, b), n in zip(allowed, counts) if n), default=math.inf)
        return counts[i], -separation, x*x+z*z, i
    counts[min(range(len(allowed)), key=score)] += 1

scale = 118
svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="1050" height="550" viewBox="0 0 1050 550">',
       '<rect width="1050" height="550" fill="#14211d"/>',
       '<g font-family="Arial" fill="#eef5ed">',
       '<text x="30" y="40" font-size="23">Листья после рубки: распределение внутри исходного гекса</text>']
for cx, title, before in [(265, 'Было: все 42 листа в одной точке', True),
                           (790, 'Станет: равномерно по доступным точкам', False)]:
    svg.append(f'<text x="{cx-230}" y="80" font-size="17">{title}</text>')
    polygon = ' '.join(f'{cx+radius*scale*math.cos(math.radians(30+60*i)):.3f},{285+radius*scale*math.sin(math.radians(30+60*i)):.3f}' for i in range(6))
    svg.append(f'<polygon points="{polygon}" fill="#1b3026" stroke="#8db8a0" stroke-width="2"/>')
    for x, z, _, fits in nodes:
        color = '#7eab97' if fits else '#dca267'
        svg.append(f'<circle cx="{cx+x*scale:.3f}" cy="{285+z*scale:.3f}" r="2.4" fill="{color}"/>')
    draw = [(0, 0, 42)] if before else [(x, z, n) for (x, z), n in zip(allowed, counts)]
    for x, z, n in draw:
        px, py = cx+x*scale, 285+z*scale
        svg.append(f'<circle cx="{px:.3f}" cy="{py:.3f}" r="{leaf_radius*scale:.3f}" fill="#a3e35a" fill-opacity=".08" stroke="#73ac63" stroke-opacity=".3"/>')
        svg.append(f'<ellipse cx="{px:.3f}" cy="{py:.3f}" rx="{single[3]*scale:.3f}" ry="{single[5]*scale:.3f}" fill="#9bdd61" fill-opacity=".45"/>')
        svg.append(f'<text x="{px:.3f}" y="{py+5:.3f}" text-anchor="middle" font-size="14">{n}</text>')
svg.extend([
    f'<text x="30" y="475" font-size="16">Гекс: R={radius:g} wu; сетка: {sum(n[2] for n in nodes)} внутренних + {sum(not n[2] for n in nodes)} граничных узла; шаг {radius/boundary:g} wu.</text>',
    f'<text x="30" y="504" font-size="16">{len(allowed)} допустимых точек; лист: 0,825 wu, защитный радиус {leaf_radius:.4f} wu + край 0,01 wu.</text>',
    '<text x="30" y="532" font-size="15">Оранжевые узлы: лист пересёк бы край. Круги: защитный размер листа. Числа: количество листьев.</text>',
    '</g></svg>'])
(ROOT / 'Docs/PalmLeafDistribution429.svg').write_text('\n'.join(svg), encoding='utf-8')
print(f'42 leaves / {len(allowed)} anchors; layers {min(counts)}..{max(counts)}; leaf radius {leaf_radius:.6f} wu')

"""#466: real lattice geometry and measured 0.1-wu release hop padding.

The five-node route is translated from the saved camp to (0,0), not drawn
by eye. No world save or private evidence is embedded in the SVG.
"""
from pathlib import Path
import math
import re

ROOT = Path(__file__).resolve().parents[2]
spatial = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text()
layout = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text()


def constant(text, name):
    return float(re.search(r'const (?:float|int) ' + name + r' = ([\d.]+)f?;', text)[1])


radius, sqrt3 = constant(spatial, 'HexRadius'), constant(spatial, 'Sqrt3')
interior, boundary = int(constant(layout, 'InteriorRadius')), int(constant(layout, 'BoundaryRadius'))
padding = .1  # measured simdata override; not the default HexHopTuning initializer


def tile(q, r):
    return radius * sqrt3 * (q + r * .5), radius * 1.5 * r


def point(q, r):
    return radius / (boundary * sqrt3) * 1.5 * q, radius / boundary * (r + q * .5)


def add(a, b):
    return a[0] + b[0], a[1] + b[1]


near, far, third = tile(0, 0), tile(-1, 0), tile(-1, 1)
route = [point(-3, 2), point(-3, 3), point(-4, 4), add(far, point(3, 0)), add(far, point(3, -1))]


def hop(origin):
    crossing = tuple((a + b) / 2 for a, b in zip(origin, far))
    length = math.dist(origin, far)
    direction = tuple((b - a) / length for a, b in zip(origin, far))
    along = sum((p - c) * d for p, c, d in zip(route[0], crossing, direction))
    takeoff = tuple(c + d * max(-padding, min(-.02, along)) for c, d in zip(crossing, direction))
    landing = tuple(c + d * padding for c, d in zip(crossing, direction))
    return takeoff, landing


nodes = [point(q, r) for r in range(-boundary, boundary + 1)
         for q in range(max(-boundary, -r-boundary), min(boundary, -r+boundary)+1)]
assert len(nodes) == 61
svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="820" viewBox="0 0 1280 820">',
       '<defs><marker id="red" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L0,6 L7,3 Z" fill="#ff8d83"/></marker>',
       '<marker id="green" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L0,6 L7,3 Z" fill="#a9e36f"/></marker></defs>',
       '<rect width="1280" height="820" fill="#15211e"/>',
       '<g font-family="Arial" fill="#f1f6ed"><text x="30" y="42" font-size="24">Ника: ложный переход через третий гекс в общей вершине</text>']
scale = 85
for origin_x, old in [(460, True), (1090, False)]:
    def screen(p):
        return origin_x + p[0] * scale, 410 - p[1] * scale
    svg.append(f'<text x="{origin_x-420}" y="83" font-size="20">{"Было: крюк и обратный поворот" if old else "После: переход между гексами маршрута"}</text>')
    for center, name, level in [(near, 'Исходный', 1), (far, 'Целевой', 2), (third, 'Третий — вне маршрута', 1)]:
        polygon = ' '.join(f'{screen(add(center,(radius*math.cos(math.radians(30+60*i)),radius*math.sin(math.radians(30+60*i)))))[0]:.3f},{screen(add(center,(radius*math.cos(math.radians(30+60*i)),radius*math.sin(math.radians(30+60*i)))))[1]:.3f}' for i in range(6))
        svg.append(f'<polygon points="{polygon}" fill="{"#2b422b" if level == 2 else "#1e302a"}" stroke="#7c9b8e" stroke-width="1.5"/>')
        for n in nodes:
            x, y = screen(add(center, n))
            svg.append(f'<circle cx="{x:.3f}" cy="{y:.3f}" r="2" fill="#577466"/>')
        x, y = screen(center)
        svg.append(f'<text x="{x:.3f}" y="{y+45:.3f}" text-anchor="middle" font-size="14">{name}</text><text x="{x:.3f}" y="{y+64:.3f}" text-anchor="middle" font-size="13">Высота {level}</text>')
    coordinates = ' '.join(f'{screen(p)[0]:.3f},{screen(p)[1]:.3f}' for p in route)
    svg.append(f'<polyline points="{coordinates}" fill="none" stroke="#69b3d8" stroke-width="3" stroke-dasharray="5 4"/>')
    for i, p in enumerate(route):
        x, y = screen(p)
        svg.append(f'<circle cx="{x:.3f}" cy="{y:.3f}" r="4" fill="#69b3d8"/><text x="{x+5:.3f}" y="{y-8:.3f}" font-size="13">{i}</text>')
    takeoff, landing = hop(third if old else near)
    color, marker = ('#ff8d83', 'red') if old else ('#a9e36f', 'green')
    for a, b in [(route[0], takeoff), (takeoff, landing), (landing, route[-1])]:
        x1, y1 = screen(a); x2, y2 = screen(b)
        svg.append(f'<line x1="{x1:.3f}" y1="{y1:.3f}" x2="{x2:.3f}" y2="{y2:.3f}" stroke="{color}" stroke-width="3" marker-end="url(#{marker})"/>')
    x, y = screen(takeoff)
    svg.append(f'<circle cx="{x:.3f}" cy="{y:.3f}" r="6" fill="{color}"/><text x="{x-12:.3f}" y="{y-17:.3f}" text-anchor="end" font-size="13">Отрыв</text>')
    svg.append(f'<text x="{origin_x-420}" y="580" font-size="16">{"Взлёт ошибочно: третий → целевой гекс" if old else "Взлёт: исходный → целевой гекс"}</text>')
    svg.append(f'<text x="{origin_x-420}" y="608" font-size="15">{"Направление меняется на 104,7° перед прыжком" if old else "Касание угла сохраняет исходную сторону"}</text>')
svg.extend([
    '<text x="30" y="660" font-size="16">Синий пунктир: неизменный маршрут из 5 узлов. Стрелки: подход, полёт, завершение подхода.</text>',
    '<text x="30" y="688" font-size="16">Серые точки: штатная сетка. Препятствия и занятость не изменяются; исключённых узлов в этом примере нет.</text>',
    f'<text x="30" y="724" font-size="16">R={radius:g} wu; {1+3*interior*(interior+1)} внутренних + {6*boundary} граничных узла на гекс; шаг {radius/boundary:g} wu.</text>',
    f'<text x="30" y="752" font-size="16">Замеренный отступ взлёта и посадки {padding:g} wu. Реальная вершина: (89,63363; 27,75), seed 629209070.</text>',
    '<text x="30" y="786" font-size="14">Схема локального воспроизведения сохранения. Исторический эпизод игрока в живом наблюдении не пойман.</text>',
    '</g></svg>'])
(ROOT / 'Docs/NikaCornerHop466.svg').write_text('\n'.join(svg), encoding='utf-8')
print(f'R={radius:g}; nodes {1+3*interior*(interior+1)}+{6*boundary}; step={radius/boundary:g}; measured padding={padding:g}')

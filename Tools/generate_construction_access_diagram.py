"""Draw §120.12 from the production hex radius and private-ground radius."""
from pathlib import Path
import math
import re

ROOT = Path(__file__).resolve().parents[1]


def generate():
    spatial = (ROOT / 'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text(encoding='utf-8-sig')
    theft = (ROOT / 'Assets/HexLive/Simulation/Runtime/Helpers/TheftMath.cs').read_text(encoding='utf-8-sig')
    radius = float(re.search(r'const float HexRadius = ([\d.]+)f', spatial)[1])
    private = int(re.search(r'const int PrivateGroundRadiusTiles = (\d+)', theft)[1])
    count = 1 + 3 * private * (private + 1)
    scale = 19
    ours, theirs = (-7, 0), (7, 0)

    def distance(a, b):
        dq, dr = a[0] - b[0], a[1] - b[1]
        return max(abs(dq), abs(dr), abs(dq + dr))

    def position(q, r):
        return 760 + radius * math.sqrt(3) * (q + r * .5) * scale, 400 + radius * 1.5 * r * scale

    parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1520" height="840" viewBox="0 0 1520 840">',
             '<rect width="1520" height="840" fill="#101821"/>',
             '<g font-family="Arial,sans-serif" fill="#edf2f7">',
             '<text x="40" y="48" font-size="28">Строительство: свой лагерь и чужая территория</text>',
             f'<text x="40" y="85" font-size="19">Гекс R={radius:g} wu · приватная зона r={private} гексов · {count} гекс в каждом диске</text>']
    for q in range(-17, 18):
        for r in range(-7, 8):
            x, y = position(q, r)
            if not 30 < x < 1490 or not 150 < y < 670:
                continue
            foreign = distance((q, r), theirs) <= private
            own = distance((q, r), ours) <= private
            fill = '#6a2936' if foreign else '#235544' if own else '#24313e'
            points = ' '.join(f'{x + radius * scale * math.cos(math.radians(30 + 60*k)):.2f},'
                              f'{y + radius * scale * math.sin(math.radians(30 + 60*k)):.2f}' for k in range(6))
            parts.append(f'<polygon points="{points}" fill="{fill}" stroke="#526170" stroke-width=".7"/>')
    for home, label in [(ours, 'Свой костёр'), (theirs, 'Чужой костёр')]:
        x, y = position(*home)
        parts += [f'<circle cx="{x}" cy="{y}" r="9" fill="#ffd37f"/>',
                  f'<text x="{x}" y="{y-40}" text-anchor="middle" font-size="21">{label}</text>']
    x, y = position(1, 0)
    x2, y2 = position(2, 0)
    edge = (x+x2)/2
    parts += [f'<path d="M{edge},{y-14} V{y+14}" stroke="#ff8d44" stroke-width="6"/>',
              f'<path d="M{edge},{y+20} L{edge},{y+125} L{edge-160},{y+125}" fill="none" stroke="#ffb97d" stroke-width="2"/>',
              f'<text x="{edge-165}" y="{y+153}" text-anchor="middle" font-size="17" fill="#ffcb9f">Общая грань с чужой зоной:</text>',
              f'<text x="{edge-165}" y="{y+178}" text-anchor="middle" font-size="17" fill="#ffcb9f">размещение / изменение запрещено</text>']
    legends = [('#235544', 'Своя земля: разрешено'), ('#6a2936', 'Чужая земля: запрещено'), ('#24313e', 'Ничья земля: разрешено')]
    for i, (color, label) in enumerate(legends):
        left = 50 + i * 480
        parts += [f'<rect x="{left}" y="708" width="26" height="26" fill="{color}" stroke="#8294a6"/>',
                  f'<text x="{left+40}" y="729" font-size="20">{label}</text>']
    parts += ['<text x="50" y="775" font-size="19">Любой запрещённый элемент отменяет весь жест. Чужой штамп владельца защищён и вне зоны.</text>',
              '<text x="50" y="811" font-size="17" fill="#b5c1cf">Проверка: отправитель → назначенные живые NPC → фракции → старая и новая геометрия → изменение мира.</text>',
              '</g></svg>']
    destination = ROOT / 'Docs/Architecture/construction-permissions.svg'
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text('\n'.join(parts) + '\n', encoding='utf-8')
    print(f'{destination}: R={radius}, private radius={private}, hexes={count}')


if __name__ == '__main__':
    generate()

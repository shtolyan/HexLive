"""Draw §119 approach from the same constants and lattice formula as the sim."""
from pathlib import Path
import math
import re

ROOT = Path(__file__).resolve().parents[2]

def number(path, name):
    source = (ROOT / path).read_text(encoding="utf-8-sig")
    match = re.search(rf"\b{name}\s*=\s*([0-9.]+)f?\s*[;\n]", source)
    if not match:
        raise ValueError(name)
    return float(match.group(1))

radius = number("Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs", "HexRadius")
obstacle = number("Assets/HexLive/Simulation/Runtime/Balance/Spec119.cs", "WorkbenchObstacleRadius")
distance = number("Assets/HexLive/Simulation/Runtime/Balance/Spec119.cs", "WorkbenchStandDistance")
boundary = int(number("Assets/HexLive/Simulation/Spatial/HexPointLayout.cs", "BoundaryRadius"))
interior = int(number("Assets/HexLive/Simulation/Spatial/HexPointLayout.cs", "InteriorRadius"))
step = radius / boundary
points = []
for r in range(-interior, interior + 1):
    for q in range(max(-interior, -r-interior), min(interior, -r+interior)+1):
        points.append((step*math.sqrt(3)*q/2, step*(r+q/2)))
stand = min((p for p in points if math.hypot(*p) > obstacle),
            key=lambda p: (p[0]+distance)**2+p[1]**2)
scale, cx, cy = 180, 320, 285
def xy(p): return cx+p[0]*scale, cy-p[1]*scale
blocked = sum(math.hypot(*p) <= obstacle for p in points)
parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="880" height="570" viewBox="0 0 880 570">',
         '<rect width="880" height="570" fill="#13191f"/>',
         '<g font-family="Arial" fill="#e6edf3"><text x="30" y="38" font-size="23">Верстак: получение своего результата (§119 / #422)</text>',
         f'<circle cx="{cx}" cy="{cy}" r="{obstacle*scale}" fill="#8c453b" fill-opacity=".25" stroke="#cf7668"/>',
         f'<circle cx="{cx}" cy="{cy}" r="10" fill="#b5ca85"/>']
for p in points:
    x,y = xy(p)
    color = '#d57468' if math.hypot(*p)<=obstacle else '#78818b'
    parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="4" fill="{color}"/>')
sx,sy = xy(stand)
parts += [f'<circle cx="{sx:.2f}" cy="{sy:.2f}" r="10" fill="#70bfdf"/>',
          f'<path d="M {sx+12:.2f},{sy:.2f} L {cx-16},{cy}" stroke="#70bfdf" stroke-width="3"/>',
          f'<path d="M {cx-23},{cy-6} L {cx-14},{cy} L {cx-23},{cy+6}" fill="none" stroke="#70bfdf" stroke-width="3"/>']
lines = ["Синий: назначенная рабочая точка", "Зелёный: выход проекта на столе", "Красный: препятствие стола",
         f"Сетка: шаг {step:.3f} wu, {len(points)} узлов", f"Заблокировано столом: {blocked} узлов",
         f"Радиус препятствия: {obstacle:.2f} wu", f"Рабочая точка: ({stand[0]:.4f}; {stand[1]:.4f})",
         f"Заданный отступ: {distance:.2f} wu", "Подбор разрешён через свою опору.", "Чужие предметы и стены остаются преградой."]
for i,line in enumerate(lines):
    parts.append(f'<text x="480" y="{125+i*29}" font-size="15">{line}</text>')
parts += ['<text x="30" y="535" font-size="14">Геометрия: HexPointLayout + HexSpatialMath + Spec119; yaw=0°, свободная соседняя сетка.</text>', '</g></svg>']
target = ROOT / "Docs/WorkbenchCraftingInteraction.svg"
target.write_text('\n'.join(parts)+'\n', encoding='utf-8')
print(f"{target}: {len(points)} nodes, {blocked} blocked, stand={stand}")

import math
import re
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
spatial = (ROOT / "Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs").read_text(encoding="utf-8")
radius = float(re.search(r"HexRadius = ([\d.]+)f", spatial)[1])
spacing = radius * math.sqrt(3)

arrival = (ROOT / 'Assets/HexLive/Simulation/Runtime/Helpers/PopulationArrivalMath.cs').read_text(encoding='utf-8')
threat = (ROOT / 'Assets/HexLive/Simulation/Runtime/Balance/Spec62.cs').read_text(encoding='utf-8')
mobs = (ROOT / 'Assets/HexLive/Simulation/Content/MobCatalog.cs').read_text(encoding='utf-8')
margin = int(re.search(r'PredatorLandingMarginTiles = (\d+)', arrival)[1])
danger = int(re.search(r'DangerRingTiles = (\d+)', threat)[1])
aggro = int(re.search(r'AggroRadiusTiles = (\d+)', mobs)[1])
excluded = max(danger, aggro) + margin
scale, cx, cy = 19, 350, 345
parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1100" height="730" viewBox="0 0 1100 730">',
         '<rect width="1100" height="730" fill="#18232b"/>',
         '<g font-family="sans-serif" fill="#e9eff3">',
         '<text x="28" y="38" font-size="24">Как станет: человек появляется вдали от волка</text>']
for q in range(-6, 7):
    for r in range(max(-6,-q-6), min(6,-q+6)+1):
        distance = max(abs(q),abs(r),abs(q+r))
        x,y=cx+spacing*(q+r/2)*scale,cy+radius*1.5*r*scale
        colour='#ba6259' if distance<=excluded else '#79b982'
        points=' '.join(f'{x+radius*math.cos(math.radians(30+k*60))*scale:.2f},{y+radius*math.sin(math.radians(30+k*60))*scale:.2f}' for k in range(6))
        parts.append(f'<polygon points="{points}" fill="{colour}" fill-opacity=".13" stroke="{colour}" stroke-opacity=".5"/>')
        parts.append(f'<circle cx="{x}" cy="{y}" r="2" fill="{colour}"/>')
parts += [f'<circle cx="{cx}" cy="{cy}" r="10" fill="#efc267"/>',
          f'<text x="{cx-24}" y="{cy+29}" font-size="15">Волк</text>',
          f'<circle cx="{cx+spacing*5*scale}" cy="{cy}" r="10" fill="#9edb92" stroke="#e9eff3" stroke-width="2"/>',
          '<text x="710" y="145" font-size="20" fill="#ef9b90">Красный: высадка запрещена</text>',
          f'<text x="710" y="183" font-size="17">max(агро {aggro}, опасность {danger}) + {margin}</text>',
          f'<text x="710" y="215" font-size="17">= {excluded} гекса вокруг хищника.</text>',
          '<text x="710" y="285" font-size="20" fill="#9edb92">Зелёный: можно искать точку</text>',
          f'<text x="710" y="320" font-size="17">Минимум {excluded+1} гексов.</text>',
          '<text x="710" y="385" font-size="17">Проверяются живые волки</text>',
          '<text x="710" y="415" font-size="17">и каждая точка виртуального</text>',
          '<text x="710" y="445" font-size="17">патруля, ещё до появления.</text>',
          '<text x="710" y="515" font-size="17">Нет безопасной точки —</text>',
          '<text x="710" y="545" font-size="17">человек не высаживается.</text>',
          f'<text x="28" y="670" font-size="17">R = {radius:g} wu · шаг центров = {spacing:.3f} wu · пример: 5 гексов по оси = {spacing*5:.3f} wu</text>',
          '<text x="28" y="705" font-size="15">Дополнительно нужны сухая проходимая земля и свободный junction. Показана зона одного хищника.</text>',
          '</g></svg>']
out=ROOT/'Docs/SafeHumanLandingDiagram.svg'
out.write_text('\n'.join(parts)+'\n',encoding='utf-8')
print(out)

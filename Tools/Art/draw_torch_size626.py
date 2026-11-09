"""Draw #626 dimensions from the shared runtime size table and recorded base."""
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = 'Assets/HexLive/Simulation/Content/GroundPileCatalog.cs'
radius = float(re.search(r'HexRadius = ([\d.]+)f',
    (ROOT/'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text())[1])
pattern = r'definitionId == ContentIds.Torch\) return r \* ([\d.]+)f'
new = radius * float(re.search(pattern, (ROOT/CATALOG).read_text())[1])
baseline = subprocess.check_output(['git','show','1f8a6cbae:'+CATALOG], cwd=ROOT, text=True)
old = radius * float(re.search(pattern, baseline)[1])
svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="740" height="420" viewBox="0 0 740 420">',
       '<rect width="740" height="420" fill="#16232d"/><g font-family="sans-serif" fill="#e1ebf2">',
       '<text x="25" y="32" font-size="21">#626: размер факела и точка хвата</text>']
for x, size, title in [(180, old, 'До'), (510, new, 'После')]:
    scale = 300
    height = size * scale
    width = size * .16/1.02 * scale
    grip = size * .24/1.02 * scale
    svg += [f'<text x="{x-85}" y="65" font-size="17">{title}: {size:.2f} wu</text>',
        f'<rect x="{x-width*.19}" y="{350-height}" width="{width*.38}" height="{height}" fill="#a77c43"/>',
        f'<rect x="{x-width*.5}" y="{350-height}" width="{width}" height="{height*.255}" fill="#cfceba"/>',
        f'<circle cx="{x}" cy="{350-grip}" r="6" fill="#9fe870"/>',
        f'<path d="M{x+50} {350-height}V350 M{x+44} {350-height}h12 M{x+44} 350h12" stroke="#7795ab" fill="none"/>',
        f'<text x="{x-80}" y="377" font-size="14">Хват: {grip/scale:.3f} wu от торца</text>']
svg += [f'<text x="25" y="407" font-size="14">R={radius:g} wu; общий ObjectFit для земли и руки. Размер уменьшен в {old/new:g} раза. Свет прежний.</text>', '</g></svg>']
(ROOT/'Docs/TorchSize626.svg').write_text('\n'.join(svg)+'\n', encoding='utf-8')
print(f'Torch: {old:g} -> {new:g} wu; grip={new*.24/1.02:g} wu')

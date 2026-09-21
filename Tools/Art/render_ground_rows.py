"""Draw #418 placement diagrams from actual Unity fixture renderer bounds."""
import html
import hashlib
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
rows = json.loads((ROOT / "Tools/Art/GroundRows418.json").read_text(encoding="utf-8"))
sources = {path: digest for row in rows for path, digest in row["sourceHashes"].items()
           if path.startswith("Assets/")}
for path, digest in sources.items():
    if hashlib.sha256((ROOT / path).read_bytes()).hexdigest() != digest:
        raise SystemExit(f"Remeasure changed ground source: {path}")
if not all(row["allSlotsContained"] and row["sourceDirtyUnchanged"] for row in rows):
    raise SystemExit("Incomplete Unity ground-row proof")
cards = []
for index, row in enumerate(rows):
    slots = row["slots"]
    low = [min(s["min"][axis] for s in slots) for axis in range(3)]
    high = [max(s["max"][axis] for s in slots) for axis in range(3)]
    size = [b - a for a, b in zip(low, high)]
    scale = min(410 / max(size[0], .375), 125 / max(size[2], .375))
    cx, cy = 245, 124
    x, y = (index % 2) * 510, (index // 2) * 240 + 65
    card = [f'<g transform="translate({x},{y})"><rect x="8" y="4" width="494" height="230" rx="8" fill="#202b31"/>',
            f'<text x="24" y="30" fill="white">{html.escape(row["id"])} · {row["capacity"]} шт.</text>']
    # Project the real 0.375-wu subgrid, keeping the simulation anchor at (0,0).
    for axis in range(-12, 13):
        pos = axis * .375 * scale
        if abs(pos) <= 210:
            card.append(f'<path d="M {cx + pos:.3f} 48 V 185" stroke="#3d484e"/>')
        if abs(pos) <= 65:
            card.append(f'<path d="M 30 {cy + pos:.3f} H 460" stroke="#3d484e"/>')
    for slot in slots:
        a, b = slot["min"], slot["max"]
        card.append(f'<rect x="{cx+a[0]*scale:.3f}" y="{cy+a[2]*scale:.3f}" '
                    f'width="{(b[0]-a[0])*scale:.3f}" height="{(b[2]-a[2])*scale:.3f}" '
                    'fill="#91c873" fill-opacity=".16" stroke="#a8d58b" stroke-width="1"/>')
    card.extend([f'<circle cx="{cx}" cy="{cy}" r="3" fill="#65baff"/>',
                 f'<text x="24" y="207" fill="#d2e3ec">Габарит X×Z {size[0]:.3f}×{size[2]:.3f} wu; высота {size[1]:.3f} wu</text>',
                 '</g>'])
    cards.append("\n".join(card))
height = 80 + math.ceil(len(rows) / 2) * 240
svg = (f'<svg xmlns="http://www.w3.org/2000/svg" width="1020" height="{height}" viewBox="0 0 1020 {height}">'
       '<rect width="100%" height="100%" fill="#131b21"/>'
       '<g font-family="Arial,sans-serif" font-size="14"><text x="24" y="25" fill="white">#418 · Наземные укладки: реальные габариты штатных моделей, вид сверху</text>'
       '<text x="24" y="49" fill="#b8ccd8">Сетка 0.375 wu · синий — junction · зелёный — AABB каждого экземпляра; слои ткани перекрываются в проекции</text>'
       + "\n".join(cards) + '</g></svg>')
(ROOT / "Docs/GroundRows418.svg").write_text(svg, encoding="utf-8")
print(f"Docs/GroundRows418.svg; {len(rows)} measured profiles, {len(sources)} source hashes verified")

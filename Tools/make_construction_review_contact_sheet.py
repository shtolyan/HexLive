"""Compose rendered QA frames; does not alter any game art."""
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]/"Build/ConstructionReview-20261016"
FONT = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 22)
SMALL = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 16)
report = json.loads((ROOT/"audit.json").read_text())
sheet = Image.new("RGB", (1408, 1488), "#10171b")
draw = ImageDraw.Draw(sheet)
draw.text((24, 18), "HexLive · 14 моделей · 299 отдельных строительных деталей", fill="#c5e77a", font=FONT)
for index, row in enumerate(report):
    x, y = (index%4)*352, 72+(index//4)*350
    picture = Image.open(ROOT/row["id"]/f'{row["units"]:03d}.png').convert("RGB")
    picture.thumbnail((336, 288))
    sheet.paste(picture, (x+8, y))
    draw.text((x+14, y+290), row["title"], fill="#e5e9e7", font=FONT)
    draw.text((x+14, y+319), str(row["units"])+" деталей", fill="#aab8b3", font=SMALL)
sheet.save(ROOT/"overview.jpg", quality=92)

for ident in ("station.drying_rack", "furniture.wardrobe"):
    row = next(r for r in report if r["id"] == ident)
    strip = Image.new("RGB", (1680, 535), "#10171b")
    text = ImageDraw.Draw(strip)
    for index, count in enumerate(row["stageEnds"]):
        picture = Image.open(ROOT/ident/f"{count:03d}.png").convert("RGB")
        strip.paste(picture, (index*560, 0))
        text.text((index*560+16, 490), f'{row["title"]} · стадия {index+1} · {count}/{row["units"]}', fill="#e5e9e7", font=FONT)
    strip.save(ROOT/(ident+"-stages.jpg"), quality=92)
print(ROOT/"overview.jpg")

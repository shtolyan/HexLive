#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""HV-002 / §171.14: bake every emoji the game shows into ONE atlas, so UI text
draws the same Apple-style pictures on macOS, Windows and in the browser
(WebGL has no system emoji font: text emoji rendered empty there).

    python3 Tools/emoji/bake_emoji_atlas.py            # scan → atlas + manifest
    python3 Tools/emoji/bake_emoji_atlas.py --dry-run  # list what would be baked

Sources scanned: C# under Assets/HexLive, I2Languages.asset, SimData. A symbol
goes into the atlas only if the emoji font actually has a picture for it —
plain text symbols (→ ✓ ★) stay with the regular font. A sequence (ZWJ,
FE0F) is keyed by its FIRST code point: that is how TextCore looks up a sprite.

Output (consumed by the Unity menu HexLive ▸ UI ▸ Build Emoji Sprite Asset):
    Assets/Resources/HexLive/UI/EmojiAtlas.png
    Assets/Resources/HexLive/UI/EmojiAtlas.json   [{unicode, sequence, x, y, w, h}]
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

REPO = Path(__file__).resolve().parents[2]
FONT = Path("/System/Library/Fonts/Apple Color Emoji.ttc")
FONT_SIZE = 160          # an sbix strike size: the font has bitmaps at 20/32/40/48/64/96/160
CELL = 72                # atlas cell; the picture is GLYPH px with padding against bleeding
GLYPH = 64
OUT_DIR = REPO / "Assets/Resources/HexLive/UI"

EMOJI = re.compile(
    "(?:[\U0001F1E6-\U0001F1FF]{2}"
    "|[\U0001F000-\U0001FAFF☀-➿⬀-⯿⌀-⏿←-⇿©®"
    "‼⁉™ℹ〰〽㊗㊙](?:️|⃣)?"
    "(?:‍[\U0001F000-\U0001FAFF☀-➿]️?)*)")


def sources() -> list[Path]:
    listed = subprocess.run(["git", "ls-files", "Assets/HexLive", "Assets/Resources/I2Languages.asset",
                             "SimData/simdata.json"], cwd=REPO, capture_output=True, text=True, check=True)
    return [REPO / f for f in listed.stdout.split() if f.endswith((".cs", ".asset", ".json", ".uxml"))]


def unescape_yaml(text: str) -> str:
    # I2 stores non-ASCII as \uXXXX (surrogate pairs for emoji) inside double-quoted YAML.
    text = re.sub(r"\\U([0-9A-Fa-f]{8})", lambda m: chr(int(m.group(1), 16)), text)
    text = re.sub(r"\\u([0-9A-Fa-f]{4})", lambda m: chr(int(m.group(1), 16)), text)
    return text.encode("utf-16", "surrogatepass").decode("utf-16", "replace")


def scan() -> dict[int, str]:
    found: dict[int, str] = {}
    for path in sources():
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        if path.suffix == ".asset":
            text = unescape_yaml(text)
        for seq in EMOJI.findall(text):
            first = ord(seq[0])
            # Prefer the richest spelling seen (with FE0F) for rendering, key stays the first code point.
            if first not in found or len(seq) > len(found[first]):
                found[first] = seq
    return found


def render(font: ImageFont.FreeTypeFont, seq: str) -> Image.Image | None:
    # Without libraqm a ZWJ sequence would render as separate pictures: draw the
    # base (first code point + FE0F) — the same thing TextCore will look up.
    text = seq[0] + ("️" if "️" in seq else "")
    canvas = Image.new("RGBA", (FONT_SIZE * 2, FONT_SIZE * 2), (0, 0, 0, 0))
    ImageDraw.Draw(canvas).text((FONT_SIZE // 2, FONT_SIZE // 2), text, font=font, embedded_color=True)
    box = canvas.getbbox()
    if box is None:
        return None  # the emoji font has no picture: leave it to the regular text font
    glyph = canvas.crop(box)
    scale = GLYPH / max(glyph.width, glyph.height)
    glyph = glyph.resize((max(1, round(glyph.width * scale)), max(1, round(glyph.height * scale))), Image.LANCZOS)
    cell = Image.new("RGBA", (GLYPH, GLYPH), (0, 0, 0, 0))
    cell.paste(glyph, ((GLYPH - glyph.width) // 2, (GLYPH - glyph.height) // 2), glyph)
    return cell


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    if not FONT.exists():
        print(f"emoji font not found: {FONT} (run on macOS)", file=sys.stderr)
        return 2
    font = ImageFont.truetype(str(FONT), FONT_SIZE)
    found = scan()
    baked: list[tuple[int, str, Image.Image]] = []
    skipped: list[str] = []
    for cp, seq in sorted(found.items()):
        image = render(font, seq)
        if image is None:
            skipped.append(seq)
        else:
            baked.append((cp, seq, image))
    print(f"scanned {len(found)} symbols: {len(baked)} baked, {len(skipped)} left to the text font ({' '.join(skipped)})")
    if args.dry_run:
        print(" ".join(seq for _, seq, _ in baked))
        return 0
    columns = 14
    rows = (len(baked) + columns - 1) // columns
    size = 1
    while size < max(columns, rows) * CELL:
        size *= 2
    atlas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    manifest = []
    pad = (CELL - GLYPH) // 2
    for i, (cp, seq, image) in enumerate(baked):
        x = (i % columns) * CELL + pad
        y = (i // columns) * CELL + pad
        atlas.paste(image, (x, y), image)
        # Unity texture rects are bottom-up.
        manifest.append({"unicode": cp, "sequence": seq, "x": x, "y": size - y - GLYPH, "w": GLYPH, "h": GLYPH})
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    atlas.save(OUT_DIR / "EmojiAtlas.png", optimize=True)
    (OUT_DIR / "EmojiAtlas.json").write_text(json.dumps(
        {"atlasSize": size, "glyph": GLYPH, "font": FONT.name, "sprites": manifest},
        ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"atlas {size}x{size} → {OUT_DIR / 'EmojiAtlas.png'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

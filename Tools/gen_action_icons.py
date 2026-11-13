#!/usr/bin/env python3
"""Draw the player action-queue icons (spec §121.13).

Every kind of order the action list can hold gets its own flat glyph: a
white single-colour silhouette on transparency, so the UI can tint it
(active order green, waiting ones muted). No emoji and no font glyphs —
only geometric primitives, drawn 8× supersampled and downsampled to
128×128 for clean edges.

    python Tools/gen_action_icons.py                 # (re)write PNGs, keep metas
    python Tools/gen_action_icons.py --sheet out.png # also a review contact sheet

Output: Assets/Resources/HexLive/UI/ActionIcons/<slug>.png. They are Player
Resources (like the race and identity icons), so both the UI Toolkit card
and the WebGL HTML card (which receives them as data URLs through
HtmlUi.Asset) draw them without any content bundle. A missing .meta is
written with a fresh GUID; an existing one is never touched, so rerunning
the script keeps every reference stable.

The slug list here and CharacterPanel.PlayerOrderIcon must stay in sync —
PlayerOrderIconsContractTests checks that every slug the C# can return has
a PNG.
"""

import argparse
import math
import pathlib
import uuid

from PIL import Image, ImageDraw

REPO = pathlib.Path(__file__).resolve().parent.parent
OUT = REPO / "Assets" / "Resources" / "HexLive" / "UI" / "ActionIcons"
META_TEMPLATE = REPO / "Assets" / "Resources" / "HexLive" / "UI" / "RaceIslandIcon.png.meta"

SIZE = 128
SS = 8
W = (255, 255, 255, 255)
CLEAR = (0, 0, 0, 0)


class Pen:
    """Drawing in 128-unit icon space on an 8× canvas."""

    def __init__(self):
        self.img = Image.new("RGBA", (SIZE * SS, SIZE * SS), CLEAR)
        self.d = ImageDraw.Draw(self.img)

    @staticmethod
    def p(x, y):
        return (x * SS, y * SS)

    def poly(self, pts, fill=W):
        self.d.polygon([self.p(x, y) for x, y in pts], fill=fill)

    def ellipse(self, cx, cy, rx, ry=None, fill=W):
        ry = rx if ry is None else ry
        self.d.ellipse([self.p(cx - rx, cy - ry), self.p(cx + rx, cy + ry)], fill=fill)

    def rect(self, x0, y0, x1, y1, r=0, fill=W):
        box = [self.p(x0, y0), self.p(x1, y1)]
        if r > 0:
            self.d.rounded_rectangle(box, radius=r * SS, fill=fill)
        else:
            self.d.rectangle(box, fill=fill)

    def line(self, pts, w, fill=W, caps=True):
        self.d.line([self.p(x, y) for x, y in pts], fill=fill, width=int(w * SS), joint="curve")
        if caps:
            for x, y in (pts[0], pts[-1]):
                self.ellipse(x, y, w / 2, fill=fill)

    def arc(self, cx, cy, r, start, end, w, fill=W, caps=True):
        self.d.arc([self.p(cx - r, cy - r), self.p(cx + r, cy + r)], start, end, fill=fill, width=int(w * SS))
        if caps:
            for a in (start, end):
                rad = math.radians(a)
                self.ellipse(cx + (r - w / 2) * math.cos(rad), cy + (r - w / 2) * math.sin(rad), w / 2, fill=fill)

    def ring(self, cx, cy, r, w, fill=W):
        self.ellipse(cx, cy, r, fill=fill)
        self.ellipse(cx, cy, r - w, fill=CLEAR)

    def heart(self, cx, cy, s, fill=W):
        # Two lobes and a point; s = half width.
        self.ellipse(cx - s * 0.5, cy - s * 0.25, s * 0.52, fill=fill)
        self.ellipse(cx + s * 0.5, cy - s * 0.25, s * 0.52, fill=fill)
        self.poly([(cx - s * 0.98, cy - s * 0.08), (cx + s * 0.98, cy - s * 0.08), (cx, cy + s * 0.95)], fill=fill)

    def drop(self, cx, cy, r, fill=W):
        self.ellipse(cx, cy, r, fill=fill)
        self.poly([(cx - r * 0.92, cy - r * 0.35), (cx + r * 0.92, cy - r * 0.35), (cx, cy - r * 2.1)], fill=fill)

    def arrow(self, x0, y0, x1, y1, w, head, fill=W):
        ang = math.atan2(y1 - y0, x1 - x0)
        bx, by = x1 - head * math.cos(ang), y1 - head * math.sin(ang)
        self.line([(x0, y0), (bx, by)], w, fill=fill)
        nx, ny = -math.sin(ang), math.cos(ang)
        self.poly([(x1, y1), (bx + nx * head * 0.75, by + ny * head * 0.75),
                   (bx - nx * head * 0.75, by - ny * head * 0.75)], fill=fill)

    def figure(self, cx, top, s=1.0, fill=W):
        """A simple person bust: head and rounded shoulders."""
        self.ellipse(cx, top + 11 * s, 10 * s, fill=fill)
        self.d.chord([self.p(cx - 20 * s, top + 25 * s), self.p(cx + 20 * s, top + 65 * s)], 180, 360, fill=fill)

    def save(self, path):
        self.img.resize((SIZE, SIZE), Image.LANCZOS).save(path)


# ── glyphs ─────────────────────────────────────────────────────────────────

def walk(p):
    # Two footprints stepping up-right.
    for cx, cy, flip in ((44, 74, -1), (82, 46, 1)):
        p.ellipse(cx, cy - 6, 12, 18)
        p.ellipse(cx + flip * 1, cy + 22, 9, 8)
        p.rect(cx - 13, cy + 9, cx + 13, cy + 13, fill=CLEAR)


def talk(p):
    p.rect(14, 22, 114, 88, r=22)
    p.poly([(34, 84), (60, 86), (28, 110)])
    for x in (42, 64, 86):
        p.ellipse(x, 55, 7, fill=CLEAR)


def hug(p):
    # Two people shoulder to shoulder, one arm wrapped round the other.
    p.ellipse(42, 30, 15)
    p.ellipse(86, 30, 15)
    p.d.chord([p.p(14, 50), p.p(70, 120)], 180, 360, fill=W)
    p.d.chord([p.p(58, 50), p.p(114, 120)], 180, 360, fill=W)
    p.rect(14, 84, 114, 120)
    p.line([(30, 92), (60, 70), (98, 62)], 18, fill=CLEAR)
    p.line([(30, 92), (60, 70), (98, 62)], 9)


def kiss(p):
    # Lips: upper lip with a cupid's bow, fuller lower lip, mouth line cut out.
    p.ellipse(46, 56, 26, 16)
    p.ellipse(82, 56, 26, 16)
    p.poly([(14, 64), (64, 58), (114, 64), (64, 74)])
    p.d.chord([p.p(18, 40), p.p(110, 98)], 0, 180, fill=W)
    p.poly([(14, 64), (40, 66), (64, 70), (88, 66), (114, 64), (88, 72), (64, 76), (40, 72)], fill=CLEAR)
    p.poly([(56, 40), (64, 50), (72, 40)], fill=CLEAR)


def romance(p):
    p.heart(64, 60, 46)


def give(p):
    # A box resting on an open palm.
    p.rect(42, 18, 86, 58, r=4)
    p.rect(61, 18, 67, 58, fill=CLEAR)
    p.d.chord([p.p(14, 50), p.p(114, 110)], 0, 180, fill=W)
    p.rect(14, 76, 114, 82, fill=CLEAR)
    p.rect(14, 64, 114, 80, fill=CLEAR)
    p.rect(8, 70, 24, 96, r=6)


def sack(p, top=56):
    p.poly([(40, top), (88, top), (104, 112), (24, 112)])
    p.ellipse(64, 104, 40, 12)
    p.rect(46, top - 8, 82, top + 2, r=4)


def pickup(p):
    sack(p, 60)
    p.arrow(64, 6, 64, 50, 12, 18)
    p.line([(64, 6), (64, 30)], 12)


def gather_all(p):
    sack(p, 64)
    for x, y0 in ((34, 10), (64, 4), (94, 10)):
        p.arrow(x, y0, x, y0 + 46, 9, 14)


def build(p):
    # Hammer, handle down-left.
    p.line([(30, 108), (74, 52)], 14)
    p.poly([(52, 22), (98, 58), (86, 74), (40, 38)])
    p.poly([(98, 58), (112, 50), (108, 70), (92, 80)])


def chop(p):
    # Axe: long handle and a wedge blade.
    p.line([(30, 114), (80, 20)], 12)
    p.poly([(62, 24), (90, 8), (114, 26), (104, 58), (78, 46)])


def harvest(p):
    # Branch with a leaf and two hanging fruits.
    p.arc(64, 70, 52, 200, 300, 7)
    p.poly([(70, 20), (108, 10), (96, 40)])
    p.line([(48, 34), (40, 72)], 5)
    p.line([(64, 30), (78, 76)], 5)
    p.ellipse(38, 86, 18)
    p.ellipse(82, 90, 18)


def butcher(p):
    # Cleaver: wide rectangular blade with a hole, short handle.
    p.rect(12, 30, 84, 84, r=6)
    p.ellipse(26, 44, 6, fill=CLEAR)
    p.rect(84, 46, 118, 64, r=8)


def eat(p):
    # Drumstick: meat oval with a bone end.
    p.ellipse(54, 54, 34, 30)
    p.line([(72, 74), (98, 100)], 12)
    p.ellipse(100, 98, 8)
    p.ellipse(108, 92, 8)
    p.ellipse(94, 108, 8)


def drink(p):
    # Mug with handle and a liquid line.
    p.rect(26, 34, 84, 110, r=10)
    p.ring(90, 70, 22, 9)
    p.rect(26, 50, 84, 56, fill=CLEAR)
    p.rect(60, 48, 84, 92, fill=W)
    p.rect(26, 34, 84, 110, r=10)
    p.rect(26, 52, 84, 56, fill=CLEAR)


def fill(p):
    # Bottle with a drop falling into it.
    p.rect(52, 40, 76, 56, r=3)
    p.d.chord([p.p(34, 52), p.p(94, 90)], 180, 360, fill=W)
    p.rect(34, 70, 94, 118, r=12)
    p.rect(50, 80, 78, 102, r=4, fill=CLEAR)
    p.drop(64, 22, 9)


def cook(p):
    # Pot with lid knob and three steam curls.
    p.rect(22, 66, 106, 112, r=14)
    p.rect(14, 60, 114, 70, r=5)
    p.rect(8, 76, 24, 86, r=4)
    p.rect(104, 76, 120, 86, r=4)
    for x in (44, 64, 84):
        p.line([(x, 52), (x - 6, 42), (x + 2, 30), (x - 4, 18)], 6)


def flame(p, cx=64, base=114, h=100, w=42):
    pts = []
    for i in range(0, 181, 6):
        t = math.radians(i)
        pts.append((cx - w * math.sin(t) * (1 - 0.15 * math.cos(t)), base - w + w * math.cos(t)))
    p.ellipse(cx, base - w * 0.95, w * 0.95, w * 0.95)
    p.poly([(cx - w * 0.95, base - w * 1.0), (cx + w * 0.95, base - w * 1.0), (cx + w * 0.3, base - h),
            (cx + w * 0.05, base - h * 0.72), (cx - w * 0.45, base - h * 0.88)])


def fire(p):
    flame(p)
    p.ellipse(64, 86, 16, 22, fill=CLEAR)
    p.poly([(52, 80), (76, 80), (60, 54)], fill=CLEAR)


def sleep(p):
    # Crescent moon and two small Z marks drawn as polygons.
    p.ellipse(52, 70, 40)
    p.ellipse(72, 56, 36, fill=CLEAR)
    for x, y, s in ((84, 30, 18), (106, 60, 12)):
        p.poly([(x, y), (x + s, y), (x + s, y + s * 0.28), (x + s * 0.32, y + s * 0.78),
                (x + s, y + s * 0.78), (x + s, y + s * 1.06), (x, y + s * 1.06), (x, y + s * 0.78),
                (x + s * 0.68, y + s * 0.28), (x, y + s * 0.28)])


def sit(p):
    # A person sitting on a stool.
    p.ellipse(54, 20, 12)
    p.line([(54, 40), (50, 74)], 16)
    p.line([(50, 74), (86, 74)], 14)
    p.line([(86, 74), (86, 108)], 13)
    p.line([(56, 46), (78, 62)], 10)
    p.rect(26, 82, 74, 92, r=4)
    p.rect(32, 90, 40, 116)
    p.rect(60, 90, 68, 116)


def attack(p):
    # Upright sword with a crossguard.
    p.poly([(56, 76), (56, 22), (64, 8), (72, 22), (72, 76)])
    p.rect(64 - 1.5, 22, 64 + 1.5, 70, fill=CLEAR)
    p.rect(34, 76, 94, 86, r=4)
    p.rect(58, 86, 70, 108)
    p.ellipse(64, 114, 9)


def carry(p):
    # A walker holding a lying person across her shoulders.
    p.ellipse(64, 46, 11)
    p.rect(48, 60, 80, 96, r=10)
    p.rect(50, 92, 60, 120, r=4)
    p.rect(68, 92, 78, 120, r=4)
    p.rect(14, 18, 102, 34, r=8)
    p.ellipse(110, 24, 10)
    p.line([(48, 64), (34, 36)], 8)
    p.line([(80, 64), (94, 36)], 8)


def put_down(p):
    # A lying person on a ground line, a down arrow above.
    p.rect(16, 84, 96, 100, r=8)
    p.ellipse(106, 90, 10)
    p.rect(8, 110, 120, 116, r=3)
    p.arrow(64, 8, 64, 70, 10, 18)


def loot(p):
    # Open chest with an item rising out of it.
    p.rect(18, 66, 110, 112, r=6)
    p.rect(18, 82, 110, 88, fill=CLEAR)
    p.rect(58, 76, 70, 94, r=2)
    p.poly([(18, 62), (28, 34), (100, 34), (110, 62)])
    p.poly([(26, 58), (32, 42), (96, 42), (102, 58)], fill=CLEAR)
    p.arrow(64, 70, 64, 6, 10, 16)


def craft(p):
    # A cog.
    for i in range(8):
        a = math.radians(i * 45)
        ca, sa = math.cos(a), math.sin(a)
        nx, ny = -sa, ca
        p.poly([(64 + ca * 30 + nx * 9, 64 + sa * 30 + ny * 9), (64 + ca * 54 + nx * 7, 64 + sa * 54 + ny * 7),
                (64 + ca * 54 - nx * 7, 64 + sa * 54 - ny * 7), (64 + ca * 30 - nx * 9, 64 + sa * 30 - ny * 9)])
    p.ellipse(64, 64, 40)
    p.ellipse(64, 64, 16, fill=CLEAR)


def plant(p):
    # Sprout: stem, two leaves, a mound.
    p.d.chord([p.p(18, 92), p.p(110, 132)], 180, 360, fill=W)
    p.line([(64, 98), (64, 46)], 8)
    p.poly([(64, 62), (44, 34), (14, 32), (30, 56), (60, 70)])
    p.poly([(64, 50), (82, 18), (116, 12), (102, 42), (68, 58)])


def door(p):
    p.d.chord([p.p(28, 8), p.p(100, 72)], 180, 360, fill=W)
    p.rect(28, 40, 100, 116)
    p.d.chord([p.p(38, 18), p.p(90, 64)], 180, 360, fill=CLEAR)
    p.rect(38, 41, 90, 106, fill=CLEAR)
    p.rect(44, 40, 84, 102, r=2)
    p.ellipse(74, 72, 5, fill=CLEAR)
    p.rect(14, 110, 114, 118, r=3)


def portal(p):
    # An oval gateway ring and an arrow stepping in.
    p.ellipse(74, 64, 40, 54)
    p.ellipse(74, 64, 28, 42, fill=CLEAR)
    p.ellipse(74, 64, 16, 28)
    p.ellipse(74, 64, 7, 15, fill=CLEAR)
    p.rect(0, 52, 52, 76, fill=CLEAR)
    p.arrow(8, 64, 66, 64, 11, 18)


def medical(p):
    p.rect(14, 14, 114, 114, r=22)
    p.rect(52, 28, 76, 100, r=4, fill=CLEAR)
    p.rect(28, 52, 100, 76, r=4, fill=CLEAR)


def aid(p):
    # A heart resting on an open palm.
    p.heart(64, 38, 30)
    p.d.chord([p.p(14, 50), p.p(114, 110)], 0, 180, fill=W)
    p.rect(14, 64, 114, 80, fill=CLEAR)
    p.rect(8, 70, 24, 96, r=6)


def wear(p):
    # T-shirt.
    p.poly([(40, 18), (54, 18), (64, 28), (74, 18), (88, 18), (118, 40), (104, 62), (92, 54),
            (92, 112), (36, 112), (36, 54), (24, 62), (10, 40)])
    p.ellipse(64, 20, 11, 9, fill=CLEAR)


def bathe(p):
    # A head above three water waves.
    p.ellipse(64, 30, 18)
    for y in (64, 84, 104):
        pts = [(12 + i * 2, y + 6 * math.sin(i * 2 * math.pi / 26)) for i in range(53)]
        p.line(pts, 9)


def wash(p):
    # A tub with soap bubbles above.
    p.poly([(14, 64), (114, 64), (102, 112), (26, 112)])
    p.rect(8, 58, 120, 68, r=5)
    p.ring(42, 36, 14, 6)
    p.ring(76, 26, 18, 6)
    p.ring(100, 46, 9, 5)


def shout(p):
    # Megaphone with sound arcs.
    p.poly([(14, 52), (40, 52), (78, 26), (78, 102), (40, 76), (14, 76)])
    p.rect(26, 74, 40, 96, r=4)
    p.arc(78, 64, 26, -40, 40, 7)
    p.arc(78, 64, 44, -42, 42, 7)


def home(p):
    p.poly([(64, 10), (120, 60), (106, 60), (106, 116), (22, 116), (22, 60), (8, 60)])
    p.rect(52, 76, 76, 116, r=3, fill=CLEAR)
    p.rect(84, 18, 98, 46)


def explore(p):
    # Compass: ring and a diamond needle.
    p.ring(64, 64, 52, 9)
    p.poly([(64, 24), (78, 64), (64, 104), (50, 64)])
    p.poly([(64, 64), (78, 64), (64, 104), (50, 64)], fill=CLEAR)
    p.poly([(64, 70), (73, 66), (64, 96), (55, 66)])
    p.ellipse(64, 64, 5, fill=CLEAR)


def use(p):
    # A lever switch: base plate and a tilted handle with a knob.
    p.rect(20, 82, 108, 112, r=10)
    p.ellipse(64, 82, 16)
    p.line([(64, 82), (94, 30)], 12)
    p.ellipse(96, 26, 14)


def action(p):
    # Generic fallback: a four-point sparkle.
    p.poly([(64, 8), (76, 52), (120, 64), (76, 76), (64, 120), (52, 76), (8, 64), (52, 52)])
    p.ellipse(64, 64, 9, fill=CLEAR)


GLYPHS = {
    "walk": walk, "talk": talk, "hug": hug, "kiss": kiss, "romance": romance, "give": give,
    "pickup": pickup, "gather_all": gather_all, "build": build, "chop": chop, "harvest": harvest,
    "butcher": butcher, "eat": eat, "drink": drink, "fill": fill, "cook": cook, "fire": fire,
    "sleep": sleep, "sit": sit, "attack": attack, "carry": carry, "put_down": put_down, "loot": loot,
    "craft": craft, "plant": plant, "door": door, "portal": portal, "medical": medical, "aid": aid,
    "wear": wear, "bathe": bathe, "wash": wash, "shout": shout, "home": home, "explore": explore,
    "use": use, "action": action,
}


def write_meta(png: pathlib.Path):
    meta = png.with_name(png.name + ".meta")
    if meta.exists():
        return
    text = META_TEMPLATE.read_text(encoding="utf-8")
    lines = text.splitlines(keepends=True)
    out = []
    for line in lines:
        if line.startswith("guid: "):
            line = "guid: " + uuid.uuid4().hex + line[len(line.rstrip("\r\n")):]
        elif line.strip() == "textureType: 0":
            line = line.replace("textureType: 0", "textureType: 8")
        elif line.strip() == "spriteMode: 0":
            line = line.replace("spriteMode: 0", "spriteMode: 1")
        out.append(line)
    meta.write_text("".join(out), encoding="utf-8", newline="")


def folder_meta():
    meta = OUT.with_name(OUT.name + ".meta")
    if meta.exists():
        return
    meta.write_text(
        "fileFormatVersion: 2\nguid: " + uuid.uuid4().hex +
        "\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n"
        "  assetBundleName: \n  assetBundleVariant: \n", encoding="utf-8")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--sheet", help="also write a dark contact sheet for review")
    args = ap.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    folder_meta()
    for slug, draw in GLYPHS.items():
        pen = Pen()
        draw(pen)
        path = OUT / f"{slug}.png"
        pen.save(path)
        write_meta(path)
    if args.sheet:
        cols = 8
        rows = (len(GLYPHS) + cols - 1) // cols
        cell = 96
        sheet = Image.new("RGBA", (cols * cell, rows * cell), (34, 44, 52, 255))
        for i, slug in enumerate(GLYPHS):
            icon = Image.open(OUT / f"{slug}.png").resize((64, 64), Image.LANCZOS)
            x, y = (i % cols) * cell + 16, (i // cols) * cell + 8
            sheet.alpha_composite(icon, (x, y))
            ImageDraw.Draw(sheet).text((x - 8, y + 68), slug, fill=(200, 210, 220, 255))
        sheet.save(args.sheet)
    print(f"{len(GLYPHS)} icons -> {OUT}")


if __name__ == "__main__":
    main()

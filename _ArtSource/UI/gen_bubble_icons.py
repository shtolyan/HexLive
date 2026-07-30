"""§67.10 / §28.15E: bubble icons for the speech catalog.

Rasterises Apple Color Emoji into Resources/HexLive/UI/Emoji/<Key>.png (128px,
straight alpha) + a .meta clone, so every SpeechCatalog line has a picture in
the SAME style as the original 13 talk-topic icons. Run from the repo root.

Only strike sizes [20,26,32,40,48,64,96,160] are valid for the emoji font —
any other size raises "invalid pixel size". Draw with embedded_color=True,
then LANCZOS down to 128.
"""
from PIL import Image, ImageDraw, ImageFont
import os, uuid

OUT = "Assets/Resources/HexLive/UI/Emoji"
FONT = "/System/Library/Fonts/Apple Color Emoji.ttc"
STRIKE = 160
FINAL = 128

ICONS = {
    "Hunger": "\U0001F37D",   # plate
    "Thirst": "\U0001F4A7",   # droplet
    "Cold":   "\U0001F976",   # freezing face
    "Heat":   "\U0001F975",   # hot face
    "Tired":  "\U0001F634",   # sleeping face
    "Bed":    "\U0001F6CF",   # bed
    "Pain":   "\U0001F915",   # head bandage
    "Blood":  "\U0001FA78",   # blood drop
    "Sick":   "\U0001F922",   # nauseated
    "Wash":   "\U0001F9FC",   # soap
    "Lonely": "\U0001F494",   # broken heart
    "Relief": "\U0001F60C",   # relieved
    "Chop":   "\U0001FA93",   # axe
    "Build":  "\U0001F528",   # hammer
    "Haul":   "\U0001FAB5",   # wood
    "Done":   "✅",       # check
    "Fail":   "❌",       # cross
    "Help":   "\U0001F198",   # SOS
    "Attack": "⚔️", # crossed swords
    "Victory":"\U0001F3C6",   # trophy
    "Flee":   "\U0001F3C3",   # runner
    "Death":  "\U0001F480",   # skull
    "Grief":  "\U0001F62D",   # loudly crying
    "Bury":   "⚰️", # coffin
    "Dream":  "\U0001F4AD",   # thought balloon
    "Gift":   "\U0001F381",   # gift
    "Dress":  "\U0001F455",   # t-shirt
    "Thanks": "\U0001F64F",   # folded hands
    "Aid":    "\U0001F91D",   # handshake
    "Agree":  "\U0001F44D",   # thumbs up
    "Call":   "\U0001F44B",   # waving hand
    "Craft":  "\U0001F9F6",   # yarn
    "FireOut":"\U0001F4A8",   # dashing away (smoke)
    "Faint":  "\U0001F635",   # knocked out
    "Wet":    "\U0001F4A6",   # sweat droplets
}

META = open("Assets/Resources/HexLive/UI/Emoji/Food.png.meta").read()
font = ImageFont.truetype(FONT, STRIKE)
bad = []
for name, ch in ICONS.items():
    img = Image.new("RGBA", (STRIKE * 2, STRIKE * 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.text((STRIKE * 0.5, STRIKE * 0.4), ch, font=font, embedded_color=True)
    bbox = img.getbbox()
    if bbox is None:
        bad.append(name); continue
    img = img.crop(bbox)
    # square canvas, centred, then downscale
    side = max(img.size)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(img, ((side - img.width) // 2, (side - img.height) // 2))
    sq = sq.resize((FINAL, FINAL), Image.LANCZOS)
    # opaque-pixel sanity
    if sq.getchannel("A").getextrema()[1] < 40:
        bad.append(name); continue
    path = os.path.join(OUT, name + ".png")
    sq.save(path)
    guid = uuid.uuid4().hex
    old = META.split("guid: ")[1][:32]
    open(path + ".meta", "w").write(META.replace(old, guid))
print("written:", len(ICONS) - len(bad))
print("FAILED:", bad)

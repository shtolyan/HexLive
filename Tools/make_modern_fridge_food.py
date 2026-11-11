"""Author the six low-poly apartment foods and render their inventory sprites.

blender -b -t 1 -P Tools/make_modern_fridge_food.py -- --icons
"""
from pathlib import Path
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import make_modern_loot as loot
from make_modern_loot import box, mesh, prism, rings, shade


def yogurt():
    plastic = shade("Prosthetic_Metal", "YogurtCup", (.62, .73, .76))
    berry = shade("Heartwood", "YogurtBerry", (.72, .16, .22))
    foil = shade("Prosthetic_Metal", "YogurtFoil", (.68, .79, .88), .37)
    green = shade("LeafGreen", "YogurtLeaf", (.82, .91, .69))
    profile = [(0, .182), (.032, .196), (.137, .214), (.156, .218),
               (.303, .241), (.326, .241), (.343, .263), (.359, .255)]
    rings("Yogurt tub", [(0, 0, z) for z, _ in profile], [(r, r) for _, r in profile],
          [plastic, berry, foil], [0, 0, 0, 1, 1, 0, 2, 2], sides=10, vertical=True)
    prism("Pull tab", [(-.059, -.235), (.045, -.235), (.051, -.300), (-.029, -.307)],
          .348, .359, foil)
    prism("Strawberry", [(-.092, .031), (-.072, -.027), (0, -.102), (.069, -.025),
                          (.086, .047), (.042, .082), (-.013, .071), (-.058, .081)],
          .360, .364, berry)
    prism("Strawberry leaf", [(-.067, .057), (-.025, .043), (0, .074),
                             (.032, .049), (.076, .062), (.026, .103), (-.011, .111)],
          .362, .369, green)


def cheese():
    flesh = shade("Sapwood", "CheeseGold", (.71, .62, .22))
    rind = shade("Heartwood", "CheeseRind", (.71, .31, .105))
    well = shade("Heartwood", "CheeseDimples", (.64, .40, .17))
    edge = [(-.32, -.216), (.321, -.216), (.267, -.009),
            (.147, .151), (-.18, .241), (-.260, .178)]
    prism("Cheese wedge", edge, 0, .195, flesh)
    prism("Rind", [(-.32, -.222), (.322, -.222), (.322, -.191), (-.316, -.191)], 0, .195, rind)
    for x, y, width in ((-.148, -.088, .048), (.103, -.058, .038), (-.015, .088, .042)):
        rings("Cheese dimple", [(x, y, .195), (x, y, .196)], [(width, width * .75)] * 2,
              [well], [0, 0], sides=7, vertical=True)


def chocolate():
    cocoa = shade("Heartwood", "ChocolateCocoa", (.25, .21, .16))
    sugar = shade("Heartwood", "ChocolateTop", (.39, .30, .23))
    paper = shade("Prosthetic_Metal", "ChocolateViolet", (.39, .22, .61))
    ink = shade("Sapwood", "ChocolateInk", (.64, .55, .43))
    foil = shade("Prosthetic_Metal", "ChocolateFoil", (.68, .73, .80), .43)
    box("Chocolate base", 0, .012, .055, .364, .748, .087, cocoa)
    for x in (-.091, .091):
        for y in (.076, .205, .334):
            prism("Chocolate segment", [(x-.074, y-.051), (x+.074, y-.051),
                                          (x+.074, y+.051), (x-.074, y+.051)],
                  .096, .133, sugar)
    box("Paper wrapper", 0, -.201, .054, .401, .403, .109, paper)
    prism("Torn foil", [(-.205, -.022), (.205, -.022), (.192, .019),
                         (.090, .023), (.025, .052), (-.052, .033), (-.196, .059)],
          .093, .114, foil)
    box("Label", 0, -.209, .110, .286, .125, .007, ink)


def sandwich():
    crust = shade("Heartwood", "SandwichCrust", (.61, .42, .27))
    bread = shade("Sapwood", "SandwichBread", (.65, .62, .43))
    ham = shade("Heartwood", "SandwichHam", (.81, .34, .35))
    lettuce = shade("LeafGreen", "SandwichLettuce", (.75, 1.15, .55))
    triangle = [(-.33, -.22), (.32, -.22), (.31, -.16), (-.23, .28), (-.30, .28)]
    crumb = [(x * .94, y * .94) for x, y in triangle]
    prism("Bottom crust", triangle, 0, .064, crust)
    prism("Lettuce", [(-.34, -.218), (.315, -.23), (.28, -.172), (.185, -.093),
                      (.062, .034), (-.06, .121), (-.208, .29), (-.30, .282)], .062, .094, lettuce)
    prism("Folded ham", [(x * .98, y * .97) for x, y in triangle], .091, .128, ham)
    prism("Upper crust", triangle, .124, .184, crust)
    prism("Crumb", crumb, .178, .193, bread)


def orange():
    peel = shade("Heartwood", "OrangePeel", (.79, .32, .07))
    cheek = shade("Sapwood", "OrangeCheek", (.77, .38, .085))
    green = shade("LeafGreen", "OrangeLeaf", (.62, .93, .57))
    stem = shade("Bark", "OrangeStem", (.52, .42, .35))
    profile = [(0, .06), (.048, .136), (.119, .199), (.235, .230), (.346, .202),
               (.413, .138), (.446, .055), (.442, .019)]
    rings("Faceted orange", [(0, 0, z) for z, _ in profile], [(r, r * .98) for _, r in profile],
          [peel, cheek], [0, 0, 0, 1, 0, 1, 0, 0], sides=12, vertical=True)
    rings("Stem", [(0, 0, .431), (.025, 0, .474)], [(.023, .023), (.019, .019)],
          [stem], [0, 0], sides=6, vertical=True)
    mesh("Leaf", [(0, 0, .455), (.089, -.033, .466), (.182, -.007, .491),
                  (.106, .035, .472), (.086, .004, .478)],
         [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (3, 2, 1, 0)], [green])


def peaches():
    metal = shade("Prosthetic_Metal", "PeachesTin", (.76, .81, .86), .39)
    label = shade("LeafGreen", "PeachesLabel", (.82, 1.22, .91))
    peach = shade("Heartwood", "PeachesIllustration", (.96, .42, .17))
    tab = shade("Prosthetic_MetalDark", "PeachesTab", (.69, .75, .81), .44)
    profile = [(0, .181), (.011, .207), (.034, .207), (.050, .192), (.443, .192),
               (.458, .208), (.480, .208), (.486, .195), (.480, .174)]
    rings("Peach tin", [(0, 0, z) for z, _ in profile], [(r, r) for _, r in profile],
          [metal, label], [0, 0, 0, 1, 0, 0, 0, 0, 0], sides=12, vertical=True)
    box("Ring tab", 0, -.036, .495, .078, .128, .024, tab)
    box("Ring tab highlight", 0, -.046, .507, .038, .074, .005, metal)
    mesh("Printed peaches", [(-.064, -.195, .244), (-.043, -.195, .189), (.018, -.195, .177),
                             (.071, -.195, .216), (.064, -.195, .273), (.023, -.195, .302),
                             (-.029, -.195, .293)], [(0, 1, 2, 3, 4, 5, 6)], [peach])


ITEMS = {
    "food.strawberry_yogurt": (yogurt, 1.20, 0),
    "food.cheese_wedge": (cheese, 1.43, 15),
    "food.chocolate_bar": (chocolate, 1.12, 24),
    "food.ham_sandwich": (sandwich, 1.45, 16),
    "food.orange": (orange, 1.22, 0),
    "food.canned_peaches": (peaches, 1.22, 0),
}


def main():
    loot.BUILD = loot.ROOT / "Build/Diagnostics/modern-fridge-food"
    loot.BUILD.mkdir(parents=True, exist_ok=True)
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    requested = [args[index + 1] for index, arg in enumerate(args) if arg == "--item"]
    items = {item: ITEMS[item] for item in requested} if requested else ITEMS
    template = (loot.OBJECTS / "food.apple.fbx.meta").read_text(encoding="utf-8")
    for item_id, (author, _, _) in items.items():
        loot.export(item_id, author)
        metadata = loot.OBJECTS / (item_id + ".fbx.meta")
        if not metadata.exists():
            guid = uuid.uuid5(uuid.NAMESPACE_URL, "hexlive/native/" + item_id).hex
            body = template.replace(template.splitlines()[1], "guid: " + guid)
            metadata.write_text(body, encoding="utf-8", newline="\n")
    if "--icons" in args:
        loot.render_icons(items)


if __name__ == "__main__":
    main()

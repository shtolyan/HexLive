import bpy
import math
import os
import random
from mathutils import Vector


ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_BLEND = os.path.join(ROOT, "Assets", "ArtSource", "Building", "hexlive_hexfit_bed.blend")
OUT_PREVIEW = os.path.join("/private/tmp", "hexlive_hexfit_bed_preview.png")
SQRT3 = math.sqrt(3.0)
HEX_R = 1.5
APOTHEM = HEX_R * SQRT3 / 2.0
LOGICAL_W = APOTHEM / 2.0
LOGICAL_L = 1.5
ART_W = 0.57
ART_L = 1.42


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.studio_light = "rim.sl"
    scene.display.shading.color_type = "MATERIAL"
    scene.display.shading.show_shadows = True
    scene.display.shading.show_cavity = True
    scene.display.shading.cavity_type = "BOTH"
    scene.render.resolution_x = 900
    scene.render.resolution_y = 760
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = OUT_PREVIEW
    scene.render.film_transparent = False
    if scene.world is None:
        scene.world = bpy.data.worlds.new("HexFitBedWorld")
    scene.world.color = (0.035, 0.052, 0.040)
    return scene


def material(name, color, roughness=0.86, emission=0.0):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1.0)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    if emission:
        bsdf.inputs["Emission"].default_value = (*color, 1.0)
        bsdf.inputs["Emission Strength"].default_value = emission
    return mat


def link_only(obj, collection):
    for old in list(obj.users_collection):
        old.objects.unlink(obj)
    collection.objects.link(obj)


def cube(name, location, scale, mat, collection, bevel=0.012, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location, rotation=rotation)
    obj = bpy.context.object
    obj.name = name
    obj.dimensions = scale
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel:
        mod = obj.modifiers.new("hand-hewn edges", "BEVEL")
        mod.width = bevel
        mod.segments = 1
    obj.data.materials.append(mat)
    link_only(obj, collection)
    return obj


def cylinder_between(name, a, b, radius, mat, collection, vertices=7):
    a, b = Vector(a), Vector(b)
    delta = b - a
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius,
        depth=delta.length, location=(a + b) * 0.5)
    obj = bpy.context.object
    obj.name = name
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(delta.normalized())
    obj.data.materials.append(mat)
    link_only(obj, collection)
    return obj


def torus(name, location, major, minor, mat, collection, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_torus_add(align="WORLD", major_segments=8, minor_segments=4,
        location=location, rotation=rotation, major_radius=major, minor_radius=minor)
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    link_only(obj, collection)
    return obj


def build_bed(collection, mats):
    rng = random.Random(7441)
    half_w, half_l = ART_W / 2.0, ART_L / 2.0
    rail_z = 0.155
    # Four rough frame poles: visually handmade, but kept inside padded art bounds.
    cylinder_between("log_00_side_left", (-half_w + .027, -half_l + .025, rail_z),
        (-half_w + .027, half_l - .025, rail_z + .006), .038, mats["bark"], collection)
    cylinder_between("log_01_side_right", (half_w - .027, -half_l + .025, rail_z + .004),
        (half_w - .027, half_l - .025, rail_z), .038, mats["bark_light"], collection)
    cylinder_between("log_02_head", (-half_w + .015, half_l - .025, rail_z),
        (half_w - .015, half_l - .025, rail_z + .003), .038, mats["heart"], collection)
    cylinder_between("log_03_foot", (-half_w + .015, -half_l + .025, rail_z + .003),
        (half_w - .015, -half_l + .025, rail_z), .038, mats["heart"], collection)

    # Low legs keep it recognisably the same primitive tribal cot.
    for i, (x, y) in enumerate(((-half_w+.035,-half_l+.04),(half_w-.035,-half_l+.04),
                                (-half_w+.035,half_l-.04),(half_w-.035,half_l-.04))):
        tilt = rng.uniform(-0.025, 0.025)
        cylinder_between("stick_%02d_leg" % i, (x, y, .025),
            (x + tilt, y - tilt*.4, rail_z + .015), .026, mats["bark"], collection, 7)

    # Seven cross slats give the same resource-built reading without making a solid slab.
    for i in range(7):
        y = -half_l + .13 + i * ((ART_L - .26) / 6.0)
        y += rng.uniform(-.008, .008)
        cylinder_between("stick_%02d_cross" % (i+4), (-half_w+.045, y, rail_z+.012),
            (half_w-.045, y+rng.uniform(-.006,.006), rail_z+.017), .021,
            mats["sap" if i % 2 else "heart"], collection, 6)

    # Woven sleeping surface: narrow split-leaf ribbons with restrained irregularity.
    ribbon_w = .031
    for i in range(13):
        x = -half_w + .072 + i * ((ART_W - .144) / 12.0)
        x += rng.uniform(-.005, .005)
        length = ART_L - .165 - rng.uniform(0, .025)
        ribbon = cube("leaf_%02d_woven" % i, (x, rng.uniform(-.006,.006), rail_z+.052),
            (ribbon_w+rng.uniform(-.004,.004), length, .012),
            mats["leaf_a" if i % 3 else "leaf_b"], collection, bevel=.004,
            rotation=(0, 0, rng.uniform(-.012,.012)))
        ribbon.data.polygons.foreach_set("use_smooth", [False] * len(ribbon.data.polygons))

    # Two cross-weave bands and a small raised rolled head mat.
    for i, y in enumerate((-.22, .18)):
        cube("leaf_%02d_crossband" % (13+i), (0, y, rail_z+.061),
            (ART_W-.105, .036, .014), mats["leaf_b"], collection, bevel=.005,
            rotation=(0, 0, rng.uniform(-.018,.018)))
    cylinder_between("leaf_15_headroll", (-half_w+.085, half_l-.15, rail_z+.095),
        (half_w-.085, half_l-.15, rail_z+.10), .055, mats["leaf_b"], collection, 8)

    # Rope lashings are chunky enough to survive the game camera.
    corners = [(-half_w+.03,-half_l+.03),(half_w-.03,-half_l+.03),
               (-half_w+.03,half_l-.03),(half_w-.03,half_l-.03)]
    for i, (x, y) in enumerate(corners):
        torus("rope_%02d_lashing" % i, (x, y, rail_z+.006), .048, .010,
            mats["rope"], collection, rotation=(math.pi/2, 0, 0))

    point = cube("point", (0, 0, rail_z+.085), (.035,.035,.035),
        mats["point"], collection, bevel=.006)
    point.hide_render = True


def add_line(name, a, b, radius, mat, collection):
    return cylinder_between(name, a, b, radius, mat, collection, 8)


def point_xy(q, r):
    return (.375 * SQRT3 / 2.0 * q, .375 * (r + q * .5))


def occupied(x, y):
    eps = 1e-4
    return ((LOGICAL_W-eps <= abs(x) <= APOTHEM+eps) and
            (-.75-eps <= y <= .75+eps))


def build_preview(scene, bed_collection, mats):
    preview = bpy.data.collections.new("HL_HEX_FIT_BED_PRESENTATION")
    scene.collection.children.link(preview)
    # Floor and exact R=1.5 outline.
    verts = [(0,HEX_R), (APOTHEM,.75), (APOTHEM,-.75), (0,-HEX_R),
             (-APOTHEM,-.75), (-APOTHEM,.75)]
    mesh = bpy.data.meshes.new("hex_floor_mesh")
    mesh.from_pydata([(x,y,0) for x,y in verts], [], [[0,1,2,3,4,5]])
    floor = bpy.data.objects.new("Hex R=1.5", mesh)
    floor.data.materials.append(mats["ground"])
    preview.objects.link(floor)
    for i in range(6):
        a, b = verts[i], verts[(i+1)%6]
        add_line("hex_edge_%d"%i, (a[0],a[1],.025),(b[0],b[1],.025),.012,
            mats["rim"],preview)

    # The two preview copies sit inside the exact logical rectangles. Avoid
    # collection instances here: Blender 3.2's headless Workbench renderer can
    # terminate while evaluating an instanced collection that is also linked
    # into the active scene.
    center_x = (APOTHEM + LOGICAL_W) * .5
    for source in bed_collection.objects:
        source.hide_render = True
    for side in (-1, 1):
        for source in bed_collection.objects:
            copy = source.copy()
            if source.data is not None:
                copy.data = source.data.copy()
            copy.name = ("L_" if side < 0 else "R_") + source.name
            copy.location = source.location + Vector((side*center_x, 0, .04))
            copy.hide_render = source.name == "point"
            preview.objects.link(copy)

    # All 61 real junctions. Red means furniture-placement reservation.
    points=[]
    for rr in range(-3,4):
        for qq in range(max(-3,-rr-3),min(3,-rr+3)+1):
            points.append(point_xy(qq,rr))
    dirs=((1,0),(1,-1),(0,-1),(-1,0),(-1,1),(0,1))
    qq,rr=-4,4
    for dq,dr in dirs:
        for _ in range(4):
            points.append(point_xy(qq,rr));qq+=dq;rr+=dr
    for i,(x,y) in enumerate(points):
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=.030,
            location=(x,y,.245 if occupied(x,y) else .035))
        obj=bpy.context.object
        obj.name=("reserved_" if occupied(x,y) else "free_")+str(i).zfill(2)
        obj.data.materials.append(mats["reserved"] if occupied(x,y) else mats["free"])
        link_only(obj,preview)

    # Two cyan approach nodes on the corridor side; not part of the footprint.
    for side in (-1,1):
        x=side*(.375*SQRT3/2)
        y=side*.1875
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2,radius=.047,location=(x,y,.07))
        obj=bpy.context.object;obj.name="interaction_approach"
        obj.data.materials.append(mats["approach"]);link_only(obj,preview)

    # Camera, warm key and soft fill.
    bpy.ops.object.camera_add(location=(3.85,-4.65,4.65))
    cam=bpy.context.object;cam.name="HexFitBedCam";link_only(cam,preview)
    direction=Vector((0,0,.25))-cam.location
    cam.rotation_euler=direction.to_track_quat('-Z','Y').to_euler()
    cam.data.lens=52
    scene.camera=cam
    bpy.ops.object.light_add(type='AREA',location=(-2.4,-2.8,5.2))
    key=bpy.context.object;key.name="WarmKey";key.data.energy=720;key.data.size=4.0
    key.data.color=(1.0,.72,.48);link_only(key,preview)
    bpy.ops.object.light_add(type='AREA',location=(3.0,2.2,3.4))
    fill=bpy.context.object;fill.name="CoolFill";fill.data.energy=420;fill.data.size=3.0
    fill.data.color=(.48,.68,1.0);link_only(fill,preview)


def main():
    scene=reset()
    mats={
        "bark":material("HL_Bark",(.30,.17,.075)),
        "bark_light":material("HL_BarkLight",(.43,.27,.13)),
        "heart":material("HL_Heartwood",(.54,.285,.105)),
        "sap":material("HL_Sapwood",(.72,.49,.235)),
        "rope":material("HL_Rope",(.74,.62,.39)),
        "leaf_a":material("HL_BedLeaf_A",(.22,.38,.13)),
        "leaf_b":material("HL_BedLeaf_B",(.34,.48,.18)),
        "ground":material("HL_InteriorGround",(.075,.105,.08)),
        "rim":material("HL_HexRim",(.70,.53,.255)),
        "free":material("HL_InteriorAnchor",(.12,.46,.50),emission=.10),
        "reserved":material("HL_FurnitureReserved",(.86,.08,.055),emission=.16),
        "approach":material("HL_InteractionApproach",(.10,.55,.86),emission=.20),
        "point":material("HL_SleepPoint",(.2,.65,1.0),emission=.4),
    }
    bed=bpy.data.collections.new("HL_HEX_FIT_BED_EXPORT")
    scene.collection.children.link(bed)
    build_bed(bed,mats)
    build_preview(scene,bed,mats)
    bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND)
    scene.render.filepath=OUT_PREVIEW
    bpy.ops.render.render(write_still=True)
    print("HEX_FIT_BED_BLEND="+OUT_BLEND)
    print("HEX_FIT_BED_PREVIEW="+OUT_PREVIEW)
    print("LOGICAL=%.6fx%.3f ART=%.3fx%.3f"%(LOGICAL_W,LOGICAL_L,ART_W,ART_L))


if __name__ == "__main__":
    main()

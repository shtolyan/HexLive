"""§175 deterministic faceted props. Run with Blender -b -P Tools/make_survival_tools.py.

The shared source palette is appended read-only; this never rewrites source.blend.
"""
import math
import random
from pathlib import Path
import bpy
import bmesh
from mathutils import Vector
from mathutils import Matrix

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'Assets/HexLiveContent/RuntimeSource/Objects'


def palette():
    names = ['Sapwood', 'Bark', 'Rope', 'StoneGrey']
    with bpy.data.libraries.load(str(ROOT / 'Assets/HexLiveContent/source.blend'), link=False) as (source, target):
        assert all(n in source.materials for n in names)
        target.materials = names
    result = []
    for name, source, factor in [('SurvivalWood', 'Sapwood', .72), ('SurvivalBark', 'Bark', .85),
                                 ('SurvivalRope', 'Rope', .85), ('SurvivalFlint', 'StoneGrey', .39),
                                 ('SurvivalStone', 'StoneGrey', .82), ('SurvivalCloth', 'Sapwood', .88)]:
        mat = bpy.data.materials[source].copy()
        mat.name = name
        mat.use_nodes = True
        bsdf = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
        rgb = bsdf.inputs['Base Color'].default_value[:3]
        if name == 'SurvivalCloth':
            mean = sum(rgb) / 3
            rgb = tuple(.35*c + .65*mean for c in rgb)
        bsdf.inputs['Base Color'].default_value = (*[min(.42, c*factor) for c in rgb], 1)
        bsdf.inputs['Roughness'].default_value = .94
        mat.diffuse_color = bsdf.inputs['Base Color'].default_value
        result.append(mat)
    return result


def finish(obj, material):
    obj.data.materials.clear()
    for mat in MATERIALS: obj.data.materials.append(mat)
    for face in obj.data.polygons:
        face.material_index = material
        face.use_smooth = False
    return obj


def cylinder(name, a, b, radius, material, vertices=8):
    a, b = Vector(a), Vector(b)
    delta = b-a
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=delta.length, location=(a+b)*.5)
    obj = bpy.context.object
    obj.name = name
    obj.rotation_euler = delta.to_track_quat('Z', 'Y').to_euler()
    return finish(obj, material)


def stone(name, center, scale, material):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=1, location=center)
    obj = bpy.context.object
    obj.name = name
    rng = random.Random(name)
    for vertex in obj.data.vertices:
        for axis in range(3): vertex.co[axis] *= scale[axis] * rng.uniform(.85, 1.12)
    return finish(obj, material)


def blade():
    # One closed, thin chipped stone plate; the teeth are real silhouette geometry.
    polygon = [(-.10, .22), (-.10, .94), (.08, .94)]
    for i in range(7, -1, -1):
        z = .22 + i*.09
        polygon += [(.10, z+.035), (.17, z)]
    n = len(polygon)
    verts = [(x,y,z) for y in [-.022,.022] for x,z in polygon]
    faces = [tuple(reversed(range(n))), tuple(range(n,2*n))]
    faces += [(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    mesh = bpy.data.meshes.new('Chipped saw blade')
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new('Chipped saw blade', mesh)
    bpy.context.collection.objects.link(obj)
    bm = bmesh.new(); bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bmesh.ops.triangulate(bm, faces=list(bm.faces))
    bm.to_mesh(mesh); bm.free()
    return finish(obj, 3)


def export(item_id, objects, tip=None):
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objects: obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    bpy.ops.object.join()
    root = bpy.context.object
    root.name = item_id
    # Keep a floor/grip pivot and bake mesh placement, never runtime axis compensation.
    offset = root.location.copy()
    for v in root.data.vertices: v.co += offset
    root.location = (0,0,0)
    if item_id == 'tool.saw_crude':
        root.data.transform(Matrix.Rotation(-math.pi/2, 4, 'Z'))
    if tip:
        marker = bpy.data.objects.new('fire_point', None)
        bpy.context.collection.objects.link(marker)
        marker.parent = root
        marker.location = tip
        marker.select_set(True)
    bpy.ops.export_scene.fbx(filepath=str(OUT / (item_id+'.fbx')), use_selection=True,
        object_types={'MESH','EMPTY'}, axis_forward='-Z', axis_up='Y', path_mode='STRIP',
        add_leaf_bones=False, bake_anim=False, mesh_smooth_type='FACE')
    tris = sum(len(p.vertices)-2 for p in root.data.polygons)
    assert tris <= 400, (item_id, tris)
    print('SURVIVAL_PROP', item_id, 'triangles', tris, 'bounds', list(root.dimensions))
    return root


bpy.ops.wm.read_factory_settings(use_empty=True)
MATERIALS = palette()
export('resource.flint', [stone('Flint', (0,0,.13), (.17,.11,.14), 3)])
bpy.ops.object.select_all(action='DESELECT')
export('tool.firestarter', [stone('Flint striker', (-.10,0,.16), (.11,.07,.16),3),
                           stone('Striking stone', (.10,0,.10), (.105,.085,.10),4)])
export('tool.saw_crude', [blade(), cylinder('Wooden grip', (-.015,0,.02),(-.015,0,.30),.055,0),
                         cylinder('Lashing lower',(-.015,0,.20),(-.015,0,.23),.064,2),
                         cylinder('Lashing upper',(-.015,0,.26),(-.015,0,.29),.064,2)])
export('tool.torch', [cylinder('Bark handle', (0,0,0),(0,0,.93),.034,1),
                      cylinder('Cloth head',(0,0,.76),(0,0,1.02),.075,5),
                      cylinder('Head binding',(0,0,.78),(0,0,.82),.080,2),
                      cylinder('Grip',(0,0,.10),(0,0,.38),.038,0)], tip=(0,0,1.04))

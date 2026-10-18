"""§56.7 / #485. Blender CLI cuts of shipped low-poly bodies; no publication.

blender -b -t 2 -P Tools/Art/slice_human_parts.py
Source bodies are read-only; no armature, morph, hair or genital mesh is exported.
"""
from pathlib import Path
import hashlib
import json
import bpy
import bmesh
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Assets/HexLiveContent/RuntimeSource/Objects'


def material(name, color):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1)
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = (*color, 1)
    shader.inputs['Roughness'].default_value = .85
    return mat


def source_parts(body):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    path = ROOT / f'Assets/HexLiveContent/People/Source/Models/{body}.fbx'
    bpy.ops.import_scene.fbx(filepath=str(path))
    skin = next(o for o in bpy.context.scene.objects if o.type == 'MESH' and o.name == body + '_LOD0')
    rig = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    points = {name: rig.matrix_world @ rig.data.bones[name].head_local
              for name in ('lShldrBend', 'lThighBend', 'neckLower')}
    vertices = [skin.matrix_world @ v.co for v in skin.data.vertices]
    faces = [tuple(face.vertices) for face in skin.data.polygons]
    return path, vertices, faces, points


def make_piece(vertices, faces, points, zone, name, cooked=False):
    used = sorted({i for face in faces for i in face})
    remap = {old: new for new, old in enumerate(used)}
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([vertices[i] for i in used], [], [tuple(remap[i] for i in f) for f in faces])
    mesh.materials.append(material('CookedSkin' if cooked else 'Skin',
        (.14, .085, .05) if cooked else (.38, .22, .14)))
    mesh.materials.append(material('CutSurface', (.12, .045, .025) if cooked else (.25, .018, .022)))
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=.00001)
    shoulder = points['lShldrBend'].x + .045
    hip = points['lThighBend'].z
    neck = points['neckLower'].z
    # Bone-measured planar anatomical cuts preserve the actual source surface
    # and interpolate boundary triangles instead of a jagged majority mask.
    planes = {
        'armL': [((shoulder, 0, 0), (1, 0, 0)), ((0, 0, hip+.2), (0, 0, 1))],
        'legL': [((0, 0, hip), (0, 0, -1)), ((0, 0, 0), (1, 0, 0))],
        'torso': [((0, 0, hip), (0, 0, 1)), ((0, 0, neck), (0, 0, -1)),
                  ((shoulder, 0, 0), (-1, 0, 0)), ((-shoulder, 0, 0), (1, 0, 0))],
        'head': [((0, 0, neck), (0, 0, 1))],
    }[zone]
    for point, normal in planes:
        bmesh.ops.bisect_plane(bm, geom=list(bm.verts)+list(bm.edges)+list(bm.faces),
            plane_co=Vector(point), plane_no=Vector(normal), dist=.000001,
            clear_inner=True, clear_outer=False)
    # A fan per cut rim closes every original edge, including thin boundary
    # triangles a planar ngon triangulator can otherwise drop.
    pending = {e for e in bm.edges if e.is_boundary}
    cap_count = 0
    while pending:
        rim = {next(iter(pending))}
        fringe = list(rim)
        while fringe:
            edge = fringe.pop()
            for vertex in edge.verts:
                for neighbor in vertex.link_edges:
                    if neighbor in pending and neighbor not in rim:
                        rim.add(neighbor)
                        fringe.append(neighbor)
        pending.difference_update(rim)
        points = {v for e in rim for v in e.verts}
        center = bm.verts.new(sum((v.co for v in points), Vector()) / len(points))
        anatomical = any(all(abs((v.co-Vector(point)).dot(Vector(normal))) < .0001
                              for v in points) for point,normal in planes)
        for edge in rim:
            bm.faces.new((edge.verts[0], edge.verts[1], center)).material_index = 1 if anatomical else 0
        cap_count += 1
    bmesh.ops.triangulate(bm, faces=list(bm.faces))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    boundaries = sum(e.is_boundary for e in bm.edges)
    assert boundaries == 0, (name, 'open boundary', boundaries)
    bm.to_mesh(mesh)
    bm.free()
    for face in mesh.polygons:
        face.use_smooth = False
    # Back on ground: source +Z (head) -> ground +Y; face -> +Z.
    mesh.transform(Matrix.Rotation(-1.5707963267948966, 4, 'X'))
    low = Vector([min(v.co[i] for v in mesh.vertices) for i in range(3)])
    high = Vector([max(v.co[i] for v in mesh.vertices) for i in range(3)])
    mesh.transform(Matrix.Translation(Vector((-(low.x+high.x)/2, -(low.y+high.y)/2, -low.z))))
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    path = OUT / (name + '.fbx')
    bpy.ops.export_scene.fbx(filepath=str(path), use_selection=True, object_types={'MESH'},
        axis_forward='-Z', axis_up='Y', path_mode='STRIP', add_leaf_bones=False,
        bake_anim=False, mesh_smooth_type='FACE')
    return dict(id=name, triangles=len(mesh.polygons), sourceFaces=len(faces),
        cutLoops=cap_count, openEdges=boundaries, size=list(obj.dimensions),
        sha256=hashlib.sha256(path.read_bytes()).hexdigest())


def main():
    rows=[]
    for body in ('Marta', 'Kshishtof'):
        path, vertices, faces, points = source_parts(body)
        for part, zone in (('arm', 'armL'), ('leg', 'legL'), ('torso', 'torso')):
            for cooked in (False, True):
                suffix = '_male' if body == 'Kshishtof' else ''
                name = f'food.human_{part}{suffix}_{"cooked" if cooked else "raw"}'
                row=make_piece(vertices, faces, points, zone, name, cooked)
                row.update(body=body, source=path.relative_to(ROOT).as_posix(),
                    sourceSha256=hashlib.sha256(path.read_bytes()).hexdigest(), zone=zone)
                rows.append(row)
        if body == 'Marta':
            row=make_piece(vertices, faces, points, 'head', 'resource.human_head')
            row.update(body='universal', source=path.relative_to(ROOT).as_posix(),
                sourceSha256=hashlib.sha256(path.read_bytes()).hexdigest(), zone='head')
            rows.append(row)
    report=ROOT/'ArtSource/WorldProps/human-parts.json'
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(dict(schemaVersion=1, generator='Tools/Art/slice_human_parts.py',
        partition='bone-measured anatomical planes, interpolated boundary triangles; closed caps', rows=rows), indent=2)+'\n')
    write_geometry(rows)
    print('Verified closed source cuts:', len(rows))


def write_geometry(rows):
    lines=['// Generated by Tools/Art/slice_human_parts.py from the shipped FBX cuts.',
           '// §56.7: source metres, BEFORE the same ActorScale used by live people.',
           'namespace HexLive.Simulation.Content', '{',
           'internal static class PreparedHumanPartGeometry', '{',
           '    internal static float SourceSpan(string id) => id switch', '    {']
    for row in rows:
        lines.append(f'        "{row["id"]}" => {max(row["size"]):.6f}f,')
    lines += ['        _ => 0f', '    };', '}', '}']
    (ROOT/'Assets/HexLive/Simulation/Content/PreparedHumanPartGeometry.cs').write_text('\n'.join(lines)+'\n')


if __name__ == '__main__':
    main()

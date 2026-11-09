"""#650: self-contained low-poly leather flask, source palette reused from approved hide/torch.
blender -b -P Tools/make_leather_flask.py; icon via Tools/render_item_icon.py.
The local origin is the neck grip; drop seating belongs to production ObjectFit.
"""
import bpy,bmesh,math,json,hashlib
from pathlib import Path
from mathutils import Vector
REPO=Path(__file__).resolve().parents[1]
OUT=REPO/'Assets/HexLiveContent/RuntimeSource/Objects/tool.flask_leather.fbx'
bpy.ops.wm.read_factory_settings(use_empty=True)
def palette(file,fragment,name):
    bpy.ops.import_scene.fbx(filepath=str(REPO/'Assets/HexLiveContent/RuntimeSource/Objects'/file))
    source=next((m for o in bpy.context.scene.objects if o.type=='MESH' for m in o.data.materials
        if m and fragment in m.name),None)
    if source is None: raise RuntimeError('Missing canonical palette '+fragment)
    mat=source.copy();mat.name=name;mat.use_nodes=True
    shader=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    shader.inputs['Roughness'].default_value=.94
    # The same palette, with a small darkening of hide to retain faceted form under the icon rig.
    if name=='FlaskLeather':
        color=shader.inputs['Base Color'].default_value[:]
        shader.inputs['Base Color'].default_value=tuple(min(.38,v*.75) for v in color[:3])+(1,)
    mat.diffuse_color=shader.inputs['Base Color'].default_value
    for obj in list(bpy.data.objects):bpy.data.objects.remove(obj,do_unlink=True)
    return mat
# Hide files use their own imported material name; choose that actual first slot.
bpy.ops.import_scene.fbx(filepath=str(REPO/'Assets/HexLiveContent/RuntimeSource/Objects/resource_hide_native.fbx'))
hide=next(m.name for o in bpy.context.scene.objects if o.type=='MESH' for m in o.data.materials if m)
for obj in list(bpy.data.objects):bpy.data.objects.remove(obj,do_unlink=True)
leather=palette('resource_hide_native.fbx',hide,'FlaskLeather')
thread=palette('tool.torch.fbx','SurvivalRope','FlaskThread')
cork=palette('tool.torch.fbx','SurvivalWood','FlaskCork')
parts=[]
def finish(obj,name,mat):
    obj.name=name;obj.data.materials.clear();obj.data.materials.append(mat)
    for face in obj.data.polygons:face.use_smooth=False
    parts.append(obj);return obj
# Squashed pear pouch, visibly stitched rather than a plastic bottle silhouette.
rings=[(-.195,.027,.016),(-.174,.068,.033),(-.115,.086,.039),(-.055,.075,.035),(-.018,.025,.018)]
vertices=[];faces=[];sides=8
for z,rx,ry in rings:
 for i in range(sides):
    a=2*math.pi*i/sides;vertices.append((math.cos(a)*rx,math.sin(a)*ry,z))
for r in range(len(rings)-1):
 for i in range(sides):
    j=(i+1)%sides;faces.append((r*sides+i,r*sides+j,(r+1)*sides+j,(r+1)*sides+i))
faces.extend([tuple(reversed(range(sides))),tuple((len(rings)-1)*sides+i for i in range(sides))])
mesh=bpy.data.meshes.new('Flask stitched hide pouch');mesh.from_pydata(vertices,[],faces);mesh.update()
obj=bpy.data.objects.new('FlaskBody',mesh);bpy.context.collection.objects.link(obj);finish(obj,'FlaskBody',leather)
def cylinder(name,a,b,radius,mat,sides=8):
 a,b=Vector(a),Vector(b);delta=b-a
 bpy.ops.mesh.primitive_cylinder_add(vertices=sides,radius=radius,depth=delta.length,location=(a+b)*.5)
 obj=bpy.context.object;obj.rotation_euler=Vector((0,0,1)).rotation_difference(delta.normalized()).to_euler()
 return finish(obj,name,mat)
cylinder('Leather neck',(0,0,-.02),(0,0,.019),.021,leather)
cylinder('Wooden stopper',(0,0,.017),(0,0,.038),.018,cork)
bpy.ops.mesh.primitive_torus_add(major_segments=8,minor_segments=3,major_radius=.022,minor_radius=.0035,location=(0,0,-.008))
finish(bpy.context.object,'Rope neck binding',thread)
def seam_point(z,side):
 for lower,upper in zip(rings,rings[1:]):
    if lower[0]<=z<=upper[0]:
        t=(z-lower[0])/(upper[0]-lower[0]);rx=lower[1]+(upper[1]-lower[1])*t;ry=lower[2]+(upper[2]-lower[2])*t
        # Exact front facet of this eight-sided cross section, plus thread thickness.
        return (side*rx*.8,-ry*.4828427-.0012,z)
 raise RuntimeError('Stitch outside the authored pouch')
for side in (-1,1):
 for i,z in enumerate((-.17,-.145,-.115,-.083,-.055,-.034)):
    cylinder(f'Seam {side} {i}',seam_point(z-.004,side),seam_point(z+.004,side),.0018,thread,4)
bpy.ops.object.select_all(action='DESELECT')
for obj in parts:obj.select_set(True)
bpy.context.view_layer.objects.active=parts[0];bpy.ops.object.join();obj=bpy.context.object
obj.name='tool.flask_leather';bpy.ops.object.transform_apply(location=True,rotation=True,scale=True)
bm=bmesh.new();bm.from_mesh(obj.data);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(obj.data);bm.free()
triangles=sum(len(p.vertices)-2 for p in obj.data.polygons);assert 28<=triangles<=400,triangles
# All geometry is at its final origin; exporter retains the native Blender→Unity basis.
OUT.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.export_scene.fbx(filepath=str(OUT),use_selection=True,object_types={'MESH'},axis_forward='-Z',axis_up='Y',
    path_mode='STRIP',add_leaf_bones=False,bake_anim=False,mesh_smooth_type='FACE',use_mesh_modifiers=False)
source=REPO/'_ArtSource/Props/leather_flask.blend';source.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(source),compress=True)
report=dict(triangles=triangles,source=str(source.relative_to(REPO)),modelSha256=hashlib.sha256(OUT.read_bytes()).hexdigest(),
    bounds=list(obj.dimensions),grip=[0,0,0],targetSize=.24,materials=[m.name for m in obj.data.materials])
out=REPO/'Build/Diagnostics/bug-wave2-actions/flask';out.mkdir(parents=True,exist_ok=True)
(out/'model-authored.json').write_text(json.dumps(report,indent=2),encoding='utf8')
print('FLASK_MODEL_AUTHORED',json.dumps(report))

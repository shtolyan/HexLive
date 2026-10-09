"""§56.7 portable human parts: deterministic flat-shaded FBXs, raw/cooked variants.
Run with Blender -b -P Tools/make_human_food_models.py. Icons use render_item_icon.py.
"""
from pathlib import Path
import math
import bpy
from mathutils import Vector
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'Assets/HexLiveContent/RuntimeSource/Objects'

def material(name, color):
    m=bpy.data.materials.new(name); m.diffuse_color=(*color,1); m.use_nodes=True
    bs=m.node_tree.nodes.get('Principled BSDF');bs.inputs['Base Color'].default_value=(*color,1);bs.inputs['Roughness'].default_value=.85
    return m

def ellipsoid(name,pos,scale,mat):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=8,ring_count=4,location=pos)
    o=bpy.context.object;o.name=name;o.scale=scale;o.data.materials.append(mat)
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    return o

def segment(name,a,b,r1,r2,mat):
    delta=Vector(b)-Vector(a)
    bpy.ops.mesh.primitive_cone_add(vertices=8,radius1=r1,radius2=r2,depth=delta.length,location=(Vector(a)+Vector(b))*.5)
    o=bpy.context.object;o.name=name;o.rotation_euler=delta.to_track_quat('Z','Y').to_euler();o.data.materials.append(mat)
    return o

for cooked in (False,True):
    for part in ('arm','leg','torso'):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        # Existing palette provides the dark baked exterior; raw flesh is a muted
        # skin tone rather than wood. Keep albedo below the icon rig clipping range.
        with bpy.data.libraries.load(str(ROOT/'Assets/HexLiveContent/source.blend')) as (src,dst):
            dst.materials=['Prosthetic_Leather']
        palette=dst.materials[0].diffuse_color[:3]
        skin=material('CookedSkin' if cooked else 'Skin',tuple(v*.65 for v in palette) if cooked else (.38,.22,.14))
        cut=material('CutSurface',(.12,.045,.025) if cooked else (.25,.018,.022))
        if part=='arm':
            segment('upper_arm',(0,0,.11),(0,.44,.10),.105,.075,skin)
            segment('forearm',(0,.44,.10),(0,.80,.08),.075,.045,skin)
            ellipsoid('palm',(0,.89,.07),(.078,.12,.04),skin)
            for i in range(4):
                x=(i-1.5)*.033
                segment('finger',(x,.95,.07),(x,1.09-abs(i-1.5)*.018,.065),.018,.013,skin)
            segment('thumb',(-.055,.85,.07),(-.12,.92,.06),.025,.018,skin)
            segment('cut',(0,-.004,.11),(0,.006,.11),.104,.104,cut)
        elif part=='leg':
            segment('thigh',(0,0,.14),(0,.48,.115),.15,.10,skin)
            segment('shin',(0,.48,.115),(0,.95,.08),.10,.055,skin)
            ellipsoid('foot',(0,1.035,.055),(.075,.145,.055),skin)
            segment('cut',(0,-.004,.14),(0,.006,.14),.149,.149,cut)
        else:
            ellipsoid('pelvis',(0,.18,.13),(.235,.23,.13),skin)
            ellipsoid('torso',(0,.55,.14),(.26,.38,.14),skin)
            segment('neck',(0,.83,.14),(0,.97,.14),.085,.085,skin)
            ellipsoid('head',(0,1.07,.15),(.125,.17,.12),skin)
            for x in (-.24,.24): ellipsoid('shoulder_cut',(x,.72,.14),(.026,.075,.075),cut)
            for x in (-.12,.12): ellipsoid('hip_cut',(x,.045,.13),(.083,.025,.075),cut)
        for o in bpy.context.scene.objects:
            if o.type=='MESH':
                for p in o.data.polygons:p.use_smooth=False
                o.select_set(True)
        bpy.context.view_layer.objects.active=next(o for o in bpy.context.scene.objects if o.type=='MESH')
        bpy.ops.object.join();o=bpy.context.object
        o.name='Human_'+part
        state='cooked' if cooked else 'raw'
        path=OUT/f'food.human_{part}_{state}.fbx'
        bpy.ops.export_scene.fbx(filepath=str(path),use_selection=True,object_types={'MESH'},axis_forward='-Z',axis_up='Y',path_mode='STRIP',add_leaf_bones=False,bake_anim=False,mesh_smooth_type='FACE')
        print('EXPORTED',path.name,len(o.data.polygons))

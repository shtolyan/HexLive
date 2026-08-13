import bpy, math, os
from mathutils import Vector

COLLECTION = "HL_WARDROBE_REVIEW"
PREFIX = "HL_Wardrobe_"
CX, CY = 20.0, 0.0

old = bpy.data.collections.get(COLLECTION)
if old:
    for obj in list(old.objects): bpy.data.objects.remove(obj, do_unlink=True)
    bpy.data.collections.remove(old)
col = bpy.data.collections.new(COLLECTION)
bpy.context.scene.collection.children.link(col)

def mat(name, color, rough=.78, metallic=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = (*color, 1)
    m.use_nodes = True
    bs = m.node_tree.nodes.get('Principled BSDF')
    bs.inputs['Base Color'].default_value = (*color, 1)
    bs.inputs['Roughness'].default_value = rough
    bs.inputs['Metallic'].default_value = metallic
    return m

BARK=mat('Bark',(0.18,.075,.035)); SAP=mat('Sapwood',(.52,.27,.10)); ROPE=mat('Rope',(.48,.32,.14))
LEAF=mat('PalmLeaf',(.16,.31,.10)); FLOOR=mat('Heartwood',(.32,.14,.055)); DARK=mat('Ash',(.07,.055,.045))
RED=mat('Wardrobe_Garment_Red',(.48,.08,.07)); OCHRE=mat('Wardrobe_Garment_Ochre',(.72,.34,.08))
BLUE=mat('Wardrobe_Garment_Blue',(.08,.22,.34)); CREAM=mat('Wardrobe_Garment_Cream',(.68,.58,.40))

def link(obj):
    for c in list(obj.users_collection): c.objects.unlink(obj)
    col.objects.link(obj); return obj

def cube(name, loc, scale, material, bevel=.025):
    bpy.ops.mesh.primitive_cube_add(location=loc)
    o=link(bpy.context.object); o.name=PREFIX+name; o.scale=scale; bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    if bevel:
        mod=o.modifiers.new('Hand hewn edges','BEVEL'); mod.width=bevel; mod.segments=1
    o.data.materials.append(material); return o

def pole(name,a,b,r,material,verts=8):
    a,b=Vector(a),Vector(b); d=b-a
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=r, depth=d.length, location=(a+b)/2)
    o=link(bpy.context.object); o.name=PREFIX+name; o.rotation_mode='QUATERNION'; o.rotation_quaternion=Vector((0,0,1)).rotation_difference(d.normalized()); o.data.materials.append(material); return o

def rope(name,a,b,r=.012): return pole(name,a,b,r,ROPE,8)

def rope_wrap(name,loc,major=.047,minor=.009,rotation=(0,0,0)):
    bpy.ops.mesh.primitive_torus_add(major_segments=12,minor_segments=5,
        location=loc,major_radius=major,minor_radius=minor,rotation=rotation)
    o=link(bpy.context.object);o.name=PREFIX+name;o.data.materials.append(ROPE);return o

def hanger(name,x,y,z,tilt=0):
    root=bpy.data.objects.new(PREFIX+name,None); col.objects.link(root)
    # hook and two shoulder arms; intentionally uneven, made from bent twigs
    pole(name+'_L',(x,y,z),(x-.18,y,z-.13),.012,SAP,7).parent=root
    pole(name+'_R',(x,y,z),(x+.18,y,z-.13),.012,SAP,7).parent=root
    pole(name+'_base',(x-.18,y,z-.13),(x+.18,y,z-.13),.010,SAP,7).parent=root
    # faceted hook arc
    pts=[(x,y,z),(x,y,z+.08),(x+.045,y,z+.12),(x+.085,y,z+.09)]
    for i in range(len(pts)-1): pole(name+f'_hook{i}',pts[i],pts[i+1],.010,SAP,7).parent=root
    root.rotation_euler[2]=math.radians(tilt); return root

def garment(name,x,y,z,kind,material):
    # Low-poly display shells, separate from hangers; later runtime garment meshes replace these.
    if kind=='shirt':
        body=cube(name+'_body',(x,y-.012,z-.29),(.145,.025,.18),material,.02)
        cube(name+'_sleeveL',(x-.19,y-.01,z-.23),(.07,.023,.075),material,.018).rotation_euler[1]=math.radians(-24)
        cube(name+'_sleeveR',(x+.19,y-.01,z-.23),(.07,.023,.075),material,.018).rotation_euler[1]=math.radians(24)
    elif kind=='pants':
        cube(name+'_waist',(x,y-.012,z-.18),(.14,.026,.07),material,.018)
        a=cube(name+'_legL',(x-.07,y-.01,z-.40),(.062,.025,.19),material,.018); a.rotation_euler[1]=math.radians(-4)
        b=cube(name+'_legR',(x+.07,y-.01,z-.40),(.062,.025,.19),material,.018); b.rotation_euler[1]=math.radians(4)
    else: # bra/top
        cube(name+'_band',(x,y-.012,z-.22),(.145,.025,.045),material,.018)
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=.09,location=(x-.075,y-.025,z-.17)); o=link(bpy.context.object);o.name=PREFIX+name+'_cupL';o.scale=(1,.35,.75);o.data.materials.append(material)
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=.09,location=(x+.075,y-.025,z-.17)); o=link(bpy.context.object);o.name=PREFIX+name+'_cupR';o.scale=(1,.35,.75);o.data.materials.append(material)

def shoe(name,x,y,z,material,mirror=1):
    o=cube(name,(x,y,z),(.12,.24,.07),material,.035); o.rotation_euler[2]=math.radians(4*mirror)
    cube(name+'_heel',(x,y+.17,z+.075),(.10,.075,.07),material,.025).rotation_euler[2]=o.rotation_euler[2]

# Current approved 1x1 layout review: actual R=1.5 footprint, floor at z=.14.
for i in range(6):
    a0=math.radians(90+i*60); a1=math.radians(90+(i+1)*60)
    pole(f'HexEdge_{i}',(CX+1.5*math.cos(a0),CY+1.5*math.sin(a0),.145),(CX+1.5*math.cos(a1),CY+1.5*math.sin(a1),.145),.018,DARK,8)
for i,y in enumerate((-1.02,-.64,-.25,.14,.53,.92)):
    half=max(.72,1.27-abs(y)*.40); cube(f'Floor_{i}',(CX,y,.105),(half,.16,.035),FLOOR,.02)

# Furniture footprints from BuildingRules, shown as restrained proxies for layout truth.
def bed_proxy(name,x,y,yaw):
    root=bpy.data.objects.new(PREFIX+name,None); col.objects.link(root); root.location=(CX+x,CY+y,.14);root.rotation_euler[2]=math.radians(yaw)
    for sx in (-.28,.28):
        for sy in (-.63,.63):
            o=cube(name+f'_log_{sx}_{sy}',(0,0,0),(.29,.07,.07),BARK,.025);o.location=(sx,sy,.09);o.parent=root
    for yy in (-.56,-.28,0,.28,.56):
        o=pole(name+f'_slat_{yy}',(-.30,yy,.20),(.30,yy,.20),.018,SAP,7);o.parent=root;o.location=(0,0,0)
bed_proxy('BedA',-.974279,0,0); bed_proxy('BedB',.487139,.84375,60)

# Hearth proxy at canonical current coordinate.
for i in range(6):
    a=math.radians(i*60); x=CX-.3248+math.cos(a)*.20; y=.5625+math.sin(a)*.20
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=.11,location=(x,y,.23));o=link(bpy.context.object);o.name=PREFIX+f'HearthStone_{i}';o.scale=(1.15,.8,.55);o.data.materials.append(DARK)

# Wardrobe: one wall bay wide, shallow enough for the hut. Back sits close to lower-left wall.
# Local rack dimensions: W 0.90, D 0.31, H 1.46; five relaxed clothing slots at 0.17 pitch.
RX,RY,RZ=CX+.02,-1.02,.14
root=bpy.data.objects.new(PREFIX+'ROOT',None);col.objects.link(root)
for x in (-.44,.44):
    p=pole(f'FramePost_{x}',(RX+x,RY, RZ),(RX+x,RY,RZ+1.46),.035,BARK,8);p.parent=root
pole('TopRail',(RX-.48,RY,RZ+1.46),(RX+.48,RY,RZ+1.46),.035,BARK,8).parent=root
pole('HangRail',(RX-.40,RY-.015,RZ+1.25),(RX+.40,RY-.015,RZ+1.25),.025,SAP,8).parent=root
# Visible cord bindings, not wooden-looking diagonal pegs. Three slightly
# offset coils bind every top corner; two coils tie the shoe shelf to posts.
for x in (-.44,.44):
    for i,z in enumerate((RZ+1.405,RZ+1.43,RZ+1.455)):
        rope_wrap(f'TopBinding_{x}_{i}',(RX+x,RY,z),.046,.009).parent=root
    for i,z in enumerate((RZ+.12,RZ+.145)):
        rope_wrap(f'ShelfBinding_{x}_{i}',(RX+x,RY,z),.043,.008).parent=root
# two-board shoe shelf raised above floor
for y in (-.095,.095): cube(f'ShoeShelf_{y}',(RX,RY+y,RZ+.16),(.47,.085,.035),FLOOR,.022).parent=root

slots=[-.34,-.17,0,.17,.34]
kinds=['shirt','bra','pants','shirt','shirt']; mats=[RED,CREAM,BLUE,OCHRE,LEAF]
for i,(x,kind,mm) in enumerate(zip(slots,kinds,mats)):
    hanger(f'Hanger_{i}',RX+x,RY-.02,RZ+1.22,(-2+i)).parent=root
    garment(f'Garment_{i}',RX+x,RY-.035,RZ+1.18,kind,mm)
for i,x in enumerate((-.26,.02,.29)):
    shoe(f'Shoe_{i}',RX+x,RY-.01,RZ+.29,DARK,-1 if i%2 else 1)

# Slot markers are empties so export/runtime can address contents independently.
for i,x in enumerate(slots):
    e=bpy.data.objects.new(f'HL_Wardrobe_Slot_Clothing_{i:02}',None);col.objects.link(e);e.location=(RX+x,RY-.02,RZ+1.22);e.empty_display_type='SPHERE';e.empty_display_size=.025
for i,x in enumerate((-.26,.02,.29)):
    e=bpy.data.objects.new(f'HL_Wardrobe_Slot_Shoes_{i:02}',None);col.objects.link(e);e.location=(RX+x,RY-.01,RZ+.29);e.empty_display_type='CUBE';e.empty_display_size=.025

# Review camera / lights.
target=Vector((CX,0,.75)); cam_data=bpy.data.cameras.new('HL_Wardrobe_CameraData');cam=bpy.data.objects.new('HL_Wardrobe_Camera',cam_data);col.objects.link(cam);cam.location=(CX+4.1,-4.9,3.35);cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler();cam_data.lens=52;bpy.context.scene.camera=cam
for name,loc,energy,size in [('Key',(CX-2.5,-3,4.5),1100,4),('Fill',(CX+3,-1,2.8),700,3),('Warm',(CX,3,3.5),900,3)]:
    ld=bpy.data.lights.new(name,'AREA');ld.energy=energy;ld.shape='DISK';ld.size=size;lo=bpy.data.objects.new('HL_Wardrobe_'+name,ld);col.objects.link(lo);lo.location=loc;lo.rotation_euler=(target-lo.location).to_track_quat('-Z','Y').to_euler()

scene=bpy.context.scene
scene.render.engine='BLENDER_EEVEE';scene.render.resolution_x=1000;scene.render.resolution_y=760;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG';scene.render.filepath='/Volumes/ORICO/HexLive/_ArtSource/wardrobe_hut_review.png'
scene.world.color=(.025,.035,.025)
scene.render.film_transparent=False
bpy.ops.wm.save_as_mainfile(filepath='/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend')
bpy.ops.render.render(write_still=True)
print('WARDROBE_DONE', {'clothingSlots':5,'shoeSlots':3,'sizeWu':(0.96,0.31,1.46),'render':scene.render.filepath})

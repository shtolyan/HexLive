"""Blender -b -P Tools/blender/verify_palm_harvest.py: exact crown proof + ground preview.

Offline source check only; Unity production placement is covered separately by
GroundPileLayoutTests. No scene or source asset is modified.
"""
import json
import math
import sys
from pathlib import Path
import bpy
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[2]
SOURCES = ROOT / 'Assets/HexLiveContent/RuntimeSource/Objects'
OUT = ROOT / 'Build/bug429'
OUT.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)

def load(name):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(SOURCES / (name + '.fbx')))
    return [o for o in bpy.data.objects if o not in before and o.type == 'MESH']

def points(objects, leaf_only=False):
    result = []
    for o in objects:
        indices = {i for p in o.data.polygons
                   if not leaf_only or o.data.materials[p.material_index].name.split('.')[0] == 'LeafGreen'
                   for i in p.vertices}
        result.extend(o.matrix_world @ o.data.vertices[i].co for i in sorted(indices))
    return result

def bounds(vs):
    return [Vector([f(v[i] for v in vs) for i in range(3)]) for f in (min,max)]

def normalize(vs):
    lo, hi = bounds(vs)
    offset = Vector(((lo.x+hi.x)/2,(lo.y+hi.y)/2,lo.z))
    return sorted(tuple(round(c,5) for c in v-offset) for v in vs)

palm = load('palm_final_native')
crown = load('resource.palm_crown')
standing = points(palm,True)
fallen = points(crown)
a,b=normalize(standing),normalize(fallen)
assert len(a)==len(b)
# Export rounding can cross the fifth decimal boundary; compare sorted coordinates.
error=max(abs(x-y) for p,q in zip(a,b) for x,y in zip(p,q))
assert error < .00003, error
proof={'standingCrownVertices':len(a),'fallenCrownVertices':len(b),
       'maximumCoordinateError':error,'groundMinZ':min(v.z for v in fallen)}
assert abs(proof['groundMinZ']) < .00003
def foliage_faces(objects):
    return [p for o in objects for p in o.data.polygons
            if o.data.materials[p.material_index].name.split('.')[0] == 'LeafGreen']
def foliage_color(objects):
    return next(tuple(m.diffuse_color) for o in objects for m in o.data.materials
                if m.name.split('.')[0] == 'LeafGreen')
assert len(foliage_faces(palm)) == len(foliage_faces(crown))
assert max(abs(a-b) for a,b in zip(foliage_color(palm),foliage_color(crown))) < .00003
proof['foliagePolygons'] = len(foliage_faces(crown))
proof['materialMatchesStandingPalm'] = True
(OUT/'crown-proof.json').write_text(json.dumps(proof,indent=2))
if '--proof-only' in sys.argv:
    print('PASS',proof)
    raise SystemExit(0)
for o in palm: o.location.x -= 3
for o in crown: o.location.x += 2
for i in range(3):
    objects=load('resource.log')
    vs=points(objects);lo,hi=bounds(vs);factor=1.05/max(hi-lo)
    yaw=math.radians(18+i*37)
    matrix=Matrix.Translation(Vector((-1+i*1.35,-3,0))) @ Matrix.Rotation(yaw,4,'Z') @ Matrix.Scale(factor,4)
    for o in objects:
        o.matrix_world=matrix@o.matrix_world
    low=min(v.z for v in points(objects))
    for o in objects:o.location.z-=low
for i in range(5):
    objects=load('palm_frond_native')
    vs=points(objects);lo,hi=bounds(vs);factor=.825/max(hi-lo)
    center=(lo+hi)/2
    yaw=math.radians((i*73+24)%360)
    matrix=Matrix.Translation(Vector((-1+i*.85,-4.2,0))) @ Matrix.Rotation(yaw,4,'Z') @ Matrix.Scale(factor,4) @ Matrix.Translation(-center)
    for o in objects:o.matrix_world=matrix@o.matrix_world
    low=min(v.z for v in points(objects))
    for o in objects:o.location.z-=low
bpy.ops.mesh.primitive_plane_add(size=200)
plane=bpy.context.object
mat=bpy.data.materials.new('preview ground');mat.diffuse_color=(.13,.18,.12,1);plane.data.materials.append(mat)
for o in bpy.context.scene.objects:
    if o.type=='MESH':
        for p in o.data.polygons:p.use_smooth=False
bpy.ops.object.light_add(type='AREA',location=(0,-6,10))
light=bpy.context.object;light.data.energy=1500;light.data.size=8
bpy.ops.object.camera_add(location=(9,-15,10))
cam=bpy.context.object;target=Vector((-.4,-1,1.5));cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
cam.data.type='ORTHO';cam.data.ortho_scale=14
scene=bpy.context.scene;scene.camera=cam;scene.render.engine='CYCLES';scene.cycles.samples=32
scene.cycles.use_denoising=True;scene.world=bpy.data.worlds.new('PreviewWorld');scene.world.color=(.35,.35,.35)
scene.render.resolution_x=1400;scene.render.resolution_y=950;scene.render.resolution_percentage=100
scene.render.filepath=str(OUT/'palm-ground.png');scene.view_settings.view_transform='Standard'
bpy.ops.render.render(write_still=True)
(OUT/'crown-proof.json').write_text(json.dumps(proof,indent=2))
print('PASS',proof)

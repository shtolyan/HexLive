import bpy,sys
from pathlib import Path
from mathutils import Vector
D=Path(__file__).resolve().parent/'Clothing_Primal'
bpy.ops.wm.open_mainfile(filepath=str(D/'Primal_Run_Test.blend'))
sc=bpy.context.scene;sc.cycles.samples=12;sc.render.resolution_x=1000;sc.render.resolution_y=760;sc.render.resolution_percentage=100
cam=sc.camera;cam.data.ortho_scale=3.55
views=['front','back'] if '--all-views' in sys.argv else ['front']
for view in views:
 folder=D/('frames_'+view);folder.mkdir(exist_ok=True)
 cam.location=(1.6,-7.8,2.35) if view=='front' else (-1.6,7.8,2.35)
 cam.rotation_euler=(Vector((0,0,1.03))-cam.location).to_track_quat('-Z','Y').to_euler()
 for f in (range(1,20) if view=='front' else [1,6,11,16]):
  sc.frame_set(f);bpy.context.view_layer.update();sc.render.filepath=str(folder/f'{f:04d}.png');bpy.ops.render.render(write_still=True)
 print('RENDERED',view,flush=True)

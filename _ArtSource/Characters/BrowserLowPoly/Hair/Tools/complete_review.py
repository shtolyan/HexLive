import bpy,json,traceback
from pathlib import Path
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Hair');phase='review'
def step():
 global phase
 try:
  if phase=='review':
   state=json.loads((D/'Review/status.json').read_text())['state']
   if state=='rendering':return 1
   if state!='done':return None
   s=json.loads((D/'build_status.json').read_text())
   for name,record in s['hairs'].items():
    for lod in [0,1]:
     o=bpy.data.objects[name+f'_LOD{lod}'];record[f'lod{lod}_triangles']=sum(len(p.vertices)-2 for p in o.data.polygons)
    record['lod0_vertices']=len(bpy.data.objects[name+'_LOD0'].data.vertices)
   (D/'build_status.json').write_text(json.dumps(s,indent=2));(D/'verification.json').write_text(json.dumps({'state':'checking','hairs':{}}));phase='verify';exec(compile((D/'Tools/verify_export_hair.py').read_text(),'verify_export_hair.py','exec'),{});return 1
  state=json.loads((D/'verification.json').read_text())['state']
  if state=='checking':return 1
  if state!='done':return None
  (D/'final_status.json').write_text(json.dumps({'state':'saving'}));exec(compile((D/'Tools/finalize_scene.py').read_text(),'finalize_scene.py','exec'),{});return None
 except:(D/'finish_error.txt').write_text(traceback.format_exc());return None
bpy.app.timers.register(step,first_interval=.3)

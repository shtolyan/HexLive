import bpy,json,traceback
from pathlib import Path
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Hair');phase='build'
def finish():
 global phase
 try:
  if phase=='build':
   s=json.loads((D/'build_status.json').read_text())
   if s['state']=='error':return None
   if s['state']!='done':return 1
   for kind in ['meshes','armatures','materials','images','actions']:
    group=getattr(bpy.data,kind)
    for x in list(group):
     if x.users==0:group.remove(x)
   code=(D/'Tools/hair_controls.py').read_text();exec(compile(code,'hair_controls.py','exec'),{})
   t=bpy.data.texts.get('Primal Controls.py');marker='\n# ---- Hair controls ----\n';original=t.as_string().split(marker)[0];t.clear();t.write(original+marker+code)
   phase='verify';exec(compile((D/'Tools/verify_export_hair.py').read_text(),'verify_export_hair.py','exec'),{});return 1
  if phase=='verify':
   p=D/'verification.json'
   if not p.exists():return 1
   s=json.loads(p.read_text())
   if s['state']=='error':return None
   if s['state']!='done':return 1
   phase='review';exec(compile((D/'Tools/render_hair_review.py').read_text(),'render_hair_review.py','exec'),{});return None
 except:
  (D/'finish_error.txt').write_text(traceback.format_exc());return None
bpy.app.timers.register(finish,first_interval=.3)

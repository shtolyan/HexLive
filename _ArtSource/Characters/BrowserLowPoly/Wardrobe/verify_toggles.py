import bpy,json
from pathlib import Path
D=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Wardrobe');sc=bpy.context.scene;dg=bpy.context.evaluated_depsgraph_get();roots=[o for o in sc.objects if o.type=='EMPTY' and 'equipped' in o];old={o.name:int(o['equipped']) for o in roots};out={}
for root in roots:
 mesh=bpy.data.objects[root.name[:-5]];states=[]
 for v in [0,1]:
  root['equipped']=v;root.update_tag();sc.frame_set(sc.frame_current);dg.update();states.append({'equipped':v,'hide_render':bool(mesh.hide_render),'hide_viewport':bool(mesh.hide_viewport)});assert mesh.hide_render==bool(not v) and mesh.hide_viewport==bool(not v)
 root['equipped']=old[root.name];root.update_tag();out[root.name]=states
items=[bpy.data.objects['Kshishtof_'+i+'_Wear'] for i in ['PrimalBriefs','PrimalSkirt','PrimalDress']];gen=bpy.data.objects['Kshishtof_Genitals'];out['genitals']=[]
for values in [(0,0,0),(1,0,0),(0,1,0),(0,0,1),(1,1,1)]:
 for r,v in zip(items,values):r['equipped']=v;r.update_tag()
 sc.frame_set(sc.frame_current);dg.update();out['genitals'].append({'pelvis_items':values,'hidden':bool(gen.hide_render)});assert gen.hide_render==any(values) and gen.hide_viewport==any(values)
for r in roots:r['equipped']=old[r.name];r.update_tag()
sc.frame_set(1);dg.update();(D/'toggle_verification.json').write_text(json.dumps(out,indent=2));bpy.ops.wm.save_as_mainfile(filepath=str(D.parent/'Primal_Wardrobe.blend'));print('ALL_EQUIPMENT_TOGGLES_PASS')

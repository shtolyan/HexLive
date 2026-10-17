import bpy,json,hashlib
from pathlib import Path
from mathutils import Vector
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly');D=OUT/'Wardrobe';sc=bpy.context.scene
# Separate genital object follows actual pelvis-covering items, with no mesh deletion.
gen=bpy.data.objects['Kshishtof_Genitals']
for prop in ['hide_render','hide_viewport']:
 try:gen.driver_remove(prop)
 except:pass
 d=gen.driver_add(prop).driver
 for item,var in [('PrimalBriefs','briefs'),('PrimalSkirt','skirt'),('PrimalDress','fur')]:
  v=d.variables.new();v.name=var;v.targets[0].id=bpy.data.objects['Kshishtof_'+item+'_Wear'];v.targets[0].data_path='["equipped"]'
 d.expression='briefs or skirt or fur'
 setattr(gen,prop,True)
gen['runtime_binding']='Separate object for BodyBones.genitals. In this preview hidden when briefs, skirt or fur dress equipped.'
for l in ['Marta','Kshishtof']:
 rig=bpy.data.objects[l+'_Rig'];rig.hide_set(True)
 if 'primal_briefs_equipped' in rig:del rig['primal_briefs_equipped']
 for suffix in ['_LOD1']:
  o=bpy.data.objects.get(l+suffix)
  if o:bpy.data.objects.remove(o,do_unlink=True)
# Compact, predictable outliner.
def coll(name):
 c=bpy.data.collections.get(name)
 if not c:c=bpy.data.collections.new(name);sc.collection.children.link(c)
 return c
def move(o,c):
 for old in list(o.users_collection):old.objects.unlink(o)
 c.objects.link(o)
for o in list(sc.objects):
 if o.name.startswith('Marta_'):c=coll('01 Марта — одежда' if 'Primal' in o.name else '01 Марта — тело и риг')
 elif o.name.startswith('Kshishtof_'):c=coll('02 Кшиштоф — одежда' if 'Primal' in o.name else '02 Кшиштоф — тело и риг')
 else:c=coll('03 Студия')
 move(o,c)
for c in list(bpy.data.collections):
 if len(c.objects)==0 and len(c.children)==0:bpy.data.collections.remove(c)
# Embedded instructions and optional UI panel; no auto-executing scripts.
text=bpy.data.texts.get('Primal Controls.py') or bpy.data.texts.new('Primal Controls.py');text.clear();text.write((D/'wardrobe_controls.py').read_text());exec(compile(text.as_string(),'Primal Controls.py','exec'))
readme=bpy.data.texts.get('НАЧАТЬ ЗДЕСЬ') or bpy.data.texts.new('НАЧАТЬ ЗДЕСЬ');readme.clear();readme.write('PRIMAL — ПЕРВЫЙ КОМПЛЕКТ\n\nОдин файл, два персонажа. Пробел: бег на месте.\nПанель N → Primal: базовый / меховой комплект, отдельные вещи, расцветки.\nПосле повторного открытия: Scripting → текст Primal Controls.py → Run Script для панели.\nБез панели: свойства объектов *_Wear — equipped (0/1) и material_variant. Все переключатели и текстуры сохранены внутри файла.\n\nУ каждого предмета один skinned mesh, исходные material slots, свой скелет hip.\nИмена костей и 109 блендшейпов тела сохранены.\nМужские гениталии — отдельный объект, скрываются под трусами/юбкой/меховой одеждой.\n\nЭто Blender-примерка. Каталог и префабы игры ещё не заменены.\n')
sc.frame_set(1);sc.render.engine='CYCLES';sc.cycles.samples=12;sc.render.resolution_x=1200;sc.render.resolution_y=850;sc.render.resolution_percentage=100
for screen in bpy.data.screens:
 for area in screen.areas:
  if area.type=='CONSOLE':area.type='VIEW_3D'
  if area.type=='VIEW_3D':
   s=next((x for x in area.spaces if x.type=='VIEW_3D'),None)
   if s is None:continue
   s.region_3d.view_perspective='CAMERA';s.overlay.show_overlays=False;s.shading.type='MATERIAL';s.show_region_ui=True
# Purge temporary, unused resources only.
for dbs in [bpy.data.meshes,bpy.data.armatures,bpy.data.materials,bpy.data.images,bpy.data.actions]:
 for db in list(dbs):
  if db.users==0:dbs.remove(db)
bpy.ops.object.select_all(action='DESELECT');o=bpy.data.objects['Marta_PrimalTop_Wear'];o.select_set(True);bpy.context.view_layer.objects.active=o
bpy.context.view_layer.update();bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'))
print('WARDROBE_CONSOLIDATED')

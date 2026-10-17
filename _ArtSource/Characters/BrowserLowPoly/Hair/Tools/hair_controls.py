import bpy
HAIR_NAMES=['AdellHair','AsukaHair','BendineHair','Bob3Hair','ChunkyHair','EilisHair','Hair07','JelikaHair_32434','JenniferHair','LeonyPonytail','LoonaHair','LowPonytail','Neu09Hair','OnyxHair','ShilohHair','TootsieRollHair']
class HAIR_OT_style(bpy.types.Operator):
 bl_idname='hair.web_style';bl_label='Причёска';bl_options={'REGISTER','UNDO'}
 index:bpy.props.IntProperty(default=0)
 def execute(self,context):
  c=bpy.data.objects['Hair_Controls'];c['style']=self.index;c.update_tag();context.view_layer.update();return {'FINISHED'}
class HAIR_OT_lod(bpy.types.Operator):
 bl_idname='hair.web_lod';bl_label='Детализация';bl_options={'REGISTER','UNDO'}
 index:bpy.props.IntProperty(default=0)
 def execute(self,context):
  c=bpy.data.objects['Hair_Controls'];c['lod']=self.index;c.update_tag();context.view_layer.update();return {'FINISHED'}
class HAIR_MT_styles(bpy.types.Menu):
 bl_label='Женские причёски'
 def draw(self,context):
  self.layout.operator('hair.web_style',text='Без волос').index=0
  for i,name in enumerate(HAIR_NAMES,1):self.layout.operator('hair.web_style',text=name).index=i
class HAIR_PT_web(bpy.types.Panel):
 bl_label='Причёски — WebGL';bl_idname='HAIR_PT_web';bl_space_type='VIEW_3D';bl_region_type='UI';bl_category='Primal';bl_order=-10
 def draw(self,context):
  c=bpy.data.objects.get('Hair_Controls')
  if not c:return
  index=int(c['style']);self.layout.menu('HAIR_MT_styles',text='Без волос' if index==0 else HAIR_NAMES[index-1]);row=self.layout.row(align=True);row.operator('hair.web_lod',text='Близко — 2400').index=0;row.operator('hair.web_lod',text='Далеко — 800–1000').index=1;self.layout.label(text='Мужчина: без волос')
for cls in [HAIR_OT_style,HAIR_OT_lod,HAIR_MT_styles,HAIR_PT_web]:
 old=getattr(bpy.types,cls.__name__,None)
 if old:
  try:bpy.utils.unregister_class(old)
  except:pass
 bpy.utils.register_class(cls)

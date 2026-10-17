import bpy
PACK_LABELS=['Снять рюкзак','Кожаный мешок','Охотничий меховой','Походный со шкурой']
class PACK_OT_style(bpy.types.Operator):
 bl_idname='primal.backpack';bl_label='Рюкзак';bl_options={'REGISTER','UNDO'}
 actor:bpy.props.StringProperty(default='Marta')
 index:bpy.props.IntProperty(default=0,min=0,max=3)
 def execute(self,context):
  c=bpy.data.objects['Backpack_Controls'];c[self.actor]=self.index;c.update_tag();context.view_layer.update();return {'FINISHED'}
class PACK_PT_primal(bpy.types.Panel):
 bl_label='Рюкзаки — Primal';bl_idname='PACK_PT_primal';bl_space_type='VIEW_3D';bl_region_type='UI';bl_category='Primal';bl_order=5
 def draw(self,context):
  c=bpy.data.objects.get('Backpack_Controls')
  if c is None:return
  for actor,title in [('Marta','Марта'),('Kshishtof','Кшиштоф')]:
   box=self.layout.box();box.label(text=title)
   for i,label in enumerate(PACK_LABELS):
    op=box.operator('primal.backpack',text=label,depress=int(c[actor])==i);op.actor=actor;op.index=i
  self.layout.label(text='Пробел — бег / пауза')
for cls in [PACK_OT_style,PACK_PT_primal]:
 old=getattr(bpy.types,cls.__name__,None)
 if old:
  try:bpy.utils.unregister_class(old)
  except:pass
 bpy.utils.register_class(cls)

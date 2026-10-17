"""Optional Blender panel. Run this text after reopening; no auto-run preference needed."""
import bpy
LABELS={'PrimalBriefs':'Трусы','PrimalTop':'Лиф','PrimalSkirt':'Юбка','PrimalWrapBoots':'Обувь','PrimalArmWraps':'Обмотки рук','PrimalHeadband':'Повязка','PrimalDress':'Меховая одежда','PrimalCollar':'Меховая накидка'}
ORDER=list(LABELS)
class PRIMAL_OT_outfit(bpy.types.Operator):
 bl_idname='primal.outfit';bl_label='Комплект';bl_options={'REGISTER','UNDO'}
 fur:bpy.props.BoolProperty(default=False)
 def execute(self,context):
  for o in bpy.data.objects:
   if o.type=='EMPTY' and 'equipped' in o:
    item=o.get('source_item','');o['equipped']=int((item not in ['PrimalTop','PrimalSkirt']) if self.fur else item!='PrimalDress');o.update_tag()
  context.view_layer.update();return {'FINISHED'}
class PRIMAL_OT_variant(bpy.types.Operator):
 bl_idname='primal.variant';bl_label='Расцветка';bl_options={'REGISTER','UNDO'}
 target:bpy.props.StringProperty();index:bpy.props.IntProperty()
 def execute(self,context):
  o=bpy.data.objects[self.target];o['material_variant']=self.index;o.update_tag();context.view_layer.update();return {'FINISHED'}
class PRIMAL_PT_wardrobe(bpy.types.Panel):
 bl_label='Primal — первый комплект';bl_idname='PRIMAL_PT_wardrobe';bl_space_type='VIEW_3D';bl_region_type='UI';bl_category='Primal'
 def draw(self,context):
  ui=self.layout;row=ui.row(align=True);row.operator('primal.outfit',text='Базовый').fur=False;row.operator('primal.outfit',text='Меховой').fur=True
  ui.label(text='Пробел — бег / пауза')
  for label,title in [('Marta','Марта'),('Kshishtof','Кшиштоф')]:
   box=ui.box();box.label(text=title)
   for item in ORDER:
    o=bpy.data.objects.get(label+'_'+item+'_Wear')
    if o is None:continue
    row=box.row(align=True);row.prop(o,'["equipped"]',text=LABELS[item]);row.prop(o,'["material_variant"]',text='Цвет')
    names=o['variant_names'].split(' | ');i=int(o['material_variant']);box.label(text='   '+names[max(0,min(i,len(names)-1))])
  ui.label(text='Все текстуры встроены в файл')
for cls in [PRIMAL_OT_outfit,PRIMAL_OT_variant,PRIMAL_PT_wardrobe]:
 old=getattr(bpy.types,cls.__name__,None)
 if old:
  try:bpy.utils.unregister_class(old)
  except:pass
 bpy.utils.register_class(cls)

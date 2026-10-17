"""Draw the socket regression from the evaluated authoring scene and real hex constants."""
import json, math, re
from pathlib import Path
import bpy

REPO = Path(__file__).resolve().parents[2]
scene = next(s for s in bpy.data.scenes if s.get('hexlive_definition_id') == 'station.drying_rack')
bpy.context.window.scene = scene
bpy.context.view_layer.update()
root = next(o for o in scene.objects if o.get('hexlive_definition_id') == 'station.drying_rack')
for obj in [root, *root.children_recursive]:obj.update_tag(refresh={'OBJECT'})
bpy.context.view_layer.update()
slots = sorted((str(o.get('hexlive_export_name', o.name)), tuple(root.matrix_world.inverted() @ o.matrix_world.translation))
               for o in root.children_recursive if str(o.get('hexlive_export_name', o.name)).startswith('DryingSlot_'))
spatial = (REPO/'Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs').read_text()
layout = (REPO/'Assets/HexLive/Simulation/Spatial/HexPointLayout.cs').read_text()
radius = float(re.search(r'HexRadius = ([\d.]+)f', spatial)[1])
sqrt3 = float(re.search(r'Sqrt3 = ([\d.]+)f', spatial)[1])
inner = int(re.search(r'InteriorRadius = (\d+)', layout)[1])
boundary = int(re.search(r'BoundaryRadius = (\d+)', layout)[1])
unit = radius/(boundary*sqrt3)
svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="490" viewBox="0 0 1000 490">',
 '<rect width="1000" height="490" fill="#141c25"/><g font-family="Segoe UI, sans-serif" fill="#edf3f7">']
def text(x,y,t,size=15):svg.append(f'<text x="{x}" y="{y}" font-size="{size}">{t}</text>')
def circle(x,y,r,color):svg.append(f'<circle cx="{x:.3f}" cy="{y:.3f}" r="{r}" fill="{color}"/>')
text(28,34,'Сушилка: восстановление авторских точек, без изменения модели',23)
text(30,81,'До: FBX захватил невычисленные матрицы',15)
circle(152,350,17,'#ee6c66');text(185,355,'8 × (0, 0, 0)')
text(340,81,'После: 4 места на каждой перекладине',17)
for i,(name,(x,y,z)) in enumerate(slots):
 px=476+y*280;py=350-z*190;color='#9fe870' if i<4 else '#68b9ff'
 circle(px,py,7,color);text(px-9,py-14,name[-2:],12)
text(340,388,'Высоты: 1,14 / 0,73 wu; шаг вдоль: 0,22 wu')
text(340,413,'Показаны линии слотов, не размеры досок.',13)
text(714,81,'Контекст: гекс и 37 узлов r=3',16)
cx,cy,scale=842,236,83
corners=[(cx+radius*math.cos(math.pi/3*i)*scale,cy-radius*math.sin(math.pi/3*i)*scale) for i in range(6)]
svg.append('<polygon points="'+' '.join(f'{x:.3f},{y:.3f}' for x,y in corners)+'" fill="none" stroke="#708091"/>')
count=0
for r in range(-inner,inner+1):
 for q in range(max(-inner,-r-inner),min(inner,-r+inner)+1):
  x=unit*1.5*q;y=unit*sqrt3*(r+q*.5);circle(cx+x*scale,cy-y*scale,2.4,'#536575');count+=1
for i,(_, (x,y,z)) in enumerate(slots):circle(cx+x*scale,cy-y*scale,4,'#9fe870' if i<4 else '#68b9ff')
assert count==37 and len(slots)==8
text(714,388,f'R={radius:g} wu; шаг сетки={radius/boundary:g} wu')
text(714,413,'Вид сверху, оси исходного Blender X/Y.',13)
text(28,458,'Координаты прочитаны из .blend после обновления сцены. Повороты мебели: только штатные 0° + 60°k.',14)
svg.append('</g></svg>')
(REPO/'Docs/DryingRackSlots.svg').write_text('\n'.join(svg)+'\n',encoding='utf-8')
print('DRYING_SLOT_DIAGRAM',json.dumps(dict(slots=slots,hexRadius=radius,gridNodes=count)))

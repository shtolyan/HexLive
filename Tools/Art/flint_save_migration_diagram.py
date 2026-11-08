"""Draw §175.2 migration from canonical factory-exported coordinates, not a guessed grid."""
import argparse,html,json,math
from pathlib import Path

def main():
 p=argparse.ArgumentParser();p.add_argument('--data',type=Path,default=Path('Docs/flint-save-migration-layout.json'))
 p.add_argument('--output',type=Path,default=Path('Docs/FlintSaveMigration.svg'));p.add_argument('--preview',type=Path)
 args=p.parse_args();data=json.loads(args.data.read_text());points=data['points'];scale=65
 distances=[math.hypot(n['x']-a['x'],n['y']-a['y']) for a in points for n in a['nodes']
            if math.hypot(n['x']-a['x'],n['y']-a['y'])>.01]
 step=round(min(distances),3);added=sum(a['quantity'] for a in points if not a['occupied'])
 svg=['<svg xmlns="http://www.w3.org/2000/svg" width="1120" height="650" viewBox="0 0 1120 650">']
 commands=[]
 def rect(x,y,w,h,fill,stroke=None):
  svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" fill="{fill}"'+(f' stroke="{stroke}"' if stroke else '')+'/>')
  commands.append(('rect',(x,y,x+w,y+h),fill,stroke))
 def text(x,y,value,size=15,color='#c5d4de',anchor='start'):
  svg.append(f'<text x="{x}" y="{y}" font-family="Arial,sans-serif" font-size="{size}" fill="{color}" text-anchor="{anchor}">{html.escape(str(value))}</text>')
  commands.append(('text',(x,y),str(value),size,color,anchor))
 def circle(x,y,r,fill,stroke=None):
  svg.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{r}" fill="{fill}"'+(f' stroke="{stroke}"' if stroke else '')+'/>')
  commands.append(('circle',(x-r,y-r,x+r,y+r),fill,stroke))
 rect(0,0,1120,650,'#101b23')
 text(24,35,'Кремень в старых мирах: одноразовая миграция',25,'#eff6f8')
 text(24,64,f"Пример seed {data['seed']} · радиус гекса {data['hexRadius']} wu · шаг узлов {step} wu · окно схемы r={data['windowRadius']} wu",15)
 text(24,88,f"{len(points)} штатных точек · 1 занятая пропускается · добавится {added:g} единиц кремня · существующие объекты и tick сохраняются",15,'#a9e5b7')
 for index,a in enumerate(points):
  x=20+(index%3)*370;y=108+(index//3)*230
  rect(x,y,350,218,'#182936','#365163')
  text(x+14,y+25,f"Точка {index+1} · гекс ({a['q']}, {a['r']})",16,'#e4eff4')
  text(x+78,y+51,'Было',14,anchor='middle');text(x+272,y+51,'Станет',14,anchor='middle')
  for after,cx in [(False,x+78),(True,x+272)]:
   cy=y+115;circle(cx,cy,data['windowRadius']*scale,'#14222d','#47718a')
   for n in a['nodes']:
    circle(cx+(n['x']-a['x'])*scale,cy-(n['y']-a['y'])*scale,2.4,'#d8696b' if n['Blocked'] else '#566c7a')
   if a['occupied']:circle(cx,cy,6,'#e9a55b')
   elif after:circle(cx,cy,6,'#9ee36e')
   else:circle(cx,cy,6,'#14222d','#7293a6')
  text(x+175,y+121,'→',28,'#7ebddd',anchor='middle')
  label='Занято: оставляем прежний предмет' if a['occupied'] else f"Добавится {a['quantity']:g} шт. в штатной точке"
  text(x+175,y+187,label,14,'#e9a55b' if a['occupied'] else '#a9e5b7',anchor='middle')
  text(x+175,y+205,f"Узлов в окне: {len(a['nodes'])}",12,'#8ea7b7',anchor='middle')
 legend=[('#566c7a','узел сетки'),('#d8696b','заблокирован'),('#e9a55b','прежний предмет'),('#9ee36e','новый кремень')]
 for i,(color,label) in enumerate(legend):circle(32+i*265,600,5,color);text(46+i*265,605,label,15)
 text(24,634,'Показаны центры узлов, не размеры предметов. После сохранения v87 потраченный кремень не возрождается.',14)
 svg.append('</svg>');args.output.write_text('\n'.join(svg)+'\n',encoding='utf-8')
 if args.preview:
  from PIL import Image,ImageDraw,ImageFont
  image=Image.new('RGB',(1120,650));draw=ImageDraw.Draw(image)
  for command in commands:
   if command[0]=='rect':_,box,fill,stroke=command;draw.rounded_rectangle(box,radius=12,fill=fill,outline=stroke)
   elif command[0]=='circle':_,box,fill,stroke=command;draw.ellipse(box,fill=fill,outline=stroke)
   else:
    _,(x,y),value,size,color,anchor=command;font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',size)
    draw.text((x,y-size),value,font=font,fill=color,anchor='ma' if anchor=='middle' else 'la')
  args.preview.parent.mkdir(parents=True,exist_ok=True);image.save(args.preview)
 print(json.dumps({'seed':data['seed'],'sites':len(points),'addedUnits':added,'step':step,'hexRadius':data['hexRadius']}))
if __name__=='__main__':main()

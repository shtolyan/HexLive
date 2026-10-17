from PIL import Image,ImageDraw,ImageFont
from pathlib import Path
D=Path(__file__).resolve().parents[1]/'Review'
font=ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf',22);small=ImageFont.truetype('/System/Library/Fonts/Supplemental/Arial.ttf',16)
labels=['Кожаный мешок','Охотничий меховой','Походный со шкурой'];tris=['1 840','1 936','2 126']
for actors,filename in [(['Marta'],'Primal_Backpack_Collection.jpg'),(['Marta','Kshishtof'],'Primal_Backpack_Fits.jpg')]:
 out=Image.new('RGB',(1350,622*len(actors)),(30,36,40));draw=ImageDraw.Draw(out)
 for row,actor in enumerate(actors):
  for i,title in enumerate(labels):
   x=i*450;y=row*622;im=Image.open(D/f'{i+1}_{actor}_back.png');out.paste(im.resize((450,560)),(x,y));draw.text((x+16,y+572),title,font=font,fill=(224,211,186));draw.text((x+16,y+602),('Марта' if actor=='Marta' else 'Кшиштоф')+' · '+tris[i]+' треугольников',font=small,fill=(168,176,175))
 out.save(D/filename,quality=93)
print('Collection and male/female fit sheets saved')

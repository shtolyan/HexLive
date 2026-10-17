import bpy
from pathlib import Path
OUT=Path('/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly')
# Rebuild the hanging collar from source; do not inflate its loose strips against a nearest-body signed-distance heuristic.
namespace={};source=(OUT/'Wardrobe/Tools/build_wardrobe.py').read_text();exec(compile(source[:source.index('\njobs=')],'wardrobe_geometry_functions.py','exec'),namespace)
for label in ['Marta','Kshishtof']:
 name=label+'_PrimalCollar';old=[bpy.data.objects[name+s] for s in ['', '_Skeleton','_Wear']]
 for obj in old:obj.name='_Retired_'+obj.name
 namespace['build'](label,'PrimalCollar')
 for obj in old:bpy.data.objects.remove(obj,do_unlink=True)
bpy.context.scene.frame_set(1);bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Primal_Wardrobe.blend'))
print('COLLARS_SOURCE_FIT_RESTORED')

"""Process-local workaround for Blender 3.2 glTF exporter multi-influence bug.
Its __gather_skins reuses the last weight_id, overwriting WEIGHTS_0/WEIGHTS_1.
Do not modify the installed Blender add-on; patch only this export process.
Round-trip checks require all influences and a normalized sum on every vertex.
"""
import inspect

def preserve_all_skin_weight_sets():
 from io_scene_gltf2.blender.exp import gltf2_blender_gather_primitive_attributes as attributes
 function=getattr(attributes,'__gather_skins',None)
 if function is None:return
 source=inspect.getsource(function)
 needle='attributes[weight_id] = weight'
 if needle in source:
  fixed=source.replace(needle,"attributes['WEIGHTS_' + str(s)] = weight")
  exec(compile(fixed,'hexlive_process_local_gltf_skin_fix','exec'),attributes.__dict__)

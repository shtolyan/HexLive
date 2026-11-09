"""Move the existing two upper corner stays into ground feet, retaining all paid units.
Run on the canonical stage .blend, then export via export_construction_stage_workspace.py.
"""
import bpy,json
from pathlib import Path
from mathutils import Vector
ID = "station.drying_rack"
scene = next(s for s in bpy.data.scenes if s.get("hexlive_definition_id") == ID)
bpy.context.window.scene = scene
root = next(o for o in scene.objects if o.get("hexlive_definition_id") == ID)
for o in [root,*root.children_recursive]: o.update_tag(refresh={"OBJECT"})
bpy.context.view_layer.update()
if root.get("hexlive_rack_design") != "hangers-628":
    raise RuntimeError("Expected reviewed hanger-628 rack; do not regenerate unrelated furniture")
report=[]
for side in (-1,1):
    obj=next(o for o in root.children_recursive if o.name==f"Rack_corner_stay_{side}")
    local=root.matrix_world.inverted() @ obj.matrix_world
    caps=[local @ face.center for face in obj.data.polygons if len(face.vertices)==7]
    if len(caps)!=2: raise RuntimeError("Expected original seven-sided stick with two end caps")
    axis=caps[1]-caps[0]; centre=(caps[0]+caps[1])*.5
    rotate=axis.normalized().rotation_difference(Vector((1,0,0)))
    points=[rotate @ (local @ v.co-centre) for v in obj.data.vertices]
    # The long lower rail runs +Y. Feet run perpendicular +X at each post's
    # ground anchor Y=±0.50. Lowest vertex seats at exactly Z=0.
    newcentre=Vector((0,side*.50,-min(p.z for p in points)))
    obj.data=obj.data.copy()
    for vertex,point in zip(obj.data.vertices,points): vertex.co=local.inverted() @ (point+newcentre)
    obj["hexlive_rack_part"]="ground_foot"
    report.append(dict(name=obj.name,length=axis.length,oldCaps=[list(p) for p in caps],
        newCenter=list(newcentre),newAxis=[1,0,0],parent=obj.parent.name))
root["hexlive_rack_design"]="ground-feet-639"
bpy.context.view_layer.update()
bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)
folder=Path(__file__).resolve().parents[2]/"Build/Diagnostics/bug-wave2-actions/rack"
folder.mkdir(parents=True,exist_ok=True)
(folder/"feet-authored.json").write_text(json.dumps(report,indent=2),encoding="utf8")
print("RACK_639_FEET",report)

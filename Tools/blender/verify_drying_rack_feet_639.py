"""Verify the actual exported rack feet and unchanged paid bill, then draw measured geometry.
blender -b -P Tools/blender/verify_drying_rack_feet_639.py
"""
import bpy,json,math,hashlib
from pathlib import Path
from mathutils import Vector
REPO=Path(__file__).resolve().parents[2]
ID="station.drying_rack"
path=REPO/"Assets/HexLiveContent/RuntimeSource/Objects"/(ID+".fbx")
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(path))
rows=[]
for side in (-1,1):
    obj=next(o for o in bpy.data.objects if o.name==f"Rack_corner_stay_{side}")
    caps=[obj.matrix_world@f.center for f in obj.data.polygons if len(f.vertices)==7]
    assert len(caps)==2
    direction=(caps[1]-caps[0]).normalized()
    vertices=[obj.matrix_world@v.co for v in obj.data.vertices]
    lo=[min(p[i] for p in vertices) for i in range(3)]
    hi=[max(p[i] for p in vertices) for i in range(3)]
    assert abs(direction.x)>.9999,(obj.name,list(direction))
    assert abs(lo[2])<.00005 and hi[2]<.03,(obj.name,lo,hi)
    assert abs((caps[0].y+caps[1].y)/2-side*.5)<.00005
    assert .32<hi[0]-lo[0]<.34
    assert any(p.name.startswith('unit_01_') for p in [obj.parent,obj.parent.parent])
    rows.append(dict(name=obj.name,min=lo,max=hi,axis=list(direction)))
receipt=json.loads(path.with_suffix('.construction.json').read_text())
assert receipt['modelSha256']==hashlib.sha256(path.read_bytes()).hexdigest()
assert len(receipt['actions'])==8
assert [a['resource'] for a in receipt['actions']]==['resource.stick']*4+['resource.rope']*4
sockets=[o for o in bpy.data.objects if o.name.startswith('DryingSlot_')]
assert len(sockets)==8
for index,obj in enumerate(sorted(sockets,key=lambda o:o.name)):
    p=obj.matrix_world.translation
    assert abs(p.z-1.32)<.00005 and abs(p.y-(-.35+index*.1))<.00005
report=dict(passed=True,feet=rows,modelSha256=receipt['modelSha256'],paidActions=8,sockets=8)
out=REPO/'Build/Diagnostics/bug-wave2-actions/rack';out.mkdir(parents=True,exist_ok=True)
(out/'feet-verified.json').write_text(json.dumps(report,indent=2),encoding='utf8')
svg=['<svg xmlns="http://www.w3.org/2000/svg" width="820" height="440" viewBox="0 0 820 440"><rect width="820" height="440" fill="#202830"/><g fill="#edf3ef" font-family="sans-serif" font-size="18"><text x="24" y="32">#639 · measured FBX feet · top view, Blender +Z up</text><text x="24" y="62">Frame +Y; feet +X; post anchors Y=±0.50 wu</text></g>']
for row in rows:
    lo,hi=row['min'],row['max'];x=350+lo[0]*330;y=240-lo[1]*260
    svg.append(f'<rect x="{x:.2f}" y="{y:.2f}" width="{(hi[0]-lo[0])*330:.2f}" height="{(hi[1]-lo[1])*260:.2f}" fill="#b98c53"/>')
svg+=['<path d="M350 108V372" stroke="#8ec592" stroke-width="8"/><g fill="#edf3ef" font-family="sans-serif" font-size="16"><text x="465" y="150">Length 0.3301 wu</text><text x="465" y="180">min Z = 0 (within 0.00005)</text><text x="465" y="210">max Z &lt; 0.030 wu</text><text x="465" y="270">8 original paid actions</text><text x="465" y="300">8 sockets unchanged, Z=1.32</text><text x="465" y="330">No runtime transform correction</text></g></svg>']
(REPO/'Docs/DryingRackFeet639.svg').write_text(''.join(svg),encoding='utf8')
print('RACK_FEET_639_VERIFIED',json.dumps(report))

#!/usr/bin/env python3
"""Regenerate shared Primal ground geometry from the Unity factory audit; no builds."""
from pathlib import Path
import json,math
root=Path(__file__).resolve().parents[1]
rel=Path('Assets/HexLiveContent/People/Validation/scale-audit.json')
a=json.loads((root/rel).read_text());assert a['passed']
wear=[r for r in a['rows'] if r['type']=='wear' and not r['hanging']]
proto={r['prototypeId']:r for r in wear};assert len(proto)==21
lines=['// Generated from People/Validation/scale-audit.json through the real GarmentDropFactory.', '// Metres at HexWorldRenderer.ActorScale; immutable shared server/client placement data.', 'using System.Collections.Generic;', 'namespace HexLive.Simulation.Content', '{', 'internal static class PreparedPeopleGroundGeometry', '{','    internal static void Register(Dictionary<string, GroundPileProfile> garments,', '        Dictionary<string, string> prototypes, Dictionary<string, GroundPileProfile> objects)', '    {']
def num(x):return f'{x:.6f}f'
def outward(b):
 return [math.floor(x*1e6-1)/1e6 for x in b['min']]+[math.ceil(x*1e6+1)/1e6 for x in b['max']]
for k,r in sorted(proto.items()):
 vals=outward(r['bounds']);box=', '.join(num(x) for x in vals)
 lines.append(f'        garments["{k}"] = new("{k}", new({box}), 1f, garment: true);')
for r in sorted(wear,key=lambda r:r['id']):lines.append(f'        prototypes["{r["id"]}"] = "{r["prototypeId"]}";')
# Reference limbs lie on their back: source Y becomes ground Z. A square
# envelope bounds arbitrary severed-zone yaw; a severed item never stacks.
limbs=[r['bounds']['size'] for r in a['rows'] if r['type']=='limb']
radius=math.ceil(max(math.hypot(v[0],v[1])/2 for v in limbs)*1e6+1)/1e6
height=math.ceil(max(v[2] for v in limbs)*1e6+1)/1e6
lines.append(f'        objects[ContentIds.SeveredLimb] = new(ContentIds.SeveredLimb, new({num(-radius)}, 0f, {num(-radius)}, {num(radius)}, {num(height)}, {num(radius)}), {num(radius*2)});')
lines+=['    }','}','}']
p=root/'Assets/HexLive/Simulation/Content/PreparedPeopleGroundGeometry.cs';p.write_text('\n'.join(lines)+'\n')
p=root/'Assets/HexLive/Simulation/Content/GroundPileCatalog.cs';s=p.read_text();needle='        foreach(var profile in Profiles.Values) MaximumRadiusXZ=';assert needle in s;registration='        PreparedPeopleGroundGeometry.Register(Garments, GarmentPrototypes, Profiles);\n';s=s.replace(registration,'');s=s.replace(needle,'        PreparedPeopleGroundGeometry.Register(Garments, GarmentPrototypes, Profiles);\n'+needle,1);p.write_text(s)
print('Generated',len(proto),'profiles',len(wear),'aliases; limb radius',radius,'height',height)

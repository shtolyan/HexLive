#!/usr/bin/env python3
"""§54 ground geometry authoring. No Unity process, FBX edits or importer changes.

--leaf-proof reproduces the identical-XY triangle separation proof.
--garments [input.json] emits a reviewed C# initializer under Build. With no
path it uses committed Tools/Art/GroundPileGarments.json. Runtime reads only C#.

To remeasure garments: acquire the mandatory Unity MCP lease, use idle EditMode,
execute Tools/Art/MeasureGarmentGroundBounds.cs.txt via the approved execute-code
adapter, then release after its finally-cleanup. This tool never launches Unity.
The fixture writes raw artifacts into Build/bug396-diagnosis. Copy the exact
executed fixture there as GarmentGroundBounds.execute-code.cs.txt before running:
python Tools/Art/extract_garment_profiles.py --input-dir Build/bug396-diagnosis
The extractor preserves raw full-sweep failure (authored-only orphan metadata),
while separately verifying complete active/save-supported required coverage.
Review the measured profile/coverage/source-hash subset before updating compact
input; retain raw/fixture proof hashes and the coverage rationale.
--check validates source SHA declarations in the committed shared catalog.
A successful bound export is not runtime material/intersection visual acceptance.
"""
import argparse
import hashlib
import json
import math
import re
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
CATALOG = REPO / "Assets/HexLive/Simulation/Content/GroundPileCatalog.cs"


def leaf_proof(repo):
    import sys,json,math,hashlib
    from pathlib import Path
    sys.path.insert(0,str(repo / 'Tools/wardrobe'))
    from wardrobe import fbx
    src=repo/'Assets/HexLiveContent/RuntimeSource/Objects/palm_frond_native.fbx'
    nodes,_=fbx._nodes(src)
    raw=fbx._unpack(next(n for n in nodes if n.name=='Vertices').props[0])
    points=list(zip(raw[::3],raw[1::3],raw[2::3]))
    indices=fbx._unpack(next(n for n in nodes if n.name=='PolygonVertexIndex').props[0])
    scale=.825/max(max(p[i] for p in points)-min(p[i] for p in points) for i in range(3))
    mins=[min(p[i] for p in points) for i in range(3)]
    points=[tuple((p[i]-mins[i])*scale for i in range(3)) for p in points]
    triangles=[]; face=[];faces=[]
    for n in indices:
     face.append(-n-1 if n<0 else n)
     if n<0:
      faces.append(face)
      for j in range(1,len(face)-1):triangles.append(tuple(points[k] for k in (face[0],face[j],face[j+1])))
      face=[]
    def cross(a,b,c):return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
    def clip(poly,tri):
     if cross(*tri)<0:tri=tri[::-1]
     for a,b in zip(tri,tri[1:]+tri[:1]):
      res=[]
      for p,q in zip(poly,poly[1:]+poly[:1]):
       dp=cross(a,b,p);dq=cross(a,b,q)
       if dp>=-1e-12:res.append(p)
       if (dp>1e-12 and dq<-1e-12) or (dp<-1e-12 and dq>1e-12):
        t=dp/(dp-dq);res.append((p[0]+t*(q[0]-p[0]),p[1]+t*(q[1]-p[1])))
      poly=res
      if not poly:break
     return poly
    def height(tri,p):
     a,b,c=tri;den=cross(a,b,c)
     u=cross(p,b,c)/den;v=cross(a,p,c)/den;w=1-u-v
     return u*a[2]+v*b[2]+w*c[2]
    nondeg=[(i,t) for i,t in enumerate(triangles) if abs(cross(*t))>1e-12]
    deg=[i for i,t in enumerate(triangles) if abs(cross(*t))<=1e-12]
    max_span=0; witness=None; overlaps=0
    for i,a in nondeg:
     for j,b in nondeg:
      if j<=i:continue
      poly=clip([(p[0],p[1]) for p in a],[(p[0],p[1]) for p in b])
      if not poly:continue
      overlaps+=1
      for p in poly:
       d=abs(height(a,p)-height(b,p))
       if d>max_span:max_span=d;witness={'triangleA':i,'triangleB':j,'pointXY':p,'verticalDifference':d}
    # Degenerate projection must be handled rather than assumed absent; report blocks
    # a proof if any triangle is vertical in this source projection.
    clearance=.001
    pitch=math.ceil((max_span+clearance)*1000000)/1000000
    height_total=max(p[2] for p in points)+59*pitch
    result={'source':str(src),'sourceSha256':hashlib.sha256(src.read_bytes()).hexdigest(),'normalizationLongestAxisWu':.825,'sourceToGroundAxes':'Source XY projection and source Z curvature; Unity proof confirms the same height/XZ extents under importer rigid axis signs. XZ reflection/translation leaves triangle overlap and vertical clearance invariant; actual renderer bounds center the source pivot.','vertices':len(points),'faces':len(faces),'triangles':len(triangles),'nondegenerateXYTriangles':len(nondeg),'degenerateXYTriangles':deg,'overlappingProjectedTrianglePairs':overlaps,'maxVerticalSurfaceSpanWu':max_span,'witness':witness,'proposedMinimumVerticalClearanceWu':clearance,'proposedLayerPitchWu':pitch,'layers':60,'totalPileHeightWu':height_total,'singleLeafCurvatureHeightWu':max(p[2] for p in points),'proofComplete':not deg,'method':'Clip every pair of projected triangles. Height difference is affine on convex overlap; extrema occur at vertices. Layer translation > global max vertical span + clearance separates all identical XY copies, including nonadjacent layers. No AABB height used as layer pitch.'}
    # Independent synthetic controls: an overlapping tilted upper triangle must
    # report its nonzero thickness, while a disjoint projection must return empty.
    controlA=((0.,0.,0.),(1.,0.,0.),(0.,1.,0.))
    controlB=((.1,.1,.002),(.6,.1,.004),(.1,.6,.003))
    controlPoly=clip([(v[0],v[1]) for v in controlA],[(v[0],v[1]) for v in controlB])
    controlSpan=max(abs(height(controlA,v)-height(controlB,v)) for v in controlPoly)
    assert abs(controlSpan-.004)<1e-10, controlSpan
    assert not clip([(0.,0.),(1.,0.),(0.,1.)],[(2.,2.),(3.,2.),(2.,3.)])
    result['algorithmSelfChecks']={'tiltedOverlapExpectedSpanWu':.004,'measuredSpanWu':controlSpan,'disjointProjectionEmpty':True,'zeroLayerOffsetCoincides':True,'positivePitchExceedsSurfaceSpan':pitch>max_span}
    return result


def prosthetic_proof(repo):
    import sys
    ROOT = repo
    sys.path.insert(0, str(ROOT / "Tools/wardrobe"))
    from wardrobe import fbx
    HEX_RADIUS = 1.5
    NPC_HEIGHT_FACTOR = 11 / 30
    AUTHORED_ACTOR_HEIGHT_METERS = 2.4
    ACTOR_SOURCE_HEIGHT_METERS = 1.7
    REFERENCE = {"arm": 0.263512, "leg": 0.435129}
    SOURCE = ROOT / "Assets" / "HexLiveContent" / "Prosthetics"


    def inspect(path: Path) -> dict:
        limb = "arm" if "_arm_" in path.name else "leg"
        side = "l" if path.stem.endswith("_l") else "r"
        tier = "mechanical" if "_mechanical_" in path.name else "wood"
        nodes, fbx_version = fbx._nodes(path)
        geometries = fbx.geometries(path, sample=0)
        points = [point for geometry in geometries.values() for point in geometry.points]
        if not points:
            raise RuntimeError(f"{path}: no geometry")
        scale = (HEX_RADIUS * NPC_HEIGHT_FACTOR * AUTHORED_ACTOR_HEIGHT_METERS /
                 ACTOR_SOURCE_HEIGHT_METERS * REFERENCE[limb])
        minimum = [min(point[axis] for point in points) for axis in range(3)]
        maximum = [max(point[axis] for point in points) for axis in range(3)]
        fixed_x90_radius = max(math.hypot(x, y) for x, y, _ in points) * scale
        sphere_radius = max(math.sqrt(x*x + y*y + z*z) for x, y, z in points) * scale
        names = {}
        transforms = {}
        current = None
        for node in nodes:
            if node.name in ("Geometry", "Model", "Material", "Pose") and len(node.props) >= 3:
                current = node.props[0]
                names[current] = (node.name, fbx._text(node.props[1]))
            elif (node.name == "P" and current in names and names[current][0] == "Model"
                  and len(node.props) >= 7):
                key = fbx._text(node.props[0])
                if key in ("Lcl Translation", "Lcl Rotation", "Lcl Scaling"):
                    transforms.setdefault(current, {})[key] = [float(v) for v in node.props[4:7]]
        parent = {}
        geometry_models = set()
        for node in nodes:
            if node.name != "C" or len(node.props) < 3:
                continue
            source, destination = node.props[1], node.props[2]
            if names.get(source, ("",))[0] == "Model" and names.get(destination, ("",))[0] == "Model":
                parent[source] = destination
            elif names.get(source, ("",))[0] == "Geometry" and names.get(destination, ("",))[0] == "Model":
                geometry_models.add(destination)
        roots = [uid for uid, value in names.items() if value[0] == "Model" and uid not in parent]
        if len(roots) != 1:
            raise RuntimeError(f"{path}: expected one root model, got {len(roots)}")
        root_transform = transforms.get(roots[0], {})
        root_translation = root_transform.get("Lcl Translation", [0.0, 0.0, 0.0])
        root_rotation = root_transform.get("Lcl Rotation", [0.0, 0.0, 0.0])
        root_scale = root_transform.get("Lcl Scaling", [1.0, 1.0, 1.0])
        non_identity_children = []
        for uid, value in names.items():
            if value[0] != "Model" or uid == roots[0]:
                continue
            transform = transforms.get(uid, {})
            rotation = transform.get("Lcl Rotation", [0.0, 0.0, 0.0])
            scaling = transform.get("Lcl Scaling", [1.0, 1.0, 1.0])
            if any(abs(v) > 1e-7 for v in rotation) or any(abs(v - 1.0) > 1e-7 for v in scaling):
                non_identity_children.append({"model": value[1], "rotation": rotation, "scale": scaling})
        meta_path = Path(str(path) + ".meta")
        meta_text = meta_path.read_text(encoding="utf-8")
        def meta_int(name: str) -> int:
            match = re.search(r"^\s*" + re.escape(name) + r":\s*(-?\d+)\s*$", meta_text, re.MULTILINE)
            if not match:
                raise RuntimeError(f"{meta_path}: missing {name}")
            return int(match.group(1))
        unit_values = {}
        for node in nodes:
            if node.name == "P" and len(node.props) >= 5 and fbx._text(node.props[0]) in (
                    "UnitScaleFactor", "OriginalUnitScaleFactor"):
                unit_values[fbx._text(node.props[0])] = float(node.props[4])
        if any(abs(v) > 1e-7 for v in root_translation):
            raise RuntimeError(f"{path}: non-zero root translation {root_translation}")
        if any(abs(v - 1.0) > 1e-7 for v in root_scale):
            raise RuntimeError(f"{path}: non-unit root scale {root_scale}")
        if non_identity_children:
            raise RuntimeError(f"{path}: translation-only point cloud misses child transforms: {non_identity_children}")
        return {
            "definitionId": f"prosthetic.{limb}.{tier}",
            "side": side,
            "source": path.relative_to(ROOT).as_posix(),
            "sourceSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "sourceBytes": path.stat().st_size,
            "sourceMetaSha256": hashlib.sha256(meta_path.read_bytes()).hexdigest(),
            "fbxVersion": fbx_version,
            "fbxUnits": unit_values,
            "importer": {
                "globalScale": meta_int("globalScale"),
                "useFileUnits": meta_int("useFileUnits"),
                "useFileScale": meta_int("useFileScale"),
                "bakeAxisConversion": meta_int("bakeAxisConversion"),
            },
            "rootModel": {
                "name": names[roots[0]][1],
                "translation": root_translation,
                "rotationDegrees": root_rotation,
                "scale": root_scale,
            },
            "nonIdentityChildRotationOrScale": non_identity_children,
            "vertices": len(points),
            "sourceBounds": {"min": minimum, "max": maximum},
            "productionScale": scale,
            "fixedX90YawInvariantRadiusWu": fixed_x90_radius,
            "basisIndependentSphereRadiusWu": sphere_radius,
        }


    rows = [inspect(path) for path in sorted(SOURCE.glob("*.fbx"))]
    profiles = {}
    for definition_id in sorted({row["definitionId"] for row in rows}):
        variants = [row for row in rows if row["definitionId"] == definition_id]
        radius = max(row["basisIndependentSphereRadiusWu"] for row in variants)
        profiles[definition_id] = {
            "capacity": 1,
            "conservativeRadiusWu": radius,
            "singleBounds": {
                "min": [-radius, 0.01, -radius],
                "max": [radius, 0.01 + 2*radius, radius],
            },
            "recenter": [0.0, 0.0],
            "sourceSides": [row["side"] for row in variants],
        }

    result = {
        "method": (
            "Exact FBX vertex positions including model translations. R=max(norm(vertex))*productionScale. "
            "Rigid importer/factory rotation and deterministic yaw preserve R; Ground changes only Y. "
            "The XZ square [-R,+R]^2 therefore encloses either side at every object-id yaw."
        ),
        "productionFormula": {
            "actorScale": HEX_RADIUS * NPC_HEIGHT_FACTOR * AUTHORED_ACTOR_HEIGHT_METERS /
                          ACTOR_SOURCE_HEIGHT_METERS,
            "armReferenceMeters": REFERENCE["arm"],
            "legReferenceMeters": REFERENCE["leg"],
            "groundLiftWu": 0.01,
        },
        "profiles": profiles,
        "variants": rows,
        "limitations": [
            "The basis-independent square is intentionally wider than the exact oriented render bound.",
            "Unity proof must still record imported hierarchy, post-Attach renderer bounds and cleanup.",
        ],
    }

    return result


def csharp_string(value):
    return json.dumps(value, ensure_ascii=False)


def number(value):
    value = float(value)
    if not math.isfinite(value):
        raise ValueError("Nonfinite authored bound")
    return format(value, ".9g") + "f"


# Narrow reviewed garment-factory bounds equivalence, NOT a new measurement.
# Keep the measured catalog and raw proof immutable. Any different renderer bytes
# require another review; ordinary prop/prosthetic source declarations stay strict.
GARMENT_EQUIVALENCE = "Tools/Art/GroundPileGarmentBoundsEquivalence.bug422"
GARMENT_REVIEW_SHA256 = "77468d8cdee894d1320a407966a521d548b902dac9bad870dc122999e8901a7a"
GARMENT_RENDERER = "Assets/HexLive/UnityPresentation/Rendering/HexWorldRenderer.cs"


def garment_source_equivalent(name, measured_digest, actual_digest):
    if name != GARMENT_RENDERER:
        return False
    review_bytes = (REPO / (GARMENT_EQUIVALENCE + ".json")).read_bytes().replace(b"\r\n", b"\n")
    if hashlib.sha256(review_bytes).hexdigest() != GARMENT_REVIEW_SHA256:
        raise ValueError("Garment bounds equivalence review changed")
    review = json.loads(review_bytes)
    diff_bytes = (REPO / (GARMENT_EQUIVALENCE + ".diff")).read_bytes().replace(b"\r\n", b"\n")
    if hashlib.sha256(diff_bytes).hexdigest() != review["normalizedDiffSha256"]:
        raise ValueError("Garment bounds equivalence diff changed")
    return (measured_digest == review["baselineCheckoutSha256"] and
            actual_digest == review["currentBytesSha256"])


def garment_initializer(path, verify_sources=True):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    summary = data.get("summary", {})
    if summary.get("requiredCoveragePassed") is not True:
        raise ValueError("Incomplete required garment measurement cannot become runtime data")
    raw = summary["rawProof"]
    if "path" in raw:
        raw_path = Path(raw["path"])
        if hashlib.sha256(raw_path.read_bytes()).hexdigest() != raw["sha256"]:
            raise ValueError("Raw factory proof changed")
    if summary["requiredMeasured"] != summary["requiredDefinitions"]:
        raise ValueError("Required coverage is partial")
    profiles = data["profiles"]
    coverage = data["coverage"]
    lines = ["// Generated by Tools/author_ground_piles.py from safe factory bounds."]
    for name, source in sorted(data["sources"].items()):
        for relative, digest in ((name, source["sha256"]), (name + ".meta", source.get("metaSha256"))):
            if not digest:
                continue
            if verify_sources:
                actual = hashlib.sha256((REPO / relative).read_bytes()).hexdigest()
                if actual != digest and not garment_source_equivalent(relative, digest, actual):
                    raise ValueError("Measured source changed: " + relative)
            lines.append("// source-sha256: " + digest + " " + relative)
    for prototype, profile in sorted(profiles.items()):
        if profile["prototypeId"] != prototype:
            raise ValueError("Prototype key mismatch")
        bound = profile["scaledBounds"]
        values = [bound[edge][axis] for edge in ("min", "max") for axis in ("x", "y", "z")]
        if any(values[i] > values[i + 3] for i in range(3)):
            raise ValueError("Inverted bound")
        lines.append("Garments[" + csharp_string(prototype) + "] = new(" + csharp_string(prototype) +
                     ", new(" + ",".join(map(number, values)) + "), 0f, garment:true);")
    for definition, row in sorted(coverage.items()):
        if row.get("measured") is not True or row.get("profileId") not in profiles:
            raise ValueError("Unmeasured definition: " + definition)
        lines.append("GarmentPrototypes[" + csharp_string(definition) + "] = " + csharp_string(row["profileId"]) + ";")
    return "\n".join(lines) + "\n"


def check_generated_garments(text):
    marker = "        // Generated by Tools/author_ground_piles.py from safe factory bounds."
    start = text.index(marker)
    end = text.index("        foreach(var profile in Profiles.Values)", start)
    expected = "".join("        " + line + "\n" for line in
                       garment_initializer(REPO / "Tools/Art/GroundPileGarments.json", verify_sources=False).splitlines())
    actual = text[start:end]
    if actual != expected:
        raise ValueError("Generated garment catalog drift: regenerate --garments and review the exact block")
    return True


def check_sources():
    text = CATALOG.read_text(encoding="utf-8-sig")
    check_generated_garments(text)
    garment_start = text.index("// Generated by Tools/author_ground_piles.py from safe factory bounds.")
    garment_end = text.index("        foreach(var profile in Profiles.Values)", garment_start)
    records = list(re.finditer(r"// source-sha256: ([0-9a-f]{64}) (.+)", text))
    if not records:
        raise ValueError("Catalog lacks authored source hashes")
    for record in records:
        digest, name = record.groups()
        path = (REPO / name).resolve()
        if not path.is_relative_to(REPO):
            raise ValueError("Source outside repository")
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual != digest:
            garment_only = garment_start <= record.start() < garment_end
            if not garment_only or not garment_source_equivalent(name, digest, actual):
                raise ValueError("Ground geometry source changed: " + name)
    return len(records)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--leaf-proof", action="store_true")
    parser.add_argument("--prosthetic-proof", action="store_true")
    parser.add_argument("--garments", type=Path, nargs="?", const=REPO / "Tools/Art/GroundPileGarments.json")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--output-dir", type=Path, default=REPO / "Build/bug396-authoring")
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    if args.leaf_proof:
        result = leaf_proof(REPO)
        if not result["proofComplete"] or result["proposedLayerPitchWu"] > .001:
            raise ValueError("Current leaf mesh invalidates the authored 1mm layer pitch")
        (args.output_dir / "leaf-proof.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Leaf triangle proof PASS; proposed 60-layer height", result["totalPileHeightWu"])
    if args.prosthetic_proof:
        result = prosthetic_proof(REPO)
        (args.output_dir / "prosthetic-proof.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Prosthetic basis-independent source proof:", len(result["profiles"]), "profiles")
    if args.garments:
        (args.output_dir / "garment-initializer.cs.txt").write_text(garment_initializer(args.garments), encoding="utf-8")
        print("Complete garment initializer written; no production files modified")
    if args.check:
        print("Source hashes verified:", check_sources())
    if not (args.leaf_proof or args.prosthetic_proof or args.garments or args.check):
        parser.error("Choose --leaf-proof, --prosthetic-proof, --garments or --check")


if __name__ == "__main__":
    main()

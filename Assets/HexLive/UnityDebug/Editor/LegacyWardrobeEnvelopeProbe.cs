#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Diagnostic constraints. The 4 primary locations are edge midpoints and
    // centroid. Nine quarter-grid locations constrain/verify the interiors too.
    static readonly Vector3[] EnvelopeProbeBarycentrics={new(.5f,.5f,0),new(0,.5f,.5f),new(.5f,0,.5f),new(1f/3,1f/3,1f/3),
        new(.5f,.25f,.25f),new(.25f,.5f,.25f),new(.25f,.25f,.5f),
        new(.75f,.25f,0),new(.25f,.75f,0),new(0,.75f,.25f),new(0,.25f,.75f),new(.75f,0,.25f),new(.25f,0,.75f)};
    static readonly Vector3[] EnvelopeProbeAxes={Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};

    static JObject EnvelopeProbeResidual(Vector3[] points,int[] triangles,WardrobeBodyVolume body,float target)
    {
        int samples=0,belowTarget=0,belowMinimum=0,penetrating1=0,penetrating3=0,inside=0,ambiguous=0,onSurface=0,nonUnanimous=0,minValidRays=3;
        float maxViolation=0,maxDepth=0,minimumSigned=float.PositiveInfinity;
        Vector3 worst=default,worstBody=default,firstAmbiguous=default;int worstTriangle=-2;
        void Sample(Vector3 point,int triangle)
        {
            var c=body.Query(point);samples++;
            if(c.NonUnanimous)nonUnanimous++;minValidRays=Math.Min(minValidRays,c.ValidRays);
            if(c.Ambiguous){if(ambiguous==0)firstAmbiguous=point;ambiguous++;}
            if(c.OnSurface)onSurface++;
            if(c.Inside)inside++;
            // Physical floor is exact: the separate 0.5 mm diagnostic deficit
            // counter below never grants a 1.5 mm point acceptance at 2 mm.
            float signed=c.SignedDistance;
            minimumSigned=Mathf.Min(minimumSigned,signed);
            if(signed<target)belowMinimum++;
            if(c.Ambiguous&&!c.OnSurface)return; // An unknown sign is never accepted as outside.
            if(signed<target-.0005f)belowTarget++;
            if(signed<-.001f)penetrating1++;if(signed<-.003f)penetrating3++;
            maxDepth=Mathf.Max(maxDepth,-signed);
            if(target-signed<=maxViolation)return;
            maxViolation=target-signed;worst=point;worstBody=c.Point;worstTriangle=triangle;
        }
        foreach(var p in points)Sample(p,-1);
        for(int at=0;at<triangles.Length;at+=3)
        foreach(var w in EnvelopeProbeBarycentrics)
            Sample(points[triangles[at]]*w.x+points[triangles[at+1]]*w.y+points[triangles[at+2]]*w.z,at/3);
        return new JObject{["allVerticesAnd13BarycentricSamplesPerTriangle"]=true,["sampleCount"]=samples,["targetClearanceMetres"]=target,
            ["minimumSignedDistanceMetres"]=minimumSigned,["samplesBelowMinimum"]=belowMinimum,["physicalMinimumIsStrict"]=true,
            ["distanceMethod"]="unsigned exact-nearest distance with three deduplicated BVH ray parity inside votes",
            ["insidePolicy"]="At least two agreeing valid rays; non-unanimous majority is diagnostic, not failure.",
            ["nonUnanimousSamples"]=nonUnanimous,["minimumValidRays"]=minValidRays,
            ["insideSamples"]=inside,["ambiguousSamples"]=ambiguous,["onSurfaceSamples"]=onSurface,["firstAmbiguousPoint"]=ambiguous==0?null:Vec(firstAmbiguous),
            ["toleranceMetres"]=.0005f,["toleranceIsDiagnosticOnly"]=true,["samplesBelowTargetMinusTolerance"]=belowTarget,["samplesBelowMinus1mm"]=penetrating1,
            ["samplesBelowMinus3mm"]=penetrating3,["maximumClearanceDeficitMetres"]=maxViolation,["maximumPenetrationDepthMetres"]=maxDepth,
            ["worstTriangleIndex"]=worstTriangle,["worstPoint"]=Vec(worst),["worstBodyPoint"]=Vec(worstBody),
            ["passedSampledEnvelope"]=minimumSigned>=target&&belowMinimum==0&&inside==0&&ambiguous==0&&onSurface==0};
    }

    // Pure diagnostic geometry operation: clones world vertices and never
    // changes topology, source settings, transforms, materials or persistent art.
    // Internal because TriangleTree is an editor-internal type; authoring partials
    // in this assembly can reuse it without exposing a runtime/Public API.
    internal static Vector3[] RefineEnvelope(Vector3[] world,int[] triangles,TriangleTree body,float clearance,
        int maxPasses,float maxDisplacement,out JObject proof)
        =>RefineEnvelope(world,triangles,WardrobeBodyVolume.For(body),clearance,.002f,maxPasses,maxDisplacement,out proof);

    internal static Vector3[] RefineEnvelope(Vector3[] world,int[] triangles,WardrobeBodyVolume body,float clearance,
        int maxPasses,float maxDisplacement,out JObject proof)
        =>RefineEnvelope(world,triangles,body,clearance,.002f,maxPasses,maxDisplacement,out proof);

    internal static Vector3[] RefineEnvelope(Vector3[] world,int[] triangles,WardrobeBodyVolume body,float requestedClearance,
        float minimumAcceptedClearance,int maxPasses,float maxDisplacement,out JObject proof,int[] movementGroups=null)
    {
        if(world==null||world.Length==0||triangles==null||triangles.Length%3!=0||body==null||
           !(minimumAcceptedClearance>=.002f)||!(requestedClearance>=minimumAcceptedClearance)||maxPasses<1||
           !(maxDisplacement>requestedClearance))throw new ArgumentException("Invalid envelope probe input");
        if(triangles.Any(i=>i<0||i>=world.Length))throw new ArgumentException("Invalid triangle index");
        int passLimit=Math.Min(16,maxPasses);
        float clearance=requestedClearance;
        Dictionary<int,List<int>> members=null;
        if(movementGroups!=null)
        {
            if(movementGroups.Length!=world.Length||movementGroups.Any(i=>i<0))throw new ArgumentException("Invalid movement groups");
            members=new Dictionary<int,List<int>>();
            for(int i=0;i<world.Length;i++){int key=movementGroups[i];if(!members.TryGetValue(key,out var list))members.Add(key,list=new List<int>());list.Add(i);}
        }
        var positions=(Vector3[])world.Clone();var passes=new JArray();int clampOperations=0,ambiguousConstraintsSkipped=0,boundaryDirectionsResolved=0,boundaryDirectionsFailed=0;
        bool Correction(Vector3 point,out Vector3 direction,out float deficit)
        {
            var contact=body.Query(point);direction=contact.OutwardDirection;deficit=clearance-contact.SignedDistance;
            // Repair the actual inequality. Already admissible samples must not
            // chase the 6 mm safety bias while neighbouring constraints fail.
            if(!contact.Ambiguous&&!contact.OnSurface&&contact.SignedDistance>=minimumAcceptedClearance)return false;
            if(contact.OnSurface)
            {
                // Boundary sign/direction is explicitly unknown. Probe a bounded
                // neighbourhood and use ONLY a verified outside distance gradient;
                // never use the interpolated body normal as an inside/outside sign.
                float best=-1;direction=Vector3.zero;
                foreach(var axis in EnvelopeProbeAxes)
                {
                    var outside=body.Query(point+axis*.0001f);
                    if(outside.Ambiguous||outside.Inside||outside.OnSurface||outside.Distance<=best||outside.OutwardDirection.sqrMagnitude<.5f)continue;
                    best=outside.Distance;direction=outside.OutwardDirection;
                }
                if(direction.sqrMagnitude<.5f){boundaryDirectionsFailed++;return false;}
                boundaryDirectionsResolved++;deficit=clearance;return true;
            }
            if(contact.Ambiguous){ambiguousConstraintsSkipped++;return false;}
            return deficit>.0001f&&direction.sqrMagnitude>.5f;
        }
        float Apply(int index,Vector3 offset)
        {
            if(members!=null)
            {
                // One shared delta per positional/skin-weight group, with the
                // tightest member's displacement ball bounding every member.
                var group=members[movementGroups[index]];float fraction=1;bool clamped=false;
                foreach(int member in group)
                {
                    var relative=positions[member]-world[member];
                    if((relative+offset).sqrMagnitude<=maxDisplacement*maxDisplacement)continue;
                    double a=Vector3.Dot(offset,offset),b=Vector3.Dot(relative,offset),c=Vector3.Dot(relative,relative)-(double)maxDisplacement*maxDisplacement;
                    double discriminant=b*b-a*c;
                    float allowed=a>0&&discriminant>=0?(float)((-b+Math.Sqrt(discriminant))/a):0;
                    fraction=Mathf.Min(fraction,Mathf.Clamp01(allowed));clamped=true;
                }
                if(clamped)clampOperations++;
                var delta=offset*fraction;float maximum=0;
                foreach(int member in group){var beforePosition=positions[member];positions[member]+=delta;maximum=Mathf.Max(maximum,Vector3.Distance(beforePosition,positions[member]));}
                return maximum;
            }
            var wanted=positions[index]+offset;var total=wanted-world[index];
            if(total.sqrMagnitude>maxDisplacement*maxDisplacement){wanted=world[index]+total.normalized*maxDisplacement;clampOperations++;}
            float moved=Vector3.Distance(positions[index],wanted);positions[index]=wanted;return moved;
        }
        var before=EnvelopeProbeResidualDispatchedDraft(positions,triangles,body,minimumAcceptedClearance);
        var after=before;string stoppedReason="pass-cap";
        if((bool)before["passedSampledEnvelope"])stoppedReason="saved-input-already-passes-minimum";
        for(int pass=0;pass<passLimit&&!(bool)after["passedSampledEnvelope"];pass++)
        {
            int vertexPushes=0,trianglePushes=0;float maximumRequested=0,maximumApplied=0;
            for(int i=0;i<positions.Length;i++)
            {
                if(!Correction(positions[i],out var direction,out float deficit))continue;
                vertexPushes++;maximumRequested=Mathf.Max(maximumRequested,deficit);
                maximumApplied=Mathf.Max(maximumApplied,Apply(i,direction*deficit));
            }
            for(int at=0;at<triangles.Length;at+=3)
            foreach(var weights in EnvelopeProbeBarycentrics)
            {
                int a=triangles[at],b=triangles[at+1],c=triangles[at+2];
                var sample=positions[a]*weights.x+positions[b]*weights.y+positions[c]*weights.z;
                if(!Correction(sample,out var direction,out float deficit))continue;
                trianglePushes++;maximumRequested=Mathf.Max(maximumRequested,deficit);
                // Minimum squared vertex displacement for this linearized
                // sample constraint: sum(w_i*delta_i)=normal*deficit.
                var combined=weights;
                if(movementGroups!=null)
                {
                    if(movementGroups[a]==movementGroups[b]){combined.x+=combined.y;combined.y=0;}
                    if(movementGroups[a]==movementGroups[c]){combined.x+=combined.z;combined.z=0;}
                    else if(movementGroups[b]==movementGroups[c]){combined.y+=combined.z;combined.z=0;}
                }
                var correction=direction*(deficit/combined.sqrMagnitude);
                if(combined.x>0)maximumApplied=Mathf.Max(maximumApplied,Apply(a,correction*combined.x));
                if(combined.y>0)maximumApplied=Mathf.Max(maximumApplied,Apply(b,correction*combined.y));
                if(combined.z>0)maximumApplied=Mathf.Max(maximumApplied,Apply(c,correction*combined.z));
            }
            // Independent verification AFTER the complete Gauss-Seidel sweep:
            // later constraints can invalidate earlier ones, so moved counts or
            // requested step sizes cannot establish physical acceptance.
            after=EnvelopeProbeResidualDispatchedDraft(positions,triangles,body,minimumAcceptedClearance);
            passes.Add(new JObject{["pass"]=pass,["vertexConstraintsMoved"]=vertexPushes,["triangleConstraintsMoved"]=trianglePushes,
                ["maxRequestedCorrectionMetres"]=maximumRequested,["maxAppliedVertexStepMetres"]=maximumApplied,
                ["independentMinimumClearanceCheck"]=after.DeepClone()});
            if((bool)after["passedSampledEnvelope"]){stoppedReason="minimum-clearance-passed";break;}
            if(maximumApplied<=.00000001f){stoppedReason="no-progress-with-failed-minimum-proof";break;}
        }
        var displacement=RetargetProbeError(world,positions);
        bool passed=(bool)after["passedSampledEnvelope"]&&(float)displacement["maxMetres"]<=maxDisplacement+.000001f;
        proof=new JObject{["passed"]=passed,["maxDisplacementMetres"]=(float)displacement["maxMetres"],["clamped"]=clampOperations>0,
            ["movementGroups"]=members==null?world.Length:members.Count,["attributeVertices"]=world.Length,
            ["movementGroupPolicy"]=members==null?"unique positions":"exact bind-position and canonical skin influences; attributes remain separate",
            ["algorithm"]="parity-signed nearest-body distance + active inequality barycentric constraints",
            ["constraintActivation"]="signed distance below minimum accepted clearance; correction targets requested safety bias",
            ["ambiguousConstraintsSkipped"]=ambiguousConstraintsSkipped,["onSurfaceDirectionsResolvedByVerifiedOutsideProbe"]=boundaryDirectionsResolved,
            ["onSurfaceDirectionsUnresolved"]=boundaryDirectionsFailed,
            ["constraintSamplesPerTriangle"]=EnvelopeProbeBarycentrics.Length,["requestedClearanceMetres"]=requestedClearance,
            ["minimumAcceptedClearanceMetres"]=minimumAcceptedClearance,["targetClearanceMetres"]=minimumAcceptedClearance,
            ["maxPasses"]=passLimit,["stoppedReason"]=stoppedReason,
            ["maximumAllowedVertexDisplacementMetres"]=maxDisplacement,["clampOperations"]=clampOperations,["passes"]=passes,
            ["before"]=before,["after"]=after,["displacement"]=displacement,
            ["topologyUnchanged"]=true,["limitation"]="Body inside uses three deduplicated BVH ray votes; ambiguous queries fail final proof. Finite garment sample grid is not an exhaustive triangle-triangle proof."};
        return positions;
    }

    public static void AuditTriangleEnvelopeSmoke()=>RunTriangleEnvelopeProbe("triangle-envelope-parity-physical2mm-smoke",new[]{"clothing.sweater_naughty"},new[]{ActorName.Kshishtof});
    public static void AuditEnvelopePhysicalFloorControls()
    {
        var mesh=new Mesh();var report=new JObject{["passed"]=false,["assetsChanged"]=false,["requestedClearanceMetres"]=.006f,["minimumAcceptedClearanceMetres"]=.002f};
        Directory.CreateDirectory(OutputRoot);string file=OutputRoot+"/envelope-physical2mm-controls.json";
        try
        {
            mesh.vertices=new[]{new Vector3(-.5f,-.5f,-.5f),new Vector3(.5f,-.5f,-.5f),new Vector3(.5f,.5f,-.5f),new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.5f,-.5f,.5f),new Vector3(.5f,-.5f,.5f),new Vector3(.5f,.5f,.5f),new Vector3(-.5f,.5f,.5f)};
            mesh.triangles=new[]{0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5};mesh.RecalculateNormals();
            var volume=WardrobeBodyVolume.For(TriangleTree.ForMesh(mesh,Matrix4x4.identity));var indices=new[]{0,1,2};
            Vector3[] Triangle(float gap)=>new[]{new Vector3(.5f+gap,-.1f,-.1f),new Vector3(.5f+gap,.1f,-.1f),new Vector3(.5f+gap,0,.1f)};
            var low=EnvelopeProbeResidual(Triangle(.0019f),indices,volume,.002f);
            var high=EnvelopeProbeResidual(Triangle(.0021f),indices,volume,.002f);
            report["at1p9mm"]=low;report["at2p1mm"]=high;
            if((int)low["samplesBelowTargetMinusTolerance"]!=0||(int)low["samplesBelowMinimum"]==0||(bool)low["passedSampledEnvelope"])
                throw new InvalidOperationException("1.9mm must FAIL the 2mm floor even when the 0.5mm diagnostic tolerance counter is zero");
            if(!(bool)high["passedSampledEnvelope"]||(float)high["minimumSignedDistanceMetres"]<.002f)
                throw new InvalidOperationException("The 2.1mm independently measured clearance must PASS");
            RefineEnvelope(Triangle(.0019f),indices,volume,.006f,.002f,16,.1f,out var refined);
            RefineEnvelope(Triangle(.0021f),indices,volume,.006f,.002f,16,.1f,out var unchanged);
            report["refine1p9mm"]=refined;report["preserve2p1mm"]=unchanged;
            var mixed=new[]{new Vector3(.5019f,-.1f,0),new Vector3(.5021f,.1f,0)};
            var active=RefineEnvelope(mixed,Array.Empty<int>(),volume,.006f,.002f,16,.1f,out var activeProof);
            if(!(bool)activeProof["passed"]||active[1]!=mixed[1]||active[0].x<=mixed[0].x)
                throw new InvalidOperationException("Active inequality repair must preserve an admissible point while repairing another point");
            report["mixedAdmissiblePointPreserved"]=activeProof;
            if(!(bool)refined["passed"]||((JArray)refined["passes"]).Count!=1||(float)refined["after"]["minimumSignedDistanceMetres"]<.002f)
                throw new InvalidOperationException("Below-floor input must meet the actual physical floor after one independent verified pass");
            if(!(bool)unchanged["passed"]||((JArray)unchanged["passes"]).Count!=0||(float)unchanged["maxDisplacementMetres"]!=0)
                throw new InvalidOperationException("Already accepted source geometry must be preserved without chasing the requested 6mm bias");
            report["passed"]=true;
        }
        catch(Exception e){report["error"]=e.ToString();throw;}
        finally{Object.DestroyImmediate(mesh);File.WriteAllText(file,report.ToString());}
        Debug.Log("ENVELOPE_PHYSICAL_FLOOR_CONTROLS_PASS "+file);
    }
    public static void AuditPhysicalFloorControlsAndFemaleBoots6mm(){AuditEnvelopePhysicalFloorControls();AuditTriangleEnvelopeFemaleBoots6mm();}
    public static void AuditTriangleEnvelopeFemaleBoots6mm()=>RunTriangleEnvelopeProbe("triangle-envelope-parity-physical2mm-female-boots6mm",new[]{"clothing.boots_anarchy"},new[]{ActorName.Marta},new[]{.006f});
    public static void AuditTriangleEnvelopeBoots()=>RunTriangleEnvelopeProbe("triangle-envelope-parity-physical2mm-boots",new[]{"clothing.boots_anarchy"},new[]{ActorName.Marta,ActorName.Kshishtof});
    public static void AuditTriangleEnvelopeAll()=>RunTriangleEnvelopeProbe("triangle-envelope-parity-physical2mm-all",
        new[]{"clothing.sweater_naughty","clothing.sweater_flair","clothing.boots_anarchy","clothing.croptop_fit","clothing.gloves_strap_anarchy"},
        new[]{ActorName.Marta,ActorName.Kshishtof});

    static void RunTriangleEnvelopeProbe(string label,string[] ids,ActorName[] actors,float[] clearances=null)
    {
        string catalogPath=PreparedRoot+"/catalog.json",catalog=File.ReadAllText(catalogPath);
        var records=((JArray)JObject.Parse(catalog)["records"]).Where(r=>(string)r["type"]=="wear").ToArray();
        var selected=ids.Select(id=>records.Single(r=>(string)r["id"]==id)).ToArray();
        var hashes=selected.Select(r=>(string)r["main"]).Distinct().ToDictionary(p=>p,AssetDatabase.GetAssetDependencyHash);
        var rows=new JArray();var report=new JObject{["schemaVersion"]=1,["label"]=label,["rows"]=rows,
            ["assetsChanged"]=false,["cacheChanged"]=false,["bodyHiddenToMaskClipping"]=false,["bundlesBuilt"]=false,
            ["bodyDistanceMethod"]="WardrobeBodyVolume parity-signed actual nearest distance",
            ["minimumAcceptedClearanceMetres"]=.002f,["solveClearancesMetres"]=new JArray(clearances??new[]{.006f,.008f}),
            ["normalContactStatsAreLegacyDiagnosticOnly"]=true};
        Directory.CreateDirectory(OutputRoot);string output=OutputRoot+"/"+label+".json";
        try
        {
            foreach(var record in selected)
            {
                string id=(string)record["id"];var prefab=AssetDatabase.LoadAssetAtPath<GameObject>((string)record["main"]);
                var authored=prefab.GetComponent<Wear>();var definition=AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)record["definition"]);
                var female=SourceMesh(authored,ActorName.Marta,out _);var male=SourceMesh(authored,ActorName.Kshishtof,out _);
                foreach(var actor in actors)
                {
                    using var baseline=ConstructFit(prefab,female,male,actor);var source=actor==ActorName.Marta?female:male;
                    var volume=WardrobeBodyVolume.For(baseline.BodySurface);
                    var saved=new JObject{["id"]=id,["actor"]=actor.ToString(),["mode"]="saved",["residual"]=EnvelopeProbeResidual(baseline.WorldVertices,source.triangles,volume,.002f)};
                    rows.Add(saved);RetargetProbeRender(baseline,authored,actor,definition.variantMaterials,label+"_"+id+"_"+actor+"_saved",saved);
                    foreach(float clearance in clearances??new[]{.006f,.008f})
                    {
                        var requested=RefineEnvelope(baseline.WorldVertices,source.triangles,volume,clearance,.002f,16,.1f,out var proof);
                        var candidate=BindMesh(source,baseline,requested,baseline.WorldNormals,"diagnostic_triangle_envelope");
                        try
                        {
                            using var rebuilt=ConstructFit(prefab,actor==ActorName.Marta?candidate:female,actor==ActorName.Kshishtof?candidate:male,actor);
                            string mode="envelope"+(clearance*1000).ToString("0",System.Globalization.CultureInfo.InvariantCulture)+"mm";
                            var row=RetargetProbeRecord(id,mode,actor,rebuilt,requested,baseline.WorldVertices);row["envelope"]=proof;
                            row["actualReconstructedResidual"]=EnvelopeProbeResidual(rebuilt.WorldVertices,source.triangles,WardrobeBodyVolume.For(rebuilt.BodySurface),.002f);
                            rows.Add(row);RetargetProbeRender(rebuilt,authored,actor,definition.variantMaterials,label+"_"+id+"_"+actor+"_"+mode,row);
                        }
                        finally{Object.DestroyImmediate(candidate);}
                        File.WriteAllText(output,report.ToString());
                    }
                }
            }
            if(File.ReadAllText(catalogPath)!=catalog||hashes.Any(p=>AssetDatabase.GetAssetDependencyHash(p.Key)!=p.Value))throw new InvalidOperationException("Envelope diagnostic changed persistent inputs");
            report["completed"]=true;report["persistentInputsUnchanged"]=true;
        }
        catch(Exception e){report["completed"]=false;report["error"]=e.ToString();throw;}
        finally{File.WriteAllText(output,report.ToString());}
        Debug.Log("TRIANGLE_ENVELOPE_PROBE_COMPLETE "+label+" rows="+rows.Count);
    }
}
}
#endif

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string FinalFitVersion = "world-units-triangle-envelope-v1";
    static readonly string[] FinalFitProcessorFiles = {
        "LegacyWebGLWardrobeAssets.cs", "LegacyWebGLWardrobeGeometry.cs", "LegacyWardrobeTriangleTree.cs",
        "LegacyWebGLWardrobePreparation.cs", "LegacyWardrobeQuality.cs", "LegacyWebGLWardrobeFinalFitStorage.cs",
        "LegacyWardrobeFinalFitMath.cs", "LegacyWardrobeHeadwearUnitReference.cs", "LegacyWardrobeEnvelopeProbe.cs",
        "LegacyWardrobeRetargetProbe.cs", "LegacyWardrobeBodyVolume.cs", "LegacyWardrobeAdaptiveEnvelope.cs", "LegacyWardrobeSourceClearanceSeed.cs",
        "LegacyWardrobeFinalFitRuntimeConstruction.cs", "LegacyWardrobeUniformFootSeed.cs", "LegacyWardrobeParallelResidual.cs", "LegacyWardrobeSandalsRigidPanels.cs", "LegacyWardrobeFinalRevalidation.cs", "LegacyWardrobeCollapsedZeroIslands.cs", "PaintPointMapGenerator.cs",
        "LegacyWardrobeSourceCapGeometry.cs", "LegacyWardrobeSourceCapAttachments.cs", "LegacyWardrobeAuthoredDonor.cs", "LegacyWardrobeAuthoredDonorEvidence.cs", "LegacyWardrobeFcoDonorEvidence.cs",
        "LegacyWardrobeReikoOpenCuffs.cs", "LegacyWardrobeGlovesOpenings.cs"
    };

    static string[] FinalFitSourcePaths()=>FinalFitProcessorFiles.Select(name=>"Assets/HexLive/UnityDebug/Editor/"+name)
        .Concat(new[]{"Assets/HexLive/UnityPresentation/Wearing/Wear.cs","Assets/HexLive/UnityPresentation/Wearing/BodyBones.cs", "Assets/HexLive/UnityPresentation/Wearing/ParentConnection.cs", "Assets/HexLive/UnityPresentation/Wearing/ActorBodyResolver.cs"})
        .Concat(SourceCapInputPaths()).Concat(AuthoredDonorInputPaths()).ToArray();

    public static void PrepareFinalFitCollection()
    {
        BuildFinalFitCollection(FinalFitVersion, FinalFitSourcePaths(), MakeFinalFitProduct);
    }

    public static void AuditFinalFitProductControls()
    {
        string stamp=DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff"),directory=OutputRoot+"/final-fit-controls-"+stamp;
        Directory.CreateDirectory(directory);
        var ids=(Environment.GetEnvironmentVariable("HEXLIVE_FINAL_FIT_PROBE_IDS")??
            "clothing.sweater_naughty,clothing.boots_anarchy,clothing.gloves_strap_anarchy,clothing.helmet_carbon,clothing.helmet_space,clothing.helmet_retro,clothing.skirt_anarchy,clothing.ankleboots_amy").Split(',');
        var cache=JObject.Parse(File.ReadAllText(PreparedRoot+"/preparation.json"));
        var catalog=JObject.Parse(File.ReadAllText(PreparedRoot+"/catalog.json"));
        var protection=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string path in new[]{PreparedRoot+"/preparation.json",PreparedRoot+"/catalog.json",PeopleRoot+"/catalog.json"}.Concat(
            FinalFitSourcePaths()))protection[path]=FinalFileSHA(path);
        var inputs=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string id in ids)
        foreach(string field in new[]{"prepared","source"})
        {string path=(string)cache[id][field];inputs[path]=AssetDatabase.GetAssetDependencyHash(path).ToString();}
        var rows=new JArray();var report=new JObject{["passed"]=false,["directory"]=directory,["rows"]=rows,
            ["assetsModified"]=false,["baseModified"]=false,["catalogModified"]=false,["bundlesBuilt"]=false,["productionChanged"]=false};
        try
        {
            foreach(string id in ids)
            {
                Debug.Log("LEGACY_FINAL_FIT_CONTROL_START "+id);
                using var input=new FinalFitInput(id,(JObject)cache[id],FinalJsonSHA(new JObject{["control"]=stamp,["id"]=id,["version"]=FinalFitVersion}));
                FinalFitProduct product=null;
                var row=new JObject{["id"]=id,["passed"]=false,["views"]=new JArray()};rows.Add(row);
                try
                {
                    product=MakeFinalFitProduct(input);
                    AssignFinalMeshes(product.Template,product.MartaMesh,product.KshishtofMesh);
                    row["evidence"]=product.Evidence.DeepClone();
                    var definitionPath=(string)catalog["records"].Where(r=>(string)r["type"]=="wear"&&(string)r["main"]==input.BasePrefabPath)
                        .OrderBy(r=>(string)r["id"],StringComparer.Ordinal).First()["definition"];
                    var definition=AssetDatabase.LoadAssetAtPath<HexLive.UnityPresentation.Wearing.Garments.GarmentDefinition>(definitionPath);
                    foreach(var actor in new[]{ActorName.Marta,ActorName.Kshishtof})
                    {
                        var body=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Actors/"+actor+".prefab"));
                        try
                        {
                            body.GetComponent<Animator>().enabled=false;
                            var bones=body.GetComponent<BodyBones>();bones.Construct(actor,649);
                            var wear=product.Template.GetComponent<Wear>();
                            if(!(wear.Layer==VisualWearLayer.Underwear&&wear.Slots.Contains(VisualWearSlot.Pelvis)))
                                bones.Equip("final-control-baseline",AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Wear/"+actor+"_PrimalBriefs.prefab").GetComponent<Wear>(),null);
                            bones.Equip(id,wear,definition.variantMaterials);
                            typeof(BodyBones).GetMethod("LateUpdate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(bones,null);
                            foreach(bool back in new[]{false,true})
                            {
                                string key="final-control-"+stamp+"-"+Key(id)+"-"+actor+"-"+(back?"back":"front");
                                RenderWardrobe(body,key,back);
                                string image=directory+"/"+key+".png";File.Copy(OutputRoot+"/review/"+key+".png",image,false);
                                ((JArray)row["views"]).Add(new JObject{["actor"]=actor.ToString(),["back"]=back,["png"]=image,["sha"]=FinalFileSHA(image)});
                            }
                        }
                        finally{Object.DestroyImmediate(body);}
                    }
                    row["passed"]=true;
                    File.WriteAllText(directory+"/final-fit-controls.json",report.ToString());
                }
                catch(Exception error){row["exception"]=error.ToString();}
                finally{if(product!=null)DestroyFinalPrivate(product.Template,product.MartaMesh,product.KshishtofMesh);}
            }
            report["passed"]=rows.Count==ids.Length&&rows.All(r=>(bool?)r["passed"]==true);
        }
        catch(Exception error){report["exception"]=error.ToString();throw;}
        finally
        {
            foreach(var pair in protection)if(FinalFileSHA(pair.Key)!=pair.Value){report["passed"]=false;report["changedProtectedFile"]=pair.Key;}
            foreach(var pair in inputs)if(AssetDatabase.GetAssetDependencyHash(pair.Key).ToString()!=pair.Value){report["passed"]=false;report["changedInputDependency"]=pair.Key;}
            File.WriteAllText(directory+"/final-fit-controls.json",report.ToString());
        }
        if(!(bool)report["passed"])throw new InvalidOperationException("Final controls failed; inspect "+directory+"/final-fit-controls.json");
        Debug.Log("LEGACY_FINAL_FIT_CONTROLS_PASS "+directory);
    }

    static Vector3[] FitVisibleUnderlayers(Vector3[] source, Fit target, out JObject evidence)
    {
        var points = (Vector3[])source.Clone(); int moved = 0;
        foreach (var layer in target.Underlayers)
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i]; var box = layer.Bounds;
            if (p.x < box.min.x-.008f || p.x > box.max.x+.008f || p.y < box.min.y-.008f || p.y > box.max.y+.008f) continue;
            var radial = new Vector2(p.x-target.RadialCentre.x,p.z-target.RadialCentre.z);
            float radius=radial.magnitude; if (radius<.00001f) continue;
            float envelope=layer.RadialEnvelope(p,target.RadialCentre);
            if (envelope<=0 || radius>=envelope+.006f) continue;
            radial*=(envelope+.006f)/radius; p.x=target.RadialCentre.x+radial.x;p.z=target.RadialCentre.z+radial.y;
            points[i]=p;moved++;
        }
        evidence = new JObject { ["visibleUnderlayers"]=target.Underlayers.Length,["verticesMoved"]=moved,
            ["clearanceMetres"]=.006f,["displacement"]=RetargetProbeError(source,points) };
        return points;
    }

    static int RequireRigidHeadInfluences(Mesh mesh,SkinnedMeshRenderer skin)
    {
        int head=Array.FindIndex(skin.bones,b=>b!=null&&b.name=="head");
        if(head<0||mesh.boneWeights.Length!=mesh.vertexCount)throw new InvalidOperationException("Rigid headwear must have a complete named head binding");
        foreach(var w in mesh.boneWeights)
        {
            float total=0;
            foreach(var pair in new[]{(w.boneIndex0,w.weight0),(w.boneIndex1,w.weight1),(w.boneIndex2,w.weight2),(w.boneIndex3,w.weight3)})
            {if(pair.Item2<0||!float.IsFinite(pair.Item2))throw new InvalidOperationException("Invalid rigid skin influence");if(pair.Item2>0&&pair.Item1!=head)throw new InvalidOperationException("Headwear is not rigidly attached to head");total+=pair.Item2;}
            if(Mathf.Abs(total-1f)>.00001f)throw new InvalidOperationException("Rigid headwear weights are not normalized");
        }
        return head;
    }

    static JObject RigidBodyContactDiagnostic(Fit fit)
    {
        var volume=WardrobeBodyVolume.For(fit.BodySurface);int count=Math.Min(4096,fit.WorldVertices.Length),inside=0,ambiguous=0;
        float minimum=float.PositiveInfinity;
        for(int i=0;i<count;i++)
        {
            var query=volume.Query(fit.WorldVertices[(int)((long)i*fit.WorldVertices.Length/count)]);
            if(query.Inside)inside++;if(query.Ambiguous)ambiguous++;minimum=Mathf.Min(minimum,query.SignedDistance);
        }
        return new JObject{["acceptance"]=false,["scope"]="preserved authored rigid geometry; body intersections are diagnostic only",
            ["sampleCount"]=count,["insideSamples"]=inside,["ambiguousSamples"]=ambiguous,["minimumSignedDistanceMetres"]=minimum,
            ["reason"]="Rigid authoring can contain a hidden neck/chest attachment. Fitting preserves the approved shape instead of applying a cloth deformation."};
    }

    static FinalFitProduct MakeFinalFitProduct(FinalFitInput input)
    {
        var template=input.PrivateTemplate; var wear=template.GetComponent<Wear>();
        var meshes=new Dictionary<ActorName,Mesh>(); var transient=new List<Mesh>();
        var evidence=new JObject { ["finalQualityPassed"]=false,["baseQualityScope"]="base-reduction-only",
            ["authoringSettingsChanged"]=false,["bodies"]=new JObject(),["version"]=FinalFitVersion };
        if(input.RevalidationProvenance!=null)evidence["revalidationProvenance"]=input.RevalidationProvenance.DeepClone();
        bool headwear=wear.Slots.Contains(VisualWearSlot.Head) && wear.GetHeadwearFit().Any;
        evidence["fitKind"]=headwear?"authored-rigid-headwear":"deformable-garment";
        ActorName donor=(ActorName)Enum.Parse(typeof(ActorName),input.Donor);
        ActorName recipient=donor==ActorName.Marta?ActorName.Kshishtof:ActorName.Marta;
        Mesh donorMesh=input.PrimaryMesh,recipientMesh=null,clearanceRecipientMesh=null,uniformRecipientMesh=null,clearanceDonorMesh=null,uniformDonorMesh=null,donorCutMesh=null,recipientCutMesh=null,recipientClearanceCutMesh=null;
        JObject donorCutProof=null,recipientClearanceCutProof=null;SourceCapAnnulusLineage[] donorCutLineage=null;
        JObject collapsedNativeProof=null;
        try
        {
            if(!headwear&&CollapsedZeroInfluences(input.PrimaryMesh,out _,out _).Any(z=>z))
            {
                var native=AssetDatabase.LoadAssetAtPath<GameObject>(input.NativeSource);
                collapsedNativeProof=CaptureNativeCollapsedZeroEvidence(native,donor);
                string nativeMesh=AssetDatabase.GetAssetPath(SourceMesh(native.GetComponent<Wear>(),donor,out _));
                if((string)collapsedNativeProof["nativeSource"]!=input.NativeSource||(string)collapsedNativeProof["actor"]!=input.Donor||
                    (string)collapsedNativeProof["nativeSourceDependency"]!=AssetDatabase.GetAssetDependencyHash(input.NativeSource).ToString()||
                    (string)collapsedNativeProof["nativeMesh"]!=nativeMesh||(string)collapsedNativeProof["nativeMeshSha"]!=FinalFileSHA(nativeMesh)||
                    (string)collapsedNativeProof["equipmentFactory"]!="BodyBones.Equip")
                    throw new InvalidOperationException("Collapsed-island evidence belongs to another actual Native input");
                evidence["collapsedNativeEvidence"]=collapsedNativeProof.DeepClone();
            }
            if(headwear)
            {
                var native=AssetDatabase.LoadAssetAtPath<GameObject>(input.NativeSource);
                var raw=SourceMesh(native.GetComponent<Wear>(),donor,out _);
                using var reference=ConstructHeadwearUnitReference(native,donor,out JObject units);
                using var runtime=ConstructFit(template,input.MartaMesh,input.KshishtofMesh,donor);
                RequireRigidHeadInfluences(raw,reference.Skin);
                if(!reference.Paths.SequenceEqual(runtime.Paths))throw new InvalidOperationException(input.Id+": headwear bone order changed");
                var posed=BindMesh(raw,runtime,reference.WorldVertices,reference.WorldNormals,"FinalHeadInput_"+input.Id);
                transient.Add(posed);posed.ClearBlendShapes();
                string qualityKey="FinalHead_"+input.InputHash;
                // Reuse a reduction only after a NEW error measurement in the
                // corrected authoring units. The old tiny-world proof is not transferable.
                var remapped=Retarget(input.PrimaryMesh,runtime,reference,out _,false,false);transient.Add(remapped);
                var candidateWorld=remapped.vertices.Select(reference.Skin.transform.localToWorldMatrix.MultiplyPoint3x4).ToArray();
                var candidateNormalMatrix=reference.Skin.transform.worldToLocalMatrix.transpose;
                var candidateNormals=remapped.normals.Select(n=>candidateNormalMatrix.MultiplyVector(n).normalized).ToArray();
                var candidate=BindMesh(input.PrimaryMesh,runtime,candidateWorld,candidateNormals,qualityKey);
                transient.Add(candidate);
                var world=runtime.Skin.transform.localToWorldMatrix;
                var forward=QualityError(QualitySamples(posed,world),TriangleTree.ForMesh(candidate,world));
                var reverse=QualityError(QualitySamples(candidate,world),TriangleTree.ForMesh(posed,world));
                JObject headQuality;
                if(WithinQuality(forward)&&WithinQuality(reverse))
                {
                    donorMesh=candidate;
                    headQuality=new JObject{["mode"]="validated-base-candidate",["inputTriangles"]=TriangleCount(posed),
                        ["actualTriangles"]=TriangleCount(candidate),["maxErrorLimitMetres"]=QualityMaxError,["p95ErrorLimitMetres"]=QualityP95Error,
                        ["sourceToCandidate"]=forward,["candidateToSource"]=reverse,["passed"]=true,
                        ["metric"]="new bidirectional sampled error after correcting authoring world units"};
                }
                else
                {
                    donorMesh=Simplify(posed,qualityKey,world);transient.Add(donorMesh);
                    headQuality=(JObject)MeshQuality[qualityKey].DeepClone();
                    headQuality["rejectedBaseCandidate"]=new JObject{["sourceToCandidate"]=forward,["candidateToSource"]=reverse};
                }
                evidence["headwearAuthoringUnits"]=units;
                evidence["correctedHeadwearReduction"]=headQuality;
                // Head-attached topology follows the same named body bones; derive
                // the second fit from this corrected donor, never the old tiny fit.
            }
            using(var sourceFit=ConstructFit(template,donorMesh,donorMesh,donor))
            using(var targetFit=ConstructFit(template,donorMesh,donorMesh,recipient))
            {
                recipientMesh=Retarget(donorMesh,sourceFit,targetFit,out _,false,false);
                transient.Add(recipientMesh);
                if(!headwear)
                {
                    // Immutable BASE is still tried first. The authored donor can
                    // also intersect today's canonical body, so offer the same generic
                    // source-clearance transfer against that body as a separate seed.
                    clearanceDonorMesh=CreateSourceClearanceSeed(donorMesh,sourceFit,sourceFit,out JObject donorClearanceProof);
                    transient.Add(clearanceDonorMesh);evidence["donorSourceClearanceSeed"]=donorClearanceProof;
                    if((bool?)donorClearanceProof["passed"]!=true)clearanceDonorMesh=null;
                    if(clearanceDonorMesh!=null)
                    {
                        var donorWorld=sourceFit.Skin.transform.localToWorldMatrix;
                        var donorGapWorld=clearanceDonorMesh.vertices.Select(donorWorld.MultiplyPoint3x4).ToArray();
                        uniformDonorMesh=ApplyUniformFootProfileSeed(clearanceDonorMesh,sourceFit,sourceFit.WorldVertices,
                            donorGapWorld,sourceFit.WorldNormals,out JObject donorUniformProof);
                        transient.Add(uniformDonorMesh);evidence["uniformDonorFootSeed"]=donorUniformProof;
                        if((bool?)donorUniformProof["applicable"]!=true)uniformDonorMesh=null;
                    }
                    clearanceRecipientMesh=CreateSourceClearanceSeed(donorMesh,sourceFit,targetFit,out JObject clearanceSeedProof);
                    transient.Add(clearanceRecipientMesh);evidence["sourceClearanceSeed"]=clearanceSeedProof;
                    if((bool?)clearanceSeedProof["passed"]!=true)clearanceRecipientMesh=null;
                    if(clearanceRecipientMesh!=null)
                    {
                        var world=targetFit.Skin.transform.localToWorldMatrix;
                        var boneWorld=recipientMesh.vertices.Select(world.MultiplyPoint3x4).ToArray();
                        var gapWorld=clearanceRecipientMesh.vertices.Select(world.MultiplyPoint3x4).ToArray();
                        var normalWorld=targetFit.Skin.transform.worldToLocalMatrix.transpose;
                        var boneNormals=recipientMesh.normals.Select(n=>normalWorld.MultiplyVector(n).normalized).ToArray();
                        // Clone the gap seed to preserve its already transported normals/attributes.
                        uniformRecipientMesh=ApplyUniformFootProfileSeed(clearanceRecipientMesh,targetFit,boneWorld,gapWorld,boneNormals,out JObject uniformProof);
                        transient.Add(uniformRecipientMesh);evidence["uniformFootSeed"]=uniformProof;
                        if((bool?)uniformProof["applicable"]!=true)uniformRecipientMesh=null;
                    }
                }
            }
            // One SOURCE-evidenced cut can be transported before target repair.
            // The donor cut is not misreported as a target-body clip certificate.
            if(!headwear&&clearanceDonorMesh!=null&&HasSourceCapAdmission(input))
            using(var originalDonor=ConstructFit(template,donorMesh,donorMesh,donor))
            using(var cutSourceFit=ConstructFit(template,clearanceDonorMesh,clearanceDonorMesh,donor))
            using(var originalTarget=ConstructFit(template,donorMesh,donorMesh,recipient))
            {
                var under=FitVisibleUnderlayers(cutSourceFit.WorldVertices,cutSourceFit,out _);
                var cut=TrySourceCapAnnulusWithLineage(input,clearanceDonorMesh,cutSourceFit,under,cutSourceFit.WorldNormals,
                    new[]{cutSourceFit.WorldVertices,originalDonor.WorldVertices},out var cutWorld,out var cutNormals,out _,out donorCutProof,out donorCutLineage);
                if(cut!=null)
                {
                    transient.Add(cut);donorCutMesh=BindMesh(cut,cutSourceFit,cutWorld,cutNormals,"SOURCE_admitted_donor_cap");transient.Add(donorCutMesh);
                    using var transportedSource=ConstructFit(template,donorCutMesh,donorCutMesh,donor);
                    recipientCutMesh=Retarget(donorCutMesh,transportedSource,originalTarget,out _,false,false);transient.Add(recipientCutMesh);
                    using var clippedTarget=ConstructFit(template,donorCutMesh,donorCutMesh,recipient);
                    recipientClearanceCutMesh=CreateSourceClearanceSeed(donorCutMesh,transportedSource,clippedTarget,out recipientClearanceCutProof);
                    transient.Add(recipientClearanceCutMesh);
                    if((bool?)recipientClearanceCutProof["passed"]!=true)recipientClearanceCutMesh=null;
                }
            }
            var seedM=donor==ActorName.Marta?donorMesh:recipientMesh;
            var seedK=donor==ActorName.Kshishtof?donorMesh:recipientMesh;
            foreach(var actor in new[]{ActorName.Marta,ActorName.Kshishtof})
            {
                Debug.Log("LEGACY_FINAL_FIT_SOLVE "+input.Id+" "+actor);
                using var referenceFit=ConstructFit(template,seedM,seedK,actor);
                if(headwear)
                {
                    var rigidSeed=actor==ActorName.Marta?seedM:seedK;
                    int headIndex=RequireRigidHeadInfluences(rigidSeed,referenceFit.Skin);
                    var result=BindMesh(rigidSeed,referenceFit,referenceFit.WorldVertices,referenceFit.WorldNormals,"Final_"+input.InputHash+"_"+actor);
                    meshes[actor]=result;
                    using var replay=ConstructFinalRuntimeFit(template,actor==ActorName.Marta?result:seedM,actor==ActorName.Kshishtof?result:seedK,actor);
                    RequireRigidHeadInfluences(result,replay.Skin);
                    var reconstruction=RetargetProbeError(referenceFit.WorldVertices,replay.WorldVertices);
                    if((float)reconstruction["maxMetres"]>.0002f)throw new InvalidOperationException(input.Id+"/"+actor+": rigid head-frame reconstruction changed authored shape");
                    evidence["bodies"][actor.ToString()]=new JObject{
                        ["rigidHeadwearProof"]=new JObject{["passed"]=true,["headBoneIndex"]=headIndex,["headBoneName"]="head",["rigidBoneOnly"]=true,
                            ["shapeDeformationAllowed"]=false,["deformationMaxMetres"]=reconstruction["maxMetres"]?.DeepClone(),["maxAllowedDeformationMetres"]=.0002f},
                        ["runtimeConstruction"]="BodyBones.Equip",["reconstruction"]=reconstruction,["bodyContactDiagnostic"]=RigidBodyContactDiagnostic(referenceFit),
                        ["worldBounds"]=Box(VertexBounds(replay.WorldVertices))};
                    continue;
                }
                if(TryAuthoredDonorProduct(input,actor,out var preservedDonor,out var authoredProof))
                {
                    meshes[actor]=preservedDonor;evidence["bodies"][actor.ToString()]=authoredProof;
                    continue;
                }
                var candidates=new List<(string kind,Mesh m,Mesh k)>{("immutable-base-fit",input.MartaMesh,input.KshishtofMesh)};
                if (input.Id == ReikoOpenCuffsId && actor == ActorName.Kshishtof)
                    candidates.Insert(0, (ReikoOpenCuffsSeed, seedM, seedK));
                if (input.Id == GlovesOpeningId && actor == ActorName.Kshishtof)
                    candidates.Insert(0, (GlovesOpeningSeed, seedM, seedK));
                if(actor==recipient&&recipientClearanceCutMesh!=null)
                    candidates.Add(("source-admitted-donor-cap-then-clearance",donor==ActorName.Marta?donorCutMesh:recipientClearanceCutMesh,
                        donor==ActorName.Kshishtof?donorCutMesh:recipientClearanceCutMesh));
                if(actor==recipient&&recipientCutMesh!=null)
                    candidates.Add(("source-admitted-donor-cap-before-retarget",donor==ActorName.Marta?donorCutMesh:recipientCutMesh,
                        donor==ActorName.Kshishtof?donorCutMesh:recipientCutMesh));
                if(actor==donor&&uniformDonorMesh!=null)
                    candidates.Add(("uniform-donor-foot-profile",donor==ActorName.Marta?uniformDonorMesh:seedM,
                        donor==ActorName.Kshishtof?uniformDonorMesh:seedK));
                if(actor==donor&&clearanceDonorMesh!=null)
                    candidates.Add(("source-clearance-donor",donor==ActorName.Marta?clearanceDonorMesh:seedM,
                        donor==ActorName.Kshishtof?clearanceDonorMesh:seedK));
                if(uniformRecipientMesh!=null)
                    candidates.Add(("uniform-foot-profile",donor==ActorName.Marta?donorMesh:uniformRecipientMesh,
                        donor==ActorName.Kshishtof?donorMesh:uniformRecipientMesh));
                if(clearanceRecipientMesh!=null)
                    candidates.Add(("source-clearance-retarget",donor==ActorName.Marta?donorMesh:clearanceRecipientMesh,
                        donor==ActorName.Kshishtof?donorMesh:clearanceRecipientMesh));
                candidates.Add(("fresh-bone-retarget",seedM,seedK));
                if(TryRevalidateFinalBody(input,actor,template,referenceFit,candidates,out Mesh retainedGeometry,out JObject retainedProof))
                {
                    meshes[actor]=retainedGeometry;evidence["bodies"][actor.ToString()]=retainedProof;
                    Debug.Log("LEGACY_FINAL_FIT_REVALIDATED "+input.Id+"/"+actor);
                    continue;
                }
                var attempts=new JArray();
                // On the authored donor body both labels can select the exact same immutable mesh.
                // Retrying that same reference does not add another fit or acceptance case.
                foreach (var candidate in candidates
                    .GroupBy(c => (mesh: actor == ActorName.Marta ? c.m : c.k,
                        opensClosures: c.kind == ReikoOpenCuffsSeed || c.kind == GlovesOpeningSeed))
                    .Select(group => group.First()))
                {
                    var seed=actor==ActorName.Marta?candidate.m:candidate.k;
                    using var fit=ConstructFit(template,candidate.m,candidate.k,actor);
                    Mesh topology=null,result=null,cleanedSeed=null,capSeed=null;
                    try
                    {
                        bool opensReikoCuffs = candidate.kind == ReikoOpenCuffsSeed;
                        bool opensGloveClosures = candidate.kind == GlovesOpeningSeed;
                        bool clearanceAfterCap=candidate.kind=="source-admitted-donor-cap-then-clearance";
                        bool transferredCap=clearanceAfterCap||candidate.kind=="source-admitted-donor-cap-before-retarget";
                        var commonReference=referenceFit.WorldVertices;
                        if(transferredCap)
                        {
                            if(donorCutLineage==null||donorCutLineage.Length!=fit.WorldVertices.Length)throw new InvalidDataException("Transferred cap lineage does not cover target topology");
                            commonReference=donorCutLineage.Select(v=>v.OriginalIndex>=0?referenceFit.WorldVertices[v.OriginalIndex]:
                                referenceFit.WorldVertices[v.Indices[0]]*v.Barycentric.x+referenceFit.WorldVertices[v.Indices[1]]*v.Barycentric.y+referenceFit.WorldVertices[v.Indices[2]]*v.Barycentric.z).ToArray();
                        }
                        cleanedSeed=CleanCollapsedUnweightedDerived(seed,fit.WorldVertices,fit.WorldNormals,
                            new[]{fit.WorldVertices,commonReference},collapsedNativeProof,
                            out var cleanWorld,out var cleanNormals,out var cleanReferences,out JObject cleanupProof);
                        var underlayers=FitVisibleUnderlayers(cleanWorld,fit,out JObject underlayerProof);
                        Vector3[] capWorld,capNormals;Vector3[][] capReferences;JObject sourceCapProof;
                        JObject authoredOpening = null;
                        int[] openingRetained = null;
                        if (opensReikoCuffs || opensGloveClosures)
                        {
                            if (cleanedSeed != null || actor != ActorName.Kshishtof)
                                throw new InvalidDataException("Authored opening requires the uncut recipient topology");
                            if (opensReikoCuffs)
                            {
                                ValidateReikoCuffInputs(input);
                                capSeed = RemoveReikoCuffClosures(seed, out openingRetained, out authoredOpening);
                            }
                            else
                            {
                                ValidateGlovesOpeningInputs(input);
                                capSeed = OpenGlovesKClosures(seed, out openingRetained, out authoredOpening);
                            }
                            capWorld = openingRetained.Select(i => underlayers[i]).ToArray();
                            capNormals = openingRetained.Select(i => cleanNormals[i]).ToArray();
                            capReferences = cleanReferences.Select(r => openingRetained.Select(i => r[i]).ToArray()).ToArray();
                            sourceCapProof = new JObject { ["applicable"] = false };
                        }
                        else if(transferredCap)
                        {
                            capWorld=underlayers;capNormals=cleanNormals;capReferences=cleanReferences;sourceCapProof=(JObject)donorCutProof.DeepClone();
                            sourceCapProof["recipientTransfer"]=new JObject{["sourceActor"]=donor.ToString(),["targetActor"]=actor.ToString(),
                                ["targetClipWasRecomputed"]=false,["sourceClipScope"]="SOURCE-evidenced donor geometry before target bone retarget and strict repair",
                                ["ownReferencePolicy"]=clearanceAfterCap?"actual cut-retarget-clearance target seed before underlayers":"actual cut-retarget target seed before underlayers",
                                ["commonReferencePolicy"]="source lineage interpolated ORIGINAL UNCUT fresh-bone target reference"};
                            if(clearanceAfterCap)
                            {
                                sourceCapProof["recipientTransfer"]["seedOperator"]="CreateSourceClearanceSeed on SOURCE-evidenced clipped topology";
                                sourceCapProof["recipientTransfer"]["targetClearanceSeed"]=recipientClearanceCutProof.DeepClone();
                            }
                        }
                        else capSeed=TrySourceCapAnnulus(input,cleanedSeed??seed,fit,underlayers,cleanNormals,cleanReferences,
                            out capWorld,out capNormals,out capReferences,out sourceCapProof);
                        var fittingWorld = capWorld;
                        JObject sandalsPanelPlacement = null;
                        if (input.Id == "clothing.sandals_summer4" && actor == ActorName.Kshishtof &&
                            candidate.kind == "uniform-foot-profile")
                        {
                            if (capSeed != null || cleanedSeed != null)
                                throw new InvalidDataException("Reviewed Sandals panel placement requires original topology");
                            fittingWorld = PlaceSandalsRigidPanels(input, seed, capWorld, capNormals, capReferences,
                                fit.BodySurface, out sandalsPanelPlacement);
                        }
                        topology=RefineEnvelopeMeshWithReferences(capSeed??cleanedSeed??seed,fittingWorld,capNormals,fit.BodySurface,.006f,.002f,16,.1f,
                            capReferences,out var points,out var normals,out JObject topologyProof,out var references);
                        if (sandalsPanelPlacement != null)
                        {
                            if (!(bool)topologyProof["passed"] || !points.SequenceEqual(fittingWorld))
                                throw new InvalidDataException("Canonical solver changed the reviewed rigid Sandals panel placement");
                            topologyProof["sandalsPanelPlacement"] = sandalsPanelPlacement;
                        }
                        if (authoredOpening != null)
                        {
                            long original = TriangleCount(seed);
                            int budget = Math.Max((int)Math.Ceiling(original * .10), 2048);
                            long added = TriangleCount(topology) - original;
                            authoredOpening["aggregateOriginalAdditionalTriangles"] = added;
                            authoredOpening["aggregateOriginalAdditionalTriangleBudget"] = budget;
                            topologyProof[opensReikoCuffs ? "reikoCuffOpening" : "glovesOpening"] = authoredOpening;
                            if (added > budget)
                            {
                                attempts.Add(new JObject { ["seed"] = candidate.kind, ["candidateRejected"] = true,
                                    ["rejectionKind"] = "authored-opening-original-triangle-budget", ["topologyRefinement"] = topologyProof });
                                continue;
                            }
                            if (opensGloveClosures && (bool?)topologyProof["passed"] == true)
                            {
                                authoredOpening["watchLayer"] = PlaceWatchOverLeftCuff(fit, openingRetained, points, normals);
                                var adjusted = EnvelopeProbeResidualDispatchedDraft(points, topology.triangles,
                                    WardrobeBodyVolume.For(fit.BodySurface), .002f);
                                authoredOpening["watchLayerEnvelope"] = adjusted;
                                if ((bool?)adjusted["passedSampledEnvelope"] != true)
                                {
                                    attempts.Add(new JObject { ["seed"] = candidate.kind, ["candidateRejected"] = true,
                                        ["rejectionKind"] = "stars-gloves-watch-layer-physical-floor", ["topologyRefinement"] = topologyProof });
                                    continue;
                                }
                            }
                        }
                        else if(capSeed!=null||transferredCap)
                        {
                            topologyProof["sourceCapAnnulus"]=sourceCapProof;
                            int original=(int)sourceCapProof["originalSourceTriangles"],budget=(int)sourceCapProof["canonicalOriginalAdditionalTriangleBudget"];
                            topologyProof["aggregateSourceAdditionalTriangles"]=TriangleCount(topology)-original;
                            topologyProof["aggregateSourceAdditionalTriangleBudget"]=budget;
                            if(TriangleCount(topology)-original>budget)
                            {
                                attempts.Add(new JObject{["seed"]=candidate.kind,["candidateRejected"]=true,["rejectionKind"]="combined-cap-and-adaptive-triangle-budget",["topologyRefinement"]=topologyProof});
                                continue;
                            }
                        }
                        if((bool?)cleanupProof["applicable"]==true)
                        {
                            topologyProof["collapsedZeroIslandCleanup"]=cleanupProof;
                            topologyProof["originalSourceVertices"]=seed.vertexCount;topologyProof["originalSourceTriangles"]=TriangleCount(seed);
                        }
                        var surfaceProof=topologyProof["surfaceEnvelope"] as JObject;
                        if(references.Length!=2||references.Any(r=>r.Length!=points.Length))
                            throw new InvalidOperationException(input.Id+"/"+actor+": companion references must cover ALL final vertices");
                        var totalDisplacement=RetargetProbeError(references[0],points);
                        var commonDisplacement=RetargetProbeError(references[1],points);
                        foreach(var measurement in new[]{totalDisplacement,commonDisplacement})
                        {
                            measurement["measurementScope"]="all-output-vertices";
                            measurement["originalReferenceVertices"]=seed.vertexCount;
                            measurement["measuredVertices"]=points.Length;
                        }
                        attempts.Add(new JObject{["seed"]=candidate.kind,["topologyRefinement"]=topologyProof.DeepClone(),
                            ["fromOwnSeed"]=totalDisplacement,["fromCommonBoneReference"]=commonDisplacement,["sourceCapPreparation"]=sourceCapProof.DeepClone()});
                        if((bool?)topologyProof["passed"]!=true||(bool?)surfaceProof?["passed"]!=true||
                            (float)totalDisplacement["maxMetres"]>.12f||(float)commonDisplacement["maxMetres"]>.12f)continue;
                        result=BindMesh(topology,fit,points,normals,"Final_"+input.InputHash+"_"+actor);
                        using var replay=ConstructFinalRuntimeFit(template,actor==ActorName.Marta?result:candidate.m,
                            actor==ActorName.Kshishtof?result:candidate.k,actor);
                        var reconstruction=RetargetProbeError(points,replay.WorldVertices);
                        if((float)reconstruction["maxMetres"]>.0002f)
                            throw new InvalidOperationException(input.Id+"/"+actor+": final bind reconstruction changed fitted geometry");
                        totalDisplacement=RetargetProbeError(references[0],replay.WorldVertices);
                        commonDisplacement=RetargetProbeError(references[1],replay.WorldVertices);
                        foreach(var measurement in new[]{totalDisplacement,commonDisplacement})
                        {
                            measurement["measurementScope"]="all-output-vertices";
                            measurement["positionScope"]="actual-post-bind-world-vertices";
                            measurement["originalReferenceVertices"]=seed.vertexCount;
                            measurement["measuredVertices"]=replay.WorldVertices.Length;
                        }
                        ((JObject)attempts.Last)["postBindFromOwnSeed"]=totalDisplacement.DeepClone();
                        ((JObject)attempts.Last)["postBindFromCommonBoneReference"]=commonDisplacement.DeepClone();
                        if((float)totalDisplacement["maxMetres"]>.12f||(float)commonDisplacement["maxMetres"]>.12f)continue;
                        var actualSurface=EnvelopeProbeResidualDispatchedDraft(replay.WorldVertices,result.triangles,WardrobeBodyVolume.For(replay.BodySurface),.002f);
                        if((bool?)actualSurface["passedSampledEnvelope"]!=true)
                        {
                            ((JObject)attempts.Last)["postBindSurfaceEnvelope"]=actualSurface;
                            Object.DestroyImmediate(result);
                            continue;
                        }
                        meshes[actor]=result;
                        evidence["bodies"][actor.ToString()]=new JObject { ["postBindSurfaceEnvelope"]=actualSurface,
                            ["surfaceEnvelope"]=surfaceProof?.DeepClone(),["topologyRefinement"]=topologyProof,
                            ["visibleUnderlayers"]=underlayerProof,["runtimeConstruction"]="BodyBones.Equip",["reconstruction"]=reconstruction,["seed"]=candidate.kind,["seedAttempts"]=attempts,
                            ["baseToFinalDisplacement"]=totalDisplacement,["commonBoneReferenceDisplacement"]=commonDisplacement,["worldBounds"]=Box(VertexBounds(points)) };
                        break;
                    }
                    catch(EnvelopeCandidateDegenerateFaceException error)
                    {
                        // This seed cannot be refined. Keep its exact diagnostic
                        // and try the next existing seed; all final gates still run.
                        attempts.Add(new JObject{["seed"]=candidate.kind,["candidateRejected"]=true,
                            ["rejectionKind"]="degenerate-refinement-face",["context"]=error.Evidence.DeepClone()});
                    }
                    finally{if(topology!=null)Object.DestroyImmediate(topology);if(capSeed!=null)Object.DestroyImmediate(capSeed);if(cleanedSeed!=null)Object.DestroyImmediate(cleanedSeed);if(result!=null&&!meshes.Values.Contains(result))Object.DestroyImmediate(result);}
                }
                if(!meshes.ContainsKey(actor))throw new InvalidOperationException(input.Id+"/"+actor+": all final envelope candidates failed: "+attempts);
            }
            bool revalidated=((JObject)evidence["bodies"]).Properties().All(p=>(string)p.Value["geometryMode"]=="revalidated-unmodified-topology");
            bool authoredDonor=((JObject)evidence["bodies"]).Properties().Any(p=>(string)p.Value["geometryMode"]==AuthoredDonorMode);
            evidence["finalQualityPassed"]=true;
            return new FinalFitProduct { Template=template,MartaMesh=meshes[ActorName.Marta],
                KshishtofMesh=meshes[ActorName.Kshishtof],Evidence=evidence,Mode=authoredDonor?"mixed-authored-donor-and-strict-recipient":revalidated?"revalidated-unmodified-topology":headwear?"authored-rigid-headwear":"base-or-bone-fit-and-triangle-envelope" };
        }
        catch
        {
            foreach(var mesh in meshes.Values)if(mesh!=null&&!EditorUtility.IsPersistent(mesh))Object.DestroyImmediate(mesh);
            throw;
        }
        finally
        {
            foreach(var mesh in transient)if(mesh!=null&&!EditorUtility.IsPersistent(mesh))Object.DestroyImmediate(mesh);
        }
    }
}
}
#endif

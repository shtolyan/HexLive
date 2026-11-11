#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    static bool FinalReductionAccepted(JToken quality)
    {
        if (!(quality is JObject)) return false;
        string mode=(string)quality["mode"];
        long? input=(long?)quality["inputTriangles"], actual=(long?)quality["actualTriangles"];
        if (!(input>0) || !(actual>0)) return false;
        if(mode=="unchanged"||mode=="source-preserved") return actual==input;
        bool fresh=mode=="validated-base-candidate";
        if(!fresh&&mode!="adaptive")return false;
        JToken measurement=fresh?quality:(quality["attempts"] as JArray)?.LastOrDefault();
        if((bool?)measurement?["passed"]!=true)return false;
        if(fresh&&((float?)quality["maxErrorLimitMetres"]!=.003f||(float?)quality["p95ErrorLimitMetres"]!=.0005f))return false;
        foreach(string direction in new[]{"sourceToCandidate","candidateToSource"})
        {
            var values=measurement?[direction];
            if(fresh&&!((int?)values?["sampleCount"]>0))return false;
            float? max=(float?)values?["max"], p95=(float?)values?["p95"];
            if(!(max>=0&&max<=.003f&&p95>=0&&p95<=.0005f))return false;
        }
        return true;
    }

    public static void ValidateFinalPreparedCollectionAndReview() => ValidatePreparedFitAndReview(false);

    public static void PrepareCuratedModernWardrobeReleaseAndReview()
    {
        PrepareCuratedModernWardrobeRelease();
        ValidatePreparedFitAndReview(true);
    }

    public static void ValidateCuratedModernWardrobeRelease() => ValidatePreparedFitAndReview(true);

    static void ValidatePreparedFitAndReview(bool curated)
    {
        Directory.CreateDirectory(OutputRoot);
        var errors=new JArray();var prototypes=new JArray();var dependencies=new JArray();
        var report=new JObject { ["passed"]=false,["utc"]=DateTime.UtcNow.ToString("O"),["errors"]=errors,
            ["prototypes"]=prototypes,["dependencies"]=dependencies,["assetsModified"]=false,
            ["bundlesBuilt"]=false,["productionChanged"]=false,["validationStage"]="final-fit" };
        string catalogPath = curated ? ModernReleaseCatalogPath : PreparedRoot + "/catalog.json";
        string manifestPath = curated ? ModernReleaseFitPath : PreparedRoot + "/final-fit.json";
        string reviewPath = OutputRoot + (curated ? "/prepared-collection-release-validation.json" : "/prepared-collection-validation.json");
        var protectedFiles=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string path in new[]{PeopleRoot+"/catalog.json",catalogPath,manifestPath,PreparedRoot+"/base-catalog.json",PreparedRoot+"/preparation.json"})
            protectedFiles[path]=FinalFileSHA(path);
        if (curated) protectedFiles[ModernReleaseSelectionPath] = FinalFileSHA(ModernReleaseSelectionPath);
        report["catalogSHA256"] = protectedFiles[catalogPath];
        report["fitManifestSHA256"] = protectedFiles[manifestPath];
        void Check(bool ok,string message){if(!ok)errors.Add(message);}
        try
        {
            var active=JObject.Parse(File.ReadAllText(PeopleRoot+"/catalog.json"));
            var stage=JObject.Parse(File.ReadAllText(catalogPath));
            var manifest=JObject.Parse(File.ReadAllText(manifestPath));
            var generation=manifest["baseGeneration"];
            var baseCache=JObject.Parse(File.ReadAllText(PreparedRoot+"/preparation.json"));
            var baseReport=JObject.Parse(File.ReadAllText((string)generation["fullReportPath"]));
            var baseCatalog=JObject.Parse(File.ReadAllText((string)generation["stagedCatalogSnapshot"]));
            var baseMetrics=((JArray)baseReport["prototypes"]).ToDictionary(r=>(string)r["id"],StringComparer.Ordinal);
            var rows=(JObject)manifest["rows"];
            var records=((JArray)stage["records"]).ToArray();
            var wearRows=records.Where(r=>(string)r["type"]=="wear").ToArray();
            var mapRows=records.Where(r=>(string)r["type"]=="config").ToArray();
            var expected=new HashSet<string>(GarmentLibrary.Defaults.Select(g=>g.Id),StringComparer.Ordinal);
            expected.ExceptWith(active["records"].Where(r=>(string)r["type"]=="wear").Select(r=>(string)r["id"]));
            Check(expected.Count == 660, "Complete Modern variant coverage changed before curation");
            var selectedModels = baseMetrics.Keys.ToHashSet(StringComparer.Ordinal);
            if (curated)
            {
                string selectionSHA = protectedFiles[ModernReleaseSelectionPath];
                Check((string)stage["releaseSelectionSHA256"] == selectionSHA &&
                    (string)manifest["releaseSelectionSHA256"] == selectionSHA, "Modern release selection changed");
                var exclusions = ReadModernReleaseExclusions(baseCache);
                selectedModels.ExceptWith(exclusions.Keys);
                var receipts = ((JArray)manifest["excludedModels"]).OfType<JObject>()
                    .ToDictionary(r => (string)r["id"], StringComparer.Ordinal);
                Check(receipts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(exclusions.Keys), "Wrong excluded models in release receipt");
                foreach (var pair in exclusions)
                {
                    string prefab = (string)baseCache[pair.Key]["prepared"];
                    var variants = baseCatalog["records"].Where(r => (string)r["type"] == "wear" && (string)r["main"] == prefab)
                        .Select(r => (string)r["id"]).ToHashSet(StringComparer.Ordinal);
                    Check(variants.Count > 0 && variants.IsSubsetOf(expected), "Unresolved excluded variants: " + pair.Key);
                    expected.ExceptWith(variants);
                    if (!receipts.TryGetValue(pair.Key, out var receipt)) continue;
                    var raw = (JObject)receipt.DeepClone();
                    var ids = raw["logicalIds"] as JArray;
                    raw.Remove("logicalIds");
                    Check(JToken.DeepEquals(raw, pair.Value) && ids != null && ids.Count == variants.Count &&
                        variants.SetEquals(ids.Values<string>()), "Excluded variants or source changed: " + pair.Key);
                }
            }
            int selectedItems = expected.Count;
            int selectedMeshCount = selectedModels.Count * 2;
            Check((bool?)stage["finalFit"]==true&&(string)stage["finalFitManifest"]==manifestPath,"Missing final catalog routing");
            Check((string)stage["stage"]=="prepared-only"&&(bool?)stage["gameReady"]==false&&(bool?)stage["bundleBuildAuthorized"]==false,"Final authorization flags changed");
            Check((string)stage["contentOrigin"] == "modern", "Final catalog is missing its modern origin");
            foreach (var record in records)
                Check((string)record["metadata"]?["contentOrigin"] == "modern", "Final record origin absent " + (string)record["id"]);
            Check((bool?)manifest["passed"]==true&&(bool?)manifest["catalogPromoted"]==true&&(string)manifest["stage"]=="final-fit-prepared-only","Final manifest incomplete");
            Check((bool?)manifest["bundlesBuilt"]==false&&(bool?)manifest["productionChanged"]==false,"Final manifest claims publication");
            Check((string)manifest["finalCatalog"]?["path"]==catalogPath&&FinalFileSHA(catalogPath)==(string)manifest["finalCatalog"]?["sha"],"Final catalog hash mismatch");
            Check((int?)manifest["counts"]?["models"] == selectedModels.Count && (int?)manifest["counts"]?["logicalIds"] == selectedItems &&
                (int?)manifest["counts"]?["meshes"] == selectedMeshCount && (int?)manifest["counts"]?["maps"] == selectedMeshCount, "Final manifest coverage changed");
            Check(rows.Count == selectedModels.Count && baseMetrics.Count == 183 && baseCache.Count == 183, "Wrong base or selected model count");
            Check(new HashSet<string>(rows.Properties().Select(p => p.Name), StringComparer.Ordinal).SetEquals(selectedModels), "Final model selection differs");
            Check(wearRows.Length == selectedItems && new HashSet<string>(wearRows.Select(r => (string)r["id"]), StringComparer.Ordinal)
                .SetEquals(expected), "Final logical ID coverage differs");
            Check(records.Length==records.Select(r=>(string)r["id"]).Distinct(StringComparer.Ordinal).Count(),"Duplicate final record IDs");
            foreach(var tuple in new[]{("cachePath","cacheSHA"),("fullReportPath","fullReportSHA"),("stagedCatalogSnapshot","stagedCatalogSHA")})
                Check(FinalFileSHA((string)generation[tuple.Item1])==(string)generation[tuple.Item2],"Base generation document changed "+tuple.Item1);
            foreach(var file in generation["baseFiles"])
                Check(File.Exists((string)file["path"])&&FinalFileSHA((string)file["path"])==(string)file["sha"],"Pinned base file changed "+file["path"]);
            foreach(var source in manifest["processor"]["sources"])
                Check(FinalFileSHA((string)source["path"])==(string)source["sha"],"Final processor source changed "+source["path"]);
            var baseWear=baseCatalog["records"].Where(r=>(string)r["type"]=="wear").ToDictionary(r=>(string)r["id"],StringComparer.Ordinal);
            foreach(var record in wearRows)
            {
                string id=(string)record["id"];
                Check((bool?)record["metadata"]?["legacyWardrobe"]==true&&(bool?)record["metadata"]?["finalFit"]==true,"Final record ownership absent "+id);
                Check(baseWear.ContainsKey(id),"Final record has no base ID "+id);
                if(!baseWear.TryGetValue(id,out var original))continue;
                var observed=(JObject)record.DeepClone();observed.Remove("main");(observed["metadata"] as JObject)?.Remove("finalFit");
                var retained=(JObject)original.DeepClone();retained.Remove("main");(retained["metadata"] as JObject)?.Remove("finalFit");
                (observed["metadata"] as JObject)?.Remove("contentOrigin");
                (retained["metadata"] as JObject)?.Remove("contentOrigin");
                Check(JToken.DeepEquals(observed,retained),"Final record changed statistics, definition, flags or aliases "+id);
            }
            var allMeshes=new HashSet<Mesh>();var finalPaths=new List<string>();long totalM=0,totalK=0,totalSource=0;
            foreach(var property in rows.Properties())
            {
                string id=property.Name;var row=property.Value;var baseRow=row["base"];var final=row["final"];
                if(!baseMetrics.TryGetValue(id,out var metric)){Check(false,"Missing base metric "+id);continue;}
                Check(JToken.DeepEquals(baseCache[id],metric),"Base cache/report mismatch "+id);
                Check((string)baseRow["rowSHA"]==FinalJsonSHA(metric)&&(string)baseRow["inputFP"]==(string)metric["fingerprint"],"Final base provenance mismatch "+id);
                Check((string)baseRow["qualityScope"]=="base-reduction-only"&&JToken.DeepEquals(baseRow["quality"],metric["quality"]),"Base quality was reinterpreted "+id);
                Check(FinalReductionAccepted(baseRow["quality"]),"Base reduction proof failed "+id);
                Check(PrototypeInputFingerprint((string)baseRow["source"])==(string)baseRow["inputFP"],"Current base inputs changed "+id);
                var basePrefab=AssetDatabase.LoadAssetAtPath<GameObject>((string)baseRow["prepared"]);
                Check(CachedPrototypeValid(basePrefab),"Invalid immutable base prefab "+id);
                if(basePrefab!=null)Check(JToken.DeepEquals(JObject.Parse(Settings(basePrefab.GetComponent<Wear>()).ToString(Formatting.None)),baseRow["settings"]),"Current base settings differ "+id);
                Check(FinalRowMatches((JObject)row,(string)final["inputHash"],(JObject)baseRow["settings"]),"Final actor mesh/map/settings binding failed "+id);
                string path=(string)final["prefab"];ValidateFinalPrefab(path);finalPaths.Add(path);
                Check((bool?)final["artifactValidationPassed"]==true,"Final artifact receipt missing "+id);
                foreach(var artifact in final["outputs"])Check(FinalArtifactMatches(artifact),"Final artifact bytes/GUID/dependencies changed "+artifact["path"]);
                var evidence=final["processorEvidence"];
                Check((bool?)evidence?["finalQualityPassed"]==true,"Final geometry quality was not independently accepted "+id);
                foreach(var body in (evidence?["bodies"] as JObject)?.Properties()??Enumerable.Empty<JProperty>())
                if(body.Value["topologyRefinement"] is JObject topology)
                {
                    Check(SourceCapTopologyProofAccepted(topology),"Invalid source-evidenced attachment fan/budget proof "+id+"/"+body.Name);
                    if(topology["sourceCapAnnulus"] is JObject cap)ValidateCurrentSourceCapProof(cap,body.Name);
                }
                if(evidence?["correctedHeadwearReduction"] is JObject headQuality)
                    Check(FinalReductionAccepted(headQuality),"Corrected headwear reduction failed "+id);
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);var wear=prefab.GetComponent<Wear>();
                var actualSettings=JObject.Parse(Settings(wear).ToString(Formatting.None));
                Check(JToken.DeepEquals(actualSettings,final["settings"]),"Current final settings differ "+id);
                Check(JToken.DeepEquals(FitSettingsOnly(actualSettings),FitSettingsOnly((JObject)baseRow["settings"])),"Final fitting altered authored controls "+id);
                var logical=wearRows.Where(r=>(string)r["main"]==path).Select(r=>(string)r["id"]).OrderBy(v=>v,StringComparer.Ordinal).ToArray();
                Check(logical.SequenceEqual(final["logicalIds"].Values<string>().OrderBy(v=>v,StringComparer.Ordinal)),"Final variant/prefab mapping differs "+id);
                var fits=new JArray();
                foreach(var actor in new[]{ActorName.Marta,ActorName.Kshishtof})
                {
                    string actorName=actor.ToString();var descriptor=final["meshes"][actorName];
                    string meshPath=(string)descriptor["path"];var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    Check(mesh!=null&&mesh.isReadable&&mesh.vertexCount==(int?)descriptor["vertices"]&&TriangleCount(mesh)==(long?)descriptor["triangles"],"Final body mesh/counts differ "+id+"/"+actor);
                    Check(FinalArtifactMatches(descriptor),"Final body mesh artifact changed "+id+"/"+actor);
                    if(mesh!=null)allMeshes.Add(mesh);
                    var bodyEvidence=evidence?["bodies"]?[actorName];
                    if((string)evidence?["fitKind"]=="authored-rigid-headwear")
                    {
                        Check(wear.Slots.Contains(VisualWearSlot.Head)&&wear.GetHeadwearFit().Any,"Rigid headwear receipt attached to another garment kind "+id);
                        RequireRigidHeadInfluences(mesh,prefab.GetComponentInChildren<SkinnedMeshRenderer>(true));
                        var rigidity=bodyEvidence?["rigidHeadwearProof"];
                        Check((bool?)rigidity?["passed"]==true&&(bool?)rigidity?["rigidBoneOnly"]==true&&(string)rigidity?["headBoneName"]=="head"&&
                            (bool?)rigidity?["shapeDeformationAllowed"]==false&&(float?)rigidity?["deformationMaxMetres"]<=.0002f,"Rigid authored shape/head binding proof failed "+id+"/"+actor);
                        Check((bool?)bodyEvidence?["bodyContactDiagnostic"]?["acceptance"]==false&&bodyEvidence?["surfaceEnvelope"]==null,
                            "Rigid body contacts were mislabeled as cloth clearance acceptance "+id+"/"+actor);
                    }
                    else if((string)bodyEvidence?["geometryMode"]==AuthoredDonorMode)
                    {
                        Check((string)evidence?["fitKind"]=="deformable-garment","Authored donor is a per-body admission, not a headwear bypass "+id);
                        ValidateCurrentAuthoredDonorProof(bodyEvidence,actorName,(string)baseRow["source"],(string)baseRow["donor"]);
                        var native=AssetDatabase.LoadAssetAtPath<GameObject>((string)baseRow["source"]);
                        var source=SourceMesh(native.GetComponent<Wear>(),actor,out _);NativeCopyBufferProof(source,mesh);
                    }
                    else
                    {
                    Check((string)evidence?["fitKind"]=="deformable-garment","Unknown final fit acceptance kind "+id);
                    var actualSurface=bodyEvidence?["postBindSurfaceEnvelope"];
                    Check((bool?)actualSurface?["passedSampledEnvelope"]==true&&(long?)actualSurface?["sampleCount"]==(long?)descriptor["vertices"]+13L*(long?)descriptor["triangles"]&&
                        (float?)actualSurface?["targetClearanceMetres"]==.002f&&(float?)actualSurface?["minimumSignedDistanceMetres"]>=.002f&&
                        (int?)actualSurface?["samplesBelowMinimum"]==0&&(int?)actualSurface?["insideSamples"]==0&&
                        (int?)actualSurface?["ambiguousSamples"]==0&&(int?)actualSurface?["onSurfaceSamples"]==0,
                        "Actual post-bind surface failed physical minimum "+id+"/"+actor);
                    foreach(string field in new[]{"baseToFinalDisplacement","commonBoneReferenceDisplacement"})
                        Check((string)bodyEvidence?[field]?["measurementScope"]=="all-output-vertices"&&
                            (int?)bodyEvidence?[field]?["measuredVertices"]==(int?)descriptor["vertices"]&&
                            (string)bodyEvidence?[field]?["positionScope"]=="actual-post-bind-world-vertices"&&
                            (float?)bodyEvidence?[field]?["maxMetres"]<=.12f,"Final all-vertex displacement proof failed "+id+"/"+actor+"/"+field);
                    if((string)bodyEvidence?["geometryMode"]=="revalidated-unmodified-topology")
                    {
                        Check(FinalRevalidationProvenanceAccepted(evidence?["revalidationProvenance"]),"Original producer/input provenance incomplete "+id);
                        var provenance=evidence["revalidationProvenance"];var originalInput=provenance?["originalInput"];
                        Check((string)originalInput?["id"]==id&&(string)originalInput?["baseRowSHA"]==(string)baseRow["rowSHA"]&&
                            (string)originalInput?["baseFP"]==(string)baseRow["inputFP"]&&JToken.DeepEquals(originalInput?["settings"],baseRow["settings"]),"Original revalidation input belongs to another base row "+id);
                        Check((string)originalInput?["nativeSourceDependency"]==AssetDatabase.GetAssetDependencyHash((string)metric["source"]).ToString()&&
                            (string)originalInput?["basePrefabDependency"]==AssetDatabase.GetAssetDependencyHash((string)metric["prepared"]).ToString(),"Original native/base dependency graph differs "+id);
                        Check((string)bodyEvidence?["currentGeometryRevalidation"]?["priorOriginalInputHash"]==(string)provenance?["priorOriginalInputHash"],"Current body revalidated another original input "+id);
                        Check(JToken.DeepEquals(bodyEvidence?["historicalSolverEvidence"],provenance?["originalRow"]?["final"]?["processorEvidence"]?["bodies"]?[actorName]),"Historical body does not belong to the frozen original row "+id);
                        Check(CurrentFinalRevalidationAccepted(bodyEvidence,(int)descriptor["vertices"],(long)descriptor["triangles"]),"Current independent unchanged-topology validation failed "+id);
                        Check(bodyEvidence?["surfaceEnvelope"]==null&&bodyEvidence?["topologyRefinement"]==null,"Revalidation must not fabricate a new solver attempt "+id);
                    }
                    else
                    {
                    Check((bool?)bodyEvidence?["surfaceEnvelope"]?["passed"]==true&&
                        (int?)bodyEvidence?["surfaceEnvelope"]?["after"]?["samplesBelowTargetMinusTolerance"]==0,"Final sampled surface residual failed "+id+"/"+actor);
                    Check((float?)bodyEvidence?["surfaceEnvelope"]?["minimumAcceptedClearanceMetres"]>=.002f&&
                        (float?)bodyEvidence?["surfaceEnvelope"]?["after"]?["targetClearanceMetres"]>=.002f&&
                        (float?)bodyEvidence?["surfaceEnvelope"]?["after"]?["toleranceMetres"]<=.0005f&&
                        (int?)bodyEvidence?["surfaceEnvelope"]?["after"]?["ambiguousSamples"]==0&&
                        (int?)bodyEvidence?["surfaceEnvelope"]?["after"]?["onSurfaceSamples"]==0&&
                        (int?)bodyEvidence?["surfaceEnvelope"]?["after"]?["insideSamples"]==0&&
                        (int?)bodyEvidence?["surfaceEnvelope"]?["after"]?["samplesBelowMinimum"]==0&&
                        (float?)bodyEvidence?["surfaceEnvelope"]?["after"]?["minimumSignedDistanceMetres"]>=.002f,"Final volume clearance/sign proof incomplete "+id+"/"+actor);
                    Check((bool?)bodyEvidence?["topologyRefinement"]?["passed"]==true&&
                        (long?)bodyEvidence?["topologyRefinement"]?["outputTriangles"]==(long?)descriptor["triangles"]&&
                        (int?)bodyEvidence?["topologyRefinement"]?["outputVertices"]==(int?)descriptor["vertices"],"Final adaptive geometry counts/proof mismatch "+id+"/"+actor);
                    }
                    }
                    var cleanup=bodyEvidence?["topologyRefinement"]?["collapsedZeroIslandCleanup"];
                    if(cleanup!=null)
                    {
                        var topology=bodyEvidence["topologyRefinement"];var nativeProof=cleanup["nativeActualEvidence"];
                        var baseConfig=((JArray)baseRow["settings"]["configs"]).Single(c=>(string)c["actor"]==actorName);
                        var originalMesh=AssetDatabase.LoadAssetAtPath<Mesh>((string)baseConfig["mesh"]);
                        var removed=cleanup["removedOriginalVertexIndices"]?.Values<int>().ToArray()??Array.Empty<int>();
                        var retainedIndices=cleanup["retainedOriginalVertexIndices"]?.Values<int>().ToArray()??Array.Empty<int>();
                        var zero=CollapsedZeroInfluences(originalMesh,out _,out _);
                        Check((bool?)cleanup["passed"]==true&&(bool?)cleanup["sourceUnchanged"]==true&&
                            (bool?)cleanup["keptNonSkinAttributeBytesVerifiedAfterSkinWrite"]==true&&(bool?)cleanup["fullSkinInfluencesVerifiedAfterWrite"]==true&&
                            (bool?)cleanup["allCompanionReferencesRemapped"]==true&&(bool?)cleanup["submeshSlotsAndRetainedTriangleOrderPreserved"]==true,
                            "Collapsed-island remap/attribute proof incomplete "+id);
                        Check(removed.SequenceEqual(Enumerable.Range(0,zero.Length).Where(i=>zero[i]))&&
                            retainedIndices.SequenceEqual(Enumerable.Range(0,zero.Length).Where(i=>!zero[i]))&&
                            removed.Length==(int?)cleanup["removedVertices"]&&originalMesh.vertexCount-removed.Length==(int?)topology["inputVertices"]&&
                            TriangleCount(originalMesh)-(long?)cleanup["removedTriangles"]==(long?)topology["inputTriangles"],"Collapsed-island coverage differs from actual BASE indices "+id);
                        Check((bool?)nativeProof?["passed"]==true&&(bool?)nativeProof?["actualBakeMesh"]==true&&(int?)nativeProof?["actualBakedNondegenerateFaces"]==0&&
                            (int?)nativeProof?["mixedFaces"]==0&&(bool?)nativeProof?["allUnweightedBakedLocalVerticesExactlyZero"]==true&&
                            (int?)nativeProof?["unweightedVertices"]==(int?)cleanup["removedVertices"]&&(long?)nativeProof?["allUnweightedFaces"]==(long?)cleanup["removedTriangles"]&&
                            (string)nativeProof?["equipmentFactory"]=="BodyBones.Equip"&&
                            (string)nativeProof?["nativeSource"]==(string)baseRow["source"]&&(string)nativeProof?["actor"]==(string)baseRow["donor"]&&
                            (string)nativeProof?["nativeSourceDependency"]==AssetDatabase.GetAssetDependencyHash((string)baseRow["source"]).ToString()&&
                            (string)nativeProof?["nativeMeshSha"]==FinalFileSHA((string)nativeProof?["nativeMesh"]),"Collapsed cleanup Native actual provenance differs "+id);
                    }
                    if(evidence?["correctedHeadwearReduction"] is JObject reduction)
                        Check((long?)descriptor["triangles"]==(long?)reduction["actualTriangles"],"Corrected headwear topology/receipt mismatch "+id+"/"+actor);
                    Check((string)bodyEvidence?["runtimeConstruction"]=="BodyBones.Equip","Final proof did not use actual equipment factory "+id+"/"+actor);
                    Check((float?)bodyEvidence?["reconstruction"]?["maxMetres"]<=.0002f,"Final runtime bind reconstruction failed "+id+"/"+actor);
                    long triangles=(long?)descriptor["triangles"]??0;if(actor==ActorName.Marta)totalM+=triangles;else totalK+=triangles;
                    fits.Add(new JObject{["actor"]=actorName,["mesh"]=meshPath,["vertices"]=descriptor["vertices"]?.DeepClone(),["triangles"]=triangles});
                }
                totalSource+=(long?)metric["quality"]?["inputTriangles"]??0;
                prototypes.Add(new JObject{["id"]=id,["prepared"]=path,["source"]=baseRow["source"]?.DeepClone(),["logicalIds"]=new JArray(logical),["fits"]=fits,
                    ["quality"]=new JObject{["mode"]=final["mode"]?.DeepClone(),["baseReductionOnly"]=baseRow["quality"]?.DeepClone(),["finalFitEvidence"]=evidence?.DeepClone()}});
            }
            Check(finalPaths.Count == selectedModels.Count && finalPaths.Distinct(StringComparer.Ordinal).Count() == selectedModels.Count && allMeshes.Count == selectedMeshCount, "Final actual model/mesh coverage differs");
            Check(new HashSet<string>(wearRows.Select(r=>(string)r["main"]),StringComparer.Ordinal).SetEquals(finalPaths),"Final catalog/main bijection failed");
            var maps=mapRows.Select(r=>AssetDatabase.LoadAssetAtPath<PaintPointMap>((string)r["main"])).ToArray();
            Check(maps.Length == selectedMeshCount && maps.All(m => m != null), "Final imported maps incomplete");
            if (curated)
            {
                var selectedMapPaths = rows.Properties().SelectMany(row => (JArray)row.Value["final"]["maps"])
                    .Select(row => (string)row["path"]).ToHashSet(StringComparer.Ordinal);
                Check(selectedMapPaths.Count == selectedMeshCount &&
                    selectedMapPaths.SetEquals(mapRows.Select(row => (string)row["main"])),
                    "Curated catalog must use the exact selected final paint map artifacts");
            }
            foreach(var mesh in allMeshes)Check(maps.Any(m=>m!=null&&m.MeshName==mesh.name&&m.VertexCount==mesh.vertexCount),"Final body mesh has no matching paint map "+mesh.name);
            foreach(var map in maps.Where(m=>m!=null))Check(allMeshes.Any(m=>m.name==map.MeshName&&m.vertexCount==map.VertexCount),"Final map is stale/orphaned "+map.name);
            foreach(string path in records.Select(r=>(string)r["main"]).Concat(wearRows.Select(r=>(string)r["definition"])).Distinct(StringComparer.Ordinal))
            {
                var deps=AssetDatabase.GetDependencies(path,true).OrderBy(v=>v,StringComparer.Ordinal).ToArray();
                Check(deps.All(dep=>!dep.StartsWith(SourceRoot+"/",StringComparison.Ordinal)&&!dep.StartsWith("Assets/ImportedActors/Wear/",StringComparison.Ordinal)&&
                    !dep.StartsWith("Assets/HexLiveContent/RuntimeSource/Wear/",StringComparison.Ordinal)),"Final roots retain Native dependencies "+path);
                dependencies.Add(new JObject{["root"]=path,["hash"]=AssetDatabase.GetAssetDependencyHash(path).ToString(),["paths"]=new JArray(deps)});
            }
            report["coverage"] = new JObject { ["definitions"] = selectedItems, ["uniquePrefabs"] = selectedModels.Count, ["uniqueMeshes"] = selectedMeshCount, ["paintMaps"] = selectedMeshCount };
            report["triangles"]=new JObject{["sourcePerBody"]=totalSource,["finalMarta"]=totalM,["finalKshishtof"]=totalK,
                ["reductionMartaPercent"]=100d*(1d-totalM/(double)totalSource),["reductionKshishtofPercent"]=100d*(1d-totalK/(double)totalSource)};
            if(errors.Count!=0)throw new InvalidOperationException("Final collection validation errors: "+errors.Count);
            if(Environment.GetEnvironmentVariable("HEXLIVE_LEGACY_REVIEW_GALLERY")!="0")
                report["gallery"]=RenderPreparedCollectionGallery(finalPaths.OrderBy(v=>v,StringComparer.Ordinal).ToArray(),wearRows);
            report["passed"]=true;
        }
        catch(Exception error){report["exception"]=error.ToString();throw;}
        finally
        {
            foreach(var pair in protectedFiles)Check(FinalFileSHA(pair.Key)==pair.Value,"Final validation changed protected input "+pair.Key);
            if(errors.Count!=0)report["passed"]=false;
            File.WriteAllText(reviewPath, report.ToString());
        }
        if(!(bool)report["passed"])throw new InvalidOperationException("Final validation changed protected input");
        Debug.Log("LEGACY_FINAL_PREPARED_COLLECTION_VALID: " + reviewPath);
    }
}
}
#endif

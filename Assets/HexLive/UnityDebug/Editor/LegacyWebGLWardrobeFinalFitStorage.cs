#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    const string FinalStorageSource="Assets/HexLive/UnityDebug/Editor/LegacyWebGLWardrobeFinalFitStorage.cs";
    static readonly string[] BaseGeneratorSources={"LegacyWebGLWardrobeAssets.cs","LegacyWebGLWardrobeGeometry.cs","LegacyWardrobeTriangleTree.cs","LegacyWebGLWardrobePreparation.cs","LegacyWardrobeQuality.cs"};
    public sealed class FinalFitInput : IDisposable
    {
        public readonly string Id,Donor,NativeSource,BasePrefabPath,InputHash;
        public readonly JObject BaseRow,Settings;
        internal readonly JObject PriorRevalidationRow,RevalidationProvenance;
        // Persistent prefab is READ ONLY. All processor writes belong to the
        // private template/meshes; storage guards base files and dependencies.
        public readonly GameObject BasePrefab,PrivateTemplate;
        public readonly Mesh MartaMesh,KshishtofMesh;
        public Mesh PrimaryMesh => Donor=="Kshishtof" ? KshishtofMesh : MartaMesh;
        internal FinalFitInput(string id,JObject row,string hash,JObject prior=null,JObject provenance=null)
        {
            PriorRevalidationRow=prior==null?null:(JObject)prior.DeepClone();RevalidationProvenance=provenance==null?null:(JObject)provenance.DeepClone();
            Id=id;BaseRow=(JObject)row.DeepClone();Donor=(string)row["donor"];NativeSource=(string)row["source"];
            BasePrefabPath=(string)row["prepared"];InputHash=hash;
            BasePrefab=AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath);
            Settings=FinalSettings(BasePrefab.GetComponent<Wear>());
            PrivateTemplate=Object.Instantiate(BasePrefab);UnpackFinalTemplate(PrivateTemplate);
            var configs=new SerializedObject(PrivateTemplate.GetComponent<Wear>()).FindProperty("configs");
            Mesh Clone(ActorName actor)
            {
                var found=Enumerable.Range(0,configs.arraySize).Select(i=>configs.GetArrayElementAtIndex(i))
                    .Single(c=>c.FindPropertyRelative("actorName").intValue==(int)actor);
                return Object.Instantiate((Mesh)found.FindPropertyRelative("mesh").objectReferenceValue);
            }
            MartaMesh=Clone(ActorName.Marta);KshishtofMesh=Clone(ActorName.Kshishtof);
            AssignFinalMeshes(PrivateTemplate,MartaMesh,KshishtofMesh);
        }
        public void Dispose() => DestroyFinalPrivate(PrivateTemplate,MartaMesh,KshishtofMesh);
    }
    public sealed class FinalFitProduct
    {
        public GameObject Template;
        public Mesh MartaMesh,KshishtofMesh;
        public JObject Evidence;
        public string Mode;
    }
    static string FinalSHA(byte[] bytes)
    {using var hash=SHA256.Create();return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static string FinalFileSHA(string path)
    {using var stream=File.OpenRead(path);using var hash=SHA256.Create();return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    static string FinalJsonSHA(JToken value)=>FinalSHA(Encoding.UTF8.GetBytes(value.ToString(Formatting.None)));
    static JObject FinalBaseSnapshot(JObject current)
    {
        var copy=(JObject)current.DeepClone();copy.Remove("finalFit");copy.Remove("finalFitManifest");return copy;
    }
    static string FinalSkeletonSHA(GameObject template)
    {
        string PathOf(Transform transform)=>RelativePath(template.transform,transform);
        var nodes=new JArray(template.GetComponentsInChildren<Transform>(true).OrderBy(PathOf,StringComparer.Ordinal).Select(t=>new JObject{
            ["path"]=PathOf(t),["position"]=new JArray(t.localPosition.x,t.localPosition.y,t.localPosition.z),
            ["rotation"]=new JArray(t.localRotation.x,t.localRotation.y,t.localRotation.z,t.localRotation.w),["scale"]=new JArray(t.localScale.x,t.localScale.y,t.localScale.z)}));
        var skins=new JArray(template.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(s=>new JObject{
            ["path"]=PathOf(s.transform),["rootBone"]=s.rootBone==null?"":PathOf(s.rootBone),["bones"]=new JArray(s.bones.Select(b=>b==null?"NULL":PathOf(b)))}));
        return FinalJsonSHA(new JObject{["nodes"]=nodes,["skins"]=skins});
    }
    static JObject FinalSettings(Wear wear)=>JObject.Parse(Settings(wear).ToString(Formatting.None));
    static JObject FitSettingsOnly(JObject settings)
    {
        var copy=(JObject)settings.DeepClone();
        foreach(JObject config in (JArray)copy["configs"])config.Remove("mesh");
        return copy;
    }
    static void DestroyFinalPrivate(params Object[] objects)
    {foreach(var value in new HashSet<Object>(objects))if(value!=null&&!EditorUtility.IsPersistent(value))Object.DestroyImmediate(value);}
    static void UnpackFinalTemplate(GameObject template)
    {
        if(PrefabUtility.IsPartOfPrefabInstance(template))PrefabUtility.UnpackPrefabInstance(template,PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);
        StripPlayerIncompatibleComponents(template);
    }
    static void AssignFinalMeshes(GameObject template,Mesh female,Mesh male)
    {
        var wear=template.GetComponent<Wear>();
        SetFits(wear,female,male,wear.GetConfigScale(ActorName.Marta),wear.GetConfigScale(ActorName.Kshishtof),false);
        var serialized=new SerializedObject(wear);serialized.FindProperty("preparedBodyFits").boolValue=true;serialized.ApplyModifiedPropertiesWithoutUndo();
        var skins=template.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if(skins.Length!=1)throw new InvalidDataException("Final template must have exactly one skin");
        skins[0].sharedMesh=female;
    }
    static JObject FinalArtifact(string path)
    {
        var value=AssetDatabase.LoadMainAssetAtPath(path);
        if(value==null||!File.Exists(path)||!File.Exists(path+".meta"))throw new InvalidDataException("Missing final artifact "+path);
        return new JObject{["path"]=path,["guid"]=AssetDatabase.AssetPathToGUID(path),["sha"]=FinalFileSHA(path),
            ["metaSha"]=FinalFileSHA(path+".meta"),["dependencyHash"]=AssetDatabase.GetAssetDependencyHash(path).ToString()};
    }
    static bool FinalArtifactMatches(JToken row)
    {
        string path=(string)row["path"];
        return !string.IsNullOrEmpty(path)&&path.StartsWith(PreparedRoot+"/",StringComparison.Ordinal)&&File.Exists(path)&&File.Exists(path+".meta")&&
            FinalFileSHA(path)==(string)row["sha"]&&FinalFileSHA(path+".meta")== (string)row["metaSha"]&&
            AssetDatabase.AssetPathToGUID(path)==(string)row["guid"]&&AssetDatabase.GetAssetDependencyHash(path).ToString()==(string)row["dependencyHash"];
    }
    static void ValidateFinalPrefab(string path)
    {
        if(!path.StartsWith(PreparedRoot+"/Prefabs/FinalFit/",StringComparison.Ordinal)||!CachedPrototypeValid(AssetDatabase.LoadAssetAtPath<GameObject>(path)))
            throw new InvalidDataException("Invalid actual final prefab "+path);
        var wear=AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Wear>();
        var configs=new SerializedObject(wear).FindProperty("configs");
        for(int i=0;i<configs.arraySize;i++)
        {
            string mesh=AssetDatabase.GetAssetPath(configs.GetArrayElementAtIndex(i).FindPropertyRelative("mesh").objectReferenceValue);
            if(!mesh.StartsWith(PreparedRoot+"/Meshes/FinalFit/",StringComparison.Ordinal))throw new InvalidDataException("Final prefab retained a base mesh "+path);
        }
        foreach(string dep in AssetDatabase.GetDependencies(path,true))
            if(dep.StartsWith(SourceRoot+"/",StringComparison.Ordinal)||dep.StartsWith("Assets/HexLiveContent/RuntimeSource/Wear/",StringComparison.Ordinal)||
                dep.StartsWith("Assets/ImportedActors/Wear/",StringComparison.Ordinal)||
                dep.StartsWith("Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/",StringComparison.Ordinal)||
                (dep.StartsWith(PreparedRoot+"/Prefabs/",StringComparison.Ordinal)&&dep!=path))
                throw new InvalidDataException("Forbidden final dependency "+dep);
    }
    static bool FinalQualityAccepted(JToken evidence)=>evidence is JObject value &&
        value["finalQualityPassed"]?.Type==JTokenType.Boolean && (bool)value["finalQualityPassed"];
    static bool FinalRowMatches(JObject row,string inputHash,JObject baseSettings)
    {
        try
        {
            if((string)row?["final"]?["inputHash"]!=inputHash||!FinalQualityAccepted(row?["final"]?["processorEvidence"]))return false;
            var final=row["final"];
            var geometry=final["processorEvidence"];var revalidated=(geometry["bodies"] as JObject)?.Properties().Where(p=>(string)p.Value["geometryMode"]=="revalidated-unmodified-topology").ToArray();
            foreach(var body in (geometry["bodies"] as JObject)?.Properties()??Enumerable.Empty<JProperty>())
            {
                if (!ReikoCuffOpeningProofAccepted(body.Value, body.Name, (string)row["base"]?["source"], (string)row["base"]?["donor"]) ||
                    !GlovesOpeningProofAccepted(body.Value, body.Name, (string)row["base"]?["source"], (string)row["base"]?["donor"]))
                    return false;
                if(body.Value["topologyRefinement"] is JObject topology)
                {
                    if(!SourceCapTopologyProofAccepted(topology))return false;
                    if(topology["sourceCapAnnulus"] is JObject cap&&!SourceCapTransferAccepted(cap,body.Name,(string)row["base"]?["donor"]))return false;
                }
            }
            if(revalidated!=null&&revalidated.Length>0)
            {
                var provenance=geometry["revalidationProvenance"];
                if(!FinalRevalidationProvenanceAccepted(provenance))return false;
                foreach(var body in revalidated)
                {
                    var descriptor=final["meshes"]?[body.Name];
                    if(descriptor==null||!CurrentFinalRevalidationAccepted(body.Value,(int)descriptor["vertices"],(long)descriptor["triangles"])||
                        !JToken.DeepEquals(body.Value["historicalSolverEvidence"],provenance["originalRow"]["final"]["processorEvidence"]["bodies"][body.Name]))return false;
                }
            }
            if(!(final["outputs"] is JArray outputs)||outputs.Count!=5||outputs.Any(output=>!FinalArtifactMatches(output)))return false;
            string path=(string)final["prefab"];ValidateFinalPrefab(path);
            var loaded=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if(FinalSkeletonSHA(loaded)!=(string)final["skeletonSHA"]||(string)row["base"]?["skeletonSHA"]!=(string)final["skeletonSHA"])return false;
            var actual=FinalSettings(loaded.GetComponent<Wear>());
            var expectedPaths=new HashSet<string>(StringComparer.Ordinal){path};
            foreach(string actor in new[]{"Marta","Kshishtof"})
            {
                var descriptor=final["meshes"]?[actor];
                if(descriptor==null||!FinalArtifactMatches(descriptor))return false;
                string meshPath=(string)descriptor["path"];expectedPaths.Add(meshPath);
                var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if(mesh==null||mesh.vertexCount!=(int?)descriptor["vertices"]||TriangleCount(mesh)!=(long?)descriptor["triangles"])return false;
                var body=geometry["bodies"]?[actor];
                if((string)body?["geometryMode"]==AuthoredDonorMode)
                {
                    ValidateCurrentAuthoredDonorProof(body,actor,(string)row["base"]["source"],(string)row["base"]["donor"]);
                    var native=AssetDatabase.LoadAssetAtPath<GameObject>((string)row["base"]["source"]);
                    var source=SourceMesh(native.GetComponent<Wear>(),(ActorName)Enum.Parse(typeof(ActorName),actor),out _);
                    NativeCopyBufferProof(source,mesh);
                }
                if(!((JArray)actual["configs"]).Any(c=>(string)c["actor"]==actor&&(string)c["mesh"]==meshPath))return false;
            }
            if(!(final["maps"] is JArray maps)||maps.Count!=2)return false;
            foreach(var descriptor in maps)
            {
                if(!FinalArtifactMatches(descriptor))return false;
                string mapPath=(string)descriptor["path"];expectedPaths.Add(mapPath);
                var map=AssetDatabase.LoadAssetAtPath<PaintPointMap>(mapPath);
                if(map==null||map.name!=Path.GetFileNameWithoutExtension(mapPath)||map.Zones.Length!=7||!map.Zones.Any(z=>z.Points.Any(p=>p.Valid)))return false;
                if(!final["meshes"].Children<JProperty>().Any(pair=>{var mesh=AssetDatabase.LoadAssetAtPath<Mesh>((string)pair.Value["path"]);return mesh.name==map.MeshName&&mesh.vertexCount==map.VertexCount;}))return false;
            }
            if(!expectedPaths.SetEquals(outputs.Select(o=>(string)o["path"])))return false;
            return JToken.DeepEquals(actual,final["settings"])&&JToken.DeepEquals(FitSettingsOnly(actual),FitSettingsOnly(baseSettings));
        }
        catch{return false;}
    }
    static string FinalBaseReportPath()=>PreparedRoot+"/base-prepared-all.json";
    static byte[] ReadFinalBaseReport(bool currentFinal,string diagnosticPath,string snapshotPath,string committedSHA)
    {
        if(currentFinal)
        {
            if(!File.Exists(snapshotPath)||string.IsNullOrEmpty(committedSHA)||FinalFileSHA(snapshotPath)!=committedSHA)
                throw new InvalidDataException("Committed owned base report missing or changed");
            return File.ReadAllBytes(snapshotPath); // Never depend on ignored Build for resume.
        }
        if(File.Exists(diagnosticPath))return File.ReadAllBytes(diagnosticPath);
        if(File.Exists(snapshotPath))return File.ReadAllBytes(snapshotPath);
        throw new InvalidDataException("No completed base report/snapshot available");
    }
    static void AtomicFinalBytes(string path,byte[] bytes)
    {
        // A short-lived reader/asset importer on Windows may deny DELETE sharing.
        // Retry the atomic operation only: never truncate or remove the old receipt.
        string temp=path+".finalfit-"+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllBytes(temp,bytes);
        try
        {
            for(int attempt=0;;attempt++)
            {
                try
                {
                    if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
                    return;
                }
                catch(IOException) when(attempt<9 && File.Exists(temp))
                {System.Threading.Thread.Sleep(Math.Min(25*(attempt+1),250));}
            }
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
    static void AtomicFinalText(string path,string text)=>AtomicFinalBytes(path,Encoding.UTF8.GetBytes(text));
    static void SaveFinalMesh(Mesh source,string name,string path)
    {
        if(source==null||EditorUtility.IsPersistent(source))throw new InvalidDataException("Processor must return private mesh objects");
        var copy=Object.Instantiate(source);copy.name=name;var saved=Save(copy,path);AssetDatabase.SaveAssetIfDirty(saved);
        byte[] before=File.ReadAllBytes(path),after=ReadableMeshBytes(before);
        if(!before.SequenceEqual(after)){File.WriteAllBytes(path,after);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);}
        var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if(mesh==null||!mesh.isReadable||!File.ReadAllBytes(path).SequenceEqual(after))throw new InvalidDataException("Final mesh import/readability failed "+path);
    }

    // A rejected garment must not hide later failures or discard successful
    // immutable output rows. The failure callback verifies protected inputs;
    // if that callback fails, continuation is unsafe and the batch aborts.
    static void RunFinalFitBatch(IEnumerable<JToken> metrics,Action<JToken> process,Action<JToken,Exception> failure)
    {
        foreach(var metric in metrics)
        {
            try { process(metric); }
            catch(Exception error) { failure(metric,error); }
        }
    }

    static void VerifyFinalPinnedBytes(IEnumerable<KeyValuePair<string,string>> files)
    {
        foreach(var pair in files)
            if(!File.Exists(pair.Key)||FinalFileSHA(pair.Key)!=pair.Value)
                throw new InvalidDataException("Immutable base bytes changed "+pair.Key);
    }
    static void GuardFinalInputs(Action verify,JObject provisional,string provisionalPath)
    {
        try { verify(); }
        catch(Exception error)
        {
            if(provisional!=null)
            {
                provisional["invalidated"]=true;provisional["immutableInputFailure"]=error.ToString();
                AtomicFinalText(provisionalPath,provisional.ToString());
            }
            throw;
        }
    }

    static JObject FinalProcessorInput(string version,JObject metric,JObject baseSettings,JArray processorSources,Dictionary<string,string> baseDeps)
    {
        string id=(string)metric["id"],basePrefab=(string)metric["prepared"];
        return new JObject{["version"]=version,["id"]=id,["baseRowSHA"]=FinalJsonSHA(metric),["baseFP"]=metric["fingerprint"]?.DeepClone(),
                ["nativeSourceDependency"]=baseDeps[(string)metric["source"]],["basePrefabDependency"]=baseDeps[basePrefab],["settings"]=baseSettings.DeepClone(),
                ["sources"]=processorSources.DeepClone(),["bodyDependencies"]=new JArray(FittingUnderlayerPaths.Concat(new[]{PeopleRoot+"/Prefabs/Actors/Marta.prefab",PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"})
                    .Select(path=>new JObject{["path"]=path,["hash"]=baseDeps[path]})),
                ["baseMeshes"]=new JArray(((JArray)baseSettings["configs"]).Select(c=>FinalArtifact((string)c["mesh"])))};
    }

    public static void BuildFinalFitCollection(string version,string[] sourcePaths,Func<FinalFitInput,FinalFitProduct> processor)
    {
        if(string.IsNullOrWhiteSpace(version)||sourcePaths==null||processor==null)throw new ArgumentException("Version, source paths and processor required");
        string catalogPath=PreparedRoot+"/catalog.json",snapshotPath=PreparedRoot+"/base-catalog.json",cachePath=PreparedRoot+"/preparation.json";
        string fullPath=FinalBaseReportPath(),diagnosticFullPath=OutputRoot+"/prepared-all.json",provisionalPath=PreparedRoot+"/final-fit-cache.json",manifestPath=PreparedRoot+"/final-fit.json";
        byte[] originalCatalog=File.ReadAllBytes(catalogPath);byte[] originalManifest=File.Exists(manifestPath)?File.ReadAllBytes(manifestPath):null;
        var current=JObject.Parse(Encoding.UTF8.GetString(originalCatalog));
        bool currentFinal=current["records"].Where(r=>(string)r["type"]=="wear").Any(r=>((string)r["main"]).Contains("/Prefabs/FinalFit/"));
        JObject baseCatalog;
        JObject committedManifest = null;
        string committedReportSHA=null;
        if(currentFinal)
        {
            if(!File.Exists(snapshotPath)||originalManifest==null)throw new InvalidDataException("Final catalog requires its explicit committed base snapshot");
            var previous=JObject.Parse(Encoding.UTF8.GetString(originalManifest));
            if((bool?)previous["passed"]!=true||(bool?)previous["catalogPromoted"]!=true||FinalSHA(originalCatalog)!=(string)previous["finalCatalog"]?["sha"])
                throw new InvalidDataException("Current final catalog differs from its committed manifest");
            if(FinalFileSHA(snapshotPath)!=(string)previous["baseGeneration"]?["stagedCatalogSHA"])throw new InvalidDataException("Base catalog snapshot changed");
            if((string)previous["baseGeneration"]?["fullReportPath"]!=fullPath)throw new InvalidDataException("Final manifest needs a committed owned base report path");
            committedReportSHA=(string)previous["baseGeneration"]?["fullReportSHA"];
            committedManifest = previous;
            baseCatalog=JObject.Parse(File.ReadAllText(snapshotPath));
        }
        else
        {
            // PrepareCollection resets records but retains unknown top-level keys.
            baseCatalog=FinalBaseSnapshot(current);
            AtomicFinalText(snapshotPath,baseCatalog.ToString());
        }
        var baseCache=JObject.Parse(File.ReadAllText(cachePath));
        byte[] completedBytes=ReadFinalBaseReport(currentFinal,diagnosticFullPath,fullPath,committedReportSHA);
        var completed=JObject.Parse(Encoding.UTF8.GetString(completedBytes).TrimStart('\uFEFF'));
        var baseWear=((JArray)baseCatalog["records"]).Where(r=>(string)r["type"]=="wear").ToArray();
        var metrics=((JArray)completed["prototypes"]).Cast<JObject>().ToArray();
        if(baseWear.Length!=660||metrics.Length!=183||baseWear.Any(r=>((string)r["main"]).Contains("/FinalFit/")))throw new InvalidDataException("Expected immutable660/183 base collection");
        if(!new HashSet<string>(baseWear.Select(r=>(string)r["main"]),StringComparer.Ordinal).SetEquals(metrics.Select(r=>(string)r["prepared"])))throw new InvalidDataException("Base catalog/full-run coverage differs");
        var baseFiles=new Dictionary<string,string>(StringComparer.Ordinal);
        var baseDeps=new Dictionary<string,string>(StringComparer.Ordinal);
        void Pin(string path)
        {if(baseFiles.ContainsKey(path))return;
         if(File.Exists(path)){baseFiles[path]=FinalFileSHA(path);if(File.Exists(path+".meta"))baseFiles[path+".meta"]=FinalFileSHA(path+".meta");}}
        Pin(cachePath);Pin(snapshotPath);Pin(PeopleRoot+"/catalog.json");Pin(PreparedRoot+"/texture-fingerprints.json");
        var sources=sourcePaths.Concat(BaseGeneratorSources.Select(n=>"Assets/HexLive/UnityDebug/Editor/"+n)).Append(FinalStorageSource).Distinct(StringComparer.Ordinal).OrderBy(v=>v,StringComparer.Ordinal).ToArray();
        var processorSources=new JArray(sources.Select(path=>new JObject{["path"]=path,["sha"]=FinalFileSHA(path)}));
        foreach(string path in sources)Pin(path);
        var inputPaths=baseWear.Select(r=>(string)r["main"]).Concat(baseWear.Select(r=>(string)r["definition"]))
            .Concat(baseCatalog["records"].Where(r=>(string)r["type"]=="config").Select(r=>(string)r["main"]))
            .Concat(metrics.Select(r=>(string)r["source"])).Concat(FittingUnderlayerPaths)
            .Concat(new[]{PeopleRoot+"/Prefabs/Actors/Marta.prefab",PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"}).Distinct(StringComparer.Ordinal).ToArray();
        foreach(string path in inputPaths)
        {
            baseDeps[path]=AssetDatabase.GetAssetDependencyHash(path).ToString();
            foreach(string dep in AssetDatabase.GetDependencies(path,true))Pin(dep);
        }
        string simplifier=SimplifierInputFingerprint();
        foreach(var metric in metrics)
        {
            string id=(string)metric["id"],prepared=(string)metric["prepared"];
            if(!JToken.DeepEquals(baseCache[id],metric)||(string)metric["fingerprint"]!=PrototypeInputFingerprint((string)metric["source"])||
                !CachedPrototypeValid(AssetDatabase.LoadAssetAtPath<GameObject>(prepared)))throw new InvalidDataException("Base input/cache artifact mismatch "+id);
            var actual=FinalSettings(AssetDatabase.LoadAssetAtPath<GameObject>(prepared).GetComponent<Wear>());
            if(!JToken.DeepEquals(actual,metric["settings"]))throw new InvalidDataException("Base authored settings changed "+id);
        }
        // Freeze exact generation-report bytes only after metrics/sourceFP/
        // settings/coverage have all matched this immutable base generation.
        if(!currentFinal)AtomicFinalBytes(fullPath,completedBytes);
        Pin(fullPath);
        var generation=new JObject{["algorithm"]=completed["algorithm"]?.DeepClone(),["cachePath"]=cachePath,["cacheSHA"]=FinalFileSHA(cachePath),
            ["fullReportPath"]=fullPath,["fullReportSHA"]=FinalFileSHA(fullPath),["stagedCatalogSnapshot"]=snapshotPath,["stagedCatalogSHA"]=FinalFileSHA(snapshotPath),
            ["baseFiles"]=new JArray(baseFiles.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new JObject{["path"]=p.Key,["sha"]=p.Value}))};
        // Diagnostic closure freezes the ACTUAL current bytes for the final Git
        // roundtrip/EOL audit. It never rewrites inputs or supplies replacement FPs.
        Directory.CreateDirectory(OutputRoot);
        AtomicFinalText(OutputRoot+"/final-fit-input-closure.json",new JObject{["generation"]=generation.DeepClone(),
            ["processorSources"]=processorSources.DeepClone(),["capturedUtc"]=DateTime.UtcNow.ToString("O")}.ToString());
        JObject provisional=null;
        void VerifyBase()
        {
            GuardFinalInputs(() =>
            {
                VerifyFinalPinnedBytes(baseFiles);
                foreach(var pair in baseDeps)if(AssetDatabase.GetAssetDependencyHash(pair.Key).ToString()!=pair.Value)throw new InvalidDataException("Immutable base dependency changed "+pair.Key);
                if(SimplifierInputFingerprint()!=simplifier)throw new InvalidDataException("Simplifier input changed");
            },provisional,provisionalPath);
        }
        void VerifyRow(JObject metric)
        {
            try
            {
                GuardFinalInputs(() =>
                {
                    foreach(var source in processorSources)
                        if(FinalFileSHA((string)source["path"])!=(string)source["sha"])throw new InvalidDataException("Final processor changed before cache admission "+source["path"]);
                    foreach(string path in new[]{(string)metric["source"],(string)metric["prepared"]}.Concat(FittingUnderlayerPaths)
                        .Concat(new[]{PeopleRoot+"/Prefabs/Actors/Marta.prefab",PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"}))
                        if(AssetDatabase.GetAssetDependencyHash(path).ToString()!=baseDeps[path])throw new InvalidDataException("Final row dependency changed before cache admission "+path);
                    if(FinalFileSHA(cachePath)!=baseFiles[cachePath]||PrototypeInputFingerprint((string)metric["source"])!=(string)metric["fingerprint"])
                        throw new InvalidDataException("Final base row inputs changed before cache admission "+metric["id"]);
                },provisional,provisionalPath);
            }
            catch { VerifyBase(); throw; } // Fatal quick-guard failure also audits the complete byte closure.
        }
        var existing = File.Exists(provisionalPath)
            ? JObject.Parse(File.ReadAllText(provisionalPath))
            : committedManifest ?? new JObject();
        var revalidation=LoadFinalRevalidationContext();
        var oldRows=(bool?)existing["invalidated"]==true?new JObject():existing["rows"] as JObject??new JObject();var rows=new JObject();
        var failures=new JArray();
        provisional=new JObject{["schemaVersion"]=1,["stage"]="final-fit-provisional",["catalogPromoted"]=false,["rows"]=rows,["failures"]=failures,["invalidated"]=false,["immutableBatchVerified"]=false};
        foreach(string folder in new[]{"Meshes/FinalFit","Prefabs/FinalFit","PaintMaps/FinalFit"})Directory.CreateDirectory(PreparedRoot+"/"+folder);
        AssetDatabase.Refresh();
        RunFinalFitBatch(metrics.OrderBy(r=>(string)r["id"],StringComparer.Ordinal), token =>
        {
            var metric=(JObject)token;
            VerifyRow(metric);
            string id=(string)metric["id"],basePrefab=(string)metric["prepared"];
            var baseAsset=AssetDatabase.LoadAssetAtPath<GameObject>(basePrefab);
            var baseSettings=FinalSettings(baseAsset.GetComponent<Wear>());string skeletonSHA=FinalSkeletonSHA(baseAsset);
            var input=FinalProcessorInput(version,metric,baseSettings,processorSources,baseDeps);
            string inputHash=FinalJsonSHA(input),fitKey=inputHash.Substring(0,24),key=Key(id);
            var retained=oldRows[id] as JObject;
            if(FinalRowMatches(retained,inputHash,baseSettings))rows[id]=retained.DeepClone();
            else
            {
                var prior=ClaimFinalRevalidation(revalidation,input,metric,baseSettings,out JObject priorProvenance);
                using var work=new FinalFitInput(id,metric,inputHash,prior,priorProvenance);FinalFitProduct product=null;
                try
                {
                    product=processor(work);
                    if(product?.Template==null||product.MartaMesh==null||product.KshishtofMesh==null||product.Evidence==null||string.IsNullOrEmpty(product.Mode))throw new InvalidDataException("Incomplete final product "+id);
                    if(EditorUtility.IsPersistent(product.Template)||EditorUtility.IsPersistent(product.MartaMesh)||EditorUtility.IsPersistent(product.KshishtofMesh))throw new InvalidDataException("Final product must be private "+id);
                    if(!FinalQualityAccepted(product.Evidence))throw new InvalidDataException("Processor rejected final fit "+id);
                    UnpackFinalTemplate(product.Template);
                    if(FinalSkeletonSHA(product.Template)!=skeletonSHA)throw new InvalidDataException("Processor altered the base skeleton/local transforms "+id);
                    if(!JToken.DeepEquals(FitSettingsOnly(FinalSettings(product.Template.GetComponent<Wear>())),FitSettingsOnly(baseSettings)))throw new InvalidDataException("Processor altered authored settings "+id);
                    string femalePath=PreparedRoot+"/Meshes/FinalFit/"+key+"_"+fitKey+"_Marta.asset",malePath=PreparedRoot+"/Meshes/FinalFit/"+key+"_"+fitKey+"_Kshishtof.asset";
                    SaveFinalMesh(product.MartaMesh,"F_"+fitKey+"_Marta",femalePath);SaveFinalMesh(product.KshishtofMesh,"F_"+fitKey+"_Kshishtof",malePath);
                    var female=AssetDatabase.LoadAssetAtPath<Mesh>(femalePath);var male=AssetDatabase.LoadAssetAtPath<Mesh>(malePath);
                    AssignFinalMeshes(product.Template,female,male);
                    string prefabPath=PreparedRoot+"/Prefabs/FinalFit/"+key+"_"+fitKey+".prefab";
                    PrefabUtility.SaveAsPrefabAsset(product.Template,prefabPath);ValidateFinalPrefab(prefabPath);
                    string[] maps=PaintPointMapGenerator.GenerateWebGLGarmentMaps(new[]{prefabPath},PreparedRoot+"/PaintMaps/FinalFit");
                    if(maps.Length!=2||maps.Distinct(StringComparer.Ordinal).Count()!=2)throw new InvalidDataException("Expected two unique actual final maps "+id);
                    var meshRows=new JObject();
                    foreach(var tuple in new[]{(ActorName.Marta,femalePath),(ActorName.Kshishtof,malePath)})
                    {var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(tuple.Item2);var artifact=FinalArtifact(tuple.Item2);artifact["vertices"]=mesh.vertexCount;artifact["triangles"]=TriangleCount(mesh);meshRows[tuple.Item1.ToString()]=artifact;}
                    var outputSettings=FinalSettings(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<Wear>());
                    var final=new JObject{["version"]=version,["inputHash"]=inputHash,["prefab"]=prefabPath,["skeletonSHA"]=skeletonSHA,["meshes"]=meshRows,["settings"]=outputSettings,
                        ["mode"]=product.Mode,["processorEvidence"]=product.Evidence.DeepClone(),["maps"]=new JArray(maps.Select(FinalArtifact)),
                        ["outputs"]=new JArray(new[]{femalePath,malePath,prefabPath}.Concat(maps).Select(FinalArtifact)),["artifactValidationPassed"]=true,
                        ["logicalIds"]=new JArray(baseWear.Where(r=>(string)r["main"]==basePrefab).Select(r=>(string)r["id"]))};
                    rows[id]=new JObject{["base"]=new JObject{["rowSHA"]=FinalJsonSHA(metric),["inputFP"]=metric["fingerprint"]?.DeepClone(),["qualityScope"]="base-reduction-only",["quality"]=metric["quality"]?.DeepClone(),
                        ["prepared"]=basePrefab,["source"]=metric["source"]?.DeepClone(),["skeletonSHA"]=skeletonSHA,["settings"]=baseSettings.DeepClone(),["donor"]=metric["donor"]?.DeepClone()},["final"]=final};
                }
                finally {if(product!=null)DestroyFinalPrivate(product.Template,product.MartaMesh,product.KshishtofMesh);}
            }
            VerifyRow(metric); // Small current input/source guards; full byte closure is verified at batch end.
            AtomicFinalText(provisionalPath,provisional.ToString());
            Debug.Log("LEGACY_FINAL_FIT_STORED "+id+" ("+rows.Count+"/183)");
        }, (metric,error) =>
        {
            // Expected geometry rejection needs the same small current-row guard.
            // Full byte closure still gates every batch end (including rejected batches).
            VerifyRow((JObject)metric);
            failures.Add(new JObject{["id"]=metric["id"]?.DeepClone(),["error"]=error.ToString(),["utc"]=DateTime.UtcNow.ToString("O")});
            AtomicFinalText(provisionalPath,provisional.ToString());
            Debug.LogWarning("LEGACY_FINAL_FIT_REJECTED "+metric["id"]+"; retained successful rows="+rows.Count);
        });
        VerifyBase();
        provisional["immutableBatchVerified"]=true;AtomicFinalText(provisionalPath,provisional.ToString());
        if(failures.Count!=0)throw new InvalidDataException("Final collection has "+failures.Count+" rejected models; successful rows retained, catalog NOT promoted. Inspect "+provisionalPath);
        var finalCatalog=(JObject)baseCatalog.DeepClone();var records=(JArray)finalCatalog["records"];
        foreach(var record in records.Where(r=>(string)r["type"]=="config"&&((string)r["main"]).StartsWith(PreparedRoot+"/PaintMaps/",StringComparison.Ordinal)).ToArray())record.Remove();
        foreach(var row in rows.Properties())
        {
            var stored=(JObject)row.Value;var final=stored["final"];
            if(!FinalRowMatches(stored,(string)final["inputHash"],(JObject)stored["base"]["settings"]))throw new InvalidDataException("Final output changed before promotion "+row.Name);
            foreach(var wear in records.Where(r=>(string)r["type"]=="wear"&&(string)r["main"]==(string)stored["base"]["prepared"]))wear["main"]=final["prefab"].DeepClone();
            foreach(var map in final["maps"])
            {
                string path=(string)map["path"],name=Path.GetFileNameWithoutExtension(path);
                records.Add(new JObject{["type"]="config",["id"]=("PaintMaps."+name).ToLowerInvariant(),["main"]=path,["metadata"]=new JObject{
                    ["peopleCatalog"]=baseCatalog["peopleCatalog"]?.DeepClone(),["legacyWardrobe"]=true,["legacyResourcePath"]="HexLive/PaintMaps/"+name,["legacyResourceFolder"]="HexLive/PaintMaps"}});
            }
        }
        if(rows.Count!=183||records.Count(r=>(string)r["type"]=="wear")!=660||records.Count(r=>(string)r["type"]=="config")!=366||records.Select(r=>(string)r["id"]).Distinct(StringComparer.Ordinal).Count()!=records.Count)
            throw new InvalidDataException("Incomplete final registry coverage");
        finalCatalog["finalFit"]=true;finalCatalog["finalFitManifest"]=manifestPath;
        finalCatalog["contentOrigin"] = "modern";
        foreach (var record in records)
        {
            record["metadata"]["finalFit"] = true;
            record["metadata"]["contentOrigin"] = "modern";
        }
        if((string)finalCatalog["stage"]!="prepared-only"||(bool?)finalCatalog["gameReady"]!=false||(bool?)finalCatalog["bundleBuildAuthorized"]!=false)
            throw new InvalidDataException("Final catalog must retain staged-only authorization");
        string finalText=finalCatalog.ToString();
        var manifest=new JObject{["schemaVersion"]=1,["stage"]="final-fit-prepared-only",["passed"]=true,["catalogPromoted"]=true,
            ["editorValidationContext"]=new JObject{["unityVersion"]=Application.unityVersion,["activeBuildTarget"]=EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["dependencyHashesPlatformScoped"]=true,["futureWebGLRequiresExplicitPlatformScopedValidation"]=true},
            ["processor"]=new JObject{["version"]=version,["sources"]=processorSources},["baseGeneration"]=generation,["rows"]=rows,
            ["finalCatalog"]=new JObject{["path"]=catalogPath,["sha"]=FinalSHA(Encoding.UTF8.GetBytes(finalText))},
            ["counts"]=new JObject{["models"]=183,["logicalIds"]=660,["meshes"]=366,["maps"]=366},["bundlesBuilt"]=false,["productionChanged"]=false};
        VerifyBase();
        try {AtomicFinalText(manifestPath,manifest.ToString());AtomicFinalText(catalogPath,finalText);VerifyBase();}
        catch
        {
            AtomicFinalText(catalogPath,Encoding.UTF8.GetString(originalCatalog));
            if(originalManifest!=null)AtomicFinalText(manifestPath,Encoding.UTF8.GetString(originalManifest));else if(File.Exists(manifestPath))File.Delete(manifestPath);
            throw;
        }
        Debug.Log("LEGACY_FINAL_FIT_COLLECTION: 183models/660logical/366meshes/366maps; "+manifestPath);
    }
}
}
#endif

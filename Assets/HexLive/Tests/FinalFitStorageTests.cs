#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class FinalFitStorageTests
{
    static Type Storage=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
    static object Call(string method,params object[] args)=>Storage.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
    [TestCase("null",false)] [TestCase("{}",false)] [TestCase("{\"passed\":true}",false)]
    [TestCase("true",false)] [TestCase("1",false)] [TestCase("\"value\"",false)]
    [TestCase("{\"finalQualityPassed\":\"true\"}",false)] [TestCase("{\"finalQualityPassed\":1}",false)]
    [TestCase("{\"finalQualityPassed\":false}",false)] [TestCase("{\"finalQualityPassed\":true}",true)]
    public void FinalQualityRequiresExplicitNewFinalProof(string json,bool expected)
    {Assert.That((bool)Call("FinalQualityAccepted",JToken.Parse(json)),Is.EqualTo(expected));}

    [Test] public void FreshBaseSnapshotRemovesStaleRoutingOnlyWithoutChangingAuthorizationsOrRecords()
    {
        var current=JObject.Parse("{\"finalFit\":true,\"finalFitManifest\":\"old\",\"stage\":\"prepared-only\",\"gameReady\":false,\"bundleBuildAuthorized\":false,\"records\":[{\"id\":\"one\",\"main\":\"base\",\"metadata\":{\"legacyWardrobe\":true}}]}");
        var snapshot=(JObject)Call("FinalBaseSnapshot",current);
        Assert.That(snapshot.Property("finalFit"),Is.Null);Assert.That(snapshot.Property("finalFitManifest"),Is.Null);
        Assert.That((bool)current["finalFit"],Is.True);Assert.That(JToken.DeepEquals(snapshot["records"],current["records"]),Is.True);
        foreach(string key in new[]{"stage","gameReady","bundleBuildAuthorized"})Assert.That(JToken.DeepEquals(snapshot[key],current[key]),Is.True);
    }
    [Test] public void ActualSingleSettingsCanonicalizeLikePersistedDoubleAndHash()
    {
        var go=new GameObject("float-settings");
        try
        {
            var wear=go.AddComponent<Wear>();wear.SetConfigScale(ActorName.Marta,1.03f);wear.SetConfigScale(ActorName.Kshishtof,1.08f);wear.SetConfigHeight(ActorName.Marta,.013f);
            var actual=(JObject)Call("FinalSettings",wear);var persisted=JObject.Parse(actual.ToString(Formatting.None));
            Assert.That(JToken.DeepEquals(actual,persisted),Is.True);
            Assert.That((string)Call("FinalJsonSHA",actual),Is.EqualTo((string)Call("FinalJsonSHA",persisted)));
            Assert.That((double)actual["configs"][0]["scale"],Is.EqualTo(1.03d));
        }
        finally{Object.DestroyImmediate(go);}
    }
    [Test] public void SettingsComparisonIgnoresOnlyMeshPaths()
    {
        var original=JObject.Parse("{\"configs\":[{\"actor\":\"Marta\",\"scale\":1.03,\"heightOffset\":0.013,\"mesh\":\"base\"}],\"layer\":\"Wear\",\"hideWearSlots\":[\"Chest\"]}");
        var changed=(JObject)original.DeepClone();changed["configs"][0]["mesh"]="final";
        Assert.That(JToken.DeepEquals((JObject)Call("FitSettingsOnly",original),(JObject)Call("FitSettingsOnly",changed)),Is.True);
        changed["configs"][0]["heightOffset"]=.02d;
        Assert.That(JToken.DeepEquals((JObject)Call("FitSettingsOnly",original),(JObject)Call("FitSettingsOnly",changed)),Is.False);
        Assert.That((string)original["configs"][0]["mesh"],Is.EqualTo("base"));
    }
    [Test] public void ActualBaseInputOwnsDistinctPrivateClonesAndDisposePreservesAssets()
    {
        var cache=JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/Wardrobe/preparation.json"));var row=(JObject)cache["clothing.skirt_anarchy"];
        string prefab=(string)row["prepared"];byte[] before=File.ReadAllBytes(prefab);
        var type=Storage.GetNestedType("FinalFitInput",BindingFlags.Public);
        var work = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { "clothing.skirt_anarchy", row, "test-input", null, null }, null);
        GameObject template=null;Mesh female=null,male=null;
        try
        {
            var asset=(GameObject)type.GetField("BasePrefab").GetValue(work);template=(GameObject)type.GetField("PrivateTemplate").GetValue(work);
            female=(Mesh)type.GetField("MartaMesh").GetValue(work);male=(Mesh)type.GetField("KshishtofMesh").GetValue(work);
            Assert.That(EditorUtility.IsPersistent(asset),Is.True);Assert.That(EditorUtility.IsPersistent(template),Is.False);
            Assert.That(EditorUtility.IsPersistent(female),Is.False);Assert.That(EditorUtility.IsPersistent(male),Is.False);
            Assert.That(female==male,Is.False);Assert.That(template==asset,Is.False);
            Assert.That(JToken.DeepEquals((JObject)type.GetField("Settings").GetValue(work),JObject.Parse(row["settings"].ToString())),Is.True);
        }
        finally{work.Dispose();}
        Assert.That(template==null,Is.True);Assert.That(female==null,Is.True);Assert.That(male==null,Is.True);
        Assert.That(File.ReadAllBytes(prefab),Is.EqualTo(before));
        Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(prefab),Is.Not.Null);
    }
    [Test] public void SkeletonSignatureAllowsNewMeshesButRejectsBoneFrames()
    {
        var go=new GameObject("signature-root");var joint=new GameObject("joint");joint.transform.SetParent(go.transform,false);
        var skin=go.AddComponent<SkinnedMeshRenderer>();skin.bones=new[]{joint.transform};skin.rootBone=joint.transform;
        var a=new Mesh();var b=new Mesh();
        try
        {
            skin.sharedMesh=a;string baseline=(string)Call("FinalSkeletonSHA",go);skin.sharedMesh=b;
            Assert.That((string)Call("FinalSkeletonSHA",go),Is.EqualTo(baseline));
            joint.transform.localScale=Vector3.one*.01f;Assert.That((string)Call("FinalSkeletonSHA",go),Is.Not.EqualTo(baseline));
            joint.transform.localScale=Vector3.one;Assert.That((string)Call("FinalSkeletonSHA",go),Is.EqualTo(baseline));
            skin.rootBone=go.transform;Assert.That((string)Call("FinalSkeletonSHA",go),Is.Not.EqualTo(baseline));
        }
        finally{Object.DestroyImmediate(go);Object.DestroyImmediate(a);Object.DestroyImmediate(b);}
    }
    [Test] public void ExistingMeshIsFlushedBeforeByteReadAndCorruptOutputRegenerates()
    {
        const string folder="Assets/HexLiveContent/People/Wardrobe/Meshes/FinalFit";
        if(!AssetDatabase.IsValidFolder(folder))AssetDatabase.CreateFolder("Assets/HexLiveContent/People/Wardrobe/Meshes","FinalFit");
        string path=folder+"/__StorageRegression_"+Guid.NewGuid().ToString("N")+".asset";
        var a=new Mesh();var b=new Mesh();
        try
        {
            a.vertices=new[]{Vector3.zero,Vector3.right,Vector3.up};a.triangles=new[]{0,1,2};a.RecalculateNormals();
            b.vertices=new[]{Vector3.zero,Vector3.right*.7f,Vector3.up*.8f};b.triangles=new[]{0,1,2};b.RecalculateNormals();
            Call("SaveFinalMesh",a,"FinalA",path);string guid=AssetDatabase.AssetPathToGUID(path);
            Call("SaveFinalMesh",b,"FinalB",path);
            var imported=AssetDatabase.LoadAssetAtPath<Mesh>(path);Assert.That(imported.vertices,Is.EqualTo(b.vertices));Assert.That(imported.name,Is.EqualTo("FinalB"));
            var clean=File.ReadAllBytes(path);var descriptor=(JObject)Call("FinalArtifact",path);
            File.WriteAllText(path,File.ReadAllText(path).Replace("m_Name: FinalB","m_Name: Broken"));
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
            Assert.That((bool)Call("FinalArtifactMatches",descriptor),Is.False);
            Call("SaveFinalMesh",b,"FinalB",path);
            Assert.That(AssetDatabase.AssetPathToGUID(path),Is.EqualTo(guid));Assert.That(File.ReadAllBytes(path),Is.EqualTo(clean));
            Assert.That(AssetDatabase.LoadAssetAtPath<Mesh>(path).vertices,Is.EqualTo(b.vertices));
        }
        finally{AssetDatabase.DeleteAsset(path);Object.DestroyImmediate(a);Object.DestroyImmediate(b);}
    }
    [Test] public void BaseReportPathIsCommittedOwnedAndIndependentOfBuildDiagnostics()
    {Assert.That((string)Call("FinalBaseReportPath"),Is.EqualTo("Assets/HexLiveContent/People/Wardrobe/base-prepared-all.json"));}
    [Test] public void ReportSelectionUsesOwnedSnapshotForResumeAndAllowsFreshCloneWithoutDiagnostic()
    {
        string directory="Build/Diagnostics/finalfit-report-fixture-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(directory);
        string diagnostic=directory+"/ignored.json",snapshot=directory+"/owned.json";
        try
        {
            byte[] fresh=System.Text.Encoding.UTF8.GetBytes("fresh-diagnostic"),frozen=System.Text.Encoding.UTF8.GetBytes("frozen-owned");
            File.WriteAllBytes(diagnostic,fresh);File.WriteAllBytes(snapshot,frozen);string sha=(string)Call("FinalFileSHA",snapshot);
            Assert.That((byte[])Call("ReadFinalBaseReport",false,diagnostic,snapshot,null),Is.EqualTo(fresh));
            Assert.That((byte[])Call("ReadFinalBaseReport",true,diagnostic,snapshot,sha),Is.EqualTo(frozen));
            File.Delete(diagnostic);
            Assert.That((byte[])Call("ReadFinalBaseReport",false,diagnostic,snapshot,null),Is.EqualTo(frozen));
            Assert.That((byte[])Call("ReadFinalBaseReport",true,diagnostic,snapshot,sha),Is.EqualTo(frozen));
        }
        finally{if(File.Exists(diagnostic))File.Delete(diagnostic);if(File.Exists(snapshot))File.Delete(snapshot);Directory.Delete(directory);}
    }
    [Test] public void MissingOrChangedOwnedReportNeverFallsBackToIgnoredDiagnosticOnResume()
    {
        string directory="Build/Diagnostics/finalfit-report-reject-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(directory);
        string diagnostic=directory+"/ignored.json",snapshot=directory+"/owned.json";
        try
        {
            File.WriteAllText(diagnostic,"available");File.WriteAllText(snapshot,"before");string sha=(string)Call("FinalFileSHA",snapshot);
            File.WriteAllText(snapshot,"changed");
            var corrupt=Assert.Throws<TargetInvocationException>(()=>Call("ReadFinalBaseReport",true,diagnostic,snapshot,sha));
            Assert.That(corrupt.InnerException,Is.TypeOf<InvalidDataException>());File.Delete(snapshot);
            var missing=Assert.Throws<TargetInvocationException>(()=>Call("ReadFinalBaseReport",true,diagnostic,snapshot,sha));
            Assert.That(missing.InnerException,Is.TypeOf<InvalidDataException>());
        }
        finally{if(File.Exists(diagnostic))File.Delete(diagnostic);if(File.Exists(snapshot))File.Delete(snapshot);Directory.Delete(directory);}
    }
    [Test] public void AtomicSnapshotWriterPreservesExactUtf8BomCrlfBytes()
    {
        string directory="Build/Diagnostics/finalfit-report-bytes-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(directory);
        string path=directory+"/snapshot.json";
        try
        {
            byte[] bytes=System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("{\r\n  \"source\": \"café\", \"scale\":1.03\r\n}\r\n")).ToArray();
            Call("AtomicFinalBytes",path,bytes);Assert.That(File.ReadAllBytes(path),Is.EqualTo(bytes));
            Call("AtomicFinalBytes",path,bytes);Assert.That(File.ReadAllBytes(path),Is.EqualTo(bytes));
        }
        finally{if(File.Exists(path))File.Delete(path);Directory.Delete(directory);}
    }
    [Test] public void AtomicMetadataWriterReplacesAnExistingOwnFile()
    {
        string directory="Build/Diagnostics/finalfit-storage-fixture-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(directory);
        string path=directory+"/receipt.json";
        try
        {Call("AtomicFinalText",path,"old");Call("AtomicFinalText",path,"new");Assert.That(File.ReadAllText(path),Is.EqualTo("new"));Assert.That(File.Exists(path+".finalfit-tmp"),Is.False);}
        finally{if(File.Exists(path))File.Delete(path);Directory.Delete(directory);}
    }
}
}
#endif

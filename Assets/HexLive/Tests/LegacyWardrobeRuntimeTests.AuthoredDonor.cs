#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
namespace HexLive.Tests
{
public sealed partial class LegacyWardrobeRuntimeTests
{
    static void AssertAuthoredDonorReceipt(JToken body,JToken descriptor,JToken baseline,string actor,string label)
    {
        Assert.That((string)body["geometryMode"],Is.EqualTo("authored-donor-preserved"),label);
        var admission=body["sourceAdmission"];Assert.That(admission,Is.TypeOf<JObject>(),label);
        const string path=Staged+"/authored-donor-admissions.json";
        Assert.That((string)admission["manifest"],Is.EqualTo(path),label);AssertReceiptHash(path,(string)admission["manifestSha256"]);
        var document=JObject.Parse(File.ReadAllText(path));Assert.That((int?)document["schemaVersion"],Is.EqualTo(1));
        Assert.That((bool?)document["automaticFallback"],Is.False);
        string source=(string)baseline["source"],donor=(string)baseline["donor"];
        Assert.That(actor,Is.EqualTo(donor),label+" recipient is always strict");
        var record=document["records"].Single(r=>(string)r["nativeSource"]==source);
        Assert.That((string)record["actor"],Is.EqualTo(actor));
        string Hash(byte[] bytes){using var sha=System.Security.Cryptography.SHA256.Create();return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        Assert.That(Hash(System.Text.Encoding.UTF8.GetBytes(record.ToString(Newtonsoft.Json.Formatting.None))),Is.EqualTo((string)admission["recordSha256"]));
        Assert.That((string)admission["nativeSource"],Is.EqualTo(source));Assert.That((string)admission["actor"],Is.EqualTo(actor));Assert.That((string)admission["donor"],Is.EqualTo(donor));
        Assert.That((string)record["review"]?["geometryMode"],Is.EqualTo("authored-donor-preserved"));
        Assert.That((bool?)record["review"]?["sourceOnly"],Is.True);Assert.That((bool?)record["review"]?["recipientStrict"],Is.True);
        Assert.That((bool?)record["review"]?["bodyContactAcceptance"],Is.False);
        AssertReceiptHash(source,(string)record["nativeSha"]);AssertReceiptHash((string)record["nativeMesh"],(string)record["nativeMeshSha"]);
        Assert.That((string)admission["nativeMesh"],Is.EqualTo((string)record["nativeMesh"]));Assert.That((string)admission["nativeMeshSha256"],Is.EqualTo((string)record["nativeMeshSha"]));
        Assert.That(AssetDatabase.GetAssetDependencyHash(source).ToString(),Is.EqualTo((string)record["nativeDependency"]));
        foreach(var file in record["evidenceFiles"])
        {
            string owned=(string)file["path"];
            Assert.That(owned,Does.StartWith(Staged+"/Evidence/"));Assert.That(owned,Does.Not.Contain("..").And.Not.Contain("\\"));
            AssertReceiptHash(owned,(string)file["sha256"]);
        }
        foreach(string field in new[]{"allRawVertexAndIndexBytesExact","fullSkinFloatBitsExact","bindposeFloatBitsExact","submeshDescriptorsExact","ownedDerivedReadable","nativeReadableFlagUnchanged"})
            Assert.That((bool?)admission["rawBuffers"]?[field],Is.True,label+"/"+field);
        Assert.That((int?)descriptor["vertices"],Is.EqualTo((int?)record["vertices"]));Assert.That((long?)descriptor["triangles"],Is.EqualTo((long?)record["triangles"]));
        Assert.That(body["surfaceEnvelope"],Is.Null);Assert.That(body["postBindSurfaceEnvelope"],Is.Null);Assert.That(body["topologyRefinement"],Is.Null);
        Assert.That((bool?)body["bodyContactDiagnostic"]?["acceptance"],Is.False);Assert.That((bool?)body["bodyContactDiagnostic"]?["diagnosticOnly"],Is.True);
        Assert.That((string)body["runtimeConstruction"],Is.EqualTo("BodyBones.Equip"));Assert.That((float?)body["reconstruction"]?["maxMetres"],Is.Not.Null.And.InRange(0f,.0002f));
        Assert.That((bool?)admission["shapeDeformationAllowed"],Is.False);Assert.That((bool?)admission["sourceSettingsChanged"],Is.False);
    }
}
}
#endif

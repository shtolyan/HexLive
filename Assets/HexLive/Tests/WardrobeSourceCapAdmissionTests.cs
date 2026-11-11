#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
namespace HexLive.Tests
{
public sealed class WardrobeSourceCapAdmissionTests
{
    const string Manifest="Assets/HexLiveContent/People/Wardrobe/cap-attachments.json";
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
    static JObject Document()=>JObject.Parse(File.ReadAllText(Manifest));
    static JObject Record()=>(JObject)Document()["records"][0].DeepClone();
    static void Validate(JObject record,bool captured)=>Preparation.GetMethod("ValidateSourceCapRecord",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{record,captured});
    static Exception Reject(JObject record,bool captured)=>Assert.Throws<TargetInvocationException>(()=>Validate(record,captured)).InnerException;
    [Test]
    public void TamperedNativeBytesCannotBeAdmitted()
    {
        var record=Record();
        record["nativeSha"]=new string('0',64);var error=Reject(record,false);
        Assert.That(error,Is.InstanceOf<InvalidDataException>());Assert.That(error.Message,Does.Contain("authoring input changed"));
    }
    [Test]
    public void MissingExactBodyPairCannotMasqueradeAsCapturedSourceEvidence()
    {
        var record=Record();record["requiresFreshUnityDependencyCapture"]=false;
        record["nativeDependency"]=AssetDatabase.GetAssetDependencyHash((string)record["nativeSource"]).ToString();record["bodyAuthoringDependencies"]=new JArray();
        var error=Reject(record,true);Assert.That(error,Is.InstanceOf<InvalidDataException>());Assert.That(error.Message,Does.Contain("exactly the two"));
    }
    [Test]
    public void MatchingAssetDependencyDoesNotAllowTamperedRawBodySha()
    {
        var record=Record();record["requiresFreshUnityDependencyCapture"]=false;
        record["nativeDependency"]=AssetDatabase.GetAssetDependencyHash((string)record["nativeSource"]).ToString();
        record["bodyAuthoringDependencies"]=new JArray(new[]{"Marta","Kshishtof"}.Select(actor=>
        {string path="Assets/HexLiveContent/People/Prefabs/Actors/"+actor+".prefab";return new JObject{["path"]=path,["dependency"]=AssetDatabase.GetAssetDependencyHash(path).ToString(),["sha256"]=new string('0',64)};}));
        var error=Reject(record,true);Assert.That(error,Is.InstanceOf<InvalidDataException>());Assert.That(error.Message,Does.Contain("body authoring dependency changed"));
    }
    [Test]
    public void TraversingEvidencePathIsRejectedByInputEnumerationBeforeFileHashing()
    {
        var document=Document();document["records"][0]["evidenceFiles"][0]["path"]="Assets/HexLiveContent/People/Wardrobe/Evidence/../../outside-private-file";
        var method=Preparation.GetMethod("SourceCapInputPathsFromDocument",BindingFlags.NonPublic|BindingFlags.Static);
        var error=Assert.Throws<TargetInvocationException>(()=>method.Invoke(null,new object[]{document})).InnerException;
        Assert.That(error,Is.InstanceOf<InvalidDataException>());Assert.That(error.Message,Does.Contain("owned directory"));
    }
}
}
#endif

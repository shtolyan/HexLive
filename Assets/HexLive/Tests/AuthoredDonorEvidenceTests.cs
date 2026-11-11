#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
namespace HexLive.Tests
{
public sealed class AuthoredDonorEvidenceTests
{
    const string Root="Assets/HexLiveContent/People/Wardrobe";
    static JObject Record()=> (JObject)JObject.Parse(File.ReadAllText(Root+"/authored-donor-admissions.json"))["records"].Single(r=>(string)r["id"]=="FCO Boots Male"&&(string)r["nativeSource"]=="Assets/HexLiveContent/Wear/FCO Boots Male/FCO Boots Male.prefab");
    static JObject Evidence(JObject record,string role)=>JObject.Parse(File.ReadAllText((string)record["evidenceFiles"].Single(f=>(string)f["role"]==role)["path"]));
    static object Invoke(string name,params object[] args)
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).Single(t=>t!=null);
        var method=type.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic);
        Assert.That(method,Is.Not.Null,name);return method.Invoke(null,args);
    }
    static JObject MartaRecord(string id = "clothing.gloves_stars") =>
        (JObject)JObject.Parse(File.ReadAllText(Root + "/authored-donor-admissions.json"))["records"]
            .Single(r => (string)r["id"] == id && (string)r["actor"] == "Marta");

    [TestCase("clothing.gloves_stars")]
    [TestCase("clothing.outfit_reiko")]
    public void RealMartaCapturedRawSaveSixPoseCaseAndViewsPass(string id)
    {
        var r=MartaRecord(id);var poses=Evidence(r,"actual-six-pose-runtime");var review=Evidence(r,"owned-review");
        Assert.That((bool)Invoke("AuthoredDonorRawEvidenceAccepted",Evidence(r,"native-raw-clone"),r),Is.True);
        Assert.That((bool)Invoke("AuthoredDonorSixPoseEvidenceAccepted",poses,r),Is.True);
        Assert.That((bool)Invoke("AuthoredDonorStandingViewsAccepted",poses,r,review),Is.True);
        Assert.DoesNotThrow(()=>Invoke("ValidateAuthoredDonorEvidence",r));
    }

    [Test]
    public void ReikoNeedsPlayerLoopFramesAndSettledHeelWeight()
    {
        var record = MartaRecord("clothing.outfit_reiko");
        var report = Evidence(record, "actual-six-pose-runtime");
        var sleep = report["rows"].Single(row => (string)row["pose"] == "sleep");
        sleep["derivedHeelWeight"] = 1;
        Assert.That((bool)Invoke("AuthoredDonorSixPoseEvidenceAccepted", report, record), Is.False);
        sleep["derivedHeelWeight"] = 0;
        sleep["actualHeelPlayerLoopFrames"] = 0;
        Assert.That((bool)Invoke("AuthoredDonorSixPoseEvidenceAccepted", report, record), Is.False);
    }
    [TestCase("wrong-source")] [TestCase("saved-skin")] [TestCase("saved-index")] [TestCase("saved-count")]
    [TestCase("native-flag")] [TestCase("duplicate-record")] [TestCase("saved-stream")]
    public void MartaRawSaveEvidenceCannotBorrowSourceOrLoseRawIdentity(string mutation)
    {
        var r=MartaRecord();var raw=Evidence(r,"native-raw-clone");var selected=raw["records"].Single(x=>(string)x["id"]==(string)r["id"]);var b=selected["rawBuffers"];
        switch(mutation){case "wrong-source":selected["nativeSource"]="Assets/unreviewed.prefab";break;
        case "saved-skin":b["afterSaveAndImport"]["fullSkinFloatBitsExact"]=false;break;
        case "saved-index":b["afterSaveAndImport"]["indexBufferSha256"]=new string('0',64);break;
        case "saved-count":b["afterSaveAndImport"]["vertices"]=1;break;
        case "native-flag":selected["nativeReadableFlag"]=true;break;
        case "duplicate-record":((JArray)raw["records"]).Add(selected.DeepClone());break;
        case "saved-stream":b["afterSaveAndImport"]["vertexBuffers"][0]["sha256"]=new string('0',64);break;}
        Assert.That((bool)Invoke("AuthoredDonorRawEvidenceAccepted",raw,r),Is.False,mutation);
    }
    [Test] public void MartaCannotBorrowFcoPlayModeCaseOrLoseOnePoseOrView()
    {
        var r=MartaRecord();var root=Evidence(r,"root-admission");
        var xml=System.Xml.Linq.XDocument.Load((string)r["evidenceFiles"].Single(f=>(string)f["role"]=="actual-playmode-result")["path"]);
        Assert.That((bool)Invoke("AuthoredDonorPlayModeCaseAccepted",xml,r,root),Is.True);
        root["actualPlayModeCase"]="HexLive.Tests.WardrobeFcoFullNativeDonorEvidenceRuntimeTests.ActualFullNativeDerivedDonorPreservesSixPoseQualityAndUnmodifiedSources";
        Assert.That((bool)Invoke("AuthoredDonorPlayModeCaseAccepted",xml,r,root),Is.False);
        var poses=Evidence(r,"actual-six-pose-runtime");((JArray)poses["rows"]).RemoveAt(5);
        Assert.That((bool)Invoke("AuthoredDonorSixPoseEvidenceAccepted",poses,r),Is.False);
        poses=Evidence(r,"actual-six-pose-runtime");var review=Evidence(r,"owned-review");((JArray)review["standingImages"]).RemoveAt(0);
        Assert.That((bool)Invoke("AuthoredDonorStandingViewsAccepted",poses,r,review),Is.False);
    }
    [Test] public void RealCapturedRawAndSixPoseEvidencePassesAllContentAndOwnedImageChecks()
    {
        var record=Record();
        Assert.That((bool)Invoke("AuthoredDonorRawEvidenceAccepted",Evidence(record,"native-raw-clone"),record),Is.True);
        Assert.That((bool)Invoke("AuthoredDonorSixPoseEvidenceAccepted",Evidence(record,"actual-six-pose-runtime"),record),Is.True);
        Assert.DoesNotThrow(()=>Invoke("ValidateAuthoredDonorEvidence",record));
    }
    [TestCase("missing-pose")] [TestCase("duplicate-pose")] [TestCase("wrong-actor")]
    [TestCase("reduced-base")] [TestCase("old-source")] [TestCase("not-fresh")]
    [TestCase("expanded-limit")] [TestCase("failed-moved-root-quality")] [TestCase("missing-reequip")]
    public void WrongOrIncompleteSixPoseProofCannotAdmitAnAuthoredDonor(string mutation)
    {
        var record=Record();var report=Evidence(record,"actual-six-pose-runtime");
        switch(mutation)
        {
            case "missing-pose": ((JArray)report["rows"]).RemoveAt(5);break;
            case "duplicate-pose": report["rows"][5]=report["rows"][0].DeepClone();break;
            case "wrong-actor": report["actor"]="Marta";break;
            case "reduced-base": report["candidateGeometry"]="reduced-base";break;
            case "old-source": report["nativeMeshSha"]=new string('0',64);break;
            case "not-fresh": report["rows"][0]["quality"]["freshMeasurement"]=false;break;
            case "expanded-limit": report["rows"][0]["quality"]["maxLimitMetres"]=.12f;break;
            case "failed-moved-root-quality": report["rows"][3]["movedQuality"]["sourceToCandidate"]["max"]=.004f;break;
            case "missing-reequip": report.Remove("reequip");break;
            default: throw new ArgumentException(mutation);
        }
        Assert.That((bool)Invoke("AuthoredDonorSixPoseEvidenceAccepted",report,record),Is.False,mutation);
    }
    [TestCase("wrong-source")] [TestCase("recipient")] [TestCase("wrong-donor")]
    public void ExplicitSourceAdmissionCannotBeBorrowedByAnotherBodyOrSource(string mutation)
    {
        var record=Record();string source=(string)record["nativeSource"],actor="Kshishtof",donor="Kshishtof";
        if(mutation=="wrong-source")source="Assets/unreviewed.prefab";
        if(mutation=="recipient")actor="Marta";
        if(mutation=="wrong-donor")donor="Marta";
        Assert.That((bool)Invoke("AuthoredDonorIdentityAccepted",record,source,actor,donor),Is.False);
    }
    [Test] public void DroppedFullSkinProofIsRejectedEvenWhenCountsAndVertexBytesMatch()
    {
        var record=Record();var raw=Evidence(record,"native-raw-clone");raw["fullSkinFloatBitsExact"]=false;
        Assert.That((bool)Invoke("AuthoredDonorRawEvidenceAccepted",raw,record),Is.False);
    }
    [TestCase("actual-six-pose-runtime")] [TestCase("native-raw-clone")] [TestCase("root-admission")]
    [TestCase("actual-playmode-result")] [TestCase("owned-review")]
    public void MissingRequiredEvidenceRoleIsRejected(string role)
    {
        var record=Record();record["evidenceFiles"].Single(f=>(string)f["role"]==role).Remove();
        var error=Assert.Throws<TargetInvocationException>(()=>Invoke("ValidateAuthoredDonorEvidence",record));
        Assert.That(error.InnerException,Is.TypeOf<InvalidDataException>());
    }
}
}
#endif

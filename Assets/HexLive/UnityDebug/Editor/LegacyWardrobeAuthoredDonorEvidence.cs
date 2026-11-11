#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    static bool MartaDonorSchema(JToken record)=> (string)record?["rawEvidenceSchema"]=="marta-full-native-raw-copies-v1" && (string)record?["poseEvidenceSchema"]=="marta-full-native-six-pose-v1";
    static string ExpectedDonorCase(JToken record)
    {
        string id=(string)record?["id"],source=(string)record?["nativeSource"],actor=(string)record?["actor"];
        if(actor!=(string)record?["donor"])return null;
        if(actor=="Kshishtof"&&id=="FCO Boots Male"&&source=="Assets/HexLiveContent/Wear/FCO Boots Male/FCO Boots Male.prefab"&&record["rawEvidenceSchema"]==null&&record["poseEvidenceSchema"]==null)
            return "HexLive.Tests.WardrobeFcoFullNativeDonorEvidenceRuntimeTests.ActualFullNativeDerivedDonorPreservesSixPoseQualityAndUnmodifiedSources";
        if(actor!="Marta"||!MartaDonorSchema(record))return null;
        if(id=="clothing.gloves_stars"&&source=="Assets/HexLiveContent/Wear/clothing.gloves_stars/StarsGloves.prefab")
            return "HexLive.Tests.WardrobeMartaFullNativeDonorEvidenceRuntimeTests.GlovesFullNativeDerivedMartaHasActualSixPoseAndReequipEvidence";
        if(id=="clothing.outfit_reiko"&&source=="Assets/HexLiveContent/Wear/clothing.outfit_reiko/ReikoOutfit.prefab")
            return "HexLive.Tests.WardrobeMartaFullNativeDonorEvidenceRuntimeTests.ReikoFullNativeDerivedMartaHasActualSixPoseAndReequipEvidence";
        return null;
    }
    static bool AuthoredDonorPlayModeCaseAccepted(XDocument xml,JToken record,JToken review)
    {
        string expected=ExpectedDonorCase(record);if(expected==null)return false;
        if(MartaDonorSchema(record)&&((string)record["actualPlayModeCase"]!=expected||(string)review?["actualPlayModeCase"]!=expected))return false;
        if(record["actualPlayModeCase"]!=null&&(string)record["actualPlayModeCase"]!=expected||review?["actualPlayModeCase"]!=null&&(string)review["actualPlayModeCase"]!=expected)return false;
        var cases=xml.Descendants("test-case").Where(t=>(string)t.Attribute("fullname")==expected).ToArray();return cases.Length==1&&(string)cases[0].Attribute("result")=="Passed";
    }
    static bool DonorSHA(string value)=>value!=null&&value.Length==64&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
    static bool ExactDonorStreams(JToken value)=>value is JArray a&&a.Count>0&&a.Select(s=>(int?)s["stream"]).SequenceEqual(Enumerable.Range(0,a.Count).Select(i=>(int?)i))&&a.All(s=>(int?)s["bytes"]>0&&DonorSHA((string)s["sha256"]));
    static bool ExactDonorRawBuffers(JToken b,JToken r)=>b is JObject&&(int?)b["vertices"]==(int?)r["vertices"]&&(long?)b["triangles"]==(long?)r["triangles"]&&new[]{"allRawVertexAndIndexBytesExact","fullSkinFloatBitsExact","bindposeFloatBitsExact","submeshDescriptorsExact"}.All(k=>(bool?)b[k]==true)&&ExactDonorStreams(b["vertexBuffers"])&&DonorSHA((string)b["indexBufferSha256"]);
    static bool MartaDonorRawAccepted(JToken raw,JToken record)
    {
        if(ExpectedDonorCase(record)==null||!(raw?["records"] is JArray records)||(bool?)raw["diagnosticOnly"]!=true||(bool?)raw["admissionGranted"]!=false)return false;
        var matches=records.Where(r=>(string)r["id"]==(string)record["id"]&&(string)r["actor"]==(string)record["actor"]).ToArray();if(matches.Length!=1)return false;
        var r=matches[0];var b=r["rawBuffers"];var saved=b?["afterSaveAndImport"];
        return (string)r["nativeSource"]==(string)record["nativeSource"]&&(string)r["nativeSourceSha256"]==(string)record["nativeSha"]&&(string)r["nativeMesh"]==(string)record["nativeMesh"]&&(string)r["nativeMeshSha256"]==(string)record["nativeMeshSha"]&&(string)r["nativeMeshGuid"]==(string)record["nativeMeshGuid"]&&(string)r["nativeDependency"]==(string)record["nativeDependency"]&&
            (bool?)r["nativeSourceUnmodified"]==true&&(bool?)r["nativeReadableFlag"]==false&&(bool?)r["admissionGranted"]==false&&ExactDonorRawBuffers(b,record)&&ExactDonorRawBuffers(saved,record)&&(bool?)b["ownedDerivedReadable"]==true&&(bool?)b["nativeReadableFlag"]==false&&(bool?)b["nativeReadableFlagUnchanged"]==true&&(bool?)b["savedAndReloadedRawBitsExact"]==true&&JToken.DeepEquals(b["vertexBuffers"],saved["vertexBuffers"])&&(string)b["indexBufferSha256"]==(string)saved["indexBufferSha256"];
    }
    static string[] DonorViewNames(JToken record)=>!MartaDonorSchema(record)?new[]{"front","front-sole","front-cuff","back","back-sole","back-cuff"}:(string)record["id"]=="clothing.gloves_stars"?new[]{"front","back","left-wrist-hand-front","left-wrist-hand-back","right-wrist-hand-front","right-wrist-hand-back"}:new[]{"front","back","left-boot-cuff-front","left-boot-cuff-back","right-boot-cuff-front","right-boot-cuff-back"};
    static bool AuthoredDonorStandingViewsAccepted(JToken poses,JToken record,JToken review)
    {
        bool marta=MartaDonorSchema(record);if(ExpectedDonorCase(record)==null)return false;var scopes=marta?new[]{"nativeImages","derivedImages"}:new[]{"nativeImages","baseImages"};var names=DonorViewNames(record);
        if(marta&&(!JToken.DeepEquals(record["requiredViews"],JArray.FromObject(names))||!JToken.DeepEquals(record["requiredViewScopes"],JArray.FromObject(scopes))||!JToken.DeepEquals(review?["requiredViews"],record["requiredViews"])||!JToken.DeepEquals(review?["requiredViewScopes"],record["requiredViewScopes"])))return false;
        if(!(review?["standingImages"] is JArray views)||views.Count!=12)return false;
        var rows=(poses?["rows"] as JArray)?.Where(r=>(string)r["pose"]=="standing").ToArray();if(rows==null||rows.Length!=1)return false;var row=rows[0];
        foreach(string scope in scopes)foreach(string name in names)
        {
            var v=views.Where(x=>(string)x["scope"]==scope&&(string)x["view"]==name).ToArray();if(v.Length!=1||(bool?)v[0]["rootPersonallyViewed"]!=true)return false;string hash=(string)v[0]["sha256"];if(!DonorSHA(hash))return false;
            if(!marta){if((string)row[scope]?[name+"SHA256"]!=hash)return false;continue;}
            string prefix=scope=="nativeImages"?"Native-":"FullNativeDerived-";
            if(!(row[scope] is JArray captures)||captures.Count!=6)return false;var c=captures.Where(x=>Path.GetFileName((string)x["png"])==prefix+name+".png").ToArray();if(c.Length!=1||(string)c[0]["sha256"]!=hash||(string)v[0]["originalCapturedPath"]!=(string)c[0]["png"])return false;
            bool detail=name!="front"&&name!="back";string expectedScope=!detail?"whole standing authored model":(string)record["id"]=="clothing.gloves_stars"?"wrist and hand source region":"Reiko Shoes material only determines camera; whole model still rendered";
            if((string)c[0]["scope"]!=expectedScope)return false;if(detail&&((bool?)c[0]["left"]!=name.StartsWith("left-",StringComparison.Ordinal)||(bool?)c[0]["back"]!=name.EndsWith("-back",StringComparison.Ordinal)))return false;
        }
        return true;
    }
    static bool DonorEvidenceBound(JToken value,float maximum)
    {var number=(float?)value;return number.HasValue&&float.IsFinite(number.Value)&&number.Value>=0&&number.Value<=maximum;}
    static bool DonorPoseQualityAccepted(JToken quality)
    {
        if((bool?)quality?["passed"]!=true||(bool?)quality?["freshMeasurement"]!=true||
           (float?)quality?["maxLimitMetres"]!=.003f||(float?)quality?["p95LimitMetres"]!=.0005f)return false;
        return new[]{"sourceToCandidate","candidateToSource"}.All(direction=>
            (int?)quality[direction]?["sampleCount"]>=4096&&DonorEvidenceBound(quality[direction]?["max"],.003f)&&
            DonorEvidenceBound(quality[direction]?["p95"],.0005f));
    }
    static bool DonorHeelPoseAccepted(JToken row, JToken record)
    {
        if (!MartaDonorSchema(record) ||
            ((string)record["id"] == "clothing.gloves_stars" && row["actualHeelPlayerLoopFrames"] == null))
            return (int?)row["actualHeelLateUpdateCallsNative"] > 0 &&
                (int?)row["actualHeelLateUpdateCallsBase"] > 0;
        float expected = (string)row["pose"] is "sleep" or "belly" ? 0f : 1f;
        return (int?)row["actualHeelPlayerLoopFrames"] > 0 &&
            (float?)row["nativeHeelWeight"] == expected && (float?)row["derivedHeelWeight"] == expected;
    }
    // Pure predicate, exercised with the real captured report and malformed
    // variants. Hash validation and owned-path resolution are separate below.
    static bool AuthoredDonorSixPoseEvidenceAccepted(JToken report,JToken record)
    {
        if(!(report is JObject)||!(record is JObject)||
           (bool?)report["diagnosticCompleted"]!=true||(string)report["id"]!=(string)record["id"]||
           (string)report["actor"]!=(string)record["actor"]||(string)record["actor"]!=(string)record["donor"]||
           (string)report["equipmentFactory"]!="BodyBones.Equip"||
           (string)report["candidateGeometry"]!="full-native-private-derived-donor-no-rebind"||
           (string)report["nativeMesh"]!=(string)record["nativeMesh"]||(string)report["nativeMeshSha"]!=(string)record["nativeMeshSha"]||
           (int?)report["nativeVertexCount"]!=(int?)record["vertices"]||(int?)report["candidateVertexCount"]!=(int?)record["vertices"]||
           (bool?)report["noSolverExecuted"]!=true||(bool?)report["sourceAssetsUnmodified"]!=true||
           (bool?)report["acceptanceOverride"]!=false||(bool?)report["fallbackAcceptanceGranted"]!=false||
           (bool?)report["rootTranslationYawEquivarianceAllSixPosesPassed"]!=true||
           (bool?)report["freshQualityAllSixPosesMovedRootsAndReequipPassed"]!=true||
           (string)report["sourceFileSHA256"]?[(string)record["nativeSource"]]!=(string)record["nativeSha"]||
           !JToken.DeepEquals(report["nativeSettings"],record["nativeSettings"]))return false;
        bool marta=MartaDonorSchema(record);if(ExpectedDonorCase(record)==null)return false;
        if(marta&&((bool?)report["wholeAuthoredModelPreserved"]!=true||(bool?)report["recipientGeometryUnchanged"]!=true||(bool?)report["fullNativeCloneOnly"]!=true||(bool?)report["admissionGranted"]!=false||
            (string)report["sourceIdentity"]?["prefab"]!=(string)record["nativeSource"]||(string)report["sourceIdentity"]?["prefabSHA"]!=(string)record["nativeSha"]||(string)report["sourceIdentity"]?["mesh"]!=(string)record["nativeMesh"]||(string)report["sourceIdentity"]?["meshSHA"]!=(string)record["nativeMeshSha"]||(string)report["sourceIdentity"]?["selectedActor"]!=(string)record["actor"]||!JToken.DeepEquals(report["sourceIdentity"]?["settings"],record["nativeSettings"])))return false;
        var expected=new[]{"standing","run_early","run_mid","run_late","sleep","belly"};
        if(!(report["rows"] is JArray rows)||rows.Count!=6||
           !rows.Select(row=>(string)row["pose"]).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x=>x,StringComparer.Ordinal)))return false;
        foreach(var row in rows)
        {
            if(!DonorPoseQualityAccepted(row["quality"])||!DonorPoseQualityAccepted(row["movedQuality"])||
               !DonorEvidenceBound(row["nativeRootEquivariance"]?["maxMetres"],.0002f)||
               !DonorEvidenceBound(row[marta?"derivedRootEquivariance":"baseRootEquivariance"]?["maxMetres"],.0002f)||
               !DonorHeelPoseAccepted(row,record)||
               !((float?)row["unscaledDeltaTime"]>0)||string.IsNullOrEmpty((string)row["clip"])||
               (bool?)row["nativeContactDiagnostic"]?["acceptance"]!=false||(bool?)row[marta?"derivedContactDiagnostic":"baseContactDiagnostic"]?["acceptance"]!=false)return false;
            // Running must exercise actual skeletal deformation. Sleep/belly
            // include the production root/heel-suppression state and may leave
            // a flat boot's body-local sample unchanged; do not invent motion.
            if(((string)row["pose"]).StartsWith("run_",StringComparison.Ordinal)&&!((float?)row["actualBodyMotionFromStandingMetres"]>.001f))return false;
        }
        return (bool?)report["reequip"]?["newNativeInstance"]==true&&(bool?)report["reequip"]?[marta?"newDerivedInstance":"newBaseInstance"]==true&&
            DonorPoseQualityAccepted(report["reequip"]?["quality"]);
    }
    static bool AuthoredDonorRawEvidenceAccepted(JToken raw,JToken record)
        =>MartaDonorSchema(record)?MartaDonorRawAccepted(raw,record):ExpectedDonorCase(record)!=null&&raw is JObject&&(bool?)raw["passed"]==true&&(bool?)raw["privateClone"]==true&&
          (bool?)raw["sourceFlagChanged"]==false&&(bool?)raw["sourceUnmodified"]==true&&
          (string)raw["mode"]=="full-native-private-copy-no-rebind-no-setters"&&
          (string)raw["sourcePrefab"]==(string)record["nativeSource"]&&(string)raw["sourceMesh"]==(string)record["nativeMesh"]&&
          (string)raw["sourceMeshSha"]==(string)record["nativeMeshSha"]&&
          (int?)raw["vertices"]==(int?)record["vertices"]&&(long?)raw["triangles"]==(long?)record["triangles"]&&
          (bool?)raw["allRawVertexAndIndexBytesExact"]==true&&(bool?)raw["fullSkinFloatBitsExact"]==true&&
          (bool?)raw["bindposeFloatBitsExact"]==true&&(bool?)raw["submeshDescriptorsExact"]==true&&
          raw["vertexBuffers"] is JArray streams&&streams.Count>0&&streams.All(s=>(int?)s["bytes"]>0&&((string)s["sha256"])?.Length==64)&&
          ((string)raw["indexBufferSha"])?.Length==64;
    static void ValidateAuthoredDonorEvidence(JObject record)
    {
        void Require(bool ok,string error){if(!ok)throw new InvalidDataException("Authored-donor evidence: "+error);}
        var files=(record["evidenceFiles"] as JArray)?.OfType<JObject>().ToArray()??Array.Empty<JObject>();
        string RolePath(string role)
        {
            var matches=files.Where(file=>(string)file["role"]==role).ToArray();Require(matches.Length==1,"missing/duplicate role "+role);
            var entry=matches[0];string path=(string)entry["path"];
            Require(!string.IsNullOrEmpty(path)&&path.StartsWith(PreparedRoot+"/Evidence/",StringComparison.Ordinal)&&!path.Contains("..")&&!path.Contains("\\"),"unowned role path "+role);
            Require(FinalFileSHA(path)==(string)entry["sha256"],"role bytes changed "+role);return path;
        }
        var raw=JObject.Parse(File.ReadAllText(RolePath("native-raw-clone")));
        var poses=JObject.Parse(File.ReadAllText(RolePath("actual-six-pose-runtime")));
        Require(AuthoredDonorRawEvidenceAccepted(raw,record),"wrong source/raw/skin evidence");
        Require(AuthoredDonorSixPoseEvidenceAccepted(poses,record),"missing/wrong actual pose/quality/root/reequip evidence");
        var xml=XDocument.Load(RolePath("actual-playmode-result"));
        var rootReview=JObject.Parse(File.ReadAllText(RolePath("root-admission")));
        Require(AuthoredDonorPlayModeCaseAccepted(xml,record,rootReview),"wrong/missing exact-source actual PlayMode case");
        Require((string)rootReview["sourceMesh"]==(string)record["nativeMesh"]&&
            (string)rootReview["sourceMeshSha256"]==(string)record["nativeMeshSha"]&&
            (string)rootReview["nativeSourcePrefab"]==(string)record["nativeSource"]&&
            (bool?)rootReview["actualSixPoseQualityPassed"]==true&&(bool?)rootReview["actualRootAndReequipPassed"]==true&&
            (bool?)rootReview["publicationAuthorized"]==false,"wrong/missing exact-source root admission");
        var review=JObject.Parse(File.ReadAllText(RolePath("owned-review")));
        Require((string)review["geometryMode"]=="authored-donor-preserved"&&(bool?)review["sourceOnly"]==true&&
            (bool?)review["recipientStrict"]==true&&(bool?)review["bodyContactAcceptance"]==false,"wrong review scope");
        Require(AuthoredDonorStandingViewsAccepted(poses,record,review),"missing/wrong actual standing views");
        foreach(var v in (JArray)review["standingImages"])
        {
            string image=(string)v["ownedPath"],hash=(string)v["sha256"];
            Require(files.Count(f=>(string)f["role"]=="standing-view"&&(string)f["path"]==image&&(string)f["sha256"]==hash)==1,"image not in owned evidence");
            Require(!string.IsNullOrEmpty(image)&&image.StartsWith(PreparedRoot+"/Evidence/",StringComparison.Ordinal)&&!image.Contains("..")&&!image.Contains("\\")&&FinalFileSHA(image)==hash,"owned image bytes changed");
        }
    }
}
}
#endif

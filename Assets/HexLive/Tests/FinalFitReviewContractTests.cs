#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
namespace HexLive.Tests
{
public sealed class FinalFitReviewContractTests
{
    static Type Preparation => AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation",false)).First(t=>t!=null);
    static bool Accepted(JToken quality)=>(bool)Preparation.GetMethod("FinalReductionAccepted",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{quality});
    static JObject Fresh()=>JObject.Parse(@"{'mode':'validated-base-candidate','passed':true,'inputTriangles':100,'actualTriangles':80,'maxErrorLimitMetres':0.003,'p95ErrorLimitMetres':0.0005,'sourceToCandidate':{'sampleCount':20,'max':0.002,'p95':0.0004},'candidateToSource':{'sampleCount':20,'max':0.002,'p95':0.0004}}" );
    [Test] public void FreshCorrectedUnitsRequireActualPositiveSamplesAndLimits()
    {
        Assert.That(Accepted(Fresh()),Is.True);
        foreach(string direction in new[]{"sourceToCandidate","candidateToSource"})
        {
            var q=Fresh();q[direction]["sampleCount"]=0;Assert.That(Accepted(q),Is.False,direction);
            q=Fresh();q[direction]["max"]=-.001;Assert.That(Accepted(q),Is.False,direction);
            q=Fresh();q[direction]["p95"]=.0006;Assert.That(Accepted(q),Is.False,direction);
        }
        foreach(string field in new[]{"inputTriangles","actualTriangles"})
        {var q=Fresh();q[field]=0;Assert.That(Accepted(q),Is.False,field);}
        foreach(string field in new[]{"maxErrorLimitMetres","p95ErrorLimitMetres"})
        {var q=Fresh();q.Remove(field);Assert.That(Accepted(q),Is.False,field);}
    }
    [Test] public void ReductionRejectsMissingQualityAndUnmeasuredAdaptivePass()
    {
        Assert.That(Accepted(null),Is.False);Assert.That(Accepted(JValue.CreateNull()),Is.False);
        var q=Fresh();q["mode"]="adaptive";q["attempts"]=new JArray(new JObject{["passed"]=true});Assert.That(Accepted(q),Is.False);
        q=Fresh();q["mode"]="source-preserved";Assert.That(Accepted(q),Is.False);
        q["actualTriangles"]=100;Assert.That(Accepted(q),Is.True);
    }
    static void Batch(JArray rows,Action<JToken> process,Action<JToken,Exception> failure)
        =>Preparation.GetMethod("RunFinalFitBatch",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{rows,process,failure});
    [Test] public void BatchRetainsSuccessfulRowsAfterAnEarlierRejectedModel()
    {
        var success=new System.Collections.Generic.List<string>();var errors=new System.Collections.Generic.List<string>();
        var rows=new JArray(new JObject{["id"]="rejected"},new JObject{["id"]="second"},new JObject{["id"]="third"});
        Batch(rows,row=>{string id=(string)row["id"];if(id=="rejected")throw new InvalidOperationException("physical floor");success.Add(id);},
            (row,error)=>{Assert.That(error.Message,Is.EqualTo("physical floor"));errors.Add((string)row["id"]);});
        Assert.That(success,Is.EqualTo(new[]{"second","third"}));Assert.That(errors,Is.EqualTo(new[]{"rejected"}));
    }
    [Test] public void ImmutableGuardFailureAbortsBatchBeforeAnyLaterModel()
    {
        int started=0;
        var rows=new JArray(new JObject{["id"]="first"},new JObject{["id"]="never-start"});
        var error=Assert.Throws<TargetInvocationException>(()=>Batch(rows,row=>{started++;throw new InvalidOperationException("processor rejected");},
            (row,failure)=>{throw new InvalidDataException("Immutable base bytes changed");}));
        Assert.That(error.InnerException,Is.TypeOf<InvalidDataException>());Assert.That(started,Is.EqualTo(1));
    }
    [Test] public void FinalResumeRoutingFollowsActualPrefabsAndIgnoresStaleTopLevelFlags()
    {
        var method=Preparation.GetMethod("UsesFinalFitCatalog",BindingFlags.Static|BindingFlags.NonPublic);
        bool Final(string json)=>(bool)method.Invoke(null,new object[]{JObject.Parse(json)});
        Assert.That(Final(@"{'finalFit':true,'records':[{'type':'wear','main':'Assets/HexLiveContent/People/Wardrobe/Prefabs/base.prefab'}]}"),Is.False);
        Assert.That(Final(@"{'records':[{'type':'wear','main':'Assets/HexLiveContent/People/Wardrobe/Prefabs/FinalFit/real.prefab'}]}"),Is.True);
        Assert.That(Final(@"{'records':[{'type':'config','main':'Assets/HexLiveContent/People/Wardrobe/Prefabs/FinalFit/real.prefab'}]}"),Is.False);
    }
}
public sealed class FinalFitCompletedCollectionTests
{
    static Type Preparation => AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation",false)).First(t=>t!=null);
    [Test] public void ActualFinalRowBindsActorDescriptorsToImportedWearConfigs()
    {
        const string path="Assets/HexLiveContent/People/Wardrobe/release-fit.json";
        Assert.That(File.Exists(path),Is.True,"Run this completed-collection regression only after the final collection exists");
        var manifest=JObject.Parse(File.ReadAllText(path));Assert.That((bool?)manifest["passed"],Is.True);
        var row=(JObject)((JObject)manifest["rows"]).Properties().First().Value.DeepClone();
        var method=Preparation.GetMethod("FinalRowMatches",BindingFlags.Static|BindingFlags.NonPublic);
        bool Match(JObject value)=>(bool)method.Invoke(null,new object[]{value,(string)value["final"]["inputHash"],(JObject)value["base"]["settings"]});
        Assert.That(Match(row),Is.True,"Actual imported complete row is the control");
        var meshes=(JObject)row["final"]["meshes"];var m=meshes["Marta"].DeepClone();meshes["Marta"]=meshes["Kshishtof"].DeepClone();meshes["Kshishtof"]=m;
        Assert.That(Match(row),Is.False,"Swapped metadata alone must never authorize mismatched actor meshes");
    }
}
}
#endif

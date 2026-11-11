#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
namespace HexLive.Tests
{
public sealed class FinalFitInputGuardTests
{
    string directory,path;
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation",false)).First(t=>t!=null);
    static object Call(string name,params object[] args)=>Preparation.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
    [SetUp] public void Setup(){directory=Path.Combine(Path.GetTempPath(),"hexlive-final-guard-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);path=Path.Combine(directory,"provisional.json");}
    [TearDown] public void Cleanup(){Directory.Delete(directory,true);}
    static JObject Provisional()=>new JObject{["catalogPromoted"]=false,["invalidated"]=false,["immutableBatchVerified"]=false,["failures"]=new JArray()};
    [Test] public void ExpectedModelRejectionCanContinueWithoutInvalidatingInputs()
    {
        var state=Provisional();int started=0,guards=0;
        Action<JToken> process=_=>{started++;if(started==1)throw new InvalidOperationException("Rejected geometry");};
        Action<JToken,Exception> failure=(row,error)=>Call("GuardFinalInputs",(Action)(()=>guards++),state,path);
        Call("RunFinalFitBatch",new JArray(1,2),process,failure);
        Assert.That(started,Is.EqualTo(2));Assert.That(guards,Is.EqualTo(1));Assert.That((bool)state["invalidated"],Is.False);Assert.That(File.Exists(path),Is.False);
    }
    [Test] public void QuickGuardFailurePersistsInvalidationAndAbortsLaterRows()
    {
        var state=Provisional();int started=0;
        Action<JToken> process=_=>{started++;throw new InvalidOperationException("Rejected geometry");};
        Action<JToken,Exception> failure=(row,error)=>Call("GuardFinalInputs",(Action)(()=>throw new InvalidDataException("Current native dependency changed")),state,path);
        Assert.Throws<TargetInvocationException>(()=>Call("RunFinalFitBatch",new JArray(1,2),process,failure));
        Assert.That(started,Is.EqualTo(1));Assert.That((bool)state["invalidated"],Is.True);
        var disk=JObject.Parse(File.ReadAllText(path));Assert.That((bool)disk["invalidated"],Is.True);Assert.That((bool)disk["catalogPromoted"],Is.False);
    }
    [Test] public void FullByteGuardRejectsUnrelatedPinnedMutationEvenWithExistingFailureLedger()
    {
        string current=Path.Combine(directory,"current.asset"),unrelated=Path.Combine(directory,"unrelated.asset");
        File.WriteAllText(current,"current");File.WriteAllText(unrelated,"before");
        var pins=new Dictionary<string,string>{{current,(string)Call("FinalFileSHA",current)},{unrelated,(string)Call("FinalFileSHA",unrelated)}};
        var state=Provisional();((JArray)state["failures"]).Add(new JObject{["id"]="previous-rejected-model"});
        File.WriteAllText(unrelated,"after");
        Assert.Throws<TargetInvocationException>(()=>Call("GuardFinalInputs",(Action)(()=>Call("VerifyFinalPinnedBytes",pins)),state,path));
        var disk=JObject.Parse(File.ReadAllText(path));Assert.That((bool)disk["invalidated"],Is.True);Assert.That((bool)disk["catalogPromoted"],Is.False);Assert.That((bool)disk["immutableBatchVerified"],Is.False);
        Assert.That(disk["immutableInputFailure"].ToString(),Does.Contain("unrelated.asset"));
    }
}
}
#endif

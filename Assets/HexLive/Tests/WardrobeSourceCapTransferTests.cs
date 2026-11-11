#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
namespace HexLive.Tests
{
public sealed class WardrobeSourceCapTransferTests
{
    static JObject Cap()=>new JObject{["recipientTransfer"]=new JObject{["sourceActor"]="Marta",["targetActor"]="Kshishtof",
        ["targetClipWasRecomputed"]=false,["sourceClipScope"]="SOURCE-evidenced donor geometry before target bone retarget and strict repair",
        ["ownReferencePolicy"]="actual cut-retarget-clearance target seed before underlayers",
        ["commonReferencePolicy"]="source lineage interpolated ORIGINAL UNCUT fresh-bone target reference",
        ["seedOperator"]="CreateSourceClearanceSeed on SOURCE-evidenced clipped topology",["targetClearanceSeed"]=new JObject{["passed"]=true}}};
    static bool Accepted(JObject cap,string target="Kshishtof",string source="Marta")
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
        return (bool)type.GetMethod("SourceCapTransferAccepted",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{cap,target,source});
    }
    [Test] public void CurrentClippedClearanceProvenanceIsAccepted()=>Assert.That(Accepted(Cap()),Is.True);
    [TestCase("missing-seed")] [TestCase("failed-seed")] [TestCase("wrong-common")] [TestCase("false-target-clip")] [TestCase("wrong-body")]
    public void TransferCannotBorrowAnotherBodyOrReplaceTheOriginalCommonReference(string mutation)
    {
        var cap=Cap();var transfer=(JObject)cap["recipientTransfer"];
        if(mutation=="missing-seed")transfer.Remove("targetClearanceSeed");
        if(mutation=="failed-seed")transfer["targetClearanceSeed"]["passed"]=false;
        if(mutation=="wrong-common")transfer["commonReferencePolicy"]="clipped seed substituted for original common";
        if(mutation=="false-target-clip")transfer["targetClipWasRecomputed"]=true;
        Assert.That(Accepted(cap,mutation=="wrong-body"?"Marta":"Kshishtof"),Is.False);
    }
}
}
#endif

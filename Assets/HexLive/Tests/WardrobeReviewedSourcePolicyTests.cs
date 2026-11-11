#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
namespace HexLive.Tests
{
public sealed class WardrobeReviewedSourcePolicyTests
{
    const string Original="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Unknown="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string Reviewed="c847fbfa817de9d2faa9b99848630e7d937a274d39d6859a64776acfd8f656d3";
    [TestCase("LegacyWardrobeAdaptiveEnvelope.cs",Original,true)]
    [TestCase("LegacyWardrobeAdaptiveEnvelope.cs",Reviewed,true)]
    [TestCase("LegacyWardrobeAdaptiveEnvelope.cs",Unknown,false)]
    [TestCase("LegacyWardrobeSourceClearanceSeed.cs",Unknown,false)]
    public void RecoveryAllowsOnlyUnchangedOrExplicitReviewedAdaptiveSource(string file,string current,bool expected)
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
        var method=type.GetMethod("ReviewedR31SourceAllowed",BindingFlags.NonPublic|BindingFlags.Static);
        Assert.That((bool)method.Invoke(null,new object[]{"Assets/HexLive/UnityDebug/Editor/"+file,Original,current}),Is.EqualTo(expected));
    }
}
}
#endif

#if UNITY_EDITOR
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.Tests
{
public sealed class PreparedWardrobeAliasTests
{
    [Test]
    public void PreparedScaleAndHeightControlsEditTheVisibleCanonicalFit()
    {
        var go=new GameObject("Prepared alias fixture");
        try
        {
            var wear=go.AddComponent<Wear>();var data=new SerializedObject(wear);
            data.FindProperty("preparedBodyFits").boolValue=true;data.ApplyModifiedPropertiesWithoutUndo();
            wear.SetConfigScale(ActorName.Marta,1.04f);wear.SetConfigScale(ActorName.Kshishtof,1.07f);
            Assert.That(wear.GetConfigScale(ActorName.Molly),Is.EqualTo(1.04f));
            Assert.That(wear.GetConfigScale(ActorName.Tonny),Is.EqualTo(1.07f));
            wear.SetConfigScale(ActorName.Jolly,1.05f);wear.SetConfigHeight(ActorName.Tonny,.02f);
            Assert.That(wear.GetConfigScale(ActorName.Marta),Is.EqualTo(1.05f));
            Assert.That(wear.GetConfigHeight(ActorName.Kshishtof),Is.EqualTo(.02f));
            data.Update();Assert.That(data.FindProperty("configs").arraySize,Is.EqualTo(2),"An alias must not add an unused fit row.");
            data.FindProperty("preparedBodyFits").boolValue=false;data.ApplyModifiedPropertiesWithoutUndo();
            wear.SetConfigScale(ActorName.Molly,1.02f);
            Assert.That(wear.GetConfigScale(ActorName.Marta),Is.EqualTo(1.05f));
            Assert.That(wear.GetConfigScale(ActorName.Molly),Is.EqualTo(1.02f),"Native per-actor tuning stays independent.");
        }
        finally{Object.DestroyImmediate(go);}
    }
}
}
#endif

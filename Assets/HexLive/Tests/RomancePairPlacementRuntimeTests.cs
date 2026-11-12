#if UNITY_EDITOR
using System.Reflection;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.TwoPeopleTest;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class RomancePairPlacementRuntimeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void AuthoredPairOffsetsFollowBodyScaleAndAnchorRotation(bool useGameScale)
    {
        var catalog = AssetDatabase.LoadAssetAtPath<RomancePoseCatalog>(
            "Assets/HexLiveContent/RuntimeSource/Romance/RomancePoseCatalog.asset");
        Assert.That(catalog, Is.Not.Null);
        var anchor = new Vector3(12f, 3f, -7f);
        var rotation = Quaternion.Euler(0f, 137f, 0f);
        var actorScale = useGameScale ? HexWorldRenderer.ActorScale : 1f;
        var female = new GameObject("Female root").AddComponent<NpcActorView>();
        var male = new GameObject("Male root").AddComponent<NpcActorView>();
        var pair = female.gameObject.AddComponent<RomancePairView>();
        var lateUpdate = typeof(RomancePairView).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            int tested = 0;
            foreach (var entry in catalog.entries)
            {
                if (entry.femaleLoop == null || entry.maleLoop == null) continue;
                pair.Sync(female, male, entry, 1, 2, 10, 0, 1334, .25f,
                    anchor, rotation, actorScale, 1f, false);
                lateUpdate.Invoke(pair, null);

                var offset = Quaternion.Inverse(rotation) * (male.transform.position - anchor);
                Assert.That(Vector3.Distance(offset / actorScale, entry.malePosition), Is.LessThan(.00001f), entry.key);
                Assert.That(female.transform.position, Is.EqualTo(anchor), entry.key);
                Assert.That(Quaternion.Angle(female.transform.rotation, rotation), Is.LessThan(.01f), entry.key);
                var expectedMaleRotation = rotation * Quaternion.Euler(entry.maleEuler);
                Assert.That(Quaternion.Angle(male.transform.rotation, expectedMaleRotation), Is.LessThan(.01f), entry.key);
                tested++;
            }
            Assert.That(tested, Is.GreaterThan(0), "The authored catalog must contain paired runtime loops.");
        }
        finally
        {
            Object.DestroyImmediate(female.gameObject);
            Object.DestroyImmediate(male.gameObject);
        }
    }
}
}
#endif

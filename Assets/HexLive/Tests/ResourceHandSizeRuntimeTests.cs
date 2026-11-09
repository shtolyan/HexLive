#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using HexLive.UnityPresentation;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class ResourceHandSizeRuntimeTests
    {
        [TestCase("resource.yucca_seed", false)]
        [TestCase("resource.yucca_seed", true)]
        [TestCase("resource.herb_seed", false)]
        [TestCase("resource.herb_seed", true)]
        [TestCase("resource.stick", false)]
        [TestCase("resource.fiber", true)]
        [TestCase("resource.palm_crown", false)]
        [TestCase("resource.palm_crown_small", true)]
        public void RealResourceUsesGroundSizeUnderScaledRotatedHand(string id, bool left)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var handles = (IDictionary)typeof(ContentPrefabCache).GetField("Handles", flags).GetValue(null);
            var configs = (IDictionary)typeof(GearLibrary).GetField("Configs", flags).GetValue(null);
            var key = "object/" + id;
            var oldHandle = handles[key]; var oldConfig = configs[id];
            var hadHandle = handles.Contains(key); var hadConfig = configs.Contains(id);
            var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
            Assert.That(source, Is.Not.Null, id);
            var hand = new GameObject("Rotated scaled hand");
            GameObject prop = null;
            try
            {
                handles[key] = Activator.CreateInstance(typeof(ContentAssetHandle<GameObject>), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { source, null }, null);
                configs[id] = null;
                hand.transform.localScale = new Vector3(.776f, .82f, .75f);
                hand.transform.rotation = Quaternion.Euler(23, 51, 17);
                var type = typeof(NpcActorView).Assembly.GetType("HexLive.UnityPresentation.Wearing.HandPropVisual");
                prop = (GameObject)type.GetMethod("Create", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { id, hand.transform, left });
                Assert.That(prop, Is.Not.Null);
                Assert.That(ObjectFit.TryMeasuredSize(prop, id, out var actual), Is.True);
                var expected = ObjectFit.TargetWorldSize(id);
                if (HexLive.Simulation.Content.GroundPileCatalog.TryGet(id, false, out var profile) && profile.TargetSize <= 0f)
                {
                    Assert.That(ObjectFit.TryMeasuredSize(source,id,out expected),Is.True);
                }
                Assert.That(actual, Is.EqualTo(expected).Within(.0001f));
            }
            finally
            {
                Object.DestroyImmediate(hand);
                if (hadHandle) handles[key] = oldHandle; else handles.Remove(key);
                if (hadConfig) configs[id] = oldConfig; else configs.Remove(id);
            }
        }
    }
}
#endif

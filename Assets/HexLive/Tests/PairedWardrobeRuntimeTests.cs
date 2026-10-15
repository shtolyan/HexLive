#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using HexLive.UnityPresentation.Wearing;

namespace HexLive.Tests
{
    public sealed class PairedWardrobeRuntimeTests
    {
        [Test, Order(0)]
        public void RebakingAPreparedSleevePreservesItsRuntimeResourceKeyAndGuid()
        {
            Assert.That(Application.isPlaying, Is.True, "Graphics PlayMode required");
            var baker = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("HexLive.UnityDebug.Editor.PaintPointMapGenerator"))
                .First(t => t != null);
            const string prefab = "Assets/HexLiveContent/People/Prefabs/Wear/Marta_PrimalArmWraps.prefab";
            var mesh = AssetDatabase.LoadAssetAtPath<GameObject>(prefab).GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
            string key = $"garment_{mesh.name}_{mesh.vertexCount}";
            string path = "Assets/HexLiveContent/People/PaintMaps/" + key + ".asset";
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Is.Not.Empty);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                baker.GetMethod("GenerateWebGLGarmentMaps").Invoke(null, new object[] { new[] { prefab } });
                var map = AssetDatabase.LoadAssetAtPath<PaintPointMap>(path);
                Assert.That(map.name, Is.EqualTo(key), "Repeat bake lost the runtime resource key");
                Assert.That(map.VertexCount, Is.EqualTo(mesh.vertexCount));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(File.ReadAllText(path), Does.Contain("m_Name: " + key), "Resource key must also survive saving");
            }
        }

        [Test, Order(1)]
        public void EveryPreparedItemSyncsItsBodyFitRunsAndUndresses()
        {
            Assert.That(Application.isPlaying, Is.True, "Graphics PlayMode required");
            var review = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("HexLive.UnityPresentation.Wearing.Editor.PeopleWardrobeFitReview"))
                .FirstOrDefault(t => t != null);
            Assert.That(review, Is.Not.Null, "Prepared wardrobe editor audit is missing");
            try { review.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, null); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
#endif

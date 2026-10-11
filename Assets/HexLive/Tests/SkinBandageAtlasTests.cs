#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class SkinBandageAtlasTests
    {
        private const string Root = "Assets/HexLiveContent/People";
        private static readonly string[] Zones = { "Torso", "Pelvis", "ArmL", "ArmR", "LegL", "LegR" };

        [TestCase("Marta")]
        [TestCase("Kshishtof")]
        public void AtlasRejectsStaleTopologyAndKeepsNormalCoverage(string actor)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + actor + ".prefab");
            Assert.That(ActorBodyResolver.TryResolve(prefab, out var body, out var error), Is.True, error);
            var atlas = SkinBandageAtlas.Load(body.sharedMesh);
            Assert.That(atlas, Is.Not.Null, actor);
            Assert.That(atlas.Zones.Select(z => z.Zone), Is.EquivalentTo(Zones));
            foreach (var zone in Zones)
            {
                var wrap = atlas.ForZone(zone);
                Assert.That(wrap, Is.Not.Null, actor + " " + zone);
                var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(wrap.Normal));
                Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.Default), "NormalMap packing destroys stamp RGB/coverage alpha");
                Assert.That(importer.sRGBTexture, Is.False);
                Assert.That(importer.alphaSource, Is.EqualTo(TextureImporterAlphaSource.FromInput));
            }
            var stale = new Mesh { name = body.sharedMesh.name, vertices = new[] { Vector3.zero } };
            try
            {
                Assert.That(SkinBandageAtlas.UsesAtlasLayout(stale), Is.True);
                Assert.That(SkinBandageAtlas.Load(stale), Is.Null, "Do not apply stale UV art to another topology");
            }
            finally { Object.DestroyImmediate(stale); }
        }

        [UnityTest]
        public IEnumerator EveryZonePaintsSkinAndReliefWithoutTouchingEyesOrTeeth()
        {
            Assert.That(Application.isPlaying, Is.True, "Run in graphics PlayMode");
            var caches = new List<(IDictionary cache, Dictionary<object, object> saved)>();
            var oldAsync = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                // Use the repository's local-source transport fixture; actual
                // painter loading, placement and rendering remain unchanged.
                foreach (var type in new[] { typeof(PaintPointMap), typeof(SkinPositionMapSet) })
                {
                    var cache = (IDictionary)type.GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    var saved = new Dictionary<object, object>();
                    foreach (DictionaryEntry entry in cache) saved.Add(entry.Key, entry.Value);
                    caches.Add((cache, saved)); cache.Clear();
                    foreach (var guid in AssetDatabase.FindAssets("t:" + type.Name, new[] { Root + "/PaintMaps" }))
                    {
                        var map = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid), type);
                        cache[map.name] = map;
                    }
                }
                foreach (var name in new[] { ActorName.Marta, ActorName.Kshishtof })
                {
                    var actor = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/Actors/" + name + ".prefab"));
                    try
                    {
                        actor.GetComponent<Animator>().enabled = false;
                        var bones = actor.GetComponent<BodyBones>(); bones.Construct(name, 433);
                        Assert.That(ActorBodyResolver.TryResolve(actor, out var body, out var error), Is.True, error);
                        var original = body.sharedMaterials.Select(m => m.GetTexture("_BaseMap")).ToArray();
                        var painter = actor.AddComponent<SkinTexturePainter>();
                        painter.Construct(body, new[] { 0 }, bones, actor.transform, 433, name.ToString());
                        foreach (var zone in Zones)
                        {
                            painter.Sync(new List<(string zone, int seed, float heal)>(), new HashSet<string> { zone });
                            var until = Time.realtimeSinceStartup + 15;
                            while (!painter.TryPaintPresentation() && Time.realtimeSinceStartup < until) yield return null;
                            Assert.That(painter.PresentationReady, Is.True, name + " " + zone);
                            Assert.That(painter.SlotHasAlbedoPaint(0), Is.True);
                            Assert.That(body.sharedMaterials[0].GetTexture("_BaseMap"), Is.TypeOf<RenderTexture>());
                            Assert.That(body.sharedMaterials[0].GetTexture("_BumpMap"), Is.TypeOf<RenderTexture>());
                            Assert.That(body.sharedMaterials[0].IsKeywordEnabled("_NORMALMAP"), Is.True);
                            for (var i = 1; i < original.Length; i++)
                                Assert.That(body.sharedMaterials[i].GetTexture("_BaseMap"), Is.SameAs(original[i]), name + " " + zone + " non-skin slot " + i);
                        }
                    }
                    finally { Object.DestroyImmediate(actor); }
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = oldAsync;
                foreach (var (cache, saved) in caches)
                {
                    cache.Clear(); foreach (var entry in saved) cache.Add(entry.Key, entry.Value);
                }
            }
        }
    }
}
#endif

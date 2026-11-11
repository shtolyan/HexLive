using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HexLive.Tests
{
    public sealed class CombatBloodRuntimeTests
    {
        [UnityTest]
        public IEnumerator RapidDistinctHitsSpawnTwoSplashes()
        {
            var actor = new GameObject("Combat blood test");
            var view = actor.AddComponent<NpcActorView>();
            view.SetSimSpeed(5f);
            var prefabNames = BloodSplashVfx.Prefabs.Where(prefab => prefab != null)
                .Select(prefab => prefab.name + "(Clone)").ToHashSet();
            var before = LiveSplashes(prefabNames);
            try
            {
                Assert.That(prefabNames, Is.Not.Empty);
                Assert.That(view.SignalHit(42, "", "LegL", Vector3.forward, visible: true), Is.True);
                Assert.That(view.SignalHit(42, "", "LegL", Vector3.forward, visible: true), Is.False);
                Assert.That(view.SignalHit(43, "tool.knife", "Torso", Vector3.forward, visible: true), Is.True);
                Assert.That(view.SignalHit(44, "tool.knife", "Torso", Vector3.forward, visible: false), Is.False);
                Assert.That(LiveSplashes(prefabNames).Except(before).Count(), Is.EqualTo(2));
            }
            finally
            {
                foreach (var splash in LiveSplashes(prefabNames).Except(before)) Object.Destroy(splash);
                Object.Destroy(actor);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator CloseHitsEachMarkTheWaterSurfaceImmediately()
        {
            var target = new GameObject("Water blood test");
            var water = target.AddComponent<WaterBloodStains>();
            try
            {
                water.OnNpcTick(1, 1f, Vector3.zero, 1f, 40);
                water.OnHit(1, .9f, Vector3.zero, 1f, 41);
                water.OnNpcTick(1, .9f, Vector3.zero, 1f, 41);
                water.OnHit(1, .8f, Vector3.zero, 1f, 42);
                water.OnNpcTick(1, .8f, Vector3.zero, 1f, 42);
                water.Advance(42);
                Assert.That(target.transform.childCount, Is.EqualTo(2));
                Assert.That(target.transform.GetChild(1).localScale.x, Is.GreaterThan(0f));
            }
            finally
            {
                Object.Destroy(target);
            }

            yield return null;
        }

        private static HashSet<GameObject> LiveSplashes(HashSet<string> prefabNames) =>
            Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None)
                .Select(particle => particle.transform.root.gameObject)
                .Where(root => prefabNames.Contains(root.name))
                .ToHashSet();
    }
}

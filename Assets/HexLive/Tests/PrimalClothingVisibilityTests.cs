#if UNITY_EDITOR
using System;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class PrimalClothingVisibilityTests
    {
        const string Root = "Assets/HexLiveContent/People/Prefabs/";
        static Wear Prefab(string actor, string item) =>
            AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Wear/" + actor + "_" + item + ".prefab").GetComponent<Wear>();
        static string[] Items(string actor, string outer) =>
            new[] { "PrimalBriefs", "PrimalTop", outer, "PrimalCollar" };

        [TestCase("Marta", false)]
        [TestCase("Marta", true)]
        [TestCase("Kshishtof", false)]
        [TestCase("Kshishtof", true)]
        public void SkirtAndShoulderFurKeepExistingUnderwearInEitherEquipOrder(string actor, bool reverse)
        {
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Actors/" + actor + ".prefab"));
            try
            {
                foreach (var animator in go.GetComponentsInChildren<Animator>()) animator.enabled = false;
                var bones = go.GetComponent<BodyBones>();
                bones.Construct((ActorName)Enum.Parse(typeof(ActorName), actor), 713);
                var items = Items(actor, "PrimalSkirt");
                foreach (var item in reverse ? items.Reverse() : items) bones.Equip(item, Prefab(actor, item), null);
                foreach (var item in items) AssertVisible(bones, item);
                bones.TakeOff("PrimalCollar");
                bones.TakeOff("PrimalSkirt");
                AssertVisible(bones, "PrimalBriefs");
                AssertVisible(bones, "PrimalTop");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase("Marta", false)]
        [TestCase("Marta", true)]
        [TestCase("Kshishtof", false)]
        [TestCase("Kshishtof", true)]
        public void FurDressAndCollarKeepUnderwearInEitherEquipOrder(string actor, bool reverse)
        {
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Actors/" + actor + ".prefab"));
            try
            {
                foreach (var animator in go.GetComponentsInChildren<Animator>()) animator.enabled = false;
                var bones = go.GetComponent<BodyBones>();
                bones.Construct((ActorName)Enum.Parse(typeof(ActorName), actor), 713);
                var items = Items(actor, "PrimalDress");
                foreach (var item in reverse ? items.Reverse() : items) bones.Equip(item, Prefab(actor, item), null);
                foreach (var item in items) AssertVisible(bones, item);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void EveryAuthoredSkirtAndBothFurFitsDisableTheirWholeMask()
        {
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/HexLiveContent/Wear" })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.Contains("/clothing.skirt_/") || p.Contains("/clothing.skirt_"))
                .Concat(from actor in new[] { "Marta", "Kshishtof" }
                        from item in new[] { "PrimalSkirt", "PrimalDress", "PrimalCollar" }
                        select Root + "Wear/" + actor + "_" + item + ".prefab").ToArray();
            Assert.That(paths.Length, Is.GreaterThanOrEqualTo(14));
            foreach (var path in paths)
            {
                var wear = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Wear>();
                Assert.That(wear, Is.Not.Null, path);
                foreach (var slot in wear.Slots) Assert.That(wear.HeedHideUnderwearSlot(slot), Is.False, path + ":" + slot);
                Assert.That(wear.HideWearSlots, Is.Empty, path);
                Assert.That(wear.HidesHair, Is.False, path);
            }
        }

        static void AssertVisible(BodyBones bones, string item)
        {
            var wear = bones.GetWorn(item);
            Assert.That(wear, Is.Not.Null, item + " must remain equipped");
            Assert.That(wear.GetComponentsInChildren<SkinnedMeshRenderer>().All(r => r.enabled), Is.True, item + " must remain rendered");
        }
    }
}
#endif

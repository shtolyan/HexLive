#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Config;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class ItemIconContentTests
    {
        [TestCase(ContentIds.Flint)]
        [TestCase(ContentIds.CrudeSaw)]
        [TestCase(ContentIds.Firestarter)]
        [TestCase(ContentIds.Torch)]
        public void SurvivalItemHasRenderableModelAndImportedInventorySprite(string id)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/" + id + ".fbx");
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/" + id + ".png");
            Assert.That(model, Is.Not.Null, id);
            Assert.That(HexLive.UnityPresentation.ObjectFit.HasRenderableGeometry(model), Is.True, id);
            Assert.That(sprite, Is.Not.Null, id);
            Assert.That(sprite.rect.size, Is.EqualTo(new Vector2(512, 512)), id);
            Assert.That(ItemIcons.Address(id), Is.EqualTo("object/" + id + "#icon"), id);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TorchKeepsSoftLightAtItsTipOnGroundAndUnderScaledHand(bool held)
        {
            var parent = new GameObject("Torch test hand");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HexLiveContent/RuntimeSource/Objects/tool.torch.fbx");
            try
            {
                if (held)
                {
                    parent.transform.rotation = Quaternion.Euler(-35, 40, 70);
                    parent.transform.localScale = Vector3.one * .013f;
                }
                var model = Object.Instantiate(prefab, parent.transform);
                model.transform.localScale *= HexLive.UnityPresentation.ObjectFit.FitScaleFactor(model, ContentIds.Torch);
                var fire = HexLive.UnityPresentation.Environment.PortableTorchVisual.Attach(model);
                var tip = model.GetComponentsInChildren<Transform>().Single(t => t.name == "fire_point");
                var light = fire.GetComponentInChildren<Light>();
                Assert.That(Vector3.Distance(fire.transform.position, tip.position), Is.LessThan(.001f));
                Assert.That(Vector3.Distance(light.transform.position, tip.position), Is.LessThan(.2f),
                    "The illumination radius must not lift the light away from a handheld flame.");
                Assert.That(light.range, Is.EqualTo(7.5f).Within(.001f));
                Assert.That(light.intensity, Is.GreaterThan(0).And.LessThanOrEqualTo(1.6f));
                Assert.That(Vector3.Distance(fire.transform.lossyScale, Vector3.one), Is.LessThan(.001f));
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void ProstheticSimulationIdsResolveToTheirAtomicOwners()
        {
            Assert.That(ItemIcons.Address("prosthetic.arm.wood"), Is.EqualTo("prosthetic/arm.wood.l#icon"));
            Assert.That(ItemIcons.Address("prosthetic.leg.mechanical"), Is.EqualTo("prosthetic/leg.mechanical.l#icon"));
        }

        [Test]
        public void EveryPreparedWearHasAnImportedSpriteAndBuilderIncludesItsExactPath()
        {
            var rows = (JArray)JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/catalog.json"))["records"];
            var builder = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("AtomicContentBatchBuild"))
                .First(t => t != null);
            var resolve = builder.GetMethod("ResolvePeople", BindingFlags.NonPublic | BindingFlags.Static);
            var count = 0;
            foreach (var row in rows.Where(row => (string)row["type"] == "wear"))
            {
                var id = (string)row["id"]; var path = "Assets/HexLiveContent/Icons/" + id + ".png";
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                Assert.That(sprite, Is.Not.Null, id); Assert.That(sprite.rect.size, Is.EqualTo(new Vector2(512, 512)), id);
                var inputs = resolve.Invoke(null, new object[] { row });
                Assert.That(inputs.GetType().GetField("Icon").GetValue(inputs), Is.EqualTo(path), id);
                count++;
            }
            Assert.That(count, Is.EqualTo(82));
        }

        [Test]
        public void ClothAndSharpStoneWorldArtIconsAndGearImportAsBuildSources()
        {
            var cloth = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/RuntimeSource/Objects/resource.cloth.fbx");
            var sharpStone = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/HexLiveContent/RuntimeSource/Objects/tool.sharp_stone.fbx");
            var clothIcon = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/HexLiveContent/Icons/resource.cloth.png");
            var sharpStoneIcon = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/HexLiveContent/Icons/tool.sharp_stone.png");
            var gear = AssetDatabase.LoadAssetAtPath<GearConfig>(
                "Assets/HexLiveContent/RuntimeSource/Gear/sharp_stone.asset");

            Assert.That(cloth, Is.Not.Null);
            Assert.That(cloth.GetComponentsInChildren<Renderer>(true), Has.Length.GreaterThanOrEqualTo(2));
            Assert.That(sharpStone, Is.Not.Null);
            Assert.That(sharpStone.GetComponentsInChildren<Renderer>(true), Has.Length.GreaterThanOrEqualTo(1));
            Assert.That(clothIcon, Is.Not.Null);
            Assert.That(clothIcon.rect.size, Is.EqualTo(new Vector2(512, 512)));
            Assert.That(sharpStoneIcon, Is.Not.Null);
            Assert.That(sharpStoneIcon.rect.size, Is.EqualTo(new Vector2(512, 512)));
            Assert.That(gear, Is.Not.Null);
            Assert.That(gear.gearId, Is.EqualTo(ContentIds.SharpStone));
            Assert.That(gear.capabilities, Does.Contain(GearCapability.CutPlant));
            Assert.That(gear.capabilities, Does.Contain(GearCapability.ButcherAnimal));
            Assert.That(gear.craftIngredients.Sum(input => input.count), Is.EqualTo(2));
        }

    }
}
#endif

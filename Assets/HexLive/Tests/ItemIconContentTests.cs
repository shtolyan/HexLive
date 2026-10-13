#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.AI;
using HexLive.Simulation.Runtime;
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
            Assert.That(count, Is.EqualTo(78));
        }

    }
}
#endif

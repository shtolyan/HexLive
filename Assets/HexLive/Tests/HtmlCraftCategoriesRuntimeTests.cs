#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class HtmlCraftCategoriesRuntimeTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void AiCraftModelRetainsRecipesAndDetailsButNeverAcceptsStaleReadyOptions()
    {
        var host = new GameObject("AI craft browser verification"); host.SetActive(false);
        var panel = host.AddComponent<CharacterPanel>();
        try
        {
            var type = typeof(CharacterPanel);
            type.GetField("_craftAvailable", Private).SetValue(panel, true);
            var options = (List<CraftRecipeOption>)type.GetField("_craftOptions", Private).GetValue(panel);
            options.Add(new CraftRecipeOption { OutputDefinitionId = "tool.hammer", CanCraft = true });
            var canOrder = type.GetField("_craftCanOrder", Private);
            var build = type.GetMethod("CraftModel", Private);
            var npc = new NpcSnapshot();
            JObject Model() => (JObject)build.Invoke(panel, new object[] { npc });
            canOrder.SetValue(panel, true);
            var manual = Model();
            Assert.That((bool)manual["detail"]["tool.hammer"]["canQueue"], Is.True);
            canOrder.SetValue(panel, false);
            var ai = Model();
            Assert.That(ai["blocked"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(JToken.DeepEquals(ai["recipes"], manual["recipes"]), Is.True);
            Assert.That(JToken.DeepEquals(ai["detail"]["tool.hammer"]["ingredients"],
                manual["detail"]["tool.hammer"]["ingredients"]), Is.True);
            Assert.That((bool)ai["detail"]["tool.hammer"]["canQueue"], Is.False);
            Assert.That((string)ai["detail"]["tool.hammer"]["block"], Is.EqualTo("NotManual"));
        }
        finally { Object.DestroyImmediate(host); }
    }

    [Test]
    public void RealCraftModelHasOneCategoryPerIdWithoutDroppingRecipesOrAccumulatingRefreshes()
    {
        var host = new GameObject("HTML craft category verification"); host.SetActive(false);
        var panel = host.AddComponent<CharacterPanel>();
        var options = (List<CraftRecipeOption>)typeof(CharacterPanel).GetField("_craftOptions", Private).GetValue(panel);
        var available = typeof(CharacterPanel).GetField("_craftAvailable", Private);
        var build = typeof(CharacterPanel).GetMethod("CraftModel", Private);
        var npc = new NpcSnapshot();
        try
        {
            var ids = JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/catalog.json"))["records"]
                .Where(r => (string)r["type"] == "wear").Select(r => (string)r["id"])
                .Concat(new[] { "tool.bow", "tool.hammer", "tool.machete", "resource.rope", "resource.cloth",
                    "food.meat", "food.fish", "prosthetic.arm.wood", "prosthetic.leg.wood", "other.fixture" }).Distinct().ToArray();
            foreach (var id in ids) options.Add(new CraftRecipeOption { OutputDefinitionId = id, CanCraft = true });
            available.SetValue(panel, true);
            JObject Model() => (JObject)build.Invoke(panel, new object[] { npc });
            var model = Model();
            var categories = model["categories"].Values<string>().ToArray();
            Assert.That(categories, Is.EqualTo(ids.Select(id => CraftCategories.For(id).ToString().ToLowerInvariant()).Distinct().ToArray()));
            Assert.That(categories.Count(c => c == "clothing"), Is.EqualTo(1));
            Assert.That(categories.Count(c => c == "tools"), Is.EqualTo(1));
            Assert.That(model["recipes"].Select(r => (string)r["id"]), Is.EqualTo(ids));
            Assert.That(((JObject)model["detail"]).Count, Is.EqualTo(ids.Length));
            Assert.That(Model().ToString(), Is.EqualTo(model.ToString()), "Refresh must not append duplicate categories or recipes.");
            var folder = "Build/Diagnostics/20261019-bug492"; Directory.CreateDirectory(folder);
            File.WriteAllText(folder + "/craft-model.json", model.ToString());
            options.Clear();
            Assert.That(Model()["categories"].Count(), Is.Zero);
            Assert.That(Model()["recipes"].Count(), Is.Zero);
            available.SetValue(panel, false);
            Assert.That((string)Model()["blocked"], Is.Not.Empty);
        }
        finally { Object.DestroyImmediate(host); }
    }
}
}
#endif

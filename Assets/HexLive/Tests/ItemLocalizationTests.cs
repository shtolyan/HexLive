#if UNITY_EDITOR
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.UI;
using System.Text.RegularExpressions;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HexLive.Tests
{

public sealed class ItemLocalizationTests
{
    [TestCase(ContentIds.Flint)]
    [TestCase(ContentIds.CrudeSaw)]
    [TestCase(ContentIds.Firestarter)]
    [TestCase(ContentIds.Torch)]
    public void SurvivalItemNameAndDescriptionResolveFromImportedSource(string id)
    {
        var original = Loc.Current;
        try
        {
            foreach (var language in new[] { Language.English, Language.Russian })
            {
                Loc.Current = language;
                foreach (var suffix in new[] { ".name", ".desc" })
                {
                    var key = "item." + ItemInfo.Slug(id) + suffix;
                    Assert.That(Loc.Has(key), Is.True, key + " " + language);
                    Assert.That(Loc.Get(key), Is.Not.Empty.And.Not.EqualTo(key), key);
                    if (language == Language.Russian)
                        Assert.That(Loc.Get(key), Does.Match("[А-Яа-яЁё]"), key);
                }
            }
        }
        finally { Loc.Current = original; }
    }

    [TestCase(ContentIds.Flint, "Flint", "Кремень")]
    [TestCase(ContentIds.CrudeSaw, "Crude saw", "Самодельная пила")]
    [TestCase(ContentIds.Firestarter, "Firestarter", "Огниво")]
    [TestCase(ContentIds.Torch, "Torch", "Факел")]
    public void SurvivalUiReadsImportedNamesBeforeCatalogEnglishFallback(string id, string english, string russian)
    {
        var original = Loc.Current;
        var definition = new ObjectDefinition { Id = id, DisplayName = "UNLOCALIZED CATALOG FALLBACK" };
        var info = ItemCatalog.Resolve(definition);
        var name = typeof(CharacterPanel).GetMethod("ItemName", BindingFlags.NonPublic | BindingFlags.Static);
        var description = typeof(CharacterPanel).GetMethod("ItemDesc", BindingFlags.NonPublic | BindingFlags.Static);
        try
        {
            foreach (var language in new[] { Language.English, Language.Russian })
            {
                Loc.Current = language;
                var expected = language == Language.Russian ? russian : english;
                Assert.That(Loc.Get(info.NameKey), Is.EqualTo(expected));
                Assert.That(name.Invoke(null, new object[] { definition, info }), Is.EqualTo(expected),
                    "Crafting, inventory and HTML ItemModel share CharacterPanel.ItemName.");
                Assert.That(description.Invoke(null, new object[] { definition, info }),
                    Is.EqualTo(Loc.Get(info.DescKey)));
                Assert.That(CraftProjectDetailsPanel.ItemName(null, id), Is.EqualTo(expected));
                AssertImportedTerm(info.NameKey);
            }
        }
        finally { Loc.Current = original; }
    }

    [TestCase("TendGarden", "Tending the garden", "Ухаживает за грядкой")]
    [TestCase("CraftHammer", "Making a hammer", "Делает молоток")]
    public void SurvivalGoalsResolveFromImportedTermsForDesktopAndHtml(string goal, string english, string russian)
    {
        var original = Loc.Current;
        try
        {
            Loc.Current = Language.Russian;
            Assert.That(Loc.Goal(goal), Is.EqualTo(russian));
            AssertImportedTerm("goal." + goal);
            Loc.Current = Language.English;
            Assert.That(Loc.Goal(goal), Is.EqualTo(english));
        }
        finally { Loc.Current = original; }
    }

    private static void AssertImportedTerm(string key)
    {
        var source = Resources.Load<ScriptableObject>("I2Languages");
        Assert.That(source, Is.Not.Null);
        var terms = new SerializedObject(source).FindProperty("mSource.mTerms");
        var matching = 0;
        for (var index = 0; index < terms.arraySize; index++)
            if (terms.GetArrayElementAtIndex(index).FindPropertyRelative("Term").stringValue == key) matching++;
        Assert.That(matching, Is.EqualTo(1), "The imported source must expose exactly one " + key);
    }

    [Test]
    public void ImportedItemTermsHaveRussianAndBackpacksResolveThroughRuntimeLoc()
    {
        var source = Resources.Load<ScriptableObject>("I2Languages");
        Assert.That(source, Is.Not.Null);
        var serialized = new SerializedObject(source);
        var languages = serialized.FindProperty("mSource.mLanguages");
        Assert.That(languages.arraySize, Is.EqualTo(2));
        Assert.That(languages.GetArrayElementAtIndex(0).FindPropertyRelative("Code").stringValue, Is.EqualTo("en"));
        Assert.That(languages.GetArrayElementAtIndex(1).FindPropertyRelative("Code").stringValue, Is.EqualTo("ru"));
        var terms = serialized.FindProperty("mSource.mTerms");
        for (int i = 0; i < terms.arraySize; i++)
        {
            var term = terms.GetArrayElementAtIndex(i);
            var key = term.FindPropertyRelative("Term").stringValue;
            if (!key.StartsWith("item.")) continue;
            var values = term.FindPropertyRelative("Languages");
            Assert.That(values.arraySize, Is.EqualTo(2), key);
            Assert.That(values.GetArrayElementAtIndex(0).stringValue, Is.Not.Null.And.Not.Empty, key);
            var ru = values.GetArrayElementAtIndex(1).stringValue;
            Assert.That(Regex.IsMatch(ru, "[А-Яа-яЁё]"), Is.True, key);
            if (key.EndsWith(".name")) Assert.That(Regex.IsMatch(ru, "[A-Za-z]"), Is.False, key);
        }
        var original = Loc.Current;
        try
        {
            Loc.Current = Language.Russian;
            var rows = JObject.Parse(System.IO.File.ReadAllText("Assets/HexLiveContent/People/catalog.json"))["records"];
            foreach (var row in rows.Where(r => r.Value<string>("type") == "wear"))
            {
                var key = "item." + ItemInfo.Slug(row.Value<string>("id"));
                Assert.That(Loc.Get(key + ".name"), Does.Match("[А-Яа-яЁё]"), key);
                Assert.That(Loc.Get(key + ".desc"), Does.Match("[А-Яа-яЁё]"), key);
            }
            var ru = Loc.Get("item.gear_backpack_primal_hunter.name");
            Assert.That(ru, Is.EqualTo("Рюкзак охотника"));
            Loc.Current = Language.English;
            Assert.That(Loc.Get("item.gear_backpack_primal_hunter.name"), Is.EqualTo("Hunter Backpack"));
            Assert.That(Loc.Get("item.gear_backpack_primal_hunter.name"), Is.Not.EqualTo(ru));
        }
        finally { Loc.Current = original; }
    }
}
}
#endif

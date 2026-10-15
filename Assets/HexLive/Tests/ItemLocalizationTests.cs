#if UNITY_EDITOR
using System.Linq;
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

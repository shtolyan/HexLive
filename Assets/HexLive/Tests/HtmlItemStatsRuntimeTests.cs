#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class HtmlItemStatsRuntimeTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private static object Call(object panel, string name, params object[] args) =>
            typeof(CharacterPanel).GetMethod(name, Instance).Invoke(panel, args);
        private static JArray Rows(VisualElement rows) => (JArray)typeof(CharacterPanel)
            .GetMethod("StatRows", Static).Invoke(null, new object[] { rows });
        private static string Palette(string name) => "#" + ColorUtility.ToHtmlStringRGB(
            (Color)typeof(CharacterPanel).GetField(name, Static).GetValue(null));

        [Test]
        public void HtmlStatsPreserveAuthoredComparisonColoursAndAllMeterEndpoints()
        {
            var host = new GameObject("Item stat export verification");
            host.SetActive(false);
            var panel = host.AddComponent<CharacterPanel>();
            try
            {
                var rows = new VisualElement();
                foreach (var comparison in new[] { (12f, true, "Good"), (8f, true, "Crit"), (10f, true, "TextDim"), (8f, false, "Good") })
                {
                    var color = (Color)typeof(CharacterPanel).GetMethod("StatComparisonColor", Static)
                        .Invoke(null, new object[] { 10f, comparison.Item1, .001f, comparison.Item2 });
                    rows.Add((VisualElement)Call(panel, "MakeStatRow", "Damage", "10 → " + comparison.Item1, color));
                    Assert.That((string)Rows(rows).Last[2], Is.EqualTo(Palette(comparison.Item3)));
                    Assert.That(Rows(rows).Last[3].Type, Is.EqualTo(JTokenType.Null));
                }
                foreach (var value in new[] { 0f, .2f, .5f, .9f, 1f })
                {
                    var block = (VisualElement)Call(panel, "MakeClothingHpBlock", value, "inv.clothing_hp");
                    rows.Add(block);
                    var row = Rows(rows).Last;
                    Assert.That((float)row[3], Is.EqualTo(value));
                    Assert.That((string)row[2], Is.EqualTo(Palette(value < .35f ? "Crit" : value < .65f ? "Warn" : "Good")));
                    Assert.That((string)row[1], Does.Contain(Mathf.RoundToInt(value * 100) + "%"));
                }
                var npc = new NpcSnapshot();
                npc.InventoryWater.Add("1\ttool.bottle\t1\t4\t1");
                npc.InventoryWater.Add("7\ttool.bottle\t3\t4\t1");
                var first = (JObject)Call(panel, "HtmlItemInformation", "tool.bottle", npc, false, 1, 1, null);
                var second = (JObject)Call(panel, "HtmlItemInformation", "tool.bottle", npc, false, 7, 1, null);
                foreach (var pair in new[] { (first, .25f), (second, .75f) })
                {
                    var meter = pair.Item1["stats"].Single(r => r[3].Type == JTokenType.Float);
                    Assert.That((float)meter[3], Is.EqualTo(pair.Item2));
                    Assert.That((string)meter[2], Is.EqualTo(Palette("Warn")));
                }
                var output = "Build/Diagnostics/20261019-bug490";
                Directory.CreateDirectory(output);
                File.WriteAllText(output + "/item-cards.json", new JObject
                {
                    ["stats"] = Rows(rows), ["water25"] = first, ["water75"] = second,
                }.ToString());
            }
            finally { Object.DestroyImmediate(host); }
        }
    }
}
#endif

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§58.5: player-facing enum names must never leak into the UI.</summary>
public sealed class LocalizationCoverageContractTests
{
    [Test]
    public void AllItemNamesAndPreparedWearDescriptionsHaveEnglishAndRussian()
    {
        var source = RepoPaths.ReadText(Path.Combine(RepoPaths.Root, "Assets/Resources/I2Languages.asset"));
        var terms = Regex.Matches(source, @"^    - Term: (?:'([^'\r\n]+)'|([^\r\n]+))\n(.*?)(?=^    - Term: |^    mLanguages:)", RegexOptions.Multiline | RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, m => m.Groups[3].Value);
        void Check(string key)
        {
            Assert.That(terms.ContainsKey(key), Is.True, key);
            var flow = Regex.Match(terms[key], @"      Languages: (\[[^\n]+\])");
            if (flow.Success)
            {
                var parsed = JsonSerializer.Deserialize<string[]>(flow.Groups[1].Value);
                Assert.That(parsed, Has.Length.EqualTo(2), key);
                Assert.That(parsed[0], Is.Not.Empty, key);
                Assert.That(parsed[1], Does.Match("[А-Яа-яЁё]"), key);
                return;
            }
            var languages = Regex.Match(terms[key], @"      Languages:\s*\n(.*?)(?=      (?:Flags|Languages_Touch):)", RegexOptions.Singleline).Groups[1].Value;
            var values = Regex.Matches(languages, @"^      - (.*?)(?=^      - |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.That(values, Has.Count.EqualTo(2), key);
            Assert.That(values[0].Groups[1].Value.Trim(), Is.Not.Empty, key);
            Assert.That(Regex.IsMatch(values[1].Groups[1].Value, @"[А-Яа-яЁё]|\\u04[0-9a-fA-F]{2}"), Is.True, key + " requires Russian, not an English fallback");
        }
        var world = TestWorld.CreateWorld();
        var simdata = JsonNode.Parse(File.ReadAllText(RepoPaths.SimData));
        var people = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "Assets/HexLiveContent/People/catalog.json")));
        var preparedIds = people["records"].AsArray().Where(r => r["type"].GetValue<string>() == "wear").Select(r => r["id"].GetValue<string>()).ToArray();
        var ids = world.Content.ObjectDefinitions.Keys.Concat(simdata["garments"].AsArray().Select(g => g["id"].GetValue<string>())).Concat(preparedIds).Distinct();
        foreach (var id in ids.Where(id => id != ContentIds.LeatherPants)) Check("item." + ItemInfo.Slug(id) + ".name");
        foreach (var id in preparedIds) Check("item." + ItemInfo.Slug(id) + ".desc");
    }

    [Test]
    public void EveryGoalHasEnglishAndRussianLocalization()
    {
        var source = RepoPaths.ReadText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        foreach (var goal in Enum.GetNames<GoalType>())
        {
            var marker = $"    - Term: goal.{goal}\n";
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                $"goal.{goal} is missing and would be rendered as a raw enum name");
            var next = source.IndexOf("    - Term: ", start + marker.Length,
                StringComparison.Ordinal);
            var block = source[start..(next < 0 ? source.Length : next)];
            Assert.That(Regex.IsMatch(block,
                    @"Languages:\s*\n\s*-\s+.+\n\s*-\s+.+",
                    RegexOptions.CultureInvariant),
                Is.True, $"goal.{goal} must have non-empty English and Russian translations");
        }
    }

    [Test]
    public void EveryStatusEffectHasLocalizedTitleAndDescription()
    {
        var source = RepoPaths.ReadText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        foreach (var effect in Enum.GetNames<EffectKind>())
        {
            var id = effect.ToLowerInvariant();
            Assert.That(source, Does.Contain($"    - Term: effect.{id}.title\n"),
                $"effect.{id}.title is missing");
            Assert.That(source, Does.Contain($"    - Term: effect.{id}.desc\n"),
                $"effect.{id}.desc is missing");
        }
    }
}

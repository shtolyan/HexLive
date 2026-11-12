using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HexLive.Simulation.Agents;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Bug #733 (§67.16): every race (<see cref="NpcSpeechOrigin"/>) has its name,
/// description and picture, and both character cards draw the badge after the name.
/// </summary>
public sealed class RaceBadgeContractTests
{
    private static string Repo(params string[] parts) =>
        Path.Combine(new[] { RepoPaths.Root }.Concat(parts).ToArray());

    [Test]
    public void EveryRaceHasBilingualTermsInsideMTerms()
    {
        var source = RepoPaths.ReadText(Repo("Assets", "Resources", "I2Languages.asset"));
        // Same reading as LocalizationCoverageContractTests: only mSource.mTerms is imported.
        var block = Regex.Match(source, @"^    mTerms:\n(.*?)(?=^    [A-Za-z_])",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.That(block.Success, Is.True, "mSource.mTerms must be a serialized sequence");
        var terms = Regex.Matches(block.Groups[1].Value,
                @"^    - Term: (?:'([^'\r\n]+)'|([^\r\n]+))\n(.*?)(?=^    - Term: |\z)",
                RegexOptions.Multiline | RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value,
                m => m.Groups[3].Value);

        void Check(string key)
        {
            Assert.That(terms.ContainsKey(key), Is.True, key);
            var languages = Regex.Match(terms[key],
                @"      Languages:\s*\n(.*?)(?=      (?:Flags|Languages_Touch):)",
                RegexOptions.Singleline).Groups[1].Value;
            var values = Regex.Matches(languages, @"^      - (.*?)(?=^      - |\z)",
                RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.That(values, Has.Count.EqualTo(2), key);
            Assert.That(values[0].Groups[1].Value.Trim(' ', '\'', '"', '\n'), Is.Not.Empty, key);
            Assert.That(Regex.IsMatch(values[1].Groups[1].Value, @"[А-Яа-яЁё]|\\u04[0-9a-fA-F]{2}"),
                Is.True, key + " requires Russian, not an English fallback");
        }

        Check("race.tooltip");
        foreach (var origin in Enum.GetNames<NpcSpeechOrigin>())
        {
            Check($"race.{origin.ToLowerInvariant()}.name");
            Check($"race.{origin.ToLowerInvariant()}.desc");
        }
    }

    [Test]
    public void EveryRaceHasAPlayerResourcePictureAndBothCardsDrawIt()
    {
        var race = File.ReadAllText(Repo("Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.Race.cs"));
        var panel = File.ReadAllText(Repo("Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.cs"));
        var html = File.ReadAllText(Repo("Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.Html.cs"));
        var page = File.ReadAllText(Repo("Assets", "WebGLTemplates", "HexLive", "ui", "screens", "character.js"));

        Assert.Multiple(() =>
        {
            foreach (var origin in Enum.GetNames<NpcSpeechOrigin>())
            {
                // Player Resources like the identity icons: no content bundle needed.
                var png = Repo("Assets", "Resources", "HexLive", "UI", $"Race{origin}Icon.png");
                Assert.That(File.Exists(png), Is.True, png);
                Assert.That(File.Exists(png + ".meta"), Is.True, png + ".meta");
                Assert.That(race, Does.Contain($"\"HexLive/UI/Race{origin}Icon\""), origin);
                var slug = origin.ToLowerInvariant();
                Assert.That(race.Contains($"NpcSpeechOrigin.{origin} => \"{slug}\"") ||
                            race.Contains($"_ => \"{slug}\""), Is.True, origin);
            }

            Assert.That(panel, Does.Contain("nameRow.Add(_nameLabel);\n            nameRow.Add(BuildRaceBadge());")
                .Or.Contain("nameRow.Add(_nameLabel);\r\n            nameRow.Add(BuildRaceBadge());"),
                "The badge sits right after the name");
            Assert.That(panel, Does.Contain("RefreshRaceBadge(npc.SpeechOrigin)"));
            Assert.That(html, Does.Contain("[\"race\"] = HtmlRace(npc.SpeechOrigin)"));
            Assert.That(page, Does.Contain("<${NameCopy} npc=${npc} /><${RaceBadge} race=${npc.race} />"));
            Assert.That(race, Does.Not.Contain("new Label("), "No emoji/text stand-in for the picture");
        });
    }
}

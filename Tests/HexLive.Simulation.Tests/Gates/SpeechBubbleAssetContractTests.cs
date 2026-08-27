using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§67.10 / #248: the bootstrap Resources UI must ship the complete
/// speech bubble, not only its code and styles.</summary>
public sealed class SpeechBubbleAssetContractTests
{
    [Test]
    public void BubbleBackgroundAndEveryCatalogPictureExistInBootstrapResources()
    {
        var ui = Path.Combine(RepoPaths.Root, "Assets", "Resources", "HexLive", "UI");
        Assert.That(Path.Combine(ui, "speech_bubble.png"), Does.Exist,
            "Белая подложка бабла потеряна при переносе bootstrap UI.");

        var catalog = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "UI",
            "SpeechCatalog.cs"));
        var iconNames = new HashSet<string>();
        foreach (Match match in Regex.Matches(
                     catalog, "new\\(\\\"([A-Za-z0-9_]+)\\\""))
        {
            iconNames.Add(match.Groups[1].Value);
        }

        iconNames.Add("SmallTalk");
        iconNames.Add("Warning");
        iconNames.Add("pop_plus");
        iconNames.Add("pop_plus2");
        iconNames.Add("pop_minus");
        iconNames.Add("pop_minus2");
        foreach (var icon in iconNames)
        {
            Assert.That(Path.Combine(ui, "Emoji", icon + ".png"), Does.Exist,
                $"SpeechCatalog ссылается на отсутствующую картинку {icon}.png");
        }
    }
}

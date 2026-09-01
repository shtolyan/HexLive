using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§150.2 (bug #343): ростер сообщает, сколько членов лагеря сейчас
/// видно, не раскрывая карточки и координаты ушедших из восприятия.</summary>
public sealed class CampRosterVisibilityCountContractTests
{
    [Test]
    public void ClanHeaderKeepsVisibleAndAuthoritativeTotalsSeparate()
    {
        var panel = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.cs"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("if (clan) totalClan++;"),
                "Общий состав надо считать до fog-фильтра карточек.");
            Assert.That(panel, Does.Contain("visibleClan++;"),
                "Числитель должен расти только у реально добавленной карточки.");
            Assert.That(panel, Does.Contain("Loc.Get(\"roster.clan_count\")"));
            Assert.That(localization, Does.Contain("Term: roster.clan_count"));
            Assert.That(localization, Does.Contain("Your camp \\u00b7 {0}/{1}"));
        });
    }
}

}

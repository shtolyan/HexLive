using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §152.5: терминальные Missing/Failed живут до следующего ЗДОРОВОГО
/// обновления реестра, а не до конца сессии. Один момент деградированного
/// реестра (оффлайн-пин) не имеет права навсегда оставить id без модели —
/// а прощение обязано быть гейтировано здоровьем реестра, иначе каждый
/// деградированный refresh повторял бы загрузку и дублировал LogError.
/// </summary>
public sealed class ContentTerminalForgivenessContractTests
{
    [Test]
    public void HealthyRegistryRefreshForgivesTerminalStatuses()
    {
        var cache = Read("Content", "ContentPrefabCache.cs");

        Assert.Multiple(() =>
        {
            Assert.That(cache,
                Does.Contain("RegistryRefreshed += ForgiveTerminalOnHealthyRegistry"),
                "Кэш обязан слушать обновление реестра.");
            Assert.That(cache, Does.Contain("LastError.Length != 0"),
                "Деградированный refresh (оффлайн-пин) не прощает ничего.");
            Assert.That(cache, Does.Contain("Terminal.Clear();"),
                "Здоровый refresh обязан снимать терминальные Missing/Failed.");
        });
    }

    private static string Read(string folder, string file) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            folder, file));
}

}

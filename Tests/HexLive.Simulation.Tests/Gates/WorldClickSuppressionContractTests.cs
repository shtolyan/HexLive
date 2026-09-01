using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §121 / bug #279: клик, закрывший окно, не смеет становиться мировым
/// приказом. Окна закрываются на pointer-DOWN в Update-фазе UI Toolkit, а
/// гейт мира читается в LateUpdate камеры — press-защёлки обязаны учитывать
/// состояние гейта на ПРОШЛОМ кадре. Окно отчёта об ошибке при этом обязано
/// блокировать мир собственным флагом: общий NpcSelection.PointerOverUi
/// каждый кадр затирается CharacterPanel.UpdatePointerOverUi.
/// </summary>
public sealed class WorldClickSuppressionContractTests
{
    [Test]
    public void PressLatchesRememberLastFrameUiOwnership()
    {
        var camera = Read("Input", "RtsCameraController.cs");

        Assert.Multiple(() =>
        {
            Assert.That(camera, Does.Contain(
                "_worldPointerBlockedLastFrame = PointerBlockedForWorld();"),
                "Семпл гейта в конце LateUpdate — источник памяти о прошлом кадре.");
            Assert.That(camera, Does.Contain(
                "_leftPressActive = !PointerBlockedForWorld() &&\n" +
                "                    !_worldPointerBlockedLastFrame;"),
                "Левый press обязан гаснуть в кадр закрытия окна.");
            Assert.That(camera, Does.Contain(
                "_rightPressActive = !PointerBlockedForWorld() &&\n" +
                "                    !_worldPointerBlockedLastFrame;"),
                "Правый press обязан гаснуть в кадр закрытия окна.");
        });
    }

    [Test]
    public void BugReportWindowBlocksTheWorldWithItsOwnFlag()
    {
        var camera = Read("Input", "RtsCameraController.cs");
        var input = Read("Input", "SimulationInputAdapter.cs");
        var panel = Read("UI", "BugReportPanel.cs");

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("public static bool IsOpen"));
            Assert.That(camera, Does.Contain("UI.BugReportPanel.IsOpen"));
            Assert.That(input, Does.Contain("BugReportPanel.IsOpen"));
        });
    }

    private static string Read(string folder, string file) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            folder, file));
}

}

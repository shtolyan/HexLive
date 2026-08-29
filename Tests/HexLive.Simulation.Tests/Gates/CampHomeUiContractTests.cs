using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §146.14 (bug #291): пункт «Сделать домом» на своём очаге. Команда идёт
/// EnqueueCommand'ом — перенос якоря лагеря не смеет переводить девушку в
/// ручной режим; предикат «свой/чужой» — общий CampHomeMath по снапшоту.
/// </summary>
public sealed class CampHomeUiContractTests
{
    [Test]
    public void MakeHomeMenuEntryUsesTheSharedPredicateAndPlainCommand()
    {
        var adapter = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Input", "SimulationInputAdapter.cs"));
        var terms = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("menu.make_home"));
            Assert.That(adapter, Does.Contain("new SetCampHomeCommand("));
            Assert.That(adapter, Does.Contain("CampHomeMath.IsForeignCampTile("));
            Assert.That(adapter, Does.Not.Contain("EnqueueOrder(actorId,\n                        new SetCampHomeCommand"),
                "Перенос якоря не смеет отбирать управление у девушки.");
            Assert.That(terms, Does.Contain("Term: menu.make_home"));
            Assert.That(terms, Does.Contain("Term: toast.order_rejected.ForeignCamp"));
            Assert.That(terms, Does.Contain("Term: toast.order_rejected.NotAHearth"));
            Assert.That(terms, Does.Contain("Term: toast.order_rejected.AlreadyHome"));
        });
    }
}

}

using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §54 / баг #352: расщепление бревна на палки (split.log) и распил на доски
/// (saw.log) — одно и то же взаимодействие Process, и инструмент в руке обязан
/// следовать за КОНКРЕТНЫМ действием. Пила после #300 бревно не рубит вовсе
/// (у неё нет ни ChopWood, ни Cut), поэтому «палки в результате, пила в руке» —
/// не косметика: Process-поза машет пилой как топором.
/// </summary>
public sealed class LogSplitHandPropContractTests
{
    [Test]
    public void SplitLogOrderExportsAxeEvenWhenTheCampNeedsBoardsAndSheCarriesASaw()
    {
        var world = CreateWorldWithBoardDemand(352, out var npc, out var log);
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Axe));
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Saw));

        StartProcess(npc, log, "split.log");

        Assert.That(HeldItemOf(world, npc), Is.EqualTo(GearCatalog.Axe));
    }

    [Test]
    public void SplitLogOrderFallsBackToTheKnifeWhenOnlyAKnifeAndASawAreCarried()
    {
        var world = CreateWorldWithBoardDemand(3521, out var npc, out var log);
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Knife));
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Saw));

        StartProcess(npc, log, "split.log");

        Assert.That(HeldItemOf(world, npc), Is.EqualTo(GearCatalog.Knife));
    }

    // Bug #309 не должен воскреснуть вместе с починкой #352.
    [Test]
    public void SawLogOrderStillExportsTheSaw()
    {
        var world = CreateWorldWithBoardDemand(3522, out var npc, out var log);
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Axe));
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Saw));

        StartProcess(npc, log, "saw.log");

        Assert.That(HeldItemOf(world, npc), Is.EqualTo(GearCatalog.Saw));
    }

    // Безымянный шаг — обычный план ИИ. Там распил выбирает исполнитель по
    // нехватке досок, и рука обязана согласиться с ним, а не с типом действия.
    [Test]
    public void UnnamedProcessStepStillExportsTheSawWhileTheCampNeedsBoards()
    {
        var world = CreateWorldWithBoardDemand(3523, out var npc, out var log);
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Axe));
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Saw));

        StartProcess(npc, log, interactionId: string.Empty);

        Assert.That(HeldItemOf(world, npc), Is.EqualTo(GearCatalog.Saw));
    }

    private static WorldState CreateWorldWithBoardDemand(
        int seed, out NPCState npc, out WorldObjectState log)
    {
        var world = TestWorld.CreateWorld(seed);
        npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        var here = npc.CurrentJunction ?? world.Junctions.Items.Keys.First();
        var tile = world.Junctions.Items[here].Tiles[0];
        var fragment = world.Junctions.Items[here].Fragment;

        log = WorldObjectMutations.SpawnObject(world, ContentIds.Log, fragment, tile, here);

        // Незакрытый билл досок своего лагеря — то, что делает нехватку
        // положительной. В настоящей игре она положительна почти всегда
        // (один дом — 71 доска), и именно поэтому ярлык «нужны доски → пила»
        // срабатывал и на рубке.
        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, fragment, tile, here);
        site.BuildProduct = ContentIds.Hut1Hex;
        site.BillBoards = 5;
        site.Owner = npc.Id;

        Assert.That(
            DecisionSystem.WoodenProstheticBoardShortfall(world, npc),
            Is.GreaterThan(0),
            "предусловие теста: лагерю не хватает досок");

        return world;
    }

    private static void StartProcess(
        NPCState npc, WorldObjectState log, string interactionId)
    {
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetObject = log.Id,
            Interaction = InteractionType.Process,
            InteractionId = interactionId
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;

        npc.Execution.CurrentInteraction = InteractionType.Process;
        npc.Execution.TargetObject = log.Id;
    }

    private static string HeldItemOf(WorldState world, NPCState npc) =>
        WorldSnapshotExporter.Export(world).Npcs
            .Single(candidate => candidate.Id.Value == npc.Id.Value)
            .HeldItemId;
}

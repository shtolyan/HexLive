using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class PlayerHostileWaveTests
{
    [Test]
    public void AutonomousSubmittedAbuseCanContinueWithoutMutualConsent()
    {
        var world = TestWorld.CreateWorld(612);
        var aggressor = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Male);
        var victim = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        foreach (var npc in world.Entities.Npcs.Values.Where(n => n != aggressor && n != victim).ToArray())
            world.Entities.Npcs.Remove(npc.Id);
        var tile = world.Tiles.Items.Values.First(t => t.Flags.HasFlag(TileFlags.Walkable) &&
            !t.Flags.HasFlag(TileFlags.Indoor) && t.Junctions.Any(j =>
                SpatialQueries.GetPassableNeighbors(world, j).Any()));
        var start = tile.Junctions.First(j => SpatialQueries.GetPassableNeighbors(world, j).Any());
        var neighbor = SpatialQueries.GetPassableNeighbors(world, start).First();
        foreach (var pair in new[] { (aggressor, start), (victim, neighbor) })
        {
            var (npc, junction) = pair;
            npc.CurrentJunction = junction;
            npc.Tile = world.Junctions.Items[junction].Tiles[0];
            npc.Position = world.Junctions.Items[junction].WorldPosition;
            npc.Fragment = world.Junctions.Items[junction].Fragment;
            npc.IsFighting = false;
            npc.Mind.ManualControl = false;
            npc.Movement.IsMoving = false;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.SetStatus(MovementStatus.Arrived);
            npc.Plan.Status = PlanStatus.None;
            npc.Plan.Steps.Clear();
            npc.Inventory.Items.Clear();
        }
        world.Tick = 3 * EnvironmentSystem.DayLengthTicks;
        while (MathUtil.Hash01(world.Seed, world.Tick, victim.Id.Value, aggressor.Id.Value) >= Spec127.ForcedAfterAbuseChance)
            world.Tick++;
        Assert.That(RomanceMath.CanConsent(world, aggressor, victim), Is.False);
        aggressor.Plan.Goal = GoalType.Abuse;
        aggressor.Plan.Status = PlanStatus.Active;
        aggressor.Plan.TargetAgentId = victim.Id;
        aggressor.Plan.TargetJunctionId = start;
        aggressor.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Interact,
            Interaction = InteractionType.Abuse, TargetJunction = start });
        aggressor.Mind.CurrentGoal = GoalType.Abuse;
        aggressor.Mind.AbuseBeat = 4;
        aggressor.Mind.AbuseVerdictTick = world.Tick - Spec81.AbuseTakeDelayTicks;
        aggressor.Mind.AbuseTargetNpcId = victim.Id;
        aggressor.Mind.SceneStartHealth = aggressor.Health;
        victim.Mind.SceneStartHealth = victim.Health;
        victim.Mind.PendingAbuseFrom = aggressor.Id;
        aggressor.Execution.Status = ExecutionStatus.InProgress;
        aggressor.Execution.CurrentInteraction = InteractionType.Abuse;
        aggressor.Execution.StartTick = world.Tick - Spec81.AbuseDurationTicks + 1;
        aggressor.Execution.EndTick = world.Tick + 1;
        new ExecutionSystem().Run(world);
        Assert.That(aggressor.Plan.Goal, Is.EqualTo(GoalType.Romance));
        Assert.That(aggressor.Mind.RomanceForced, Is.True);
        Assert.That(victim.Mind.PendingRomanceFrom, Is.EqualTo(aggressor.Id));
    }

    [Test]
    public void LegacyPlayersOnlyXmlRestoresRaidsAndStillSuppressesFriendlyArrivals()
    {
        var world = TestWorld.CreateWorld();
        world.Mode = GameMode.Islands;
        foreach (var npc in world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Outsiders).ToArray())
            world.Entities.Npcs.Remove(npc.Id);
        world.CreationConfig = WorldCreation.Defaults(world);
        world.CreationConfig.PlayersOnly = true;
        world.CreationConfig.SeaRaidIntervalDays = 0;
        var xml = WorldCreationCodec.Encode(world.CreationConfig);
        xml = xml.Replace("  <PlayerHostileRaidIntervalDays>3</PlayerHostileRaidIntervalDays>\r\n", "")
            .Replace("  <PlayerHostileCampLimit>1</PlayerHostileCampLimit>\r\n", "")
            .Replace("  <PlayerHostileRaidIntervalDays>3</PlayerHostileRaidIntervalDays>\n", "")
            .Replace("  <PlayerHostileCampLimit>1</PlayerHostileCampLimit>\n", "");
        Assert.That(xml, Does.Not.Contain("PlayerHostile"));
        world.CreationConfig = WorldCreationCodec.Decode(xml);
        world.Tick = 2 * EnvironmentSystem.DayLengthTicks - EnvironmentSystem.DayLengthTicks / 4;
        var waves = new RaidWaveSystem();
        waves.Run(world);
        var first = RaidWaveSystem.IslandRaiderId(Faction.Colony, 1);
        Assert.That(world.Entities.Npcs.ContainsKey(first), Is.True);
        var firstStrength = world.Entities.Npcs[first].Attributes.Strength;
        var residents = PopulationArrivalMath.CampResidents(world, Faction.Colony);
        new ColonyArrivalSystem().Run(world);
        Assert.That(PopulationArrivalMath.CampResidents(world, Faction.Colony), Is.EqualTo(residents));
        world.Tick += 3 * EnvironmentSystem.DayLengthTicks;
        waves.Run(world);
        Assert.That(world.Entities.Npcs.Values.Count(n => n.Faction == Faction.Outsiders), Is.EqualTo(1));
        world.Entities.Npcs.Remove(first);
        waves.Run(world);
        Assert.That(world.Entities.Npcs.Values.Any(n => n.Faction == Faction.Outsiders), Is.False,
            "A consumed boundary must not queue a replacement after death.");
        world.Tick += 3 * EnvironmentSystem.DayLengthTicks;
        waves.Run(world);
        var next = world.Entities.Npcs[RaidWaveSystem.IslandRaiderId(Faction.Colony, 3)];
        Assert.That(next.Attributes.Strength, Is.GreaterThan(firstStrength));
        Assert.That(next.Traits.Has(TraitKind.Abuser), Is.True);
    }

    [Test]
    public void RaidsRespectDisabledEmptyAndExplicitlyPeacefulPlayerCamps()
    {
        var world = TestWorld.CreateWorld();
        world.Mode = GameMode.Islands;
        world.CreationConfig = WorldCreation.Defaults(world);
        world.CreationConfig.PlayersOnly = true;
        world.CreationConfig.PlayerHostileRaidIntervalDays = 0;
        world.Tick = 3 * EnvironmentSystem.DayLengthTicks;
        var waves = new RaidWaveSystem();
        var count = world.Entities.Npcs.Count;
        waves.Run(world);
        Assert.That(world.Entities.Npcs.Count, Is.EqualTo(count));
        world.CreationConfig.PlayerHostileRaidIntervalDays = 3;
        world.CreationConfig.Camp(Faction.Colony).Enabled = false;
        waves.Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(RaidWaveSystem.IslandRaiderId(Faction.Colony, 1)), Is.False);
        world.CreationConfig.Camp(Faction.Colony).Enabled = true;
        foreach (var npc in world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).ToArray())
            world.Entities.Npcs.Remove(npc.Id);
        world.Tick += 3 * EnvironmentSystem.DayLengthTicks;
        waves.Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(RaidWaveSystem.IslandRaiderId(Faction.Colony, 2)), Is.False);
    }

    [Test]
    public void HugePlayerWorldSpawnsNearItsInhabitedCampAndConsumesMissedBoundaries()
    {
        var engine = TestWorld.CreateEngine(); engine.Step();
        var world = engine.World;
        world.Mode = GameMode.HugeIsland;
        world.CreationConfig = WorldCreation.Defaults(world);
        world.CreationConfig.PlayersOnly = true;
        foreach (var npc in world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Outsiders).ToArray())
            world.Entities.Npcs.Remove(npc.Id);
        world.Mobs.Clear(); world.MobSpawnSlots.Clear();
        world.Tick = 377 * EnvironmentSystem.DayLengthTicks;
        world.RaidWavesSpawned = 1;
        var due = EnvironmentSystem.CalendarDay(world.Tick) / 3;
        var system = new RaidWaveSystem(); system.Run(world);
        var id = RaidWaveSystem.IslandRaiderId(Faction.Colony, due);
        Assert.That(world.Entities.Npcs.ContainsKey(id), Is.True);
        var arrived = world.Entities.Npcs[id];
        Assert.That(HexSpatialMath.HexDistance(arrived.Tile, world.FactionHomes[Faction.Colony]),
            Is.InRange(Spec72.MaxCampRadiusTiles, Spec72.MaxCampRadiusTiles + 2));
        Assert.That(arrived.Traits.Has(TraitKind.Abuser), Is.True);
        Assert.That(arrived.Inventory.Items.Any(i => i.DefinitionId == RaidWaveSystem.WeaponForWave(due)), Is.True);
        system.Run(world);
        Assert.That(world.Entities.Npcs.Values.Count(n => n.Faction == Faction.Outsiders), Is.EqualTo(1));
        world.Tick += 3 * EnvironmentSystem.DayLengthTicks;
        system.Run(world);
        world.Entities.Npcs.Remove(id);
        system.Run(world);
        Assert.That(world.Entities.Npcs.Values.Any(n => n.Faction == Faction.Outsiders), Is.False,
            "A skipped full-camp boundary must not become a replacement queue.");
        world.Tick += 3 * EnvironmentSystem.DayLengthTicks;
        system.Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(RaidWaveSystem.IslandRaiderId(Faction.Colony, due + 2)), Is.True);
    }

    [TestCase(GarmentSex.Female)]
    [TestCase(GarmentSex.Male)]
    public void RaidKitUsesOnlyPublishedPrimalFitsOwnedByTheRaider(GarmentSex sex)
    {
        var previous = GarmentLibrary.Active.ToArray();
        try
        {
            var records = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root,
                "Assets/HexLiveContent/People/catalog.json")))!["records"]!.AsArray();
            var garments = new JsonArray();
            foreach (var row in records.Where(r => r!["type"]!.GetValue<string>() == "wear"))
            {
                var item = row!["metadata"]!["simulation"]!.DeepClone();
                item["id"] = row["id"]!.GetValue<string>(); garments.Add(item);
            }
            Assert.That(SimDataFile.ApplyJson(new JsonObject { ["garments"] = garments }.ToJsonString()), Is.True);
            var world = TestWorld.CreateWorld();
            var npc = new NPCState { Id = new(7100), ActorMesh = sex == GarmentSex.Male ? "Kshishtof" : "Marta" };
            RaidSpawnWardrobe.EquipPrimal(world, npc);
            Assert.That(npc.WornItems, Has.Count.EqualTo(4));
            foreach (var item in npc.WornItems)
            {
                Assert.That(item.DefinitionId, Does.Contain("primal"));
                Assert.That(GarmentLibrary.FitsSex(sex, item.DefinitionId), Is.True);
                Assert.That(world.Content.ObjectDefinitions.ContainsKey(item.DefinitionId), Is.True);
                Assert.That(item.OwnerId, Is.EqualTo(npc.Id.Value));
            }
        }
        finally { GarmentLibrary.Override(previous); }
    }
}

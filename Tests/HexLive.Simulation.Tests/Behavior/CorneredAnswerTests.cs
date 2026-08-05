using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Баг #13: разбитый чужак не отвечал на побои В СВОЁМ ЖЕ ЛАГЕРЕ.
///
/// <para>
/// Клапан §109.8 (здоровье ниже RaidFleeHealth → выйти из размена и бежать
/// домой) зовёт <c>MobSystem.TryFleeToCamp</c>. Свой джанкшен занят самим
/// собой, так что стоящему в лагере поиск отдавал СОСЕДНИЙ свободный узел,
/// «бегство» удавалось каждый средний тик — и клапан съедал его ход раньше,
/// чем дело доходило до «бей в ответ». Его били во дворе его же лагеря, а он
/// шаркал на месте и не отвечал ни разу.
/// </para>
///
/// <para>
/// Фикс — guard «дом должен быть ДАЛЬШЕ, чем стоишь»: бегство, не приближающее
/// к якорю, — не бегство, а съеденный ход. TryFleeToCamp возвращает false, и
/// вызывающий (клапан §109.8) проваливается в ответный бой — форма §108
/// TryFleeHome, где <c>refuge.Equals(from)</c> стоял с самого начала.
/// </para>
/// </summary>
public sealed class CorneredAnswerTests
{
    private static void Teleport(WorldState world, NPCState npc, TileCoord tile)
    {
        var junctionId = world.Tiles.Items[tile].Junctions[0];
        var junction = world.Junctions.Items[junctionId];
        npc.Tile = tile;
        npc.CurrentJunction = junctionId;
        npc.Position = junction.WorldPosition;
    }

    [Test]
    public void AtHisOwnCamp_FleeRefuses_SoTheValveFallsThroughToFighting()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        Assert.That(world.FactionHomes.TryGetValue(stranger.Faction, out var camp), Is.True,
            "У чужака обязан быть свой якорь-лагерь.");

        Teleport(world, stranger, camp);
        var goalBefore = stranger.Mind.CurrentGoal;

        Assert.That(MobSystem.TryFleeToCamp(world, stranger, "test"), Is.False,
            "Стоя в собственном лагере, «бежать домой» некуда: до фикса поиск " +
            "отдавал соседний свободный джанкшен, бегство «удавалось» каждый " +
            "средний тик, и клапан §109.8 никогда не пропускал его в ответный бой.");
        Assert.That(stranger.Mind.CurrentGoal, Is.EqualTo(goalBefore),
            "Отказ обязан быть чистым: ни цели Flee, ни разорванного плана.");
    }

    [Test]
    public void FarFromCamp_FleeStillWorks()
    {
        var world = TestWorld.CreateWorld();
        var stranger = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        Assert.That(world.FactionHomes.TryGetValue(stranger.Faction, out var camp), Is.True);

        // Любой ходибельный гекс подальше от якоря: guard не должен задеть
        // штатное отступление §109.8 из чужого двора.
        var far = world.Tiles.Items.Values
            .Where(t => t.Flags.HasFlag(TileFlags.Walkable) &&
                        !t.Flags.HasFlag(TileFlags.Water) &&
                        t.Junctions.Count > 0)
            .OrderByDescending(t => HexSpatialMath.HexDistance(t.Coord, camp))
            .First();
        Assert.That(HexSpatialMath.HexDistance(far.Coord, camp), Is.GreaterThanOrEqualTo(3),
            "Прототипный мир обязан давать точку заметно дальше трёх гексов от лагеря.");

        Teleport(world, stranger, far.Coord);

        Assert.That(MobSystem.TryFleeToCamp(world, stranger, "test"), Is.True,
            "Издалека бегство домой обязано работать как раньше.");
        Assert.That(stranger.Mind.CurrentGoal, Is.EqualTo(GoalType.Flee));
    }
}

}

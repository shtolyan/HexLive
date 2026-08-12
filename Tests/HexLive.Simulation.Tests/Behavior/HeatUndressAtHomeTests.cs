using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: в жару вещи не бросают там, где застало пекло. Она идёт домой и
/// вешает снятое в гардероб; раздеться на месте — крайний случай.
/// </summary>
public sealed class HeatUndressAtHomeTests
{
    private const string Jacket = "clothing.jacket_autumn";

    private static (SimulationEngine engine, NPCState npc) HotColonist(float discomfort)
    {
        var engine = TestWorld.CreateEngine(12345);
        for (var i = 0; i < 4; i++)
        {
            engine.Step(); // до первого тика у неё нет джанкшена — планировать нечего
        }

        var npc = engine.World.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.WornItems.Add(new ItemInstance(Jacket) { OwnerId = npc.Id.Value });
        npc.Needs.ThermalDiscomfort = discomfort;
        npc.Memory.Dangers.Clear();
        npc.Mind.CurrentGoal = GoalType.Undress;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        return (engine, npc);
    }

    private static void Plan(SimulationEngine engine, NPCState npc)
    {
        new PlanningSystem().Run(engine.World);
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active),
            "План раздевания не построился.");
    }

    /// <summary>⭐ Обычная жара — сначала домой, к гардеробу.</summary>
    [Test]
    public void OrdinaryHeatSendsHerHomeToTheWardrobe()
    {
        var (engine, npc) = HotColonist(0.6f);

        Plan(engine, npc);

        var last = npc.Plan.Steps[^1];
        Assert.That(last.Type, Is.EqualTo(PlanStepType.UndressItem));
        Assert.That(last.TargetObject, Is.Not.Null,
            "Раздевается на месте, хотя гардероб есть — вещи опять окажутся в поле.");
        Assert.That(engine.World.Entities.Objects[last.TargetObject.Value].DefinitionId,
            Is.EqualTo(ContentIds.Wardrobe));
        Assert.That(npc.Plan.Steps[0].Type, Is.EqualTo(PlanStepType.MoveToJunction),
            "К гардеробу не идут — шага дороги нет.");
    }

    /// <summary>Печёт опасно — крайний случай: снимаем здесь и сейчас.</summary>
    [Test]
    public void DangerousHeatStripsWhereSheStands()
    {
        var (engine, npc) = HotColonist(0.95f);

        Plan(engine, npc);

        Assert.That(npc.Plan.Steps.Count, Is.EqualTo(1),
            "В тепловом ударе она пошла через полострова в гардероб.");
        Assert.That(npc.Plan.Steps[0].Type, Is.EqualTo(PlanStepType.UndressItem));
        Assert.That(npc.Plan.Steps[0].TargetObject, Is.Null);
    }

    /// <summary>Дойдя до гардероба, снятое действительно вешается на него.</summary>
    [Test]
    public void TheDoffedGarmentEndsUpOnTheWardrobeNotOnTheFloor()
    {
        var (engine, npc) = HotColonist(0.6f);
        var world = engine.World;
        Plan(engine, npc);

        var wardrobeId = npc.Plan.Steps[^1].TargetObject.Value;
        var wardrobe = world.Entities.Objects[wardrobeId];

        // Ставим её на место назначения и доигрываем раздевание.
        npc.CurrentJunction = npc.Plan.Steps[^1].TargetJunction;
        npc.Movement.IsMoving = false;
        npc.Plan.Steps.RemoveAt(0);
        npc.Plan.CurrentStepIndex = 0;

        // Крутим фиксированное число тиков: раздевание двухтактное, и на
        // середине вещь уже не надета, но ещё в руке — по WornItems выходить рано.
        for (var i = 0; i < 40; i++)
        {
            new ExecutionSystem().Run(world);
            world.Tick++;
        }

        Assert.That(npc.WornItems.Any(w => w.DefinitionId == Jacket), Is.False,
            "Куртка так и не снялась.");
        var hung = world.Entities.Objects.Values.FirstOrDefault(
            o => o.DefinitionId == Jacket && o.Junctions.Count > 0 &&
                 o.Junctions[0].Equals(wardrobe.Junctions[0]));
        var anyJacket = world.Entities.Objects.Values.Where(o => o.DefinitionId == Jacket).ToArray();
        Assert.That(hung, Is.Not.Null,
            "Снятая куртка не оказалась в гардеробе. Найдено курток: " +
            string.Join(";", anyJacket.Select(o => $"obj{o.Id.Value}@{(o.Junctions.Count > 0 ? o.Junctions[0].Value.ToString() : "-")}")) +
            $" гардероб на {wardrobe.Junctions[0].Value}, она на {npc.CurrentJunction?.Value}");
        Assert.That(hung.Owner, Is.EqualTo(npc.Id), "Вещь в гардеробе потеряла хозяйку.");
    }
}

}

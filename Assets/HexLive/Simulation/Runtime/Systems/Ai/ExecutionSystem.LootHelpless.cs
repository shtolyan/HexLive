using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §111: сам обыск. Присел над лежащим и снимает по вещи за такт — оружие
// первым, — пока карманы не опустеют или он не очнётся.
//
// Сцена намеренно проще §81: там пять тактов, боевая пара и приговор, здесь
// нет ни удара, ни ответа. Тело не сопротивляется — в этом вся суть механики,
// и потому же нет ветки преследования: беспомощная никуда не идёт.
public sealed partial class ExecutionSystem
{
    private static void RunLootHelpless(WorldState world, NPCState npc)
    {
        if (npc.Plan.TargetAgentId is not { } markId ||
            !world.Entities.Npcs.TryGetValue(markId, out var mark))
        {
            AbortLootHelpless(world, npc, "MarkVanished");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Movement.Status == MovementStatus.Blocked)
        {
            AbortLootHelpless(world, npc, "ApproachBlocked");
            return;
        }

        // The route owns a FREE approach junction. The final authored station
        // is inside the lying body's occupied footprint, so reaching the route
        // target — not melee adjacency to the body's own junction — is what
        // permits the local feet snap below. Using CanStrike here deadlocked
        // bug #19: the looter arrived, stayed Goal=LootHelpless forever, but
        // never entered CurrentInteraction=Loot and therefore never animated.
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Plan.TargetJunctionId is { } wantJunction &&
            (npc.CurrentJunction is not { } atJunction || !atJunction.Equals(wantJunction)))
        {
            return;
        }

        // --- Срывы, годные на любом такте ------------------------------------

        // Умерла под руками — сцене конец, но добыче нет: с этого места её
        // подхватывает штатный §28.15F по якорю трупа, и там же снимут одежду.
        if (mark.Health <= 0f)
        {
            AbortLootHelpless(world, npc, "MarkDied");
            return;
        }

        // ⭐ Главный аборт всей фичи: ОЧНУЛАСЬ. Дальше с ней разбираются обычные
        // контуры — налёт §72, сцена §81, её собственное бегство.
        if (!mark.IsUnconscious(world.Tick))
        {
            AbortLootHelpless(world, npc, "MarkWoke");
            return;
        }

        // §106: вода — санктуарий. Встать над лежащей в воде нельзя.
        if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, mark))
        {
            AbortLootHelpless(world, npc, "MarkSwimming");
            return;
        }

        // Пусто — не провал, а конец: кто-то успел раньше (образец LootEmpty).
        if (!LootHelplessMath.HasLoot(mark))
        {
            FinishLootHelpless(world, npc, mark);
            return;
        }

        // --- Начало ----------------------------------------------------------
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            // The path may only hand off locally: accept the short move from
            // its free approach to the occupied feet station, but never
            // teleport across the camp if a future planner supplies junk.
            var feet = LyingSpot.InteractionFeet(mark);
            if (!InteractionReach.CheckStart(world, npc, feet,
                    LyingSpot.InteractionStationReach,
                    $"LootHelpless feet of NPC{mark.Id.Value}"))
            {
                AbortLootHelpless(world, npc, "FeetStationOutOfReach");
                return;
            }

            LyingSpot.AlignInteractorAtFeet(npc, mark);

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Loot;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec111.LootHelplessMaxSceneTicks;
            npc.Mind.LootHelplessTakenCount = 0;
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            // The body cannot call for help while unconscious. Nearby allies
            // who see the search treat it as an attack and run at the looter.
            var witnesses = CombatHelpSystem.RallyLootWitnesses(world, mark, npc.Id);

            Trace.Emit(world, npc.Id, "LootHelplessStarted",
                $"Mark=NPC{mark.Id.Value} Items={mark.Inventory.Items.Count} " +
                $"Weapon={GearCatalog.BestMeleeWeapon(mark.Inventory.Items, mark.Body.IntactHands)} " +
                $"Witnesses={witnesses}");
            return;
        }

        // Hold the single shared feet->head pose for the whole scene. This is
        // positional presentation data owned by the simulation, like shore
        // washing: renderer interpolation must not guess it independently.
        LyingSpot.AlignInteractorAtFeet(npc, mark);

        // Потолок сцены — страховка от зависшего такта, а не игровой срок.
        if (world.Tick >= npc.Execution.EndTick)
        {
            FinishLootHelpless(world, npc, mark);
            return;
        }

        // --- «Хоп-хоп»: вещь за тактом -----------------------------------------
        //
        // Курсор хранится ЧИСЛОМ снятого, а не выводится из времени: пропущенный
        // тик иначе проглотил бы вещь или снял её дважды (урок AbuseBeat).
        var dueTick = npc.Execution.StartTick +
            (npc.Mind.LootHelplessTakenCount + 1) * Spec111.LootHelplessTakeTicks;
        if (world.Tick < dueTick)
        {
            return;
        }

        if (!LootHelplessMath.TryTake(world, npc, mark, out var takenId))
        {
            // Не влезло в рюкзак — конец сцены, а не провал плана: он уносит
            // то, что успел.
            Trace.Emit(world, npc.Id, "LootHelplessBlocked",
                $"Mark=NPC{mark.Id.Value} Took={npc.Mind.LootHelplessTakenCount}");
            FinishLootHelpless(world, npc, mark);
            return;
        }

        npc.Mind.LootHelplessTakenCount++;
        Trace.Emit(world, npc.Id, "LootHelplessTook",
            $"Mark=NPC{mark.Id.Value} Def={takenId} Left={mark.Inventory.Items.Count}");

        if (!LootHelplessMath.HasLoot(mark))
        {
            FinishLootHelpless(world, npc, mark);
        }
    }

    private static void FinishLootHelpless(WorldState world, NPCState npc, NPCState mark)
    {
        HumanCombatPairing.ClearAssistsAgainst(world, npc.Id);
        HumanCombatPairing.ClearFor(world, npc);

        // Одно событие на СЦЕНУ, а не на вещь: по событию на нож лента истории
        // была бы одним обыском на весь экран.
        if (npc.Mind.LootHelplessTakenCount > 0)
        {
            Trace.Emit(world, npc.Id, "StrippedHelpless",
                $"Mark=NPC{mark.Id.Value} Count={npc.Mind.LootHelplessTakenCount} " +
                $"Weapon={GearCatalog.BestMeleeWeapon(npc.Inventory.Items, npc.Body.IntactHands)}");
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        if (mark.Mind.PendingLootedBy is { } claimed && claimed.Equals(npc.Id))
        {
            mark.Mind.PendingLootedBy = null;
        }

        npc.Mind.LootHelplessTargetNpcId = null;
        npc.Mind.LootHelplessTakenCount = 0;
        npc.Mind.LootHelplessCooldownUntilTick = world.Tick + Spec111.LootHelplessCooldownTicks;
        if (npc.Mind.GoalLock is { } lootLock && lootLock.Goal == GoalType.LootHelpless)
        {
            npc.Mind.GoalLock = null;
        }

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Idle);
    }

    // Bug #51: an incoming human attack is not an ordinary scene failure. The
    // search is over, but the combat that interrupted it must remain alive and
    // the next search attempt gets the full played-scene cooldown.
    internal static void AbortLootHelplessForAttack(
        WorldState world, NPCState npc, EntityId attackerId)
    {
        AbortLootHelpless(world, npc, $"AttackedByNPC{attackerId.Value}",
            Spec111.LootHelplessCooldownTicks, preserveCombat: true);
    }

    private static void AbortLootHelpless(
        WorldState world, NPCState npc, string reason,
        int cooldownTicks = -1, bool preserveCombat = false)
    {
        if (!preserveCombat)
        {
            HumanCombatPairing.ClearAssistsAgainst(world, npc.Id);
            HumanCombatPairing.ClearFor(world, npc);
        }

        // Уносит то, что успел: снятое уже у него в рюкзаке, и «прервали» не
        // значит «верни». Событие истории всё равно заслужено.
        if (npc.Mind.LootHelplessTakenCount > 0 &&
            npc.Mind.LootHelplessTargetNpcId is { } tookFromId)
        {
            Trace.Emit(world, npc.Id, "StrippedHelpless",
                $"Mark=NPC{tookFromId.Value} Count={npc.Mind.LootHelplessTakenCount} " +
                $"Weapon={GearCatalog.BestMeleeWeapon(npc.Inventory.Items, npc.Body.IntactHands)}");
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        Trace.Emit(world, npc.Id, "LootHelplessAborted", $"Reason={reason}");

        // Abort снимает клеймы, брони шагов и несомую вещь; AbandonLootHelpless —
        // заявку на тело и саму цель.
        PlanInterruption.Abort(world, npc, $"LootHelpless aborted: {reason}");
        PlanningSystem.AbandonLootHelpless(world, npc, reason, cooldownTicks);
    }
}

}

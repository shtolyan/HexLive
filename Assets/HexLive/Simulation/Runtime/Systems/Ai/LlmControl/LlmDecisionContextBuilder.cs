using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Builds a deterministic, read-only summary of the state already available
/// to an NPC. It performs no provider calls and does not mutate simulation state.
/// </summary>
public static class LlmDecisionContextBuilder
{
    public static LlmDecisionContext Build(WorldState world, NPCState npc) =>
        Build(world, npc, ContextBudget.Default);

    /// <param name="budget">§144.8. <see cref="ContextBudget.Unbounded"/> даёт
    /// прежние байты в байт — это опора теста, а не режим для обычной работы.</param>
    public static LlmDecisionContext Build(
        WorldState world, NPCState npc, ContextBudget budget)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (npc is null) throw new ArgumentNullException(nameof(npc));

        return new LlmDecisionContext(
            npc.Id,
            world.Tick,
            npc.Position,
            BuildStateSummary(world, npc),
            BuildPerceptionSummary(npc.Perception, budget),
            BuildMemorySummary(npc.Memory, budget));
    }

    private static string BuildStateSummary(WorldState world, NPCState npc)
    {
        var needs = npc.Needs;
        return
            $"health={Number(npc.Health)}; goal={npc.Mind.CurrentGoal}; " +
            $"interaction={ValueOrNone(npc.Execution.CurrentInteraction)}; " +
            $"execution={npc.Execution.Status}; moving={Bool(npc.Movement.IsMoving)}; " +
            $"fighting={Bool(npc.IsFighting)}; manualControl={Bool(npc.Mind.ManualControl)}; " +
            $"unconscious={Bool(npc.IsUnconscious(world.Tick))}; " +
            $"needs[hunger={Number(needs.Hunger)}, thirst={Number(needs.Thirst)}, " +
            $"energy={Number(needs.Energy)}, comfort={Number(needs.Comfort)}, " +
            $"social={Number(needs.Social)}, thermal={Number(needs.ThermalDiscomfort)}, " +
            $"stamina={Number(needs.Stamina)}, blood={Number(needs.Blood)}, " +
            $"stress={Number(needs.Stress)}]";
    }

    private static string BuildPerceptionSummary(
        PerceptionSnapshot perception, ContextBudget budget)
    {
        var builder = new StringBuilder();
        builder.Append("updatedTick=").Append(perception.LastUpdatedTick);
        builder.Append("; environment[temperature=").Append(Number(perception.Environment.Temperature));
        builder.Append(", crowded=").Append(Bool(perception.Environment.IsCrowded));
        builder.Append(", private=").Append(Bool(perception.Environment.IsPrivate));
        builder.Append(", nearbyAgents=").Append(perception.Environment.NearbyAgentsCount);
        builder.Append(']');

        AppendPerceivedObjects(builder, perception.Objects, budget);
        AppendPerceivedAgents(builder, "allies", perception.Agents, budget);
        AppendPerceivedAgents(builder, "hostiles", perception.Hostiles, budget);
        AppendPerceivedMobs(builder, perception.Mobs);
        return builder.ToString();
    }

    /// <summary>
    /// Сколько строк оставить и сколько при этом выброшено. Усечение обязано
    /// быть ЭЛЕМЕНТОМ ответа, а не тишиной: молча короткий список читается как
    /// «этого рядом нет», и агент уходит искать то, что лежит у него под ногами.
    /// </summary>
    private static int Keep(int count, int max, out int omitted)
    {
        if (count <= max)
        {
            omitted = 0;
            return count;
        }

        omitted = count - max;
        return max;
    }

    private static void AppendOmitted(StringBuilder builder, int omitted, bool hadRows)
    {
        if (omitted <= 0)
        {
            return;
        }

        if (hadRows) builder.Append("; ");
        builder.Append("{omitted=").Append(omitted).Append('}');
    }

    /// <summary>
    /// §144.7. Пока этой строки не было, «на меня напали» приезжало наружу как
    /// <c>fighting=true</c> при пустом <c>hostiles</c> — в том списке только
    /// люди. Контур управления видел бой без противника и не мог ни назвать
    /// напавшего, ни ударить в ответ: <c>mobId</c> взять было неоткуда.
    /// </summary>
    private static void AppendPerceivedMobs(
        StringBuilder builder, List<PerceivedMob> source)
    {
        var mobs = new List<PerceivedMob>(source);
        mobs.Sort((left, right) => left.Id.CompareTo(right.Id));

        builder.Append("; mobs=[");
        for (var i = 0; i < mobs.Count; i++)
        {
            if (i > 0) builder.Append("; ");
            var mob = mobs[i];
            builder.Append("{mobId=").Append(mob.Id);
            builder.Append(", definition=").Append(mob.MobId);
            builder.Append(", distance=").Append(mob.Distance);
            builder.Append(", health=").Append(Number(mob.Health));
            builder.Append(", status=").Append(mob.Status);
            builder.Append(", targetsMe=").Append(Bool(mob.TargetsMe));
            builder.Append('}');
        }
        builder.Append(']');
    }

    private static void AppendPerceivedObjects(
        StringBuilder builder, List<PerceivedObject> source, ContextBudget budget)
    {
        var objects = new List<PerceivedObject>(source);
        objects.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));

        builder.Append("; objects=[");

        if (!budget.Aggregate)
        {
            for (var i = 0; i < objects.Count; i++)
            {
                if (i > 0) builder.Append("; ");
                AppendObjectRow(builder, objects[i]);
            }
            builder.Append(']');
            return;
        }

        // Схлопывание по определению. Ключ — только DefinitionId: агент
        // действует по СОРТУ вещи («где ближайший кокос»), а не по конкретному
        // 146-му листу, до которого всё равно идти мимо первых трёх.
        var groups = new List<ObjectGroup>();
        var byDefinition = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < objects.Count; i++)
        {
            var item = objects[i];
            if (!byDefinition.TryGetValue(item.DefinitionId, out var index))
            {
                index = groups.Count;
                byDefinition[item.DefinitionId] = index;
                groups.Add(new ObjectGroup(item));
                continue;
            }

            groups[index].Add(item);
        }

        // Отбор — по БЛИЖАЙШЕМУ: далёкое не решает. Вывод — по id
        // представителя, как и раньше, чтобы порядок оставался свойством мира,
        // а не расстояния, которое меняется каждый шаг.
        groups.Sort((left, right) =>
        {
            var distance = left.NearestDistance.CompareTo(right.NearestDistance);
            return distance != 0
                ? distance
                : left.Representative.Id.Value.CompareTo(right.Representative.Id.Value);
        });

        var kept = Keep(groups.Count, budget.MaxObjectRows, out var omitted);
        var shown = groups.GetRange(0, kept);
        shown.Sort((left, right) =>
            left.Representative.Id.Value.CompareTo(right.Representative.Id.Value));

        for (var i = 0; i < shown.Count; i++)
        {
            if (i > 0) builder.Append("; ");
            var group = shown[i];
            if (group.Count == 1)
            {
                // Один экземпляр ничего не теряет от подробной записи — и не
                // выигрывает от переформатирования. Форма остаётся прежней.
                AppendObjectRow(builder, group.Representative);
                continue;
            }

            builder.Append("{definition=").Append(group.Representative.DefinitionId);
            builder.Append(", count=").Append(group.Count);
            builder.Append(", nearest=[");
            var names = System.Math.Min(budget.NearestPerDefinition, group.Nearest.Count);
            for (var n = 0; n < names; n++)
            {
                if (n > 0) builder.Append("; ");
                var near = group.Nearest[n];
                builder.Append("{id=").Append(near.Id.Value);
                builder.Append(", distance=").Append(Number(near.Distance));
                builder.Append(", reachable=").Append(Bool(near.IsReachable));
                builder.Append(", occupied=").Append(Bool(near.IsOccupied));
                builder.Append(", fromMemory=").Append(Bool(near.FromMemory));
                builder.Append('}');
            }
            builder.Append(']');
            builder.Append(", interactions=");
            AppendInteractions(builder, group.Representative.AvailableInteractions);
            builder.Append('}');
        }

        AppendOmitted(builder, omitted, shown.Count > 0);
        builder.Append(']');
    }

    private static void AppendObjectRow(StringBuilder builder, PerceivedObject item)
    {
        builder.Append("{id=").Append(item.Id.Value);
        builder.Append(", definition=").Append(item.DefinitionId);
        builder.Append(", distance=").Append(Number(item.Distance));
        builder.Append(", reachable=").Append(Bool(item.IsReachable));
        builder.Append(", occupied=").Append(Bool(item.IsOccupied));
        builder.Append(", fromMemory=").Append(Bool(item.FromMemory));
        builder.Append(", interactions=");
        AppendInteractions(builder, item.AvailableInteractions);
        builder.Append('}');
    }

    /// <summary>Одно определение и его ближайшие экземпляры.</summary>
    private sealed class ObjectGroup
    {
        private const int MaxNearestTracked = 8;

        public ObjectGroup(PerceivedObject first)
        {
            Representative = first;
            Nearest = new List<PerceivedObject> { first };
            NearestDistance = first.Distance;
            Count = 1;
        }

        public PerceivedObject Representative { get; private set; }

        public List<PerceivedObject> Nearest { get; }

        public float NearestDistance { get; private set; }

        public int Count { get; private set; }

        public void Add(PerceivedObject item)
        {
            Count++;

            if (item.Distance < NearestDistance)
            {
                NearestDistance = item.Distance;
                // Представитель — ближайший: именно его глаголы и его id
                // окажутся тем, по чему агент будет решать.
                Representative = item;
            }

            Nearest.Add(item);
            Nearest.Sort((left, right) =>
            {
                var distance = left.Distance.CompareTo(right.Distance);
                return distance != 0 ? distance : left.Id.Value.CompareTo(right.Id.Value);
            });

            if (Nearest.Count > MaxNearestTracked)
            {
                Nearest.RemoveRange(MaxNearestTracked, Nearest.Count - MaxNearestTracked);
            }
        }
    }

    private static void AppendPerceivedAgents(
        StringBuilder builder, string label, List<PerceivedAgent> source,
        ContextBudget budget)
    {
        var agents = new List<PerceivedAgent>(source);
        agents.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        var kept = Keep(agents.Count, budget.MaxAgentRows, out var omitted);

        builder.Append("; ").Append(label).Append("=[");
        for (var i = 0; i < kept; i++)
        {
            if (i > 0) builder.Append("; ");
            var agent = agents[i];
            builder.Append("{id=").Append(agent.Id.Value);
            builder.Append(", distance=").Append(Number(agent.Distance));
            builder.Append(", reachable=").Append(Bool(agent.IsReachable));
            builder.Append(", busy=").Append(Bool(agent.IsBusy));
            builder.Append(", moving=").Append(Bool(agent.IsMoving));
            builder.Append(", suffering=").Append(Number(agent.Suffering));
            builder.Append('}');
        }
        AppendOmitted(builder, omitted, kept > 0);
        builder.Append(']');
    }

    private static string BuildMemorySummary(MemoryState memory, ContextBudget budget)
    {
        var builder = new StringBuilder();
        AppendKnownObjects(builder, memory.KnownObjects.Values, budget);
        AppendKnownAgents(builder, memory.KnownAgents.Values, budget);
        AppendDangers(builder, memory.Dangers, budget);
        return builder.ToString();
    }

    private static void AppendKnownObjects(
        StringBuilder builder, ICollection<ObjectMemory> source, ContextBudget budget)
    {
        var objects = new List<ObjectMemory>(source);

        // Отбор памяти — по СВЕЖЕСТИ: то, что видели тысячу тиков назад, скорее
        // всего уже подобрали. Вывод по-прежнему по id.
        if (objects.Count > budget.MaxKnownObjectRows)
        {
            objects.Sort((left, right) =>
            {
                var seen = right.LastSeenTick.CompareTo(left.LastSeenTick);
                return seen != 0 ? seen : left.Id.Value.CompareTo(right.Id.Value);
            });
            objects.RemoveRange(
                budget.MaxKnownObjectRows, objects.Count - budget.MaxKnownObjectRows);
        }

        var omitted = System.Math.Max(0, source.Count - objects.Count);
        objects.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));

        builder.Append("knownObjects=[");
        for (var i = 0; i < objects.Count; i++)
        {
            if (i > 0) builder.Append("; ");
            var item = objects[i];
            builder.Append("{id=").Append(item.Id.Value);
            builder.Append(", definition=").Append(item.DefinitionId);
            builder.Append(", tile=").Append(Tile(item.Tile));
            builder.Append(", lastSeenTick=").Append(item.LastSeenTick);
            builder.Append(", permanent=").Append(Bool(item.IsPermanent));
            builder.Append('}');
        }
        AppendOmitted(builder, omitted, objects.Count > 0);
        builder.Append(']');
    }

    private static void AppendKnownAgents(
        StringBuilder builder, ICollection<AgentMemory> source, ContextBudget budget)
    {
        var agents = new List<AgentMemory>(source);
        agents.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));
        var kept = Keep(agents.Count, budget.MaxKnownAgentRows, out var omitted);

        builder.Append("; knownAgents=[");
        for (var i = 0; i < kept; i++)
        {
            if (i > 0) builder.Append("; ");
            var agent = agents[i];
            builder.Append("{id=").Append(agent.Id.Value);
            builder.Append(", faction=").Append(agent.Faction);
            builder.Append(", tile=").Append(Tile(agent.Tile));
            builder.Append(", lastSeenTick=").Append(agent.LastSeenTick);
            builder.Append(", suffering=").Append(Number(agent.Suffering));
            builder.Append(", aid=").Append(agent.AidKind);
            builder.Append(", helpless=").Append(Bool(agent.Helpless));
            builder.Append('}');
        }
        AppendOmitted(builder, omitted, kept > 0);
        builder.Append(']');
    }

    private static void AppendDangers(
        StringBuilder builder, List<DangerMemory> source, ContextBudget budget)
    {
        var dangers = new List<DangerMemory>(source);
        dangers.Sort((left, right) =>
        {
            var tick = left.Tick.CompareTo(right.Tick);
            if (tick != 0) return tick;
            var q = left.Tile.Q.CompareTo(right.Tile.Q);
            return q != 0 ? q : left.Tile.R.CompareTo(right.Tile.R);
        });

        // Свежая опасность важнее старой, поэтому режется ХВОСТ списка,
        // отсортированного по тику: самые старые метки уходят первыми.
        var kept = Keep(dangers.Count, budget.MaxDangerRows, out var omitted);
        if (omitted > 0)
        {
            dangers.RemoveRange(0, omitted);
        }

        // ⭐ Метка списка пишется ВСЕГДА, даже пустого. MockLlmControlProvider
        // ищет в этих строках подстроку "danger" — переименуй ключ или спрячь
        // его при пустом списке, и тестовый двойник §32.15 молча перевернётся.
        builder.Append("; dangers=[");
        for (var i = 0; i < kept; i++)
        {
            if (i > 0) builder.Append("; ");
            builder.Append("{tile=").Append(Tile(dangers[i].Tile));
            builder.Append(", tick=").Append(dangers[i].Tick).Append('}');
        }
        AppendOmitted(builder, omitted, kept > 0);
        builder.Append(']');
    }

    private static void AppendInteractions(
        StringBuilder builder, List<InteractionType> source)
    {
        var interactions = new List<InteractionType>(source);
        interactions.Sort((left, right) => ((int)left).CompareTo((int)right));
        builder.Append('[');
        for (var i = 0; i < interactions.Count; i++)
        {
            if (i > 0) builder.Append('|');
            builder.Append(interactions[i]);
        }
        builder.Append(']');
    }

    private static string Number(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Tile(HexLive.Simulation.Common.TileCoord tile) =>
        $"{tile.Q},{tile.R}";

    private static string ValueOrNone(InteractionType? value) =>
        value?.ToString() ?? "none";
}

}

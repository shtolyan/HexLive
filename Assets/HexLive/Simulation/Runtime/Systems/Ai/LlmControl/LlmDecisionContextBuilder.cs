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
    public static LlmDecisionContext Build(WorldState world, NPCState npc)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (npc is null) throw new ArgumentNullException(nameof(npc));

        return new LlmDecisionContext(
            npc.Id,
            world.Tick,
            npc.Position,
            BuildStateSummary(world, npc),
            BuildPerceptionSummary(npc.Perception),
            BuildMemorySummary(npc.Memory));
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

    private static string BuildPerceptionSummary(PerceptionSnapshot perception)
    {
        var builder = new StringBuilder();
        builder.Append("updatedTick=").Append(perception.LastUpdatedTick);
        builder.Append("; environment[temperature=").Append(Number(perception.Environment.Temperature));
        builder.Append(", crowded=").Append(Bool(perception.Environment.IsCrowded));
        builder.Append(", private=").Append(Bool(perception.Environment.IsPrivate));
        builder.Append(", nearbyAgents=").Append(perception.Environment.NearbyAgentsCount);
        builder.Append(']');

        AppendPerceivedObjects(builder, perception.Objects);
        AppendPerceivedAgents(builder, "allies", perception.Agents);
        AppendPerceivedAgents(builder, "hostiles", perception.Hostiles);
        return builder.ToString();
    }

    private static void AppendPerceivedObjects(
        StringBuilder builder, List<PerceivedObject> source)
    {
        var objects = new List<PerceivedObject>(source);
        objects.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));

        builder.Append("; objects=[");
        for (var i = 0; i < objects.Count; i++)
        {
            if (i > 0) builder.Append("; ");
            var item = objects[i];
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
        builder.Append(']');
    }

    private static void AppendPerceivedAgents(
        StringBuilder builder, string label, List<PerceivedAgent> source)
    {
        var agents = new List<PerceivedAgent>(source);
        agents.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));

        builder.Append("; ").Append(label).Append("=[");
        for (var i = 0; i < agents.Count; i++)
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
        builder.Append(']');
    }

    private static string BuildMemorySummary(MemoryState memory)
    {
        var builder = new StringBuilder();
        AppendKnownObjects(builder, memory.KnownObjects.Values);
        AppendKnownAgents(builder, memory.KnownAgents.Values);
        AppendDangers(builder, memory.Dangers);
        return builder.ToString();
    }

    private static void AppendKnownObjects(
        StringBuilder builder, ICollection<ObjectMemory> source)
    {
        var objects = new List<ObjectMemory>(source);
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
        builder.Append(']');
    }

    private static void AppendKnownAgents(
        StringBuilder builder, ICollection<AgentMemory> source)
    {
        var agents = new List<AgentMemory>(source);
        agents.Sort((left, right) => left.Id.Value.CompareTo(right.Id.Value));

        builder.Append("; knownAgents=[");
        for (var i = 0; i < agents.Count; i++)
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
        builder.Append(']');
    }

    private static void AppendDangers(StringBuilder builder, List<DangerMemory> source)
    {
        var dangers = new List<DangerMemory>(source);
        dangers.Sort((left, right) =>
        {
            var tick = left.Tick.CompareTo(right.Tick);
            if (tick != 0) return tick;
            var q = left.Tile.Q.CompareTo(right.Tile.Q);
            return q != 0 ? q : left.Tile.R.CompareTo(right.Tile.R);
        });

        builder.Append("; dangers=[");
        for (var i = 0; i < dangers.Count; i++)
        {
            if (i > 0) builder.Append("; ");
            builder.Append("{tile=").Append(Tile(dangers[i].Tile));
            builder.Append(", tick=").Append(dangers[i].Tick).Append('}');
        }
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

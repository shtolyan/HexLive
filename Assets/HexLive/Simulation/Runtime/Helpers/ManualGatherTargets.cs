using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §121.10 (#467): однотипные ресурсы исходного гекса и шести соседних, в каком
/// порядке его брать. Один предикат на всё: его спрашивает и приём приказа
/// «собрать всё», и продолжение очереди в <c>ManualOrderSystem</c>, — иначе
/// «все» в меню и «все» в исполнении разошлись бы, и игрок увидел бы
/// остановившуюся на середине девушку без единой причины.
/// <para>
/// Однотипность — это <c>DefinitionId</c>, а не тег и не категория: игрок
/// кликнул по конкретному пальмовому листу и сказал «все такие». Считать
/// количество для UI никто не просил, но число нужно самой симуляции как
/// остаток текущего поиска (см. <see cref="NPCMind.GatherAllRemaining"/>).
/// </para>
/// </summary>
internal static class ManualGatherTargets
{
    public const int RadiusTiles = 1;

    public static bool IsActive(NPCMind mind) =>
        mind.GatherAllDefinitionId.Length > 0 &&
        mind.GatherAllTile is not null &&
        mind.GatherAllInteraction is not null &&
        mind.GatherAllRemaining > 0;

    public static void Arm(
        NPCMind mind, string definitionId, TileCoord tile,
        InteractionType interaction, string interactionId, int remaining)
    {
        mind.GatherAllDefinitionId = definitionId ?? string.Empty;
        mind.GatherAllTile = tile;
        mind.GatherAllInteraction = interaction;
        mind.GatherAllInteractionId = interactionId ?? string.Empty;
        mind.GatherAllRemaining = remaining;
    }

    public static void Clear(NPCMind mind)
    {
        mind.GatherAllDefinitionId = string.Empty;
        mind.GatherAllTile = null;
        mind.GatherAllInteraction = null;
        mind.GatherAllInteractionId = string.Empty;
        mind.GatherAllRemaining = 0;
    }

    /// <summary>
    /// Ближайшие однотипные предметы фиксированного цветочка. Дистанция от
    /// текущей позиции; при равенстве ObjectId, независимо от обхода словаря.
    /// </summary>
    public static List<WorldObjectState> Collect(
        WorldState world, NPCState npc, string definitionId, TileCoord tile,
        InteractionType interaction)
    {
        var found = new List<WorldObjectState>();
        if (definitionId.Length == 0) return found;
        for (var dq = -RadiusTiles; dq <= RadiusTiles; dq++)
        for (var dr = System.Math.Max(-RadiusTiles, -dq - RadiusTiles);
             dr <= System.Math.Min(RadiusTiles, -dq + RadiusTiles); dr++)
        {
            var cell = new TileCoord(tile.Q + dq, tile.R + dr);
            if (!world.Caches.ObjectsByTile.TryGetValue(cell, out var ids)) continue;
            foreach (var id in ids)
                if (world.Entities.Objects.TryGetValue(id, out var candidate) &&
                    IsGatherable(world, npc, candidate, definitionId, interaction) &&
                    world.Junctions.Items.ContainsKey(candidate.Junctions[0])) found.Add(candidate);
        }
        var position = npc.Position;
        float DistanceSquared(WorldObjectState obj)
        {
            var point = world.Junctions.Items[obj.Junctions[0]].WorldPosition;
            var dx = point.X - position.X; var dz = point.Y - position.Y;
            return dx * dx + dz * dz;
        }
        found.Sort((a, b) => {
            var distance = DistanceSquared(a).CompareTo(DistanceSquared(b));
            return distance != 0 ? distance : a.Id.Value.CompareTo(b.Id.Value);
        });
        return found;
    }

    /// <summary>Первый годный предмет очереди, или <c>null</c> — гекс пуст.
    /// Пропущенные (занятые чужой, потерявшие подход) не выбрасывают приказ:
    /// перечисление вернёт их снова на следующем подходе, если освободятся.
    /// </summary>
    public static WorldObjectState? Next(
        WorldState world, NPCState npc, string definitionId, TileCoord tile,
        InteractionType interaction)
    {
        var candidates = Collect(world, npc, definitionId, tile, interaction);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static bool IsGatherable(
        WorldState world, NPCState npc, WorldObjectState candidate,
        string definitionId, InteractionType interaction)
    {
        if (!string.Equals(candidate.DefinitionId, definitionId, System.StringComparison.Ordinal) ||
            candidate.Junctions.Count == 0)
        {
            return false;
        }

        // Занятый кем-то другим предмет — не наш: приказ на него всё равно
        // отбился бы «Occupied», а очередь встала бы на первом же таком.
        if (candidate.IsOccupied && candidate.CurrentUser is { } user &&
            !user.Equals(npc.Id))
        {
            return false;
        }

        // Действие берётся из КАТАЛОГА, как и в обычном ручном приказе: меню
        // могло быть открыто до того, как объект сменил определение.
        return world.Content.ObjectDefinitions.TryGetValue(
                   candidate.DefinitionId, out var definition) &&
               HasInteraction(definition, interaction);
    }

    private static bool HasInteraction(
        ObjectDefinition definition, InteractionType interaction)
    {
        foreach (var candidate in definition.Interactions)
        {
            if (candidate.Type == interaction) return true;
        }

        return false;
    }
}

}

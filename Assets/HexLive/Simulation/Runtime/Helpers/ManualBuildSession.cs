using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §121.12: ручное «Строить» доводит площадку до конца. Это не новая механика
/// исполнения, а выбор СЛЕДУЮЩЕГО обычного ручного приказа: подобрать
/// материал рядом или сделать ещё один цикл <c>Build</c>. Один предикат на
/// приём и на продолжение — иначе клик и очередь разошлись бы в том, что
/// значит «нечем строить».
/// </summary>
internal static class ManualBuildSession
{
    /// <summary>Радиус поиска материала от гекса площадки: 37 гексов.</summary>
    public const int FetchRadiusTiles = 3;

    public enum StepKind
    {
        Build,
        Fetch,
        NoMaterials,
        MissingTool,
        PackFull,
        Blocked
    }

    public readonly struct Step
    {
        public Step(StepKind kind, WorldObjectState? fetch = null)
        {
            Kind = kind;
            Fetch = fetch;
        }

        public StepKind Kind { get; }

        public WorldObjectState? Fetch { get; }
    }

    /// <summary>Площадка, которую «Строить» ведёт до конца. Демонтаж — один
    /// взмах топора, ему очередь не нужна.</summary>
    public static bool Eligible(WorldObjectState site) =>
        BuildSiteMath.IsSite(site) && !BuildSiteMath.IsDemolitionSite(site);

    public static bool IsActive(NPCMind mind) => mind.BuildAllSiteId is not null;

    public static void Arm(NPCMind mind, WorldObjectState site)
    {
        mind.BuildAllSiteId = site.Id;
        mind.BuildAllTile = site.Tile;
        mind.BuildAllProduct = site.BuildProduct ?? string.Empty;
        mind.BuildAllProgress = Progress(site);
        mind.BuildAllYielded = false;
    }

    public static void Clear(NPCMind mind)
    {
        mind.BuildAllSiteId = null;
        mind.BuildAllTile = null;
        mind.BuildAllProduct = string.Empty;
        mind.BuildAllProgress = 0;
        mind.BuildAllYielded = false;
    }

    /// <summary>Сколько всего сдано и поставлено. Цикл, не сдвинувший это
    /// число на той же площадке, ничего не сделал.</summary>
    public static int Progress(WorldObjectState site)
    {
        var total = 0;
        foreach (var material in BuildSiteMath.AllMaterials)
        {
            total += BuildSiteMath.Delivered(site, material) +
                BuildSiteMath.Installed(site, material);
        }

        return total;
    }

    /// <summary>Текущая площадка сессии. Костёр после первой стадии
    /// поднимается в новый объект с открытым счётом (§54.14) — тогда это
    /// площадка того же продукта на том же гексе.</summary>
    public static WorldObjectState? Resolve(WorldState world, NPCMind mind)
    {
        if (mind.BuildAllSiteId is { } id &&
            world.Entities.Objects.TryGetValue(id, out var site))
        {
            return site;
        }

        if (mind.BuildAllTile is not { } tile ||
            !world.Caches.ObjectsByTile.TryGetValue(tile, out var ids))
        {
            return null;
        }

        WorldObjectState? successor = null;
        foreach (var candidateId in ids)
        {
            if (world.Entities.Objects.TryGetValue(candidateId, out var candidate) &&
                Eligible(candidate) &&
                candidate.BuildProduct == mind.BuildAllProduct &&
                (successor is null || candidate.Id.Value < successor.Id.Value))
            {
                successor = candidate;
            }
        }

        return successor;
    }

    /// <summary>Площадки больше нет, а на её гексе стоит её продукт —
    /// значит, достроена, а не отменена.</summary>
    public static bool ProductStandsOnTile(WorldState world, NPCMind mind)
    {
        if (mind.BuildAllTile is not { } tile || mind.BuildAllProduct.Length == 0 ||
            !world.Caches.ObjectsByTile.TryGetValue(tile, out var ids))
        {
            return false;
        }

        foreach (var id in ids)
        {
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == mind.BuildAllProduct)
            {
                return true;
            }
        }

        return false;
    }

    public static Step Next(WorldState world, NPCState npc, WorldObjectState site)
    {
        // 1. Донабрать охапку под текущую стадию, пока рядом есть и влезает.
        var packFull = false;
        WorldObjectState? fetch = null;
        var fetchDistance = float.MaxValue;
        foreach (var material in BuildSiteMath.AllMaterials)
        {
            var shortfall = BuildSiteMath.Remaining(site, material) -
                DecisionSystem.CountInventory(npc, material);
            if (shortfall <= 0)
            {
                continue;
            }

            if (!InventoryMath.FitsWithoutEviction(world, npc, material))
            {
                packFull = true;
                continue;
            }

            var candidate = Nearest(world, npc, site.Tile, material, out var distance);
            if (candidate is not null &&
                (distance < fetchDistance ||
                 distance == fetchDistance && candidate.Id.Value < fetch!.Id.Value))
            {
                fetch = candidate;
                fetchDistance = distance;
            }
        }

        if (fetch is not null)
        {
            return new Step(StepKind.Fetch, fetch);
        }

        // 2. Работа на площадке. Принесённое сдаётся без молотка; поставить
        // деталь или поднять собранное — только с ним (тот же гейт, что у
        // приёма приказа и у ExecutionSystem).
        foreach (var material in BuildSiteMath.AllMaterials)
        {
            if (BuildSiteMath.AcceptsDelivery(site, material) &&
                DecisionSystem.CountInventory(npc, material) > 0)
            {
                return new Step(StepKind.Build);
            }
        }

        var stocked = BuildSiteMath.IsStocked(site);
        if (BuildSiteMath.HasReadyWork(world, site) ||
            stocked && BuildSiteMath.IsAssembled(site))
        {
            return BuildSiteMath.NeedsHammer(world, site) &&
                   !BuildSiteMath.HammerAvailable(world, npc, site)
                ? new Step(StepKind.MissingTool)
                : new Step(StepKind.Build);
        }

        // 3. Здесь больше нечем помочь — назвать почему.
        if (packFull)
        {
            return new Step(StepKind.PackFull);
        }

        foreach (var material in BuildSiteMath.AllMaterials)
        {
            if (BuildSiteMath.Remaining(site, material) > 0)
            {
                return new Step(StepKind.NoMaterials);
            }
        }

        return stocked ? new Step(StepKind.Blocked) : new Step(StepKind.NoMaterials);
    }

    private static WorldObjectState? Nearest(
        WorldState world, NPCState npc, TileCoord center, string material,
        out float distance)
    {
        WorldObjectState? best = null;
        distance = float.MaxValue;
        var position = npc.Position;
        for (var dq = -FetchRadiusTiles; dq <= FetchRadiusTiles; dq++)
        for (var dr = System.Math.Max(-FetchRadiusTiles, -dq - FetchRadiusTiles);
             dr <= System.Math.Min(FetchRadiusTiles, -dq + FetchRadiusTiles); dr++)
        {
            var cell = new TileCoord(center.Q + dq, center.R + dr);
            if (!world.Caches.ObjectsByTile.TryGetValue(cell, out var ids))
            {
                continue;
            }

            foreach (var id in ids)
            {
                if (!world.Entities.Objects.TryGetValue(id, out var candidate) ||
                    !IsLooseMaterial(world, npc, candidate, material) ||
                    !world.Junctions.Items.TryGetValue(candidate.Junctions[0], out var anchor))
                {
                    continue;
                }

                var dx = anchor.WorldPosition.X - position.X;
                var dz = anchor.WorldPosition.Y - position.Y;
                var d = dx * dx + dz * dz;
                if (best is null || d < distance ||
                    d == distance && candidate.Id.Value < best.Id.Value)
                {
                    best = candidate;
                    distance = d;
                }
            }
        }

        return best;
    }

    private static bool IsLooseMaterial(
        WorldState world, NPCState npc, WorldObjectState candidate, string material)
    {
        if (candidate.DefinitionId != material || candidate.Junctions.Count == 0 ||
            candidate.IsCraftProject ||
            candidate.IsOccupied && candidate.CurrentUser is { } user && !user.Equals(npc.Id) ||
            !CampPropertyMath.CanUseUnordered(world, npc, candidate) ||
            !world.Content.ObjectDefinitions.TryGetValue(candidate.DefinitionId, out var definition))
        {
            return false;
        }

        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == InteractionType.PickUp)
            {
                return true;
            }
        }

        return false;
    }
}

}

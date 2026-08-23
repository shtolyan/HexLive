using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §133.10: a locked outfit is a persistent desired set, not only a veto.
/// The selected definitions live on the mind; while a selected piece is loose,
/// its exact world object is remembered permanently until it is worn again.
/// </summary>
public static class OutfitMaintenanceMath
{
    private static readonly Dictionary<string, int> _wornCounts =
        new(StringComparer.Ordinal);
    private static readonly HashSet<ObjectId> _assignedGroundObjects = new();

    /// <summary>Capture the current worn set when the player enables the lock.</summary>
    public static void SetLockedOutfit(WorldState world, NPCState npc, bool enabled)
    {
        // Network retries and repeated UI state sync are idempotent. A second
        // "true" while a piece is drying must not recapture a temporarily
        // naked body and erase the selected set; changing the set is explicitly
        // unlock -> dress -> lock again.
        if (enabled && npc.Mind.OutfitLocked && npc.Mind.DesiredOutfit.Count > 0)
        {
            return;
        }

        ReleaseTrackedMemories(npc);
        npc.Mind.DesiredOutfit.Clear();
        npc.Mind.OutfitLocked = enabled;
        npc.Mind.OutfitMaintenanceTargetObjectId = null;
        npc.Mind.NextOutfitMaintenanceTick = 0;

        if (!enabled)
        {
            return;
        }

        foreach (var worn in npc.WornItems)
        {
            npc.Mind.DesiredOutfit.Add(new DesiredOutfitPiece
            {
                DefinitionId = worn.DefinitionId
            });
        }

        // The captured set already matches. The first ordinary audit is
        // deliberately delayed/staggered; TrackGroundPiece keeps that cadence
        // after a laundry or drying hand-off.
        npc.Mind.NextOutfitMaintenanceTick =
            world.Tick + FirstAuditDelay(npc);
    }

    /// <summary>
    /// Record where a selected garment was laid/hung. This is called by every
    /// common garment-to-world path, so carrying it home may replace the id but
    /// cannot erase the promise to put it back on.
    /// </summary>
    public static void TrackGroundPiece(
        WorldState world, NPCState npc, ItemInstance garment, WorldObjectState ground)
    {
        if (!npc.Mind.OutfitLocked || garment is null || ground is null)
        {
            return;
        }

        var piece = FindPieceToTrack(world, npc, garment.DefinitionId);
        if (piece is null)
        {
            return; // not part of the selected set
        }

        ReleaseMemoryForReplacedObject(world, npc, piece.GroundObjectId, ground.Id);
        piece.GroundObjectId = ground.Id;
        RememberPermanently(world, npc, ground);

        npc.Mind.OutfitMaintenanceTargetObjectId = null;
        if (npc.Mind.NextOutfitMaintenanceTick <= world.Tick)
        {
            npc.Mind.NextOutfitMaintenanceTick =
                world.Tick + Spec133.OutfitAuditIntervalTicks;
        }
    }

    /// <summary>
    /// Cheap periodic audit used by DecisionSystem. Between audits it performs
    /// only an integer comparison; once a dry target is armed it remains live
    /// every decision pass so the active Dress plan is not cancelled mid-walk.
    /// </summary>
    public static bool RefreshMaintenanceTarget(WorldState world, NPCState npc)
    {
        if (!npc.Mind.OutfitLocked || npc.Mind.DesiredOutfit.Count == 0 ||
            npc.Mind.RedressGarments.Count > 0 ||
            npc.Mind.PersonalCarePhase != PersonalCarePhase.None ||
            npc.Execution.HeldGarment is not null)
        {
            npc.Mind.OutfitMaintenanceTargetObjectId = null;
            return false;
        }

        if (npc.Mind.OutfitMaintenanceTargetObjectId is { } armed &&
            TryGetReadyPerceivedPiece(world, npc, armed, out _))
        {
            return true;
        }

        npc.Mind.OutfitMaintenanceTargetObjectId = null;
        if (world.Tick < npc.Mind.NextOutfitMaintenanceTick)
        {
            return false;
        }

        npc.Mind.NextOutfitMaintenanceTick =
            world.Tick + Spec133.OutfitAuditIntervalTicks;

        ReconcileGroundObjects(world, npc);
        foreach (var piece in MissingPieces(npc))
        {
            if (piece.GroundObjectId is not { } objectId ||
                !TryGetReadyPerceivedPiece(world, npc, objectId, out _))
            {
                continue;
            }

            npc.Mind.OutfitMaintenanceTargetObjectId = objectId;
            // The normal Dress cooldown protects cold-driven wardrobe churn.
            // Restoring a selected set is a different, bounded transaction and
            // must be able to put several dried pieces back on consecutively.
            npc.Mind.Cooldowns.RemoveAll(c => c.Goal == GoalType.Dress);
            return true;
        }

        return false;
    }

    /// <summary>May a locked Dress completion consume this exact object?</summary>
    public static bool CanRestorePinnedPiece(
        WorldState world, NPCState npc, WorldObjectState garment)
    {
        return npc.Mind.OutfitLocked && garment is not null &&
            npc.Mind.OutfitMaintenanceTargetObjectId is { } target &&
            target.Equals(garment.Id) &&
            garment.Wetness <= Spec133.OutfitRedressWetnessMax &&
            npc.Mind.DesiredOutfit.Exists(piece =>
                piece.GroundObjectId is { } id && id.Equals(garment.Id) &&
                string.Equals(piece.DefinitionId, garment.DefinitionId,
                    StringComparison.Ordinal));
    }

    /// <summary>Close the exact off-body link after a successful re-dress.</summary>
    public static void MarkWorn(WorldState world, NPCState npc, ObjectId objectId)
    {
        var matched = false;
        foreach (var piece in npc.Mind.DesiredOutfit)
        {
            if (piece.GroundObjectId is not { } tracked || !tracked.Equals(objectId))
            {
                continue;
            }

            piece.GroundObjectId = null;
            matched = true;
            break;
        }

        if (!matched)
        {
            return;
        }

        if (npc.Memory.KnownObjects.Remove(objectId))
        {
            npc.Memory.Version++;
        }

        npc.Mind.OutfitMaintenanceTargetObjectId = null;
        // One tick later the next already-dry member may be armed; the normal
        // steady-state cadence resumes once the whole set matches.
        npc.Mind.NextOutfitMaintenanceTick = world.Tick + 1;
    }

    private static int FirstAuditDelay(NPCState npc) =>
        Spec133.OutfitAuditIntervalTicks +
        Math.Abs(npc.Id.Value % Spec133.OutfitAuditStaggerTicks);

    private static DesiredOutfitPiece? FindPieceToTrack(
        WorldState world, NPCState npc, string definitionId)
    {
        DesiredOutfitPiece? untracked = null;
        foreach (var piece in npc.Mind.DesiredOutfit)
        {
            if (!string.Equals(piece.DefinitionId, definitionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (piece.GroundObjectId is { } oldId)
            {
                if (!world.Entities.Objects.ContainsKey(oldId))
                {
                    return piece; // this same piece is being moved to a new object id
                }

                continue;
            }

            untracked ??= piece;
        }

        return untracked;
    }

    private static List<DesiredOutfitPiece> MissingPieces(NPCState npc)
    {
        _wornCounts.Clear();
        foreach (var worn in npc.WornItems)
        {
            _wornCounts.TryGetValue(worn.DefinitionId, out var count);
            _wornCounts[worn.DefinitionId] = count + 1;
        }

        var missing = new List<DesiredOutfitPiece>();
        foreach (var piece in npc.Mind.DesiredOutfit)
        {
            // A valid loose object is unambiguously off-body. For an untracked
            // entry, consume one matching worn instance before declaring it
            // missing; this keeps duplicate definitions correct.
            if (piece.GroundObjectId is not null)
            {
                missing.Add(piece);
                continue;
            }

            if (_wornCounts.TryGetValue(piece.DefinitionId, out var count) && count > 0)
            {
                _wornCounts[piece.DefinitionId] = count - 1;
                continue;
            }

            missing.Add(piece);
        }

        return missing;
    }

    private static void ReconcileGroundObjects(WorldState world, NPCState npc)
    {
        _assignedGroundObjects.Clear();
        foreach (var piece in npc.Mind.DesiredOutfit)
        {
            if (piece.GroundObjectId is not { } id)
            {
                continue;
            }

            if (world.Entities.Objects.TryGetValue(id, out var existing) &&
                string.Equals(existing.DefinitionId, piece.DefinitionId,
                    StringComparison.Ordinal))
            {
                _assignedGroundObjects.Add(id);
                RememberPermanently(world, npc, existing);
                continue;
            }

            ReleaseMemoryForReplacedObject(world, npc, id, replacement: null);
            piece.GroundObjectId = null;
        }

        foreach (var piece in MissingPieces(npc))
        {
            if (piece.GroundObjectId is not null)
            {
                continue;
            }

            WorldObjectState? replacement = null;
            foreach (var candidate in world.Entities.Objects.Values)
            {
                if (_assignedGroundObjects.Contains(candidate.Id) ||
                    candidate.Owner != npc.Id ||
                    (candidate.IsOccupied && candidate.CurrentUser != npc.Id) ||
                    !string.Equals(candidate.DefinitionId, piece.DefinitionId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                replacement = candidate;
                break;
            }

            if (replacement is null)
            {
                continue;
            }

            piece.GroundObjectId = replacement.Id;
            _assignedGroundObjects.Add(replacement.Id);
            RememberPermanently(world, npc, replacement);
        }
    }

    private static bool TryGetReadyPerceivedPiece(
        WorldState world, NPCState npc, ObjectId objectId,
        out WorldObjectState? garment)
    {
        garment = null;
        if (!world.Entities.Objects.TryGetValue(objectId, out var live) ||
            (live.IsOccupied && live.CurrentUser != npc.Id) ||
            live.Wetness > Spec133.OutfitRedressWetnessMax ||
            npc.Memory.IsShunned(objectId, world.Tick))
        {
            return false;
        }

        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.Id.Equals(objectId) && perceived.IsReachable &&
                perceived.AvailableInteractions.Contains(InteractionType.Dress))
            {
                garment = live;
                return true;
            }
        }

        return false;
    }

    private static void RememberPermanently(
        WorldState world, NPCState npc, WorldObjectState ground)
    {
        var junction = ground.Junctions.Count > 0 ? ground.Junctions[0] : (JunctionId?)null;
        if (!npc.Memory.KnownObjects.TryGetValue(ground.Id, out var memory))
        {
            memory = new ObjectMemory { Id = ground.Id };
            npc.Memory.KnownObjects[ground.Id] = memory;
            npc.Memory.Version++;
        }

        if (!string.Equals(memory.DefinitionId, ground.DefinitionId, StringComparison.Ordinal) ||
            !memory.Tile.Equals(ground.Tile) ||
            !NullableJunctionsEqual(memory.Junction, junction) ||
            !memory.IsPermanent)
        {
            memory.DefinitionId = ground.DefinitionId;
            memory.Tile = ground.Tile;
            memory.Junction = junction;
            memory.IsPermanent = true;
            npc.Memory.Version++;
        }

        memory.LastSeenTick = world.Tick;
    }

    private static bool NullableJunctionsEqual(JunctionId? left, JunctionId? right) =>
        left.HasValue == right.HasValue &&
        (!left.HasValue || left.Value.Equals(right.Value));

    private static void ReleaseTrackedMemories(NPCState npc)
    {
        foreach (var piece in npc.Mind.DesiredOutfit)
        {
            if (piece.GroundObjectId is not { } id ||
                !npc.Memory.KnownObjects.TryGetValue(id, out var memory) ||
                !memory.IsPermanent)
            {
                continue;
            }

            memory.IsPermanent = false;
            npc.Memory.Version++;
        }
    }

    private static void ReleaseMemoryForReplacedObject(
        WorldState world, NPCState npc, ObjectId? oldId, ObjectId? replacement)
    {
        if (oldId is not { } id || (replacement is { } next && next.Equals(id)) ||
            !npc.Memory.KnownObjects.TryGetValue(id, out var memory))
        {
            return;
        }

        if (!world.Entities.Objects.ContainsKey(id))
        {
            npc.Memory.KnownObjects.Remove(id);
        }
        else
        {
            memory.IsPermanent = false;
        }

        npc.Memory.Version++;
    }
}

}

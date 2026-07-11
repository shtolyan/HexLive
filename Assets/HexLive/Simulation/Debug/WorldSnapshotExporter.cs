using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Debug
{

public static class WorldSnapshotExporter
{
    // Trace events, per-NPC memory dumps, relationship/cooldown strings and
    // goal scores are read only by the debug panel, but cost megabytes of
    // garbage per tick when exported unconditionally. The panel opts in
    // while it is visible; everyone else gets the lean snapshot.
    public static bool IncludeDebugDetails { get; set; }

    public static WorldSnapshot Export(WorldState world) => Export(world, null);

    // Passing the previous snapshot lets the exporter refresh the tile and
    // junction lists in place instead of reallocating them every tick. The
    // caller must be the sole owner of that snapshot (consumers may not hold
    // it across ticks — HexWorldRenderer & co. re-poll every frame).
    public static WorldSnapshot Export(WorldState world, WorldSnapshot reuse)
    {
        var snapshot = reuse ?? new WorldSnapshot();
        snapshot.Tick = world.Tick;
        snapshot.Temperature = world.Environment.GlobalTemperature;
        snapshot.Clock = Runtime.EnvironmentSystem.FormatClock(world.Environment.TimeOfDayNormalized);
        snapshot.DayPhase = world.Environment.Phase.ToString();
        snapshot.UvIndex = world.Environment.UvIndex;
        snapshot.IsRaining = world.Environment.IsRaining;
        snapshot.RaftProgress = world.RaftProgress;
        snapshot.RaftTarget = HexLive.Simulation.Core.WorldState.RaftTarget;

        ExportTiles(world, snapshot);
        ExportJunctions(world, snapshot);

        snapshot.Objects.Clear();
        foreach (var pair in world.Entities.Objects)
        {
            var obj = pair.Value;
            var exported = new ObjectSnapshot
            {
                Id = obj.Id,
                DefinitionId = obj.DefinitionId,
                Tile = obj.Tile,
                ResourceAmount = obj.ResourceAmount,
                OwnerNpcId = obj.CurrentUser?.Value
            };

            foreach (var junctionId in obj.Junctions)
            {
                exported.Junctions.Add(junctionId);
            }

            snapshot.Objects.Add(exported);
        }

        snapshot.Npcs.Clear();
        foreach (var pair in world.Entities.Npcs)
        {
            snapshot.Npcs.Add(ExportNpc(world, pair.Value));
        }

        snapshot.Crabs.Clear();
        foreach (var crab in world.Rabbits)
        {
            snapshot.Crabs.Add(new CrabSnapshot
            {
                Id = crab.Id,
                Tile = crab.Tile,
                Position = crab.Position
            });
        }

        snapshot.Sharks.Clear();
        foreach (var shark in world.Sharks)
        {
            snapshot.Sharks.Add(new SharkSnapshot
            {
                Id = shark.Id,
                Tile = shark.Tile,
                Position = shark.Position
            });
        }

        snapshot.Dogs.Clear();
        foreach (var dog in world.Dogs)
        {
            snapshot.Dogs.Add(new DogSnapshot
            {
                Id = dog.Id,
                Tile = dog.Tile,
                Position = dog.Position,
                Health = dog.Health,
                Status = dog.Status.ToString()
            });
        }

        snapshot.TraceEvents.Clear();
        if (IncludeDebugDetails)
        {
            foreach (var trace in world.Events.Items)
            {
                snapshot.TraceEvents.Add(new TraceEventSnapshot
                {
                    Tick = trace.Tick,
                    EntityId = trace.EntityId,
                    Type = trace.Type,
                    Message = trace.Message
                });
            }
        }

        return snapshot;
    }

    // Tiles: coords and elevation are fixed at bootstrap; only flags mutate
    // (building floors/shelter). Reuse the snapshot objects and rewrite the
    // fields — a full rebuild happens only if the world's tile set changed.
    private static void ExportTiles(WorldState world, WorldSnapshot snapshot)
    {
        var tiles = snapshot.Tiles;
        if (tiles.Count == world.Tiles.Items.Count)
        {
            var i = 0;
            var match = true;
            foreach (var pair in world.Tiles.Items)
            {
                var tile = pair.Value;
                var cached = tiles[i++];
                if (!cached.Coord.Equals(tile.Coord))
                {
                    match = false;
                    break;
                }

                cached.Walkable = tile.Flags.HasFlag(TileFlags.Walkable);
                cached.Blocked = tile.Flags.HasFlag(TileFlags.Blocked);
                cached.Indoor = tile.Flags.HasFlag(TileFlags.Indoor);
                cached.Water = tile.Flags.HasFlag(TileFlags.Water);
                cached.Elevation = tile.Elevation;
            }

            if (match)
            {
                return;
            }
        }

        tiles.Clear();
        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            tiles.Add(new TileSnapshot
            {
                Coord = tile.Coord,
                Walkable = tile.Flags.HasFlag(TileFlags.Walkable),
                Blocked = tile.Flags.HasFlag(TileFlags.Blocked),
                Indoor = tile.Flags.HasFlag(TileFlags.Indoor),
                Water = tile.Flags.HasFlag(TileFlags.Water),
                Elevation = tile.Elevation
            });
        }
    }

    private static void ExportJunctions(WorldState world, WorldSnapshot snapshot)
    {
        var junctions = snapshot.Junctions;
        if (junctions.Count == world.Junctions.Items.Count)
        {
            var i = 0;
            var match = true;
            foreach (var pair in world.Junctions.Items)
            {
                var junction = pair.Value;
                var cached = junctions[i++];
                if (!cached.Id.Equals(junction.Id))
                {
                    match = false;
                    break;
                }

                RefreshJunction(world, junction, cached);
            }

            if (match)
            {
                return;
            }
        }

        junctions.Clear();
        foreach (var pair in world.Junctions.Items)
        {
            var junction = pair.Value;
            var js = new JunctionSnapshot
            {
                Id = junction.Id,
                WorldPosition = junction.WorldPosition
            };
            RefreshJunction(world, junction, js);
            junctions.Add(js);
        }
    }

    private static void RefreshJunction(WorldState world, Junction junction, JunctionSnapshot js)
    {
        js.Blocked = junction.Blocked;
        js.Occupied = world.Occupancy.JunctionOwner.TryGetValue(junction.Id, out var owner) && owner is not null;
        js.Reserved = world.Reservations.Junctions.ContainsKey(junction.Id);
        js.IsClimbSeam = world.ClimbSeams.Contains(junction.Id);
        js.IsSwimmable = world.SwimJunctions.Contains(junction.Id);

        js.Tiles.Clear();
        foreach (var tileCoord in junction.Tiles)
        {
            js.Tiles.Add(tileCoord);
        }

        js.Neighbors.Clear();
        foreach (var neighborId in junction.Neighbors)
        {
            js.Neighbors.Add(neighborId);
        }
    }

    private static NpcSnapshot ExportNpc(WorldState world, NPCState npc)
    {
        var npcSnapshot = new NpcSnapshot
        {
            Id = npc.Id,
            DisplayName = npc.DisplayName,
            ActorMesh = npc.ActorMesh,
            Tile = npc.Tile,
            Position = npc.Position,
            RotationDegrees = npc.RotationDegrees,
            Health = npc.Health,
            IsFighting = npc.IsFighting,
            Hunger = npc.Needs.Hunger,
            Thirst = npc.Needs.Thirst,
            Energy = npc.Needs.Energy,
            Comfort = npc.Needs.Comfort,
            Social = npc.Needs.Social,
            ThermalDiscomfort = npc.Needs.ThermalDiscomfort,
            ThermalComfort = npc.Needs.ThermalComfort,
            Stamina = npc.Needs.Stamina,
            Winded = npc.Needs.Stamina < 0.15f,
            Hygiene = npc.Needs.Hygiene,
            Blood = npc.Needs.Blood,
            TanLevel = npc.Needs.TanLevel,
            Sunburn = npc.Needs.Sunburn,
            Bandages = npc.Needs.Bandages,
            Pills = npc.Needs.Pills,
            IsFainted = world.Tick < npc.Mind.FaintedUntilTick,
            IsWaking = world.Tick < npc.Mind.WakeGraceUntilTick,
            Stress = npc.Needs.Stress,
            CurrentGoal = npc.Mind.CurrentGoal.ToString(),
            PlanStatus = npc.Plan.Status.ToString(),
            MovementStatus = npc.Movement.Status.ToString(),
            ExecutionStatus = npc.Execution.Status.ToString(),
            CurrentInteraction = npc.Execution.CurrentInteraction?.ToString() ?? "-",
            TargetTile = npc.Plan.TargetTile,
            IsStarving = npc.Mind.IsStarving,
            InventoryCapacity = npc.Inventory.Capacity,
            GoalLockEndTick = npc.Mind.GoalLock is { } goalLock &&
                goalLock.Goal == npc.Mind.CurrentGoal && goalLock.EndTick > world.Tick
                    ? goalLock.EndTick
                    : null
        };

        foreach (var item in npc.Inventory.Items)
        {
            npcSnapshot.InventoryItems.Add(item);
        }

        foreach (var item in npc.WornItems)
        {
            // Spec 40.11: per-garment durability for the character panel's
            // wear progress bars ("id\tdurability").
            npcSnapshot.WornDurability.Add($"{item.DefinitionId}\t{item.Durability:0.###}");
            // Spec 35.5: per-garment wetness — rain soaks, fire/rack dries;
            // presentation renders a wet sheen that fades as the cloth dries.
            npcSnapshot.WornWetness.Add($"{item.DefinitionId}\t{item.Wetness:0.###}");
            npcSnapshot.WornItems.Add(item);
        }

        // Spec 40.8B: open wounds — one decal each, spot/look from seed,
        // alpha fading with heal. Their unhealed damage sums into the red
        // "won't regen" segment of the HP bar (Health is the mean of parts,
        // so the lock is normalized by the part count).
        var lockedHp = 0f;
        foreach (var wound in npc.Wounds)
        {
            npcSnapshot.Wounds.Add($"{wound.Zone}|{wound.Seed}|{wound.Heal01:0.###}");
            lockedHp += wound.Severity * (1f - wound.Heal01);
        }

        npcSnapshot.WoundLockedHp = lockedHp / npc.Body.Parts.Count;

        var worstPartValue = 1f;
        var worstPartName = "-";
        foreach (var part in npc.Body.Parts)
        {
            npcSnapshot.BodyParts.Add($"{part.Key}={part.Value:F2}");
            if (part.Value < worstPartValue)
            {
                worstPartValue = part.Value;
                worstPartName = part.Key.ToString();
            }

            // Spec 40.8/40.6: skin decals live only on bare zones.
            if (!Runtime.EquipmentMath.IsPartCovered(world, npc, part.Key))
            {
                npcSnapshot.UncoveredParts.Add(part.Key.ToString());
            }
        }

        npcSnapshot.WorstBodyPart = worstPartValue < 1f
            ? $"{worstPartName} {worstPartValue:F2}"
            : "OK";

        // Spec 40.9: one authoritative injury-locomotion hint for the
        // presentation pose layer. Priority: faint > crawl (both legs) >
        // limp (one leg) > arm hang > head clutch > upright.
        float Part(BodyPart p) => npc.Body.Parts.TryGetValue(p, out var v) ? v : 1f;
        var legL = Part(BodyPart.LegL);
        var legR = Part(BodyPart.LegR);
        npcSnapshot.PostureHint =
            npcSnapshot.IsFainted ? "Faint"
            : legL < 0.4f && legR < 0.4f ? "Crawl"
            : legL < 0.4f || legR < 0.4f ? "Limp"
            : Part(BodyPart.ArmL) < 0.4f || Part(BodyPart.ArmR) < 0.4f ? "ArmHang"
            : Part(BodyPart.Head) < 0.4f ? "HeadClutch"
            : "Upright";

        // Iter 28: sitting at a junction whose tiles step exactly one
        // level = a ledge seat; the view plants the butt on the upper step.
        if (npcSnapshot.CurrentInteraction == "Sit" &&
            npc.CurrentJunction is { } sitJunctionId &&
            world.Junctions.Items.TryGetValue(sitJunctionId, out var sitJunction) &&
            sitJunction.Tiles.Count > 1)
        {
            var minElevation = int.MaxValue;
            var maxElevation = int.MinValue;
            foreach (var coord in sitJunction.Tiles)
            {
                if (world.Tiles.Items.TryGetValue(coord, out var seatTile))
                {
                    minElevation = System.Math.Min(minElevation, seatTile.Elevation);
                    maxElevation = System.Math.Max(maxElevation, seatTile.Elevation);
                }
            }

            npcSnapshot.IsLedgeSit = maxElevation - minElevation == 1;
        }

        foreach (var relation in npc.Social.Relationships)
        {
            var otherName = world.Entities.Npcs.TryGetValue(relation.Key, out var otherNpc)
                ? otherNpc.DisplayName
                : $"NPC{relation.Key.Value}";

            npcSnapshot.RelationshipDetails.Add(new RelationshipSnapshot
            {
                OtherId = relation.Key.Value,
                OtherName = string.IsNullOrEmpty(otherName) ? $"NPC{relation.Key.Value}" : otherName,
                Trust = relation.Value.Trust,
                Familiarity = relation.Value.Familiarity,
                Affinity = relation.Value.Affinity
            });
        }

        npcSnapshot.KnownObjectCount = npc.Memory.KnownObjects.Count;

        foreach (var junctionId in npc.Movement.JunctionPath)
        {
            npcSnapshot.Path.Add(junctionId);
        }

        if (IncludeDebugDetails)
        {
            foreach (var cooldown in npc.Mind.Cooldowns)
            {
                if (cooldown.EndTick > world.Tick)
                {
                    npcSnapshot.CooldownGoals.Add($"{cooldown.Goal}:{cooldown.EndTick}");
                }
            }

            foreach (var relation in npc.Social.Relationships)
            {
                npcSnapshot.Relationships.Add(
                    $"NPC{relation.Key.Value}: T={relation.Value.Trust:F2} F={relation.Value.Familiarity:F2} A={relation.Value.Affinity:F2}");
            }

            foreach (var known in npc.Memory.KnownObjects.Values)
            {
                npcSnapshot.KnownObjects.Add(
                    $"{known.DefinitionId}@{known.Tile.Q},{known.Tile.R}" +
                    (known.IsPermanent ? "" : $" (seen t{known.LastSeenTick})"));
            }

            foreach (var goalScore in npc.Mind.LastScores)
            {
                npcSnapshot.GoalScores.Add(new GoalScoreSnapshot
                {
                    Goal = goalScore.Goal.ToString(),
                    FinalScore = goalScore.FinalScore
                });
            }
        }

        return npcSnapshot;
    }
}

}

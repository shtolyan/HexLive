using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;

namespace HexLive.Simulation.Persistence
{

// Spec 41.2 v2: the save IS the model. Every piece of state the simulation
// can mutate is written verbatim; loading applies the blob onto a world
// freshly built by WorldStateFactory from the SAME seed (static topology —
// tiles, junction ids, adjacency, content catalog — is rebuilt, never
// stored). Replay-as-save (v1) died the day non-deterministic inputs were
// planned: player commands and LLM decisions can't be replayed from a seed.
//
// Not serialized, by design:
// - Events (trace ring buffer — no simulation effect),
// - Mind.LastScores / Mind.LastDecision (debug UI, rewritten every
//   decision pass),
// - JunctionComponents / ComponentsBuiltVersion (derived cache — cleared
//   on load, rebuilt on first pathfind).
public static class WorldSaveSerializer
{
    public const int BlobVersion = 7; // v7: per-item garment dirtiness
    private const int OldestReadableBlobVersion = 3;

    private const int EndMarker = unchecked((int)0x454E4421); // "END!"

    public static void Write(WorldState world, BinaryWriter w)
    {
        w.Write(BlobVersion);
        w.Write(world.Seed);
        w.Write(world.Tick);
        w.Write(world.NextRuntimeObjectId);
        w.Write(world.RaftProgress);
        w.Write(world.Completed);
        w.Write(world.DeathRecords.Count);
        foreach (var death in world.DeathRecords)
        {
            WriteDeathRecord(w, death);
        }

        w.Write(world.ColonyInDireStraits);
        w.Write(world.NextMobId);
        w.Write(world.NextRabbitId);
        w.Write(world.NextMobSpawnCheckTick);
        w.Write(world.NextRabbitSpawnCheckTick);
        w.Write(world.TopologyVersion);

        var env = world.Environment;
        w.Write(env.GlobalTemperature);
        w.Write(env.GlobalCrowdLevel);
        w.Write(env.TimeOfDayNormalized);
        w.Write((int)env.Phase);
        w.Write(env.UvIndex);
        w.Write(env.IsRaining);
        w.Write(env.RainUntilTick);

        w.Write(world.Project is not null);
        if (world.Project is { } project)
        {
            WriteTile(w, project.Tile);
            w.Write(project.DoorEdge);
            w.Write(project.FloorDone);
            for (var i = 0; i < project.EdgeDone.Length; i++)
            {
                w.Write(project.EdgeDone[i]);
            }

            w.Write(project.Completed);
        }

        // Tile flags mutate at runtime (HasFloor/Indoor from the hut build).
        w.Write(world.Tiles.Items.Count);
        foreach (var tile in world.Tiles.Items.Values)
        {
            WriteTile(w, tile.Coord);
            w.Write((int)tile.Flags);
        }

        // Junction Blocked/Door mutate at runtime (obstacles, walls, doors).
        WriteJunctionFlagSet(world, w, static junction => junction.Blocked);
        WriteJunctionFlagSet(world, w, static junction => junction.Door);

        w.Write(world.Entities.Objects.Count);
        foreach (var obj in world.Entities.Objects.Values)
        {
            WriteObject(w, obj);
        }

        w.Write(world.Entities.Npcs.Count);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            WriteNpc(w, npc);
        }

        w.Write(world.Mobs.Count);
        foreach (var dog in world.Mobs)
        {
            w.Write(dog.Id);
            w.Write(string.IsNullOrEmpty(dog.MobId) ? Content.MobIds.Dog : dog.MobId); // v5
            WriteTile(w, dog.Tile);
            w.Write(dog.Junction.Value);
            WriteFloat2(w, dog.Position);
            w.Write(dog.Health);
            w.Write((int)dog.Status);
            WriteNullableEntity(w, dog.TargetNpc);
        }

        w.Write(world.Rabbits.Count);
        foreach (var rabbit in world.Rabbits)
        {
            w.Write(rabbit.Id);
            WriteTile(w, rabbit.Tile);
            w.Write(rabbit.Junction.Value);
            WriteFloat2(w, rabbit.Position);
            w.Write(rabbit.SpookedUntilTick);
        }

        w.Write(world.Sharks.Count);
        foreach (var shark in world.Sharks)
        {
            w.Write(shark.Id);
            WriteTile(w, shark.Tile);
            w.Write(shark.Junction.Value);
            WriteFloat2(w, shark.Position);
        }

        w.Write(world.Reservations.Junctions.Count);
        foreach (var pair in world.Reservations.Junctions)
        {
            w.Write(pair.Key.Value);
            w.Write(pair.Value.Owner.Value);
            w.Write(pair.Value.StartTick);
            w.Write(pair.Value.EndTick);
        }

        var ownedJunctions = 0;
        foreach (var pair in world.Occupancy.JunctionOwner)
        {
            if (pair.Value is not null)
            {
                ownedJunctions++;
            }
        }

        w.Write(ownedJunctions);
        foreach (var pair in world.Occupancy.JunctionOwner)
        {
            if (pair.Value is { } owner)
            {
                w.Write(pair.Key.Value);
                w.Write(owner.Value);
            }
        }

        // Occupancy tile lists and the runtime caches are written VERBATIM
        // (keys and list order), not rebuilt on load: systems iterate these
        // lists, so a rebuilt order would be a different world.
        w.Write(world.Occupancy.EntitiesInTile.Count);
        foreach (var pair in world.Occupancy.EntitiesInTile)
        {
            WriteTile(w, pair.Key);
            WriteEntityList(w, pair.Value);
        }

        w.Write(world.Caches.EntitiesByTile.Count);
        foreach (var pair in world.Caches.EntitiesByTile)
        {
            WriteTile(w, pair.Key);
            WriteEntityList(w, pair.Value);
        }

        w.Write(world.Caches.EntitiesByFragment.Count);
        foreach (var pair in world.Caches.EntitiesByFragment)
        {
            w.Write(pair.Key.Value);
            WriteEntityList(w, pair.Value);
        }

        w.Write(world.Caches.ObjectsByTile.Count);
        foreach (var pair in world.Caches.ObjectsByTile)
        {
            WriteTile(w, pair.Key);
            w.Write(pair.Value.Count);
            foreach (var id in pair.Value)
            {
                w.Write(id.Value);
            }
        }

        w.Write(EndMarker);
    }

    // Applies a saved blob onto a world freshly built from the same seed.
    // Throws InvalidDataException on any mismatch — the caller must treat
    // the world as unusable and bootstrap a fresh one.
    public static void Read(WorldState world, BinaryReader r)
    {
        var version = r.ReadInt32();
        if (version < OldestReadableBlobVersion || version > BlobVersion)
        {
            throw new InvalidDataException($"Save blob version {version}, expected {BlobVersion}.");
        }

        var seed = r.ReadInt32();
        if (seed != world.Seed)
        {
            throw new InvalidDataException($"Save seed {seed} does not match world seed {world.Seed}.");
        }

        world.Tick = r.ReadInt32();
        world.NextRuntimeObjectId = r.ReadInt32();
        world.RaftProgress = r.ReadInt32();
        world.Completed = version >= 4
            ? r.ReadBoolean()
            : world.RaftProgress >= WorldState.RaftTarget;
        world.DeathRecords.Clear();
        if (version >= 4)
        {
            var deathCount = r.ReadInt32();
            for (var i = 0; i < deathCount; i++)
            {
                world.DeathRecords.Add(ReadDeathRecord(r));
            }
        }

        world.ColonyInDireStraits = r.ReadBoolean();
        world.NextMobId = r.ReadInt32();
        world.NextRabbitId = r.ReadInt32();
        world.NextMobSpawnCheckTick = r.ReadInt32();
        world.NextRabbitSpawnCheckTick = r.ReadInt32();
        world.TopologyVersion = r.ReadInt32();

        var env = world.Environment;
        env.GlobalTemperature = r.ReadSingle();
        env.GlobalCrowdLevel = r.ReadInt32();
        env.TimeOfDayNormalized = r.ReadSingle();
        env.Phase = (DayPhase)r.ReadInt32();
        env.UvIndex = r.ReadSingle();
        env.IsRaining = r.ReadBoolean();
        env.RainUntilTick = r.ReadInt32();

        world.Project = null;
        if (r.ReadBoolean())
        {
            var project = new BuildProject
            {
                Tile = ReadTile(r),
                DoorEdge = r.ReadInt32(),
                FloorDone = r.ReadBoolean()
            };
            for (var i = 0; i < project.EdgeDone.Length; i++)
            {
                project.EdgeDone[i] = r.ReadBoolean();
            }

            project.Completed = r.ReadBoolean();
            world.Project = project;
        }

        var tileCount = r.ReadInt32();
        for (var i = 0; i < tileCount; i++)
        {
            var coord = ReadTile(r);
            var flags = (TileFlags)r.ReadInt32();
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                throw new InvalidDataException($"Save references missing tile {coord}.");
            }

            tile.Flags = flags;
        }

        foreach (var junction in world.Junctions.Items.Values)
        {
            junction.Blocked = false;
            junction.Door = false;
        }

        ReadJunctionFlagSet(world, r, static junction => junction.Blocked = true);
        ReadJunctionFlagSet(world, r, static junction => junction.Door = true);

        // Derived reachability cache: rebuilt on first pathfind.
        world.JunctionComponents.Clear();
        world.ComponentsBuiltVersion = 0;

        world.Entities.Objects.Clear();
        var objectCount = r.ReadInt32();
        for (var i = 0; i < objectCount; i++)
        {
            var obj = ReadObject(r, version);
            world.Entities.Objects[obj.Id] = obj;
        }

        world.Entities.Npcs.Clear();
        var npcCount = r.ReadInt32();
        for (var i = 0; i < npcCount; i++)
        {
            var npc = ReadNpc(r, version);
            world.Entities.Npcs[npc.Id] = npc;
        }

        world.Mobs.Clear();
        var dogCount = r.ReadInt32();
        for (var i = 0; i < dogCount; i++)
        {
            var dogPos = default(Float2);
            var dog = new MobState
            {
                Id = r.ReadInt32(),
                // v5 field; older blobs are all-dog worlds.
                MobId = version >= 5 ? r.ReadString() : Content.MobIds.Dog,
                Tile = ReadTile(r),
                Junction = new JunctionId(r.ReadInt32()),
                Position = dogPos = ReadFloat2(r),
                Health = r.ReadSingle(),
                Status = (MobStatus)r.ReadInt32(),
                TargetNpc = ReadNullableEntity(r)
            };
            // The glide is a render-only smoothing; a loaded dog stands at its
            // saved position with no pending hop, so anchor the target there.
            dog.TargetPosition = dogPos;
            dog.GlideAnchor = dogPos;
            world.Mobs.Add(dog);
        }

        world.Rabbits.Clear();
        var rabbitCount = r.ReadInt32();
        for (var i = 0; i < rabbitCount; i++)
        {
            world.Rabbits.Add(new RabbitState
            {
                Id = r.ReadInt32(),
                Tile = ReadTile(r),
                Junction = new JunctionId(r.ReadInt32()),
                Position = ReadFloat2(r),
                SpookedUntilTick = r.ReadInt32()
            });
        }

        world.Sharks.Clear();
        var sharkCount = r.ReadInt32();
        for (var i = 0; i < sharkCount; i++)
        {
            world.Sharks.Add(new SharkState
            {
                Id = r.ReadInt32(),
                Tile = ReadTile(r),
                Junction = new JunctionId(r.ReadInt32()),
                Position = ReadFloat2(r)
            });
        }

        world.Reservations.Junctions.Clear();
        var reservationCount = r.ReadInt32();
        for (var i = 0; i < reservationCount; i++)
        {
            world.Reservations.Junctions[new JunctionId(r.ReadInt32())] = new ReservationRecord
            {
                Owner = new EntityId(r.ReadInt32()),
                StartTick = r.ReadInt32(),
                EndTick = r.ReadInt32()
            };
        }

        foreach (var junctionId in world.Junctions.Items.Keys)
        {
            world.Occupancy.JunctionOwner[junctionId] = null;
        }

        var ownedCount = r.ReadInt32();
        for (var i = 0; i < ownedCount; i++)
        {
            world.Occupancy.JunctionOwner[new JunctionId(r.ReadInt32())] = new EntityId(r.ReadInt32());
        }

        world.Occupancy.EntitiesInTile.Clear();
        var occupiedTileCount = r.ReadInt32();
        for (var i = 0; i < occupiedTileCount; i++)
        {
            world.Occupancy.EntitiesInTile[ReadTile(r)] = ReadEntityList(r);
        }

        world.Caches.EntitiesByTile.Clear();
        var entityTileCount = r.ReadInt32();
        for (var i = 0; i < entityTileCount; i++)
        {
            world.Caches.EntitiesByTile[ReadTile(r)] = ReadEntityList(r);
        }

        world.Caches.EntitiesByFragment.Clear();
        var fragmentCount = r.ReadInt32();
        for (var i = 0; i < fragmentCount; i++)
        {
            world.Caches.EntitiesByFragment[new FragmentId(r.ReadInt32())] = ReadEntityList(r);
        }

        world.Caches.ObjectsByTile.Clear();
        var objectTileCount = r.ReadInt32();
        for (var i = 0; i < objectTileCount; i++)
        {
            var coord = ReadTile(r);
            var count = r.ReadInt32();
            var list = new List<ObjectId>(count);
            for (var j = 0; j < count; j++)
            {
                list.Add(new ObjectId(r.ReadInt32()));
            }

            world.Caches.ObjectsByTile[coord] = list;
        }

        world.Events.Clear();

        if (r.ReadInt32() != EndMarker)
        {
            throw new InvalidDataException("Save blob end marker missing — truncated or corrupt save.");
        }

        MigrateRetiredContent(world);
    }

    // Save migration: content retired from the bootstrap still lives inside
    // older saves' entity lists — despawn it on load or the girls keep using
    // ghosts (e.g. the water.pond anchors: removed from the world, invisible
    // to the renderer, yet loaded NPCs kept hiking to them for water).
    private static void MigrateRetiredContent(WorldState world)
    {
        var retired = new List<ObjectId>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == "water.pond")
            {
                retired.Add(obj.Id);
            }
        }

        foreach (var id in retired)
        {
            WorldObjectMutations.DespawnObject(world, id);
        }
    }

    private static void WriteDeathRecord(BinaryWriter w, DeathRecord death)
    {
        w.Write(death.EntityId.Value);
        w.Write(death.DisplayName);
        w.Write(death.Tick);
        WriteTile(w, death.Tile);
        w.Write(death.Cause);
    }

    private static DeathRecord ReadDeathRecord(BinaryReader r)
    {
        return new DeathRecord
        {
            EntityId = new EntityId(r.ReadInt32()),
            DisplayName = r.ReadString(),
            Tick = r.ReadInt32(),
            Tile = ReadTile(r),
            Cause = r.ReadString()
        };
    }

    private static void WriteObject(BinaryWriter w, WorldObjectState obj)
    {
        w.Write(obj.Id.Value);
        w.Write(obj.DefinitionId);
        w.Write(obj.Fragment.Value);
        WriteTile(w, obj.Tile);
        WriteJunctionList(w, obj.Junctions);
        w.Write(obj.IsOccupied);
        WriteNullableEntity(w, obj.CurrentUser);
        w.Write(obj.ResourceAmount);
        w.Write(obj.Wetness);
        w.Write(obj.Durability);
        w.Write(obj.Dirtiness);
        w.Write(obj.Bloodiness);
        w.Write(obj.SpawnTick);
        WriteJunctionList(w, obj.BlockedJunctions);
        w.Write(obj.NextProductionTick);
        w.Write(obj.ProducedItems.Count);
        foreach (var id in obj.ProducedItems)
        {
            w.Write(id.Value);
        }
    }

    private static WorldObjectState ReadObject(BinaryReader r, int version)
    {
        var obj = new WorldObjectState
        {
            Id = new ObjectId(r.ReadInt32()),
            DefinitionId = r.ReadString(),
            Fragment = new FragmentId(r.ReadInt32()),
            Tile = ReadTile(r)
        };
        ReadJunctionList(r, obj.Junctions);
        obj.IsOccupied = r.ReadBoolean();
        obj.CurrentUser = ReadNullableEntity(r);
        obj.ResourceAmount = r.ReadSingle();
        obj.Wetness = r.ReadSingle();
        obj.Durability = r.ReadSingle();
        obj.Dirtiness = version >= 7 ? r.ReadSingle() : 0f;
        obj.Bloodiness = version >= 7 ? r.ReadSingle() : 0f;
        obj.SpawnTick = r.ReadInt32();
        ReadJunctionList(r, obj.BlockedJunctions);
        obj.NextProductionTick = r.ReadInt32();
        var producedCount = r.ReadInt32();
        for (var i = 0; i < producedCount; i++)
        {
            obj.ProducedItems.Add(new ObjectId(r.ReadInt32()));
        }

        return obj;
    }

    private static void WriteNpc(BinaryWriter w, NPCState npc)
    {
        w.Write(npc.Id.Value);
        w.Write(npc.DisplayName);
        w.Write(npc.ActorMesh);
        w.Write(npc.Fragment.Value);
        WriteTile(w, npc.Tile);
        WriteNullableJunction(w, npc.CurrentJunction);
        WriteFloat2(w, npc.Position);
        w.Write(npc.RotationDegrees);
        w.Write(npc.MoveSpeed);
        w.Write(npc.TurnSpeed);
        w.Write(npc.PostTurnPause);
        w.Write(npc.EquippedWarmth);
        w.Write(npc.EquippedArmor);
        w.Write(npc.Health);
        w.Write(npc.IsFighting);
        w.Write(npc.SunExposure);
        w.Write(npc.NextWoundId);
        w.Write((int)npc.BottleWater);

        WriteItemList(w, npc.WornItems);
        WriteJunctionList(w, npc.ClaimedJunctions);

        w.Write(npc.Body.Parts.Count);
        foreach (var pair in npc.Body.Parts)
        {
            w.Write((int)pair.Key);
            w.Write(pair.Value);
        }

        w.Write(npc.Wounds.Count);
        foreach (var wound in npc.Wounds)
        {
            w.Write(wound.Id);
            w.Write((int)wound.Zone);
            w.Write(wound.Severity);
            w.Write(wound.Heal01);
            w.Write(wound.Seed);
        }

        var needs = npc.Needs;
        w.Write(needs.Hunger);
        w.Write(needs.Thirst);
        w.Write(needs.Energy);
        w.Write(needs.Comfort);
        w.Write(needs.Social);
        w.Write(needs.ThermalDiscomfort);
        w.Write(needs.ThermalComfort);
        w.Write(needs.Stamina);
        w.Write(needs.Hygiene);
        w.Write(needs.Blood);
        w.Write(needs.Bandages);
        w.Write(needs.HerbalBandages);
        w.Write(needs.Pills);
        w.Write(needs.TanLevel);
        w.Write(needs.Sunburn);
        w.Write(needs.Stress);

        var mind = npc.Mind;
        w.Write((int)SaveGoal(mind.CurrentGoal));
        w.Write(mind.IsStarving);
        w.Write(mind.IsDehydrated);
        w.Write(mind.GrievingUntilTick);
        w.Write(mind.FaintedUntilTick);
        w.Write((int)mind.ComaCause); // v6, spec §60
        w.Write(mind.WakeGraceUntilTick);
        w.Write(mind.PendingTalkSinceTick);
        WriteNullableEntity(w, mind.PendingTalkFrom);
        w.Write(mind.GrievedCorpses.Count);
        foreach (var id in mind.GrievedCorpses)
        {
            w.Write(id.Value);
        }

        w.Write(mind.GoalLock is not null);
        if (mind.GoalLock is { } goalLock)
        {
            w.Write((int)SaveGoal(goalLock.Goal));
            w.Write(goalLock.StartTick);
            w.Write(goalLock.EndTick);
        }

        w.Write(mind.Cooldowns.Count);
        foreach (var cooldown in mind.Cooldowns)
        {
            w.Write((int)SaveGoal(cooldown.Goal));
            w.Write(cooldown.EndTick);
        }

        var plan = npc.Plan;
        w.Write((int)SaveGoal(plan.Goal));
        w.Write(plan.CurrentStepIndex);
        w.Write((int)plan.Status);
        WriteNullableObject(w, plan.TargetObjectId);
        WriteNullableJunction(w, plan.TargetJunctionId);
        w.Write(plan.TargetTile.HasValue);
        if (plan.TargetTile is { } targetTile)
        {
            WriteTile(w, targetTile);
        }

        WriteNullableString(w, plan.TargetItemDefinitionId);
        WriteNullableEntity(w, plan.TargetAgentId);
        w.Write(plan.Steps.Count);
        foreach (var step in plan.Steps)
        {
            w.Write((int)step.Type);
            WriteNullableJunction(w, step.TargetJunction);
            WriteNullableObject(w, step.TargetObject);
            w.Write(step.Interaction.HasValue);
            if (step.Interaction is { } interaction)
            {
                w.Write((int)interaction);
            }

            w.Write(step.TimeoutEndTick.HasValue);
            if (step.TimeoutEndTick is { } timeout)
            {
                w.Write(timeout);
            }
        }

        var execution = npc.Execution;
        w.Write((int)execution.Status);
        w.Write(execution.CurrentInteraction.HasValue);
        if (execution.CurrentInteraction is { } currentInteraction)
        {
            w.Write((int)currentInteraction);
        }

        WriteNullableObject(w, execution.TargetObject);
        w.Write(execution.StartTick);
        w.Write(execution.EndTick);
        w.Write(execution.FailureReason);
        w.Write(execution.LastCompletedTick);

        var movement = npc.Movement;
        w.Write(movement.BlockedWaitTicks);
        w.Write(movement.IsMoving);
        WriteJunctionList(w, movement.JunctionPath);
        w.Write(movement.PathIndex);
        WriteFloat2(w, movement.DesiredDirection);
        w.Write(movement.DesiredRotationDegrees);
        w.Write(movement.MoveSpeed);
        w.Write(movement.TurnSpeed);
        w.Write((int)movement.Status);
        w.Write(movement.StopReason);
        w.Write(movement.PostTurnDelay);
        w.Write(movement.PostTurnTimer);

        // Perception is rebuilt every medium tick, but decisions read it in
        // between — saved in full so the first post-load ticks match.
        var perception = npc.Perception;
        w.Write(perception.LastUpdatedTick);
        w.Write(perception.Self.Hunger);
        w.Write(perception.Self.Energy);
        w.Write(perception.Self.Comfort);
        w.Write(perception.Self.Social);
        w.Write(perception.Self.ThermalDiscomfort);
        WriteTile(w, perception.Self.Tile);
        w.Write(perception.Self.Fragment.Value);
        w.Write(perception.Environment.Temperature);
        w.Write(perception.Environment.IsCrowded);
        w.Write(perception.Environment.IsPrivate);
        w.Write(perception.Environment.NearbyAgentsCount);
        w.Write(perception.Objects.Count);
        foreach (var perceived in perception.Objects)
        {
            w.Write(perceived.Id.Value);
            w.Write(perceived.DefinitionId);
            w.Write(perceived.FromMemory);
            WriteTile(w, perceived.Tile);
            w.Write(perceived.Distance);
            w.Write(perceived.IsReachable);
            w.Write(perceived.IsOccupied);
            WriteNullableEntity(w, perceived.OccupiedBy);
            w.Write(perceived.AvailableInteractions.Count);
            foreach (var interaction in perceived.AvailableInteractions)
            {
                w.Write((int)interaction);
            }
        }

        w.Write(perception.Agents.Count);
        foreach (var agent in perception.Agents)
        {
            w.Write(agent.Id.Value);
            WriteTile(w, agent.Tile);
            w.Write(agent.Distance);
            w.Write(agent.CanSee);
            w.Write(agent.CanHear);
            WriteNullableJunction(w, agent.Junction);
            w.Write(agent.IsReachable);
            w.Write(agent.IsBusy);
            w.Write(agent.IsMoving);
            w.Write(agent.Relationship.Trust);
            w.Write(agent.Relationship.Affinity);
        }

        w.Write(npc.Memory.KnownObjects.Count);
        foreach (var memory in npc.Memory.KnownObjects.Values)
        {
            w.Write(memory.Id.Value);
            w.Write(memory.DefinitionId);
            WriteTile(w, memory.Tile);
            WriteNullableJunction(w, memory.Junction);
            w.Write(memory.IsPermanent);
            w.Write(memory.LastSeenTick);
        }

        w.Write(npc.Memory.Dangers.Count);
        foreach (var danger in npc.Memory.Dangers)
        {
            WriteTile(w, danger.Tile);
            w.Write(danger.Tick);
        }

        w.Write(npc.Social.Embarrassment);
        w.Write(npc.Social.Relationships.Count);
        foreach (var pair in npc.Social.Relationships)
        {
            w.Write(pair.Key.Value);
            w.Write(pair.Value.Trust);
            w.Write(pair.Value.Familiarity);
            w.Write(pair.Value.Affinity);
        }

        w.Write(npc.Inventory.Capacity);
        WriteItemList(w, npc.Inventory.Items);
    }

    private static NPCState ReadNpc(BinaryReader r, int version)
    {
        var npc = new NPCState
        {
            Id = new EntityId(r.ReadInt32()),
            DisplayName = r.ReadString(),
            ActorMesh = r.ReadString(),
            Fragment = new FragmentId(r.ReadInt32()),
            Tile = ReadTile(r),
            CurrentJunction = ReadNullableJunction(r),
            Position = ReadFloat2(r),
            RotationDegrees = r.ReadSingle(),
            MoveSpeed = r.ReadSingle(),
            TurnSpeed = r.ReadSingle(),
            PostTurnPause = r.ReadSingle(),
            EquippedWarmth = r.ReadSingle(),
            EquippedArmor = r.ReadSingle(),
            Health = r.ReadSingle(),
            IsFighting = r.ReadBoolean(),
            SunExposure = r.ReadSingle(),
            NextWoundId = r.ReadInt32(),
            BottleWater = (WaterKind)r.ReadInt32()
        };

        ReadItemList(r, npc.WornItems, version);
        ReadJunctionList(r, npc.ClaimedJunctions);

        var partCount = r.ReadInt32();
        for (var i = 0; i < partCount; i++)
        {
            var part = (BodyPart)r.ReadInt32();
            npc.Body.Parts[part] = r.ReadSingle();
        }

        var woundCount = r.ReadInt32();
        for (var i = 0; i < woundCount; i++)
        {
            npc.Wounds.Add(new WoundState
            {
                Id = r.ReadInt32(),
                Zone = (BodyPart)r.ReadInt32(),
                Severity = r.ReadSingle(),
                Heal01 = r.ReadSingle(),
                Seed = r.ReadInt32()
            });
        }

        var needs = npc.Needs;
        needs.Hunger = r.ReadSingle();
        needs.Thirst = r.ReadSingle();
        needs.Energy = r.ReadSingle();
        needs.Comfort = r.ReadSingle();
        needs.Social = r.ReadSingle();
        needs.ThermalDiscomfort = r.ReadSingle();
        needs.ThermalComfort = r.ReadSingle();
        needs.Stamina = r.ReadSingle();
        needs.Hygiene = r.ReadSingle();
        needs.Blood = r.ReadSingle();
        needs.Bandages = r.ReadInt32();
        needs.HerbalBandages = r.ReadInt32();
        needs.Pills = r.ReadInt32();
        needs.TanLevel = r.ReadSingle();
        needs.Sunburn = r.ReadSingle();
        needs.Stress = r.ReadSingle();

        var mind = npc.Mind;
        mind.CurrentGoal = (GoalType)r.ReadInt32();
        mind.IsStarving = r.ReadBoolean();
        mind.IsDehydrated = r.ReadBoolean();
        mind.GrievingUntilTick = r.ReadInt32();
        mind.FaintedUntilTick = r.ReadInt32();
        mind.ComaCause = version >= 6 ? (ComaCause)r.ReadInt32() : ComaCause.None; // spec §60
        mind.WakeGraceUntilTick = r.ReadInt32();
        mind.PendingTalkSinceTick = r.ReadInt32();
        mind.PendingTalkFrom = ReadNullableEntity(r);
        var grievedCount = r.ReadInt32();
        for (var i = 0; i < grievedCount; i++)
        {
            mind.GrievedCorpses.Add(new ObjectId(r.ReadInt32()));
        }

        if (r.ReadBoolean())
        {
            mind.GoalLock = new GoalLock
            {
                Goal = (GoalType)r.ReadInt32(),
                StartTick = r.ReadInt32(),
                EndTick = r.ReadInt32()
            };
        }

        var cooldownCount = r.ReadInt32();
        for (var i = 0; i < cooldownCount; i++)
        {
            mind.Cooldowns.Add(new GoalCooldown
            {
                Goal = (GoalType)r.ReadInt32(),
                EndTick = r.ReadInt32()
            });
        }

        var plan = npc.Plan;
        plan.Goal = (GoalType)r.ReadInt32();
        plan.CurrentStepIndex = r.ReadInt32();
        plan.Status = (PlanStatus)r.ReadInt32();
        plan.TargetObjectId = ReadNullableObject(r);
        plan.TargetJunctionId = ReadNullableJunction(r);
        plan.TargetTile = r.ReadBoolean() ? ReadTile(r) : null;
        plan.TargetItemDefinitionId = ReadNullableString(r);
        plan.TargetAgentId = ReadNullableEntity(r);
        var stepCount = r.ReadInt32();
        for (var i = 0; i < stepCount; i++)
        {
            plan.Steps.Add(new PlanStep
            {
                Type = (PlanStepType)r.ReadInt32(),
                TargetJunction = ReadNullableJunction(r),
                TargetObject = ReadNullableObject(r),
                Interaction = r.ReadBoolean() ? (InteractionType)r.ReadInt32() : null,
                TimeoutEndTick = r.ReadBoolean() ? r.ReadInt32() : null
            });
        }

        var execution = npc.Execution;
        execution.Status = (ExecutionStatus)r.ReadInt32();
        execution.CurrentInteraction = r.ReadBoolean() ? (InteractionType)r.ReadInt32() : null;
        execution.TargetObject = ReadNullableObject(r);
        execution.StartTick = r.ReadInt32();
        execution.EndTick = r.ReadInt32();
        execution.FailureReason = r.ReadString();
        execution.LastCompletedTick = r.ReadInt32();

        var movement = npc.Movement;
        movement.BlockedWaitTicks = r.ReadInt32();
        movement.IsMoving = r.ReadBoolean();
        ReadJunctionList(r, movement.JunctionPath);
        movement.PathIndex = r.ReadInt32();
        movement.DesiredDirection = ReadFloat2(r);
        movement.DesiredRotationDegrees = r.ReadSingle();
        movement.MoveSpeed = r.ReadSingle();
        movement.TurnSpeed = r.ReadSingle();
        movement.Status = (MovementStatus)r.ReadInt32();
        movement.StopReason = r.ReadString();
        movement.PostTurnDelay = r.ReadSingle();
        movement.PostTurnTimer = r.ReadSingle();

        var perception = npc.Perception;
        perception.LastUpdatedTick = r.ReadInt32();
        perception.Self.Hunger = r.ReadSingle();
        perception.Self.Energy = r.ReadSingle();
        perception.Self.Comfort = r.ReadSingle();
        perception.Self.Social = r.ReadSingle();
        perception.Self.ThermalDiscomfort = r.ReadSingle();
        perception.Self.Tile = ReadTile(r);
        perception.Self.Fragment = new FragmentId(r.ReadInt32());
        perception.Environment.Temperature = r.ReadSingle();
        perception.Environment.IsCrowded = r.ReadBoolean();
        perception.Environment.IsPrivate = r.ReadBoolean();
        perception.Environment.NearbyAgentsCount = r.ReadInt32();
        var perceivedObjectCount = r.ReadInt32();
        for (var i = 0; i < perceivedObjectCount; i++)
        {
            var perceived = new PerceivedObject
            {
                Id = new ObjectId(r.ReadInt32()),
                DefinitionId = r.ReadString(),
                FromMemory = r.ReadBoolean(),
                Tile = ReadTile(r),
                Distance = r.ReadSingle(),
                IsReachable = r.ReadBoolean(),
                IsOccupied = r.ReadBoolean(),
                OccupiedBy = ReadNullableEntity(r)
            };
            var interactionCount = r.ReadInt32();
            for (var j = 0; j < interactionCount; j++)
            {
                perceived.AvailableInteractions.Add((InteractionType)r.ReadInt32());
            }

            perception.Objects.Add(perceived);
        }

        var perceivedAgentCount = r.ReadInt32();
        for (var i = 0; i < perceivedAgentCount; i++)
        {
            var agent = new PerceivedAgent
            {
                Id = new EntityId(r.ReadInt32()),
                Tile = ReadTile(r),
                Distance = r.ReadSingle(),
                CanSee = r.ReadBoolean(),
                CanHear = r.ReadBoolean(),
                Junction = ReadNullableJunction(r),
                IsReachable = r.ReadBoolean(),
                IsBusy = r.ReadBoolean(),
                IsMoving = r.ReadBoolean()
            };
            agent.Relationship.Trust = r.ReadSingle();
            agent.Relationship.Affinity = r.ReadSingle();
            perception.Agents.Add(agent);
        }

        var knownCount = r.ReadInt32();
        for (var i = 0; i < knownCount; i++)
        {
            var memory = new ObjectMemory
            {
                Id = new ObjectId(r.ReadInt32()),
                DefinitionId = r.ReadString(),
                Tile = ReadTile(r),
                Junction = ReadNullableJunction(r),
                IsPermanent = r.ReadBoolean(),
                LastSeenTick = r.ReadInt32()
            };
            npc.Memory.KnownObjects[memory.Id] = memory;
        }

        var dangerCount = r.ReadInt32();
        for (var i = 0; i < dangerCount; i++)
        {
            npc.Memory.Dangers.Add(new DangerMemory
            {
                Tile = ReadTile(r),
                Tick = r.ReadInt32()
            });
        }

        npc.Social.Embarrassment = r.ReadSingle();
        var relationshipCount = r.ReadInt32();
        for (var i = 0; i < relationshipCount; i++)
        {
            npc.Social.Relationships[new EntityId(r.ReadInt32())] = new RelationshipData
            {
                Trust = r.ReadSingle(),
                Familiarity = r.ReadSingle(),
                Affinity = r.ReadSingle()
            };
        }

        npc.Inventory.Capacity = r.ReadInt32();
        ReadItemList(r, npc.Inventory.Items, version);

        return npc;
    }

    private static void WriteJunctionFlagSet(
        WorldState world, BinaryWriter w, System.Func<Junction, bool> flag)
    {
        var count = 0;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (flag(junction))
            {
                count++;
            }
        }

        w.Write(count);
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (flag(junction))
            {
                w.Write(junction.Id.Value);
            }
        }
    }

    private static void ReadJunctionFlagSet(
        WorldState world, BinaryReader r, System.Action<Junction> apply)
    {
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var id = new JunctionId(r.ReadInt32());
            if (!world.Junctions.Items.TryGetValue(id, out var junction))
            {
                throw new InvalidDataException($"Save references missing junction {id}.");
            }

            apply(junction);
        }
    }

    private static void WriteItemList(BinaryWriter w, List<ItemInstance> items)
    {
        w.Write(items.Count);
        foreach (var item in items)
        {
            w.Write(item.DefinitionId);
            w.Write(item.Wetness);
            w.Write(item.Durability);
            w.Write(item.ResourceAmount);
            w.Write(item.Dirtiness);
            w.Write(item.Bloodiness);
        }
    }

    private static void ReadItemList(BinaryReader r, List<ItemInstance> items, int version)
    {
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            items.Add(new ItemInstance(r.ReadString())
            {
                Wetness = r.ReadSingle(),
                Durability = r.ReadSingle(),
                ResourceAmount = r.ReadSingle(),
                Dirtiness = version >= 7 ? r.ReadSingle() : 0f,
                Bloodiness = version >= 7 ? r.ReadSingle() : 0f
            });
        }
    }

    private static void WriteJunctionList(BinaryWriter w, List<JunctionId> list)
    {
        w.Write(list.Count);
        foreach (var id in list)
        {
            w.Write(id.Value);
        }
    }

    private static void ReadJunctionList(BinaryReader r, List<JunctionId> list)
    {
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            list.Add(new JunctionId(r.ReadInt32()));
        }
    }

    private static void WriteEntityList(BinaryWriter w, List<EntityId> list)
    {
        w.Write(list.Count);
        foreach (var id in list)
        {
            w.Write(id.Value);
        }
    }

    private static List<EntityId> ReadEntityList(BinaryReader r)
    {
        var count = r.ReadInt32();
        var list = new List<EntityId>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new EntityId(r.ReadInt32()));
        }

        return list;
    }

    private static void WriteTile(BinaryWriter w, TileCoord tile)
    {
        w.Write(tile.Q);
        w.Write(tile.R);
    }

    private static TileCoord ReadTile(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32());

    private static void WriteFloat2(BinaryWriter w, Float2 value)
    {
        w.Write(value.X);
        w.Write(value.Y);
    }

    private static Float2 ReadFloat2(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());

    private static GoalType SaveGoal(GoalType goal) =>
        goal == GoalType.Defend ? GoalType.None : goal;

    private static void WriteNullableEntity(BinaryWriter w, EntityId? id)
    {
        w.Write(id.HasValue);
        if (id is { } value)
        {
            w.Write(value.Value);
        }
    }

    private static EntityId? ReadNullableEntity(BinaryReader r) =>
        r.ReadBoolean() ? new EntityId(r.ReadInt32()) : null;

    private static void WriteNullableObject(BinaryWriter w, ObjectId? id)
    {
        w.Write(id.HasValue);
        if (id is { } value)
        {
            w.Write(value.Value);
        }
    }

    private static ObjectId? ReadNullableObject(BinaryReader r) =>
        r.ReadBoolean() ? new ObjectId(r.ReadInt32()) : null;

    private static void WriteNullableJunction(BinaryWriter w, JunctionId? id)
    {
        w.Write(id.HasValue);
        if (id is { } value)
        {
            w.Write(value.Value);
        }
    }

    private static JunctionId? ReadNullableJunction(BinaryReader r) =>
        r.ReadBoolean() ? new JunctionId(r.ReadInt32()) : null;

    private static void WriteNullableString(BinaryWriter w, string value)
    {
        w.Write(value is not null);
        if (value is not null)
        {
            w.Write(value);
        }
    }

    private static string ReadNullableString(BinaryReader r) =>
        r.ReadBoolean() ? r.ReadString() : null;
}

}

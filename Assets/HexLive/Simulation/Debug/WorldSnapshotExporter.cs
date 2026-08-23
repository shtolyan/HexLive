using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Debug
{

public static class WorldSnapshotExporter
{
    // ⭐ Баг #123: КАЖДОЕ число, уезжающее в снапшот строкой, форматируется
    // ЗДЕСЬ и только инвариантно.
    //
    // Интерполяция `$"{value:0.###}"` берёт ТЕКУЩУЮ культуру, а весь приём
    // (CharacterPanel.ApplySheet, ParseKv, разбор эффектов) читает строго
    // InvariantCulture. На русской машине производитель писал «0,7», а
    // потребитель молча возвращал 0 — и лист персонажа показывал нули у всего,
    // кроме атрибута, чьё значение оказалось целым. Ни ошибки, ни исключения:
    // TryParse просто отвечает false.
    //
    // Это формат ОБМЕНА, а не текст для игрока: те же строки едут по проводу
    // на сервер (Simulation/Wire), где культура машины вообще ни при чём.
    private static string Num(float value, string format = "0.###") =>
        value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    // Trace events, per-NPC memory dumps, relationship/cooldown strings and
    // goal scores are read only by the debug panel, but cost megabytes of
    // garbage per tick when exported unconditionally. The panel opts in
    // while it is visible; everyone else gets the lean snapshot.
    public static bool IncludeDebugDetails { get; set; }

    // PERF (profiling, Aug-2026): the same bargain, one level down. Refreshing
    // the diagnostic junction flags costs ~56 000 dictionary/hash lookups over
    // the ~14 000 junctions, every tick. The only consumers are the junction
    // debug markers and the hex inspector, so they opt in while someone is
    // actually looking; a flip is picked up on the next tick, which for a debug
    // overlay is instant enough. Blocked is excluded from this switch: §71.10
    // production interpolation reads it and its direct field copy has no lookup.
    // IsClimbSeam is deliberately NOT part of this bargain: it is worldgen
    // output (WorldStateFactory writes ClimbSeams and nothing else ever does),
    // so the values the first full export copied stay right forever — which
    // matters, because the §40.17 seam dots are always shown, debug or not.
    public static bool IncludeJunctionFlags { get; set; }

    public static WorldSnapshot Export(WorldState world) => Export(world, null);

    // Passing the previous snapshot lets the exporter refresh the tile and
    // junction lists in place instead of reallocating them every tick. The
    // caller must be the sole owner of that snapshot (consumers may not hold
    // it across ticks — HexWorldRenderer & co. re-poll every frame).
    // Ascending-id comparers, cached so sorting does not allocate a delegate per
    // tick. See SortById for why the order matters at all.
    // §128.5: буфер ячеек содержимого вещи. Экспорт идёт по одному объекту за
    // раз в одном потоке, поэтому один общий список дешевле аллокации на объект.
    private static readonly List<(string ItemId, int Count, int SourceIndex)>
        _containerCellsScratch = new();

    private static readonly Comparison<ObjectSnapshot> ByObjectId =
        (a, b) => a.Id.Value.CompareTo(b.Id.Value);

    private static readonly Comparison<NpcSnapshot> ByNpcId =
        (a, b) => a.Id.Value.CompareTo(b.Id.Value);

    private static readonly Comparison<MobSnapshot> ByMobId = (a, b) => a.Id.CompareTo(b.Id);

    // §136: дневники — по тому же правилу возрастающего id, что и все секции.
    private static readonly Comparison<NpcJournalSnapshot> ByJournalNpcId =
        (a, b) => a.NpcId.CompareTo(b.NpcId);

    private static readonly Comparison<CrabSnapshot> ByCrabId = (a, b) => a.Id.CompareTo(b.Id);

    private static readonly Comparison<SharkSnapshot> BySharkId = (a, b) => a.Id.CompareTo(b.Id);

    /// <summary>
    /// Puts every entity list in ascending-id order.
    /// <para>
    /// The lists are built by walking <c>Dictionary&lt;ObjectId, …&gt;</c>, whose
    /// enumeration order shifts after any removal — so index <i>i</i> is NOT
    /// reliably the same entity from one tick to the next, and objects despawn
    /// constantly (picked up, eaten, rotted). Today that is invisible because
    /// every slot is overwritten every tick. It stops being invisible the moment
    /// anything compares this tick to the last one: a delta encoder would resend
    /// the world on a reshuffle, and a checksum would disagree between two ends
    /// holding identical state — an alarm that cries wolf is worse than none.
    /// </para>
    /// </summary>
    private static void SortById(WorldSnapshot snapshot)
    {
        snapshot.Objects.Sort(ByObjectId);
        snapshot.Npcs.Sort(ByNpcId);
        snapshot.Corpses.Sort(ByNpcId);
        snapshot.Mobs.Sort(ByMobId);
        snapshot.Crabs.Sort(ByCrabId);
        snapshot.Sharks.Sort(BySharkId);
    }

    public static WorldSnapshot Export(WorldState world, WorldSnapshot reuse)
    {
        var snapshot = reuse ?? new WorldSnapshot();
        snapshot.Tick = world.Tick;
        snapshot.Seed = world.Seed;
        snapshot.TickDeltaTime = world.TickDeltaTime;
        snapshot.Temperature = world.Environment.GlobalTemperature;
        snapshot.Clock = Runtime.EnvironmentSystem.FormatClock(world.Environment.TimeOfDayNormalized);
        snapshot.TimeOfDayNormalized = world.Environment.TimeOfDayNormalized;
        snapshot.DayPhase = world.Environment.Phase.ToString();
        snapshot.UvIndex = world.Environment.UvIndex;
        snapshot.IsRaining = world.Environment.IsRaining;
        snapshot.RaftProgress = world.RaftProgress;
        snapshot.Completed = world.Completed;
        snapshot.SunDirection = world.SunDirection;
        snapshot.SunElevationDegrees = world.SunElevationDegrees;
        snapshot.RaftTarget = HexLive.Simulation.Core.WorldState.RaftTarget;

        ExportTiles(world, snapshot);
        ExportJunctions(world, snapshot);

        // Entity lists come out in ASCENDING ID order, always — see SortById.

        snapshot.Objects.Clear();
        // PERF: reuse last tick's record for the same object id — the fresh
        // ObjectSnapshot per object per tick (plus its four lists) was ~1.3 MB
        // of garbage every tick on the big island's 1129 objects. A reused
        // record is re-filled COMPLETELY: every scalar assigned below, every
        // computed field reset in the reset block, every list cleared.
        var objectPool = snapshot.ObjectPool;
        foreach (var pair in world.Entities.Objects)
        {
            var obj = pair.Value;
            if (!objectPool.TryGetValue(obj.Id.Value, out var exported))
            {
                exported = new ObjectSnapshot();
            }

            exported.Id = obj.Id;
            exported.DefinitionId = obj.DefinitionId;
            exported.Tile = obj.Tile;
            exported.RotationDegrees = obj.RotationDegrees; // §66: built pieces carry a yaw
            exported.ResourceAmount = obj.ResourceAmount;
            exported.Wetness = obj.Wetness;
            exported.Durability = obj.Durability;
            exported.Dirtiness = obj.Dirtiness;
            exported.Bloodiness = obj.Bloodiness;
            exported.OwnerNpcId = obj.CurrentUser?.Value;
            exported.Variant = obj.Variant;
            exported.SpawnTick = obj.SpawnTick;
            // Spec §54: build-site payload for the progressive-assembly view.
            exported.BuildProduct = obj.BuildProduct;
            exported.BillLogs = obj.BillLogs;
            exported.BillStones = obj.BillStones;
            exported.BillLeaves = obj.BillLeaves;
            exported.BillSticks = obj.BillSticks;
            exported.BillRope = obj.BillRope;
            exported.BillBoards = obj.BillBoards;
            exported.CraftWorkRequired = obj.CraftWorkRequired;
            exported.CraftWorkDone = obj.CraftWorkDone;
            exported.CraftBatchCount = obj.CraftBatchCount;
            exported.CraftStationObjectId = obj.CraftStationObjectId?.Value;
            exported.CraftActive = obj.IsCraftProject && obj.IsOccupied;
            // Reset everything the code below only increments or appends —
            // a reused record still carries last tick's values here.
            exported.DeliveredLogs = 0;
            exported.DeliveredStones = 0;
            exported.DeliveredLeaves = 0;
            exported.DeliveredSticks = 0;
            exported.DeliveredRope = 0;
            exported.DeliveredBoards = 0;
            exported.RoastingRaw = 0;
            exported.RoastingCooked = 0;
            exported.CraftIngredients.Clear();
            exported.Contents.Clear();
            exported.ArchitectureElements.Clear();
            exported.Junctions.Clear();

            var isSite = !string.IsNullOrEmpty(obj.BuildProduct);
            foreach (var item in obj.Contents)
            {
                switch (item.DefinitionId)
                {
                    case "resource.log": if (isSite) exported.DeliveredLogs++; break;
                    case "resource.stone": if (isSite) exported.DeliveredStones++; break;
                    case "resource.palm_leaf": if (isSite) exported.DeliveredLeaves++; break;
                    case "resource.stick": if (isSite) exported.DeliveredSticks++; break;
                    case "resource.rope": if (isSite) exported.DeliveredRope++; break;
                    case ContentIds.Board: if (isSite) exported.DeliveredBoards++; break;
                    // §54.14 (r2): spit meat renders whether or not the
                    // upgrade bill is still open.
                    case "food.meat_raw": exported.RoastingRaw++; break;
                    case "food.meat_cooked": exported.RoastingCooked++; break;
                }

                if (obj.IsCraftProject)
                {
                    exported.CraftIngredients.Add(item.DefinitionId);
                }
            }

            // §128.5: содержимое вещи для панели обыска — ТОЛЬКО у настоящих
            // контейнеров (истлевшее тело, снятый рюкзак, аптечка). Стройка
            // тоже держит вещи в Contents, но это доставленные материалы, а не
            // мешок: показывать их как карманы значило бы предложить игроку
            // разобрать недостроенную кровать через окно обмена.
            if (!isSite && !obj.IsCraftProject &&
                Runtime.ContainerLootMath.IsLootable(world, obj))
            {
                _containerCellsScratch.Clear();
                Runtime.ContainerLootMath.BuildCells(world, obj, _containerCellsScratch);
                for (var cell = 0; cell < _containerCellsScratch.Count; cell++)
                {
                    var (itemId, count, sourceIndex) = _containerCellsScratch[cell];
                    exported.Contents.Add(new InventorySlotSnapshot
                    {
                        Index = cell,
                        SourceIndex = sourceIndex,
                        ItemDefinitionId = itemId,
                        StackCount = count
                    });
                }
            }

            foreach (var element in obj.ArchitectureElements)
            {
                exported.ArchitectureElements.Add(new ArchitectureElementSnapshot
                {
                    ElementId = element.ElementId,
                    DefinitionId = element.DefinitionId,
                    SlotKey = element.SlotKey,
                    SlotIndex = element.SlotIndex,
                    Layer = (int)element.Layer,
                    LocalX = element.LocalX,
                    LocalZ = element.LocalZ,
                    LocalYaw = element.LocalYaw,
                    RequiredSticks = element.RequiredSticks,
                    RequiredBoards = element.RequiredBoards,
                    RequiredRope = element.RequiredRope,
                    RequiredLeaves = element.RequiredLeaves,
                    DeliveredSticks = element.DeliveredSticks,
                    DeliveredBoards = element.DeliveredBoards,
                    DeliveredRope = element.DeliveredRope,
                    DeliveredLeaves = element.DeliveredLeaves,
                    Buildable = element.Buildable,
                    WorkRequired = element.WorkRequired,
                    WorkDone = element.WorkDone
                });
            }
            exported.ArchitectureOwnerObjectId = obj.ArchitectureOwnerId?.Value;
            exported.BuildingBlueprintJson = Runtime.BuildingRules.EditablePlanJsonFor(world, obj);
            exported.IsDoorOpen = obj.IsDoorOpen;

            foreach (var junctionId in obj.Junctions)
            {
                exported.Junctions.Add(junctionId);
            }

            snapshot.Objects.Add(exported);
        }

        // Rebuild the pool as exactly the live set, so records of despawned
        // objects are dropped rather than pinned forever.
        objectPool.Clear();
        for (var poolIndex = 0; poolIndex < snapshot.Objects.Count; poolIndex++)
        {
            objectPool[snapshot.Objects[poolIndex].Id.Value] = snapshot.Objects[poolIndex];
        }

        snapshot.Npcs.Clear();
        foreach (var pair in world.Entities.Npcs)
        {
            snapshot.Npcs.Add(ExportNpc(world, pair.Value));
        }

        // §28.15C v3: тела — той же самой записью. Вид рисует покойную ровно
        // так, как рисовал живую (её меш, её одежда, её раны), и меняет только
        // одно: аниматор уходит в Death и там замирает. Отдельная, урезанная
        // запись для трупа означала бы вторую сборку тела и, значит, второе
        // место, где одежда может «не доехать».
        snapshot.Corpses.Clear();
        foreach (var pair in world.Entities.Corpses)
        {
            snapshot.Corpses.Add(ExportNpc(world, pair.Value));
        }

        // §136: дневники — своей секцией, чтобы сорок восемь записей не ездили
        // каждый тик вместе с координатами (см. WorldSnapshot.Journals).
        // Покойницы тоже здесь: последняя запись умершей — это то, ради чего
        // дневник и читают, и терять её вместе с ней было бы жестоко.
        snapshot.Journals.Clear();
        foreach (var pair in world.Entities.Npcs)
        {
            AddJournal(snapshot, pair.Key.Value, pair.Value.Journal);
        }

        foreach (var pair in world.Entities.Corpses)
        {
            AddJournal(snapshot, pair.Key.Value, pair.Value.Journal);
        }

        snapshot.Journals.Sort(ByJournalNpcId);

        snapshot.DeathRecords.Clear();
        foreach (var death in world.DeathRecords)
        {
            snapshot.DeathRecords.Add(new DeathRecordSnapshot
            {
                EntityId = death.EntityId.Value,
                DisplayName = death.DisplayName,
                Tick = death.Tick,
                Tile = death.Tile,
                Cause = death.Cause
            });
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

        // §147.5: слоты сортируются по SlotId — порядок world.MobSpawnSlots
        // это порядок генерации, а не свойство мира (правило секций дельты).
        snapshot.MobSlots.Clear();
        foreach (var slot in world.MobSpawnSlots)
        {
            var record = new MobSlotSnapshot
            {
                SlotId = slot.SlotId,
                MobId = slot.MobId,
                State = (int)slot.State,
                ReservedMobId = slot.ReservedMobId,
                CycleIndex = slot.CycleIndex,
            };
            foreach (var waypoint in slot.Ring)
            {
                record.Ring.Add(new MobSlotWaypoint
                {
                    JunctionId = waypoint.Junction.Value,
                    Tile = waypoint.Tile,
                    Position = waypoint.Position,
                });
            }

            snapshot.MobSlots.Add(record);
        }

        snapshot.MobSlots.Sort((a, b) => a.SlotId.CompareTo(b.SlotId));

        snapshot.Mobs.Clear();
        foreach (var dog in world.Mobs)
        {
            snapshot.Mobs.Add(new MobSnapshot
            {
                Id = dog.Id,
                MobId = dog.MobId,
                Tile = dog.Tile,
                Position = dog.Position,
                Health = dog.Health,
                Status = dog.Status.ToString(),
                TargetNpcId = dog.TargetNpc?.Value ?? -1,
                IsAttacking = dog.AttackLandsAtTick > 0,
                AttackStartTick = dog.AttackStartTick,
                CarriedLimbOwnerNpcId = dog.CarriedLimbOwner?.Value ?? -1,
                CarriedLimbPart = dog.CarriedLimbPart ?? string.Empty
            });
        }

        snapshot.TraceEvents.Clear();
        if (IncludeDebugDetails)
        {
            foreach (var trace in world.Events.Items)
            {
                snapshot.TraceEvents.Add(new TraceEventSnapshot
                {
                    Seq = trace.Seq,
                    Tick = trace.Tick,
                    EntityId = trace.EntityId,
                    Type = trace.Type,
                    Message = trace.Message
                });
            }
        }

        SortById(snapshot);
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

                // Bitwise on purpose: Enum.HasFlag boxes both enums on Mono
                // (and in unoptimized builds) — 4032 tiles × 6 flags × 2 boxes
                // was ~0.9 MB of garbage per tick, most of the whole export.
                cached.Walkable = (tile.Flags & TileFlags.Walkable) != 0;
                cached.Blocked = (tile.Flags & TileFlags.Blocked) != 0;
                cached.Indoor = (tile.Flags & TileFlags.Indoor) != 0;
                cached.HasFloor = (tile.Flags & TileFlags.HasFloor) != 0;
                cached.Water = (tile.Flags & TileFlags.Water) != 0;
                cached.Explored = world.ExploredTiles.Contains(tile.Coord); // §148
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
                Walkable = (tile.Flags & TileFlags.Walkable) != 0,
                Blocked = (tile.Flags & TileFlags.Blocked) != 0,
                Indoor = (tile.Flags & TileFlags.Indoor) != 0,
                HasFloor = (tile.Flags & TileFlags.HasFloor) != 0,
                Water = (tile.Flags & TileFlags.Water) != 0,
                Explored = world.ExploredTiles.Contains(tile.Coord), // §148
                Elevation = tile.Elevation
            });
        }
    }

    private static void ExportJunctions(WorldState world, WorldSnapshot snapshot)
    {
        var junctions = snapshot.Junctions;
        // Capture BEFORE reading any Blocked state: a write landing between
        // this read and the sweep below then forces one extra sweep next tick
        // instead of being silently missed.
        var blockedVersion = Spatial.Junction.BlockedWriteVersion;
        var refreshFlags = IncludeDebugDetails || IncludeJunctionFlags;

        // PERF (Aug-2026): the sweep below is ~194k iterations on the big
        // island — ~74% of the whole export — and all it does in production
        // mode is re-copy Blocked flags that almost never change. While no
        // Blocked write happened anywhere (see Junction.BlockedWriteVersion)
        // and this snapshot already mirrors THIS world, skip it outright.
        // Debug flag mode keeps sweeping: occupancy flags change every tick.
        if (!refreshFlags &&
            junctions.Count == world.Junctions.Items.Count &&
            snapshot.JunctionsSeedSeen == world.Seed &&
            snapshot.JunctionsBlockedVersionSeen == blockedVersion)
        {
            return;
        }

        if (junctions.Count == world.Junctions.Items.Count)
        {
            // See IncludeJunctionFlags: the identity sweep below stays (it is
            // what detects a world swap, and it is only a struct compare each).
            // Blocked is production movement topology and is always refreshed;
            // the remaining four hash lookups are debug-only work.
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

                cached.Blocked = junction.Blocked;
                if (refreshFlags)
                {
                    RefreshJunctionDebugFlags(world, junction, cached);
                }
            }

            if (match)
            {
                snapshot.JunctionsSeedSeen = world.Seed;
                snapshot.JunctionsBlockedVersionSeen = blockedVersion;
                snapshot.JunctionsBlockedStamp = unchecked((int)blockedVersion);
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
            js.Blocked = junction.Blocked;
            RefreshJunctionDebugFlags(world, junction, js);
            // Topology is IMMUTABLE after worldgen — copied once here, never
            // in the per-tick refresh (re-filling ~14k junctions' Tiles +
            // Neighbors lists every tick was one of the top CPU items in the
            // 2026-07-19 deep capture: ~2M list ops + GC write barriers).
            foreach (var tileCoord in junction.Tiles)
            {
                js.Tiles.Add(tileCoord);
            }

            foreach (var neighborId in junction.Neighbors)
            {
                js.Neighbors.Add(neighborId);
            }

            junctions.Add(js);
        }

        snapshot.JunctionsSeedSeen = world.Seed;
        snapshot.JunctionsBlockedVersionSeen = blockedVersion;
        snapshot.JunctionsBlockedStamp = unchecked((int)blockedVersion);
    }

    // Per-tick debug refresh: Blocked is the production topology flag and is
    // refreshed independently even when IncludeJunctionFlags is false.
    private static void RefreshJunctionDebugFlags(WorldState world, Junction junction, JunctionSnapshot js)
    {
        js.Occupied = world.Occupancy.JunctionOwner.TryGetValue(junction.Id, out var owner) && owner is not null;
        js.Reserved = world.Reservations.Junctions.ContainsKey(junction.Id);
        js.IsClimbSeam = world.ClimbSeams.Contains(junction.Id);
        js.IsSwimmable = world.SwimJunctions.Contains(junction.Id);
    }

    // §Wardrobe-anim: which garment is visually in the NPC's hand right now.
    //  - Undress: the piece already doffed off the body (Execution.HeldGarment),
    //    carried through the gather beat until it drops.
    //  - Dress: once past the handoff the target garment has been "picked up"
    //    for the don beat; before that (the gather beat) the hands are empty.
    private static string ResolveHeldGarment(WorldState world, NPCState npc, float progress)
    {
        if (npc.Execution.HeldGarment is not null)
        {
            return npc.Execution.HeldGarment.DefinitionId;
        }

        if (npc.Execution.CurrentInteraction == InteractionType.Dress &&
            progress >= Runtime.ExecutionSystem.WardrobeHandoffFraction &&
            npc.Plan.TargetObjectId is { } targetId &&
            world.Entities.Objects.TryGetValue(targetId, out var garment))
        {
            return garment.DefinitionId;
        }

        // §40.6 r14 (#175): the wash beat scrubs an AUTHORITATIVE ground object
        // (Execution.TargetObject) — deliberately never a hand-held instance,
        // so a save/interrupt cannot strand the piece in limbo. Visually it IS
        // in her hands: the renderer already hides the ground copy during
        // WashClothes, so without this arm the garment vanished entirely and
        // she scrubbed empty-handed.
        if (npc.Execution.CurrentInteraction == InteractionType.WashClothes &&
            npc.Execution.TargetObject is { } washedId &&
            world.Entities.Objects.TryGetValue(washedId, out var washed))
        {
            return washed.DefinitionId;
        }

        return string.Empty;
    }

    // Single source of truth for the ordinary hand prop. Simulation already knows
    // the active verb, goal, target inventory item and carried items; presentation
    // should not have to guess these from strings.
    private static string ResolveHeldItem(WorldState world, NPCState npc)
    {
        // §108 / bug #21: общий draw/holster-контур. Боевой intent принадлежит
        // цели, а конкретное оружие — MeleeSwing; представление ничего не
        // угадывает. Поэтому GroupHunt/Defend/Raid показывают оружие уже на
        // подходе и автоматически прячут его после снятия цели.
        var readiedWeapon = Runtime.MeleeSwing.ReadiedWeapon(npc);
        if (!string.IsNullOrEmpty(readiedWeapon))
        {
            return readiedWeapon;
        }

        if (npc.Execution.CurrentInteraction is not { } interaction)
        {
            return string.Empty;
        }

        switch (interaction)
        {
            case InteractionType.Eat:
            {
                var groundCoconut = ResolveGroundCoconutInteractionItem(world, npc, InteractionType.Eat);
                return !string.IsNullOrEmpty(groundCoconut)
                    ? groundCoconut
                    : ResolveInventoryInteractionItem(world, npc, InteractionType.Eat);
            }

            case InteractionType.Drink:
            {
                var groundCoconut = ResolveGroundCoconutInteractionItem(world, npc, InteractionType.Drink);
                if (!string.IsNullOrEmpty(groundCoconut))
                {
                    return groundCoconut;
                }

                var drink = ResolveInventoryInteractionItem(world, npc, InteractionType.Drink);
                if (!string.IsNullOrEmpty(drink))
                {
                    return drink;
                }

                return InventoryContains(npc, "tool.bottle") ? "tool.bottle" : string.Empty;
            }

            case InteractionType.FillBottle:
                return InventoryContains(npc, "tool.bottle") ? "tool.bottle" : string.Empty;

            case InteractionType.Harvest:
                if (npc.Mind.CurrentGoal == GoalType.MineBoulder &&
                    InventoryContains(npc, "tool.pickaxe_stone"))
                {
                    return "tool.pickaxe_stone";
                }

                // §84: the yucca is BLADE work (Cut, §79) — the hand shows the
                // blade that sets her pace (machete 2.0 → axe 1.0 → knife
                // 0.75). Without this arm a knife-only girl hacked the stalk
                // EMPTY-HANDED: the generic list below only knows choppers.
                if (npc.Mind.CurrentGoal == GoalType.HarvestYucca)
                {
                    return FirstCarried(npc, "tool.machete", "tool.axe_stone", "tool.knife");
                }

                // §79: the machete leads every blade list — it is the tool that
                // SETS the pace (BestSpeedMultFor picks it), so it has to be the
                // one in her hand, or the view shows an axe doing a machete's
                // 2× work.
                return FirstCarried(npc, "tool.machete", "tool.axe_stone", "tool.saw", "tool.pickaxe_stone");

            case InteractionType.Process:
                var processingLog = npc.Execution.TargetObject is { } processObjectId &&
                    world.Entities.Objects.TryGetValue(processObjectId, out var processObject) &&
                    processObject.DefinitionId == ContentIds.Log;
                if (processingLog &&
                    HexLive.Simulation.Runtime.DecisionSystem
                        .WoodenProstheticBoardShortfall(world, npc) > 0 &&
                    InventoryContains(npc, GearCatalog.Saw))
                {
                    return GearCatalog.Saw;
                }

                if ((npc.Mind.CurrentGoal == GoalType.Drink || npc.Mind.CurrentGoal == GoalType.Eat) &&
                    InventoryContains(npc, "tool.machete"))
                {
                    return "tool.machete";
                }

                if ((npc.Mind.CurrentGoal == GoalType.Drink || npc.Mind.CurrentGoal == GoalType.Eat) &&
                    InventoryContains(npc, "tool.knife"))
                {
                    return "tool.knife";
                }

                if ((npc.Mind.CurrentGoal == GoalType.Drink || npc.Mind.CurrentGoal == GoalType.Eat) &&
                    InventoryContains(npc, "tool.axe_stone"))
                {
                    return "tool.axe_stone";
                }

                // §54 SplitLog: a log splits under ChopWood OR Cut (simdata
                // split.log = [ChopWood, Cut]), so a knife is a valid splitter,
                // not only the axe — a knife-only girl was chopping bare-handed.
                // Axe stays preferred (plays the Chop clip); knife is the
                // fallback. ChopCrown still needs ChopWood, so it always shows
                // the axe first and never falls through to the knife here.
                return FirstCarried(npc, "tool.machete", "tool.axe_stone", "tool.saw", "tool.knife");

            case InteractionType.Butcher:
                return FirstCarried(npc, "tool.machete", "tool.knife");

            case InteractionType.Fuel:
                return InventoryContains(npc, "resource.stick") ? "resource.stick" : string.Empty;

            case InteractionType.Craft:
                if (npc.Mind.CurrentGoal == GoalType.CookMeat &&
                    InventoryContains(npc, "food.meat_raw"))
                {
                    return "food.meat_raw";
                }

                return InventoryContains(npc, "resource.stick") ? "resource.stick" : string.Empty;

            case InteractionType.Build:
            {
                // §54.12: at a furniture build-site the hand shows the material
                // actually being DEPOSITED — the current stage's shortfall she
                // carries. (The old hut-era "Build = carry a log" spawned a log
                // in her hand while she laid bed sticks.) A stocked site shows
                // the hammer for the raise, nothing for a hand-lashed one.
                var buildTarget = npc.Execution.TargetObject ?? npc.Plan.TargetObjectId;
                if (buildTarget is { } siteId &&
                    world.Entities.Objects.TryGetValue(siteId, out var site) &&
                    Runtime.BuildSiteMath.IsSite(site))
                {
                    foreach (var material in Runtime.BuildSiteMath.AllMaterials)
                    {
                        if (Runtime.BuildSiteMath.Needs(site, material) &&
                            InventoryContains(npc, material))
                        {
                            return material;
                        }
                    }

                    return Runtime.BuildSiteMath.IsStocked(site) && InventoryContains(npc, "tool.hammer")
                        ? "tool.hammer"
                        : string.Empty;
                }

                // The hut anchor (retired) and any non-site Build: the old log carry.
                return InventoryContains(npc, "resource.log") ? "resource.log" : string.Empty;
            }

            case InteractionType.BuildRaft:
                return InventoryContains(npc, "resource.log") ? "resource.log" : string.Empty;

            case InteractionType.FeedOther:
                return npc.Inventory.FindFirstFood(world.Content) ?? string.Empty;

            case InteractionType.HydrateOther:
                return FirstCarried(npc, "tool.bottle", "food.coconut_pierced");

            default:
                return string.Empty;
        }
    }

    private static string ResolveGroundCoconutInteractionItem(
        WorldState world,
        NPCState npc,
        InteractionType interaction)
    {
        var target = npc.Execution.TargetObject ?? npc.Plan.TargetObjectId;
        if (target is not { } targetId ||
            !world.Entities.Objects.TryGetValue(targetId, out var worldObject) ||
            !IsCoconutDefinition(worldObject.DefinitionId) ||
            !DefinitionHasInteraction(world, worldObject.DefinitionId, interaction))
        {
            return string.Empty;
        }

        return worldObject.DefinitionId;
    }

    private static bool IsCoconutDefinition(string definitionId) =>
        definitionId.StartsWith("food.coconut", StringComparison.Ordinal);

    private static string ResolveInventoryInteractionItem(
        WorldState world,
        NPCState npc,
        InteractionType interaction)
    {
        if (npc.Plan.TargetItemDefinitionId is { } target &&
            InventoryContains(npc, target) &&
            DefinitionHasInteraction(world, target, interaction))
        {
            return target;
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (DefinitionHasInteraction(world, item.DefinitionId, interaction))
            {
                return item.DefinitionId;
            }
        }

        return string.Empty;
    }

    private static bool DefinitionHasInteraction(
        WorldState world,
        string definitionId,
        InteractionType interaction)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition))
        {
            return false;
        }

        foreach (var candidate in definition.Interactions)
        {
            if (candidate.Type == interaction)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InventoryContains(NPCState npc, string definitionId)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == definitionId)
            {
                return true;
            }
        }

        return false;
    }

    // Preference list, best tool first (§79 added a fourth candidate to two of
    // the lists, so this takes as many as the caller names).
    private static string FirstCarried(NPCState npc, params string[] ids)
    {
        foreach (var id in ids)
        {
            if (id.Length > 0 && InventoryContains(npc, id))
            {
                return id;
            }
        }

        return string.Empty;
    }

    private static NpcSnapshot ExportNpc(WorldState world, NPCState npc)
    {
        // §Wardrobe-anim: progress + the garment in hand + the target object,
        // so the view can phase dress/undress and hide the picked-up ground copy.
        var execTotal = npc.Execution.EndTick - npc.Execution.StartTick;
        var hasTimedInteraction = npc.Execution.Status == ExecutionStatus.InProgress && execTotal > 0;
        var interactionProgress = hasTimedInteraction
            ? Math.Clamp((float)(world.Tick - npc.Execution.StartTick) / execTotal, 0f, 1f)
            : 0f;
        var heldGarmentId = ResolveHeldGarment(world, npc, interactionProgress);

        // Spec §53 r2: the kneeling "tending" pose only plays when the ward is
        // lying down — over a standing ward the helper just shows the item in
        // hand. Look up the aid target's posture here (the view has no cross-NPC
        // access) so SetInteraction can pick the pose.
        var aidTargetLying = npc.Execution.CurrentInteraction is
                InteractionType.FeedOther or InteractionType.HydrateOther or
                InteractionType.TreatOther or InteractionType.MedicateOther or
                InteractionType.ConsoleOther &&
            npc.Plan.TargetAgentId is { } aidWardId &&
            world.Entities.Npcs.TryGetValue(aidWardId, out var aidWard) &&
            aidWard.IsLyingDown(world.Tick);

        var npcSnapshot = new NpcSnapshot
        {
            // Zeroed unless a timed interaction is REALLY running. Note this asks
            // npc.Execution.Status, not the exported ExecutionStatus below, which
            // is overridden to InProgress for an exhaustion coma (§60 r2) — the
            // client derives progress from these two ints, so they must answer
            // the same question the server does or the two ends disagree by a
            // hair forever. (The old per-tick InteractionProgress float is gone:
            // it changed every tick and would have marked an otherwise-motionless
            // crafter dirty on every delta frame; ProgressAt(tick) derives it.)
            ExecutionStartTick = hasTimedInteraction ? npc.Execution.StartTick : 0,
            ExecutionEndTick = hasTimedInteraction ? npc.Execution.EndTick : 0,
            // §77.5: the window the view fits one playthrough of the work clip
            // into. Constant for the interaction's whole life (delta-friendly),
            // same zero-guard as the ticks above.
            InteractionSeconds = hasTimedInteraction ? execTotal * world.TickDeltaTime : 0f,
            AidTargetLyingDown = aidTargetLying,
            // §111.13: станция едет ЧИСЛОМ, а не выводится видом из геометрии —
            // рендер интерполирует кадры, и производная станция мигала бы на
            // границах (тот же урок, что §111.9 r2/r3: позицией владеет симуляция).
            LyingStationSlot = npc.Execution.LyingStationSlot,
            HeldGarmentId = heldGarmentId,
            // §40.6 r2: live condition of the held piece — the hand prop shows
            // the dirt actually washing out during the scrub.
            HeldGarmentDirt = npc.Execution.HeldGarment?.Dirtiness ?? 0f,
            HeldGarmentBlood = npc.Execution.HeldGarment?.Bloodiness ?? 0f,
            HeldGarmentWet = npc.Execution.HeldGarment?.Wetness ?? 0f,
            HeldGarmentDurability = npc.Execution.HeldGarment?.Durability ?? 1f,
            TargetObjectId = (npc.Execution.TargetObject ?? npc.Plan.TargetObjectId)?.Value,
            Id = npc.Id,
            DisplayName = npc.DisplayName,
            ActorMesh = npc.ActorMesh,
            SkinSet = npc.SkinSet,
            EyeColor = npc.EyeColor,
            Hairstyle = npc.Hairstyle,
            VoiceBank = npc.VoiceBank,
            Faction = npc.Faction,
            IsHostileToColony = Runtime.FactionRelations.AreHostile(
                world, npc.Faction, Faction.Colony),
            Tile = npc.Tile,
            Position = npc.Position,
            RotationDegrees = npc.RotationDegrees,
            Health = npc.Health,
            CompassionTrait = npc.CompassionTrait,
            MeleeStats = new MeleeStatsSnapshot
            {
                LimbMultiplier = npc.Body.LimbStrikeFactor(),
                StrengthMultiplier = Runtime.AttributeMath.MeleeStrengthMult(npc),
                CombatMultiplier = Runtime.AttributeMath.MeleeCombatMult(npc),
                AgilityRecoveryMultiplier = Runtime.AttributeMath.AttackCooldownMult(npc)
            },
            IsFighting = npc.IsFighting,
            CombatOpponentNpcId = npc.Mind.CombatOpponentNpcId?.Value ?? -1,
            IsSwinging = world.Tick < npc.AttackAnimUntilTick,
            SwingStartTick = npc.SwingStartTick,
            // Спрашиваем ТО ЖЕ правило, по которому бьёт симуляция.
            MeleeWeaponId = Runtime.MeleeSwing.EffectiveWeapon(npc),
            StrikeIndex = npc.SwingStrikeIndex,
            HitStampTick = npc.HitStampTick,
            HitWeaponId = npc.HitWeaponId ?? string.Empty,
            HitPart = npc.HitPart.ToString(),
            HitFrom = npc.HitFrom,
            Hunger = npc.Needs.Hunger,
            Thirst = npc.Needs.Thirst,
            Energy = npc.Needs.Energy,
            Comfort = npc.Needs.Comfort,
            Social = npc.Needs.Social,
            Compassion = npc.Needs.Compassion,
            ThermalDiscomfort = npc.Needs.ThermalDiscomfort,
            ThermalComfort = npc.Needs.ThermalComfort,
            Stamina = npc.Needs.Stamina,
            Winded = npc.Needs.Stamina < 0.15f,
            IsRunning = npc.Mind.IsRunning,       // §71
            Breath = npc.Needs.Breath,            // §71
            Hygiene = npc.Needs.Hygiene,
            Blood = npc.Needs.Blood,
            BloodDeficit = npc.Body.BloodDeficit,
            CarriedNpcId = npc.CarriedNpcId?.Value,
            CarriedByNpcId = npc.CarriedByNpcId?.Value,
            RescueDestinationObjectId = npc.RescueDestinationObjectId?.Value,
            TanLevel = npc.Needs.TanLevel,
            Sunburn = npc.Needs.Sunburn,
            Bandages = MedicalSupplyMath.BandageCount(npc),
            Pills = MedicalSupplyMath.PillCount(npc),
            // Spec §60 r3 (баг #8): ЛЮБАЯ кома — «без сознания». Раньше
            // энергетический крах читался как обычный СОН (r2), и вырубившаяся
            // мирно дышала в анимированной позе сна — неотличимо от здоровой.
            // Теперь вид ведёт обе комы одним путём с умиранием: падение и
            // замороженная поза (FallenIdle, скорость 0) — «как при смерти».
            IsFainted = world.Tick < npc.Mind.FaintedUntilTick,
            IsUnconscious = npc.Mind.ComaCause != AI.ComaCause.None,
            IsDying = npc.IsDying, // §105
            // §110: лежит и плачет — В СОЗНАНИИ, поэтому отдельный флаг, а не
            // ветка IsFainted: вид кладёт её как спящую (не роняет) и не
            // затыкает ей рот, чтобы всхлипы и слёзный смайл шли своим чередом.
            IsCrying = npc.IsCrying(world.Tick),
            IsSadWalk = world.Tick < npc.Mind.SadWalkUntilTick, // §81.10
            IsPlayingDead = npc.IsPlayingDead(world.Tick), // §105.14
            IsWaking = world.Tick < npc.Mind.WakeGraceUntilTick,
            Stress = npc.Needs.Stress,
            CurrentGoal = npc.Mind.CurrentGoal.ToString(),
            IsManualControl = npc.Mind.ManualControl, // §121
            OutfitLocked = npc.Mind.OutfitLocked, // §133.9
            CurrentDream = npc.Mind.CurrentDream.ToString(),
            PlanStatus = npc.Plan.Status.ToString(),
            MovementStatus = npc.Movement.Status.ToString(),
            // §60 r3 (баг #8): подмена «кома от истощения = сон» убрана —
            // теперь обе комы едут флагом IsUnconscious выше, и вид кладёт
            // тело замороженным, а не дышащим в позе сна.
            ExecutionStatus = npc.Execution.Status.ToString(),
            CurrentInteraction = npc.Execution.CurrentInteraction?.ToString() ?? "-",
            RomancePartnerNpcId = npc.Mind.RomancePartnerNpcId?.Value,
            RomanceClipKey = npc.Mind.RomanceClipKey,
            RomanceForced = npc.Mind.RomanceForced,
            RomanceAnchorX = npc.Mind.RomanceAnchorX,
            RomanceAnchorY = npc.Mind.RomanceAnchorY,
            RomanceFacingDegrees = npc.Mind.RomanceFacingDegrees,
            HeldItemId = ResolveHeldItem(world, npc),
            // Spec 28.15E: conversation subject + last outcome for the bubble.
            TalkTopic = npc.Execution.CurrentTalkTopic?.ToString() ?? string.Empty,
            TalkTopicPeerId = npc.Execution.CurrentTalkTopicPeerId?.Value,
            TalkResultTick = npc.Execution.LastTalkResultTick,
            TalkResultDelta = npc.Execution.LastTalkAffinityDelta,
            SocialCueTick = npc.Execution.LastSocialCueTick,
            SocialCueKind = npc.Execution.LastSocialCueKind,
            SocialCuePeerId = npc.Execution.LastSocialCuePeerId?.Value,
            SocialCueItemId = npc.Execution.LastSocialCueItemId,
            TargetTile = npc.Plan.TargetTile,
            IsStarving = npc.Mind.IsStarving,
            InventoryCapacity = npc.Inventory.Capacity,
            DeathAnimVariant = npc.DeathAnimVariant, // §28.15C v3
            InventoryUsedSlots = npc.Inventory.UsedSlots,
            GoalLockEndTick = npc.Mind.GoalLock is { } goalLock &&
                goalLock.Goal == npc.Mind.CurrentGoal && goalLock.EndTick > world.Tick
                    ? goalLock.EndTick
                    : null
        };

        var inventoryLayout = HexLive.Simulation.Runtime.InventoryLayoutBuilder.Build(world, npc);
        npcSnapshot.FavoriteWeaponId = inventoryLayout.FavoriteWeaponId;
        foreach (var sourceContainer in inventoryLayout.Containers)
        {
            var targetContainer = new InventoryContainerSnapshot
            {
                Id = sourceContainer.Id,
                Kind = sourceContainer.Kind,
                OwnerItemDefinitionId = sourceContainer.OwnerItemDefinitionId,
                OwnerSourceIndex = sourceContainer.OwnerSourceIndex,
                BodyAnchor = sourceContainer.BodyAnchor,
                Capacity = sourceContainer.Capacity,
                BaseCapacity = sourceContainer.BaseCapacity,
                StrengthBonus = sourceContainer.StrengthBonus,
                BackpackCapacity = sourceContainer.BackpackCapacity
            };
            foreach (var sourceSlot in sourceContainer.Slots)
            {
                targetContainer.Slots.Add(new InventorySlotSnapshot
                {
                    Index = sourceSlot.Index,
                    SourceIndex = sourceSlot.SourceIndex,
                    ItemDefinitionId = sourceSlot.ItemDefinitionId,
                    StackCount = sourceSlot.StackCount,
                    AcceptedItemDefinitionId = sourceSlot.AcceptedItemDefinitionId
                });
            }

            npcSnapshot.InventoryContainers.Add(targetContainer);
        }

        // Spec 35.4: per-NPC effective UV — same formula as TemperatureSystem
        // (indoor/water block it entirely, shade cuts the index to 20%).
        // §35.4 r2 (#167): ровно та же функция, что копит загар — панель обязана
        // показывать то, что происходит, а не вторую копию формулы.
        npcSnapshot.IsShaded = Runtime.TemperatureSystem.IsShaded(world, npc.Tile);
        npcSnapshot.EffectiveUv = Runtime.TemperatureSystem.EffectiveUv(world, npc.Tile);

        var stackCounts = new Dictionary<string, int>();
        var stackOrder = new List<string>();
        foreach (var item in npc.Inventory.Items)
        {
            npcSnapshot.InventoryOwnerIds.Add(item.OwnerId);
            if (!InventoryState.IsStackable(item.DefinitionId))
            {
                continue;
            }

            if (!stackCounts.ContainsKey(item.DefinitionId))
            {
                stackOrder.Add(item.DefinitionId);
                stackCounts[item.DefinitionId] = 0;
            }

            stackCounts[item.DefinitionId]++;
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (InventoryState.IsStackable(item.DefinitionId))
            {
                continue;
            }

            npcSnapshot.InventoryItems.Add(item);
            npcSnapshot.InventoryDurability.Add($"{item.DefinitionId}\t{Num(item.Durability)}");
            npcSnapshot.InventoryWetness.Add($"{item.DefinitionId}\t{Num(item.Wetness)}");
            npcSnapshot.InventoryDirtiness.Add($"{item.DefinitionId}\t{Num(item.Dirtiness)}");
            npcSnapshot.InventoryBloodiness.Add($"{item.DefinitionId}\t{Num(item.Bloodiness)}");
            if (item.DefinitionId == "tool.bottle")
            {
                npcSnapshot.InventoryWater.Add(
                    $"{item.DefinitionId}\t{npc.BottleCharges.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{Runtime.SimBalance.BottleCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            else if (item.DefinitionId == "food.coconut_pierced")
            {
                npcSnapshot.InventoryWater.Add(
                    $"{item.DefinitionId}\t{item.ResourceAmount.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\t{Runtime.SimBalance.CoconutWaterCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
        }

        foreach (var definitionId in stackOrder)
        {
            npcSnapshot.InventoryItems.Add(definitionId);
            npcSnapshot.InventoryStacks.Add(
                $"{definitionId}\t{stackCounts[definitionId].ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        foreach (var item in npc.WornItems)
        {
            // Spec 40.11: per-garment durability for the character panel's
            // wear progress bars ("id\tdurability").
            npcSnapshot.WornDurability.Add($"{item.DefinitionId}\t{Num(item.Durability)}");
            // Spec 35.5: per-garment wetness — rain soaks, fire/rack dries;
            // presentation renders a wet sheen that fades as the cloth dries.
            npcSnapshot.WornWetness.Add($"{item.DefinitionId}\t{Num(item.Wetness)}");
            npcSnapshot.WornDirtiness.Add($"{item.DefinitionId}\t{Num(item.Dirtiness)}");
            npcSnapshot.WornBloodiness.Add($"{item.DefinitionId}\t{Num(item.Bloodiness)}");
            npcSnapshot.WornItems.Add(item);
            npcSnapshot.WornOwnerIds.Add(item.OwnerId);
        }

        // Spec §52.8: which typed holster slots are actually filled right now —
        // the presentation pins each tool to its leg anchor.
        foreach (var slotId in npc.Inventory.HolsterSlotIds)
        {
            if (npc.Inventory.IsSlotFilled(slotId))
            {
                npcSnapshot.HolsteredItems.Add(slotId);
            }
        }

        // Spec 40.8B: open wounds — one decal each, spot/look from seed,
        // alpha fading with heal. Their unhealed damage sums into the red
        // "won't regen" segment of the HP bar (Health is the mean of parts,
        // so the lock is normalized by the part count).
        var lockedHp = 0f;
        foreach (var wound in npc.Wounds)
        {
            // Legacy presentation strings carry a VISUAL age. Clotting dries
            // the mark and ends pain/wet-gloss cues without lying to typed
            // medical consumers: OpenWounds below keeps authoritative Heal01.
            var visualHeal = HexLive.Simulation.Runtime.WoundMath.VisualHeal01(wound);
            // §118.2: поля 4 и 5 — свёртываемость и глубина, для крови,
            // проступающей СКВОЗЬ повязку. Дописаны в хвост сознательно: все
            // четыре читателя строки проверяют Length >= 3 и берут [0..2],
            // поэтому старый разбор не ломается, а типизированный OpenWounds
            // остаётся источником правды для медицины.
            npcSnapshot.Wounds.Add(
                $"{wound.Zone}|{wound.Seed}|{Num(visualHeal)}|" +
                $"{Num(wound.Clot01)}|{Num(wound.Severity)}|" +
                $"{(wound.Plastered ? 1 : 0)}");
            npcSnapshot.OpenWounds.Add(new WoundSnapshot
            {
                Id = wound.Id,
                Part = wound.Zone,
                Severity = wound.Severity,
                Heal01 = wound.Heal01,
                Clot01 = wound.Clot01,
                Stabilized = wound.Stabilized,
                Plastered = wound.Plastered,
                BleedFactor = wound.BleedFactor,
                Seed = wound.Seed
            });
            lockedHp += wound.Severity * (1f - wound.Heal01);
        }

        npcSnapshot.WoundLockedHp = lockedHp / npc.Body.Parts.Count;
        npcSnapshot.VitalHealth = npc.Body.VitalHealth(); // §105 r2
        npcSnapshot.DisplayHealth = npc.Body.DisplayHealth(); // §105 r3

        // Spec §48: derive the active status effects (buffs/debuffs) from this
        // NPC's live state — read-only, so nothing here touches balance. Each
        // exports as "Kind\tintensity\tdetailKey" for the character panel's
        // chip row; the optional third field explains the concrete cause.
        var effects = new List<ActiveEffect>();
        // Spec §49.8: a lit campfire within warming range earns the Cozy buff —
        // same warmth probe the temperature/sleep-comfort systems use.
        var nearLitFire = Runtime.TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0f;
        // §54.11: is she asleep on a bed right now? Drives the "Snug" buff (the
        // bed she built speeding her recovery). Same object the sleep bonus keys on.
        var restingInBed = npc.Execution.CurrentInteraction == Content.InteractionType.Sleep &&
            npc.Execution.TargetObject is { } bedId &&
            world.Entities.Objects.TryGetValue(bedId, out var bedObj) &&
            bedObj.DefinitionId == ContentIds.BedBasic;
        EffectEvaluator.Collect(npc, world.Tick, npcSnapshot.EffectiveUv, nearLitFire, restingInBed, effects);
        foreach (var effect in effects)
        {
            var encoded = $"{effect.Kind}\t{Num(effect.Intensity)}";
            if (!string.IsNullOrEmpty(effect.DetailKey))
            {
                encoded += $"\t{effect.DetailKey}";
            }
            npcSnapshot.Effects.Add(encoded);
        }

        // §48.7: systems wrote these rows beside the real parameter mutation.
        // The bridge does not infer causes from final values and sends no
        // changing magnitude, keeping this Character group delta-friendly.
        foreach (var impact in npc.EffectImpacts.Items)
        {
            var encoded = $"{impact.Need}\t{impact.Kind}\t{impact.Direction}";
            // Fast and slow owners may both describe the same cause. Cadence
            // stays separate inside the ledger so either owner can clear its
            // own row without erasing the other, but cadence is deliberately
            // absent from the wire contract: export one visible triple only.
            if (!npcSnapshot.EffectImpacts.Contains(encoded))
            {
                npcSnapshot.EffectImpacts.Add(encoded);
            }
        }

        // §76: the character sheet. Looped off AttributeSet.All/SkillSet.All so
        // adding a seventh attribute never means remembering this file.
        foreach (var kind in Agents.AttributeSet.All)
        {
            npcSnapshot.Attributes.Add($"{kind}\t{Num(npc.Attributes.Get(kind))}");
        }
        npcSnapshot.Attributes.Add($"CompassionTrait\t{Num(npc.CompassionTrait)}");

        foreach (var kind in Agents.SkillSet.All)
        {
            npcSnapshot.Skills.Add($"{kind}\t{Num(npc.Skills.Get(kind))}");
        }

        Runtime.AttributeMath.CollectPerks(npc, npcSnapshot.Perks);

        // §126: черты характера для вкладки «Характер».
        Runtime.TraitMath.CollectTraits(npc, npcSnapshot.Traits);

        // §125: готовый радиус восприятия для тумана войны и кольца в дебаге.
        npcSnapshot.PerceptionRadiusTiles = Runtime.PerceptionMath.RadiusTiles(npc);

        var worstPartValue = 1f;
        var worstPartName = "-";
        foreach (var part in npc.Body.Parts)
        {
            npcSnapshot.BodyParts.Add($"{part.Key}={Num(part.Value, "F2")}");
            var armor = Runtime.EquipmentMath.ArmorForPart(world, npc, part.Key);
            npcSnapshot.PartArmor.Add($"{part.Key}={Num(armor, "F2")}");
            var condition = npc.Body.Condition(part.Key);
            var conditionSnapshot = new BodyPartConditionSnapshot
            {
                Part = part.Key,
                Health = part.Value,
                Armor = armor,
                CriticalTrauma = condition.CriticalTrauma,
                BluntDamage = condition.BluntDamage,
                SplintSupport = condition.SplintSupport,
                HitBias = condition.HitBias,
                // #185: the visible hygiene invariant belongs at the snapshot
                // boundary, so it also repairs old saves and future cleaning
                // paths that reach fully clean without knowing about BloodSoil.
                // Open wounds are exported separately and remain visible.
                BloodSoil = npc.Needs.Hygiene >= 1f
                    ? 0f
                    : condition.BloodSoil,
                IntimacySoil = condition.IntimacySoil,
                Severed = npc.Body.IsSevered(part.Key),
                BandageKind = npc.BandagedZones.Contains(part.Key) ? "herbal" :
                    npc.GauzeZones.Contains(part.Key) ? "gauze" : string.Empty
            };
            if (condition.Prosthetic is { } prosthetic)
            {
                conditionSnapshot.Prosthetic = new ProstheticSnapshot
                {
                    DefinitionId = prosthetic.DefinitionId,
                    Part = prosthetic.Part,
                    Condition = prosthetic.Condition,
                    MaxCondition = prosthetic.MaxCondition,
                    Function = prosthetic.Function,
                    Mechanical = prosthetic.Mechanical
                };
            }
            npcSnapshot.BodyPartConditions.Add(conditionSnapshot);
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

        // Spec 44: dressing decals on the dressed zones — herbal leaf wraps as
        // the bare zone name, medkit gauze wraps tagged with a "|g" suffix
        // (presentation splits them into leaf-wrap vs gauze decals).
        foreach (var zone in npc.BandagedZones)
        {
            npcSnapshot.BandagedZones.Add(zone.ToString());
        }

        foreach (var zone in npc.GauzeZones)
        {
            npcSnapshot.BandagedZones.Add($"{zone}|g");
        }

        // Spec §50: severed zones — the view hides the bone chain + stumps them.
        foreach (var zone in npc.Body.Severed)
        {
            npcSnapshot.SeveredParts.Add(zone.ToString());
        }

        npcSnapshot.WorstBodyPart = worstPartValue < 1f
            ? $"{worstPartName} {Num(worstPartValue, "F2")}"
            : "OK";

        // Spec 40.9 / §50: one authoritative injury-locomotion hint for the
        // presentation pose layer. Priority: faint > crawl > limp > arm hang >
        // head clutch > upright. A LOST leg (one or both, §50) forces Crawl —
        // the real crawl clip + 1/3 speed; two merely-mauled legs also crawl.
        float Part(BodyPart p) => npc.Body.Parts.TryGetValue(p, out var v) ? v : 1f;
        var legL = npc.Body.LimbFunction(BodyPart.LegL);
        var legR = npc.Body.LimbFunction(BodyPart.LegR);
        var legLImpaired = legL < 0.4f ||
            npc.Body.Condition(BodyPart.LegL).Prosthetic?.Function < 0.999f;
        var legRImpaired = legR < 0.4f ||
            npc.Body.Condition(BodyPart.LegR).Prosthetic?.Function < 0.999f;
        npcSnapshot.PostureHint =
            npcSnapshot.IsUnconscious ? "Faint" // §60: comatose lies limp too
            : npcSnapshot.IsFainted ? "Faint"
            // Порог ползания живёт в BodyState (IsCrawling): на нём же теперь
            // гейтится переноска, и разъехаться вид с симуляцией не может.
            : npc.Body.IsCrawling ? "Crawl"
            : legLImpaired || legRImpaired ? "Limp"
            : Part(BodyPart.ArmL) < 0.4f || Part(BodyPart.ArmR) < 0.4f ? "ArmHang"
            : Part(BodyPart.Head) < 0.4f ? "HeadClutch"
            : "Upright";

        // §50: «ноги в ноль» — ОТДЕЛЬНО от подсказки позы, потому что подсказка
        // ранжирована и обморок в ней перебивает ползание. Вид держит на этом
        // не походку, а вопрос «есть ли на чём вставать»: у кого ноги в нуле,
        // тот на землю не падает клипом падения, он там уже.
        npcSnapshot.LegsLost = npc.Body.IsProne;

        // §21.21B hex-step hop: signal the jump traversal to the view.
        // §57.11: спуск без прыжка — не прыжок, а сползание-падение; вид
        // играет его без отталкивания, клипом падения и с подъёмом после.
        //
        // ⭐ Кто именно «без прыжка» — это НЕ CanJump. Тот требует обе ноги на
        // 0.75 и выше, потому что он про ПУТЬ: подниматься на уступ с побитой
        // ногой нельзя, и планировщик обязан обходить. Спуск же доступен любой
        // стоящей — вниз можно шагнуть, а не только оттолкнуться. На прежнем
        // условии девушка с лёгкой хромотой (нога 0.7) валилась и вставала на
        // КАЖДОЙ ступеньке вниз, хотя стоит она нормально.
        //
        // Падение — это когда стоять уже нечем: ноги в нуле (IsProne, §50), то
        // есть она и так ползёт. Тогда «спрыгнуть» действительно сползание.
        npcSnapshot.HopKind = npc.Movement.HopTimer > 0f
            ? (npc.Movement.HopUp ? "Up"
                : npc.Body.IsProne ? "Fall"
                : "Down")
            : string.Empty;
        npcSnapshot.HopStartTick = npc.Movement.HopStartTick;
        npcSnapshot.HopTargetTile = npc.Movement.HopTargetTile;
        npcSnapshot.HopFromTile = npc.Movement.HopFromTile;

        // Iter 28: sitting at a junction whose tiles step exactly one
        // level = a ledge seat; the view plants the butt on the upper step.
        var usesEdgePose = npcSnapshot.CurrentInteraction == "WashClothes" ||
            npcSnapshot.CurrentInteraction == "Sit" && npc.Execution.TargetObject is null;
        if (usesEdgePose &&
            npc.CurrentJunction is { } sitJunctionId &&
            world.Junctions.Items.TryGetValue(sitJunctionId, out var sitJunction) &&
            sitJunction.Tiles.Count > 1)
        {
            var waterOnly = npcSnapshot.CurrentInteraction == "WashClothes";
            npcSnapshot.IsLedgeSit = Runtime.PlanningSystem.TryGetEdgeSeatGeometry(
                world, sitJunction, waterOnly, out var seatStandTile, out _);

            // How far below the seat (higher tile) her own tile sits: 0 if
            // she stands on the higher tile (a land/water rim — sit right on
            // her edge, no lift), 1 if she perches up from the lower tile.
            var standElevation = world.Tiles.Items.TryGetValue(npc.Tile, out var standSeat)
                ? standSeat.Elevation : 0;
            var seatElevation = world.Tiles.Items.TryGetValue(seatStandTile, out var seatStand)
                ? seatStand.Elevation : standElevation;
            npcSnapshot.LedgeSeatStepsUp = System.Math.Max(0, seatElevation - standElevation);
        }

        foreach (var relation in npc.Social.Relationships)
        {
            // Мёртвых в списке отношений не показываем. Запись в Social
            // остаётся (симу она нужна — свидетельства, страх, история), но
            // труп уже не в Npcs, имя не находилось, и вкладка рисовалась
            // безымянной «NPC1001» — так §72.14-волна и выдала себя после
            // гибели.
            if (!world.Entities.Npcs.TryGetValue(relation.Key, out var otherNpc))
            {
                continue;
            }

            npcSnapshot.RelationshipDetails.Add(new RelationshipSnapshot
            {
                OtherId = relation.Key.Value,
                OtherName = string.IsNullOrEmpty(otherNpc.DisplayName)
                    ? $"NPC{relation.Key.Value}"
                    : otherNpc.DisplayName,
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
                    $"NPC{relation.Key.Value}: T={Num(relation.Value.Trust, "F2")} " +
                    $"F={Num(relation.Value.Familiarity, "F2")} A={Num(relation.Value.Affinity, "F2")}");
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

    /// <summary>§136: перелить кольцо дневника в секцию снапшота.</summary>
    private static void AddJournal(
        WorldSnapshot snapshot,
        int npcId,
        Runtime.Journal.NpcJournal journal)
    {
        if (journal == null || journal.Entries.Count == 0)
        {
            return;
        }

        var record = new NpcJournalSnapshot { NpcId = npcId };
        var entries = journal.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            record.Entries.Add(new JournalEntrySnapshot
            {
                Tick = entry.Tick,
                Type = entry.Type ?? string.Empty,
                Register = (int)entry.Register,
                Bond = (int)entry.Bond,
                Perspective = (int)entry.Perspective,
                SubjectNameId = entry.SubjectNameId ?? string.Empty,
                Extra = entry.Extra ?? string.Empty,
                Variant = entry.Variant,
                QuietHours = entry.QuietHours,
                Chore0 = entry.Chore0 ?? string.Empty,
                Chore1 = entry.Chore1 ?? string.Empty,
                Chore2 = entry.Chore2 ?? string.Empty
            });
        }

        snapshot.Journals.Add(record);
    }
}

}

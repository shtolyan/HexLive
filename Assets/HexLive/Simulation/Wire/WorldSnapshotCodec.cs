using System.IO;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// Turns a <see cref="WorldSnapshot"/> into bytes and back — the payload of one
/// tick frame.
/// <para>
/// Written by hand, field by field, in the same style as
/// <c>WorldSaveSerializer</c>: an explicit version, an end marker, and no
/// reflection. The single failure mode of that style is a field added to the
/// snapshot and forgotten here, so it is guarded by a reflection-based coverage
/// test that round-trips every public property and fails on any that does not
/// survive (the same trick the balance layer already uses to prove every tuned
/// static reaches an asset).
/// </para>
/// <para>
/// <b>What is NOT on the wire, and why.</b> Neither tiles nor junctions: both are
/// pure worldgen output, and the receiver rebuilds them from the seed — the same
/// trick the save format already relies on ("static topology is rebuilt, never
/// stored"). Tiles do carry a changed-set that is normally empty; junctions carry
/// nothing at all, since nothing in the shipped render path reads their mutable
/// flags — only the debug overlays do.
/// Trace events are not here either — they are a separate stream keyed by
/// <c>SimulationEvent.Seq</c>, because a snapshot carrying the whole 2048-entry
/// ring would be ~200 KB per frame.
/// </para>
/// </summary>
public static class WorldSnapshotCodec
{
    /// <summary>
    /// Bump on ANY change to the byte layout. Both ends link this assembly, so a
    /// mismatch means someone is running a different build — refuse rather than
    /// decode garbage.
    /// </summary>
    /// v2: tiles reduced to a changed-set (the receiver regenerates them from the
    /// seed), optional parts of an object record behind a flags byte, definition
    /// ids interned against the shared content catalog.
    /// v3: §74 appearance (SkinSet/Hairstyle/VoiceBank), §72 faction pair,
    /// §77.5 InteractionSeconds, §76 attributes/skills/perks.
    /// v4: §103 MeleeWeaponId — чем сим бьёт СЕЙЧАС (сцена может назначить
    /// кулаки при ноже в рюкзаке); вид считал это сам и показывал не то.
    /// v5: §28.15C v3 — секция Corpses (тела не исчезают) + DeathAnimVariant
    /// в записи человека.
    /// v6: §105 IsDying — она лежит и умирает, и вид роняет её на землю.
    /// v7: §85 EyeColor — цвет глаз отвязан от SkinSet и катится своей осью.
    /// v8: §110 IsCrying — стресс-крах кладёт её плакать, и это НЕ обморок:
    /// вид укладывает её сонной цепочкой и не глушит ей речь.
    /// v9: §81.10 IsSadWalk — понурая походка стала состоянием сима (она ещё и
    /// ходит вдвое медленнее), а была таймером внутри вида.
    /// v10: §105 r2 VitalHealth — худшая витальная зона; кольцо вокруг
    /// портрета показывает ЕЁ, а не среднее по семи зонам.
    /// v11: §105.14 IsPlayingDead — притворяется мёртвой; вид держит её
    /// упавшей, а панель показывает чип и строку состояния.
    /// v12: §116 typed body conditions, wounds, prosthetics and carry links.
    /// v13: §32 CompassionTrait + §34 melee stat multipliers for item cards.
    /// v14: §111.12 item id carried by a social cue bubble.
    /// v16: §120 independently persisted architecture elements.
    /// v17: §51/§52 typed inventory containers and §75A favorite-weapon projection.
    /// v18: §120 floor state rides keyframes and deltas so presentation can
    /// remove terrain grass as soon as the architectural floor is complete.
    /// v19: §126 черты характера — вкладка «Характер» на листе персонажа.
    /// v20: §128 physical source indices for drag-transfer inventory cells and
    /// their worn container owners.
    /// v22: §105 r3 DisplayHealth — число на портрете замечает разбитые руки и
    /// ноги, а VitalHealth остаётся тем же числом для порогов и трасс.
    /// v23: §40.8-H r10 BloodSoil — накопительная кровяная подложка per-zone
    /// (растёт от ран, смывается водой) едет в типизированных кондициях.
    /// v24: §136 дневники — СВОЯ секция, а не поле в записи NPC: она меняется
    /// раз в игровой час, тогда как запись NPC переписывается каждый тик, и
    /// внутри неё дневник стоил бы втрое больше всего остального трафика.
    /// v25: §50 LegsLost — «ноги в ноль» едет отдельно от PostureHint, иначе
    /// обморок стирал этот факт и вид ронял разбитую в ноль как здоровую.
    /// v26: §57 RelationshipDetails уехали из отладочного блока в основную
    /// запись NPC — вкладка «Отношения» на листе персонажа это ИГРА, а не дамп
    /// панели, и на сервере без --debug-details она молча пустовала.
    /// v27: §83.2 r12 — запись NPC разложена на группы полей и едет с маской
    /// групп. Дельта шлёт только изменившиеся; замер до/после — в §83.5.
    /// v28: §83.2 r13 — набор runtime-тайлов в дельте едет за флагом «изменился»,
    /// а не каждым кадром. Ключевой кадр по-прежнему несёт его всегда.
    /// v30: §120.9 editable building owners expose their authoritative draft.
    /// v31: §133.9 per-NPC outfit lock for the inventory header switch.
    /// v32: §127 paired romance state and washable pelvis stain.
    /// v33: §133/#205 physical carried/worn item owner ids for inventory cards.
    /// v34: #218 per-pair last interaction tick for relationship ordering.
    /// v35: §120.10 architecture demolition and queued slot replacement.
    /// v36: §146.14 camp home anchors for the campfire «make home» menu.
    /// v37: §121.11/#294 per-NPC default pace for the walk/run card toggle.
    /// v38: §55.4/#347 typed bottle-water provenance in the inventory group.
    /// v39: §52/#355 per-instance vessel amount/provenance in object and slot rows.
    /// v40: §159 companion profile, authored appearance, Hexkufa and voice bond.
    // v42: per-vessel visual provenance and exact held-bottle fill (§55.5).
    // v43: persistent craft project current/last worker (§119, #422).
    public const int WireVersion = 43;

    private const int EndMarker = unchecked((int)0x534E4150); // "SNAP"

    /// <summary>
    /// Encodes the dynamic half of <paramref name="snapshot"/>.
    /// <paramref name="includeDebugDetails"/> mirrors
    /// <see cref="WorldSnapshotExporter.IncludeDebugDetails"/>: the per-NPC
    /// relationship/memory/goal-score dumps cost megabytes per tick and only the
    /// debug panel reads them, so they ride only when it is open.
    /// </summary>
    public static void Write(WorldSnapshot snapshot, BinaryWriter w, bool includeDebugDetails)
    {
        w.Write(WireVersion);
        w.Write(includeDebugDetails);

        w.Write(snapshot.Tick);
        WriteHeaderRecord(w, snapshot);

        WriteTiles(snapshot, w);
        WriteExplored(snapshot, w);
        WriteObjects(snapshot, w);
        WriteNpcs(snapshot, w, includeDebugDetails);
        WriteCorpses(snapshot, w, includeDebugDetails);
        WriteMobs(snapshot, w);
        WriteMobSlots(snapshot, w);
        WriteCrabs(snapshot, w);
        WriteDeathRecords(snapshot, w);
        WriteJournals(snapshot, w);

        w.Write(EndMarker);
    }

    /// <summary>
    /// Which tick a queued frame carries, without decoding it.
    /// <para>
    /// The receiving clock has to know what ARRIVED before it can decide what to
    /// present, and the tick is the only thing it needs — reading a 27 KB
    /// keyframe on the socket thread to learn one int would be absurd. Both
    /// writers put it in the same place by construction: version, the debug-details
    /// flag, then (for a delta only) the baseline tick, then the tick. This method
    /// is the one place that knows that, so the two headers cannot drift from the
    /// peek in silence.
    /// </para>
    /// </summary>
    public static int PeekFrameTick(byte[] payload, bool keyframe)
    {
        var offset = keyframe
            ? sizeof(int) + sizeof(bool)                 // version + flag
            : sizeof(int) + sizeof(bool) + sizeof(int);  // …+ baseline tick

        if (payload == null || payload.Length < offset + sizeof(int))
        {
            throw new InvalidDataException(
                $"Frame is {payload?.Length ?? 0} bytes — too short to carry a tick.");
        }

        return payload[offset]
               | (payload[offset + 1] << 8)
               | (payload[offset + 2] << 16)
               | (payload[offset + 3] << 24);
    }

    /// <summary>
    /// Decodes into <paramref name="into"/>, reusing its lists and payload
    /// objects (same contract as <c>WorldSnapshotExporter.Export(world, reuse)</c>:
    /// the caller owns the instance and consumers must not hold it across frames).
    /// Leaves <c>Junctions</c>, <c>Tiles</c> and <c>TraceEvents</c> untouched apart
    /// from the tile changed-set — topology is filled by the receiver from its own
    /// worldgen, events come on their own stream.
    /// </summary>
    public static void Read(BinaryReader r, WorldSnapshot into)
    {
        var version = r.ReadInt32();
        if (version != WireVersion)
        {
            throw new InvalidDataException(
                $"Snapshot wire version {version}, expected {WireVersion} — " +
                "the two ends are running different builds of HexLive.Simulation.");
        }

        var includeDebugDetails = r.ReadBoolean();

        into.Tick = r.ReadInt32();
        ReadHeaderRecord(r, into);

        ReadTiles(r, into);
        ReadExplored(r, into);
        ReadObjects(r, into);
        ReadNpcs(r, into, includeDebugDetails);
        ReadCorpses(r, into, includeDebugDetails);
        ReadMobs(r, into);
        ReadMobSlots(r, into);
        ReadCrabs(r, into);
        ReadDeathRecords(r, into);
        ReadJournals(r, into);

        var marker = r.ReadInt32();
        if (marker != EndMarker)
        {
            throw new InvalidDataException(
                "Snapshot frame did not end where it should — the reader and writer " +
                "disagree about the layout (a field written but not read, or vice versa).");
        }
    }

    // ── world header ──────────────────────────────────────────────────────

    /// <summary>
    /// The world's scalars, minus the tick (which frames carry themselves).
    /// <para>
    /// Almost all of it moves on the same slow beat: <c>EnvironmentSystem</c> and
    /// <c>WeatherSystem</c> run on the slow layer, so the clock, temperature,
    /// weather and sun change once every sixteen ticks and are identical in
    /// between. A delta replaces the block whole rather than masking fields —
    /// it is ~60 bytes, and a second code path through the safety-critical part
    /// of the codec would cost more than it saves.
    /// </para>
    /// </summary>
    internal static void WriteHeaderRecord(BinaryWriter w, WorldSnapshot snapshot)
    {
        w.Write(snapshot.Seed);
        w.Write(snapshot.TickDeltaTime);
        w.Write(snapshot.Temperature);
        WireIo.WriteString(w, snapshot.Clock);
        w.Write(snapshot.TimeOfDayNormalized);
        WireIo.WriteString(w, snapshot.DayPhase);
        w.Write(snapshot.UvIndex);
        w.Write(snapshot.IsRaining);
        w.Write(snapshot.RaftProgress);
        w.Write(snapshot.RaftTarget);
        w.Write(snapshot.Completed);
        WireIo.WriteFloat2(w, snapshot.SunDirection);
        w.Write(snapshot.SunElevationDegrees);
        w.Write(snapshot.JunctionsBlockedStamp);
        // §146.14: домашние якоря лагерей (≤7, порядок — ординал фракции).
        // Живут в заголовке: меняются редко, дельта резендит блок целиком.
        w.Write((byte)snapshot.CampHomes.Count);
        foreach (var home in snapshot.CampHomes)
        {
            w.Write((byte)home.Faction);
            WireIo.WriteTile(w, home.Tile);
        }
    }

    internal static void ReadHeaderRecord(BinaryReader r, WorldSnapshot into)
    {
        into.Seed = r.ReadInt32();
        into.TickDeltaTime = r.ReadSingle();
        into.Temperature = r.ReadSingle();
        into.Clock = r.ReadString();
        into.TimeOfDayNormalized = r.ReadSingle();
        into.DayPhase = r.ReadString();
        into.UvIndex = r.ReadSingle();
        into.IsRaining = r.ReadBoolean();
        into.RaftProgress = r.ReadInt32();
        into.RaftTarget = r.ReadInt32();
        into.Completed = r.ReadBoolean();
        into.SunDirection = WireIo.ReadFloat2(r);
        into.SunElevationDegrees = r.ReadSingle();
        into.JunctionsBlockedStamp = r.ReadInt32();
        var campHomes = r.ReadByte();
        into.CampHomes.Clear();
        for (var i = 0; i < campHomes; i++)
        {
            into.CampHomes.Add(new Debug.CampHomeSnapshot
            {
                Faction = (Agents.Faction)r.ReadByte(),
                Tile = WireIo.ReadTile(r)
            });
        }
    }

    // ── tiles ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Tiles that differ from what worldgen produced — normally none.
    /// <para>
    /// All 285 tiles used to ride every frame: 3.7 KB, 15 KB/s, for data that
    /// never changes. Coord and Elevation are written once at bootstrap, and the
    /// only two runtime writers of tile flags (<c>ExecutionSystem.Build</c>) are
    /// gated on <c>world.Project</c>, which nothing assigns — the communal hut
    /// was retired and <c>CreateBuildProject</c> has no callers. The receiver
    /// regenerates tiles from the seed, exactly as it already does for the ~14 000
    /// junctions.
    /// </para>
    /// <para>
    /// This still sends a CHANGED-SET rather than nothing at all, because
    /// "immutable" is one restored save away from false: a blob written while a
    /// hut project existed would bring one back. Two bytes to observe that
    /// instead of assuming it.
    /// </para>
    /// </summary>
    internal static void WriteTiles(WorldSnapshot snapshot, BinaryWriter w)
    {
        var changed = 0;
        foreach (var tile in snapshot.Tiles)
        {
            if (tile.HasFloor || tile.Indoor) changed++;
        }

        w.Write((ushort)changed);
        foreach (var tile in snapshot.Tiles)
        {
            if (!tile.HasFloor && !tile.Indoor) continue;
            WireIo.WriteTile(w, tile.Coord);
            w.Write(tile.Elevation);
            w.Write((byte)((tile.Walkable ? 1 : 0) |
                           (tile.Blocked ? 2 : 0) |
                           (tile.Indoor ? 4 : 0) |
                           (tile.Water ? 8 : 0) |
                           (tile.HasFloor ? 16 : 0)));
        }
    }

    /// <summary>
    /// §148: разведанные гексы — своей секцией, а не полем в записи тайла.
    /// <para>
    /// Тайлы едут ИЗМЕНЁННЫМ НАБОРОМ (обычно пустым), и подмешать в него
    /// разведку значило бы гонять весь остров каждый раз, как открылся один
    /// гекс. Здесь — плоский список координат: он растёт и никогда не убывает,
    /// а дельта присылает секцию целиком только когда она изменилась (тот же
    /// приём, что у заголовка и тайлов).
    /// </para>
    /// </summary>
    internal static void WriteExplored(WorldSnapshot snapshot, BinaryWriter w)
    {
        var count = 0;
        foreach (var tile in snapshot.Tiles)
        {
            if (tile.Explored) count++;
        }

        w.Write(count);
        foreach (var tile in snapshot.Tiles)
        {
            if (tile.Explored)
            {
                WireIo.WriteTile(w, tile.Coord);
            }
        }
    }

    internal static void ReadExplored(BinaryReader r, WorldSnapshot into)
    {
        var count = r.ReadInt32();
        if (count == 0)
        {
            foreach (var tile in into.Tiles)
            {
                tile.Explored = false;
            }

            return;
        }

        // Словарь, а не линейный поиск по координате: разведанных гексов
        // тысячи, и поиск в списке дал бы миллионы сравнений на кадр.
        _exploredLookup.Clear();
        foreach (var tile in into.Tiles)
        {
            tile.Explored = false;
            _exploredLookup[tile.Coord] = tile;
        }

        for (var i = 0; i < count; i++)
        {
            var coord = WireIo.ReadTile(r);
            if (_exploredLookup.TryGetValue(coord, out var tile))
            {
                tile.Explored = true;
            }
        }
    }

    private static readonly System.Collections.Generic.Dictionary<
        HexLive.Simulation.Common.TileCoord, TileSnapshot> _exploredLookup = new();

    internal static void ReadTiles(BinaryReader r, WorldSnapshot into)
    {
        // Both keyframes and deltas carry the complete (normally tiny) set of
        // runtime-mutated tiles. Clear the previous mirror before applying it.
        foreach (var tile in into.Tiles)
        {
            tile.HasFloor = false;
            tile.Indoor = false;
        }

        var changed = r.ReadUInt16();
        for (var i = 0; i < changed; i++)
        {
            var coord = WireIo.ReadTile(r);
            var elevation = r.ReadInt32();
            var flags = r.ReadByte();

            // Locate by coord, not by index: the receiver's tile list came from
            // its own worldgen and shares no ordering contract with the sender's.
            for (var j = 0; j < into.Tiles.Count; j++)
            {
                var t = into.Tiles[j];
                if (!t.Coord.Equals(coord))
                {
                    continue;
                }

                t.Elevation = elevation;
                t.Walkable = (flags & 1) != 0;
                t.Blocked = (flags & 2) != 0;
                t.Indoor = (flags & 4) != 0;
                t.Water = (flags & 8) != 0;
                t.HasFloor = (flags & 16) != 0;
                break;
            }
        }
    }

    // ── objects ───────────────────────────────────────────────────────────

    // Optional parts of an object record, one bit each.
    //
    // The build-site block is twelve ints — 48 bytes, more than a third of the
    // whole record — and it is all zeroes on about 215 of the 218 objects in a
    // typical world. Rocks, sticks and coconuts do not have a bill of materials.
    // Same story for the other three: a variant tag, a build product name and an
    // owner exist on a handful of objects and nowhere else.
    [System.Flags]
    private enum ObjectParts : byte
    {
        None = 0,
        BuildSite = 1 << 0,   // Bill*/Delivered*/Roasting* — 48 bytes
        Variant = 1 << 1,
        BuildProduct = 1 << 2,
        Owner = 1 << 3,
        CraftProject = 1 << 4,
        Architecture = 1 << 5,
        ArchitectureOwner = 1 << 6,
        DoorClosed = 1 << 7,
    }

    // Definition ids repeat across every object; both ends derive the same table
    // from the same content catalog. See DefinitionIdTable. Index 0 = unknown,
    // spell it out — a runtime-added definition must still travel correctly.
    private static void WriteDefinitionId(BinaryWriter w, string id)
    {
        var index = DefinitionIdTable.IndexOf(id);
        if (index > 0 && index <= ushort.MaxValue)
        {
            w.Write((ushort)index);
            return;
        }

        w.Write((ushort)0);
        WireIo.WriteString(w, id);
    }

    private static string ReadDefinitionId(BinaryReader r)
    {
        var index = r.ReadUInt16();
        return index > 0 ? DefinitionIdTable.Resolve(index) : r.ReadString();
    }

    private static void WriteObjects(WorldSnapshot snapshot, BinaryWriter w)
    {
        var objects = snapshot.Objects;
        w.Write(objects.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            WriteObjectRecord(w, objects[i]);
        }
    }

    /// <summary>
    /// ONE object, self-contained. The delta encoder calls exactly this to build
    /// the bytes it byte-compares against the previous frame — so a field can only
    /// be forgotten in one place, and that place is already covered by the
    /// reflection round-trip gate.
    /// </summary>
    internal static void WriteObjectRecord(BinaryWriter w, ObjectSnapshot o)
    {
        var parts = ObjectParts.None;
        if (o.BillLogs != 0 || o.BillStones != 0 || o.BillLeaves != 0 ||
            o.BillSticks != 0 || o.BillRope != 0 || o.BillBoards != 0 ||
            o.DeliveredLogs != 0 || o.DeliveredStones != 0 || o.DeliveredLeaves != 0 ||
            o.DeliveredSticks != 0 || o.DeliveredRope != 0 || o.DeliveredBoards != 0 ||
            o.RoastingRaw != 0 || o.RoastingCooked != 0)
        {
            parts |= ObjectParts.BuildSite;
        }

        if (!string.IsNullOrEmpty(o.Variant))
        {
            parts |= ObjectParts.Variant;
        }

        if (!string.IsNullOrEmpty(o.BuildProduct))
        {
            parts |= ObjectParts.BuildProduct;
        }

        if (o.OwnerNpcId.HasValue)
        {
            parts |= ObjectParts.Owner;
        }

        if (o.CraftWorkRequired > 0 || o.CraftWorkDone > 0 ||
            o.CraftStationObjectId.HasValue || o.CraftIngredients.Count > 0)
        {
            parts |= ObjectParts.CraftProject;
        }
        if (o.ArchitectureElements.Count > 0) parts |= ObjectParts.Architecture;
        if (o.ArchitectureOwnerObjectId.HasValue) parts |= ObjectParts.ArchitectureOwner;
        if (!o.IsDoorOpen) parts |= ObjectParts.DoorClosed;

        w.Write((byte)parts);
        w.Write(o.Id.Value);
        WriteDefinitionId(w, o.DefinitionId);
        WireIo.WriteTile(w, o.Tile);
        w.Write(o.RotationDegrees);
        w.Write(o.ResourceAmount);
        w.Write((byte)o.WaterKind);
        w.Write((byte)o.LastAddedWaterKind);
        w.Write(o.Wetness);
        w.Write(o.Durability);
        w.Write(o.Dirtiness);
        w.Write(o.Bloodiness);
        w.Write(o.SpawnTick);
        WireIo.WriteJunctions(w, o.Junctions);
        // Usually empty and therefore one byte. It is deliberately outside the
        // exhausted ObjectParts byte: only architecture owners pay the JSON.
        WireIo.WriteString(w, o.BuildingBlueprintJson);

        // §128.5: содержимое вещи. Своего бита в ObjectParts ему не досталось —
        // байт флагов занят все восемь; расширять его до ushort ради поля,
        // которое почти всегда пусто, значило бы переписать формат КАЖДОГО
        // объекта. Поэтому счётчик пишется всегда одним байтом: в дельте
        // постоянный ноль не стоит ничего (дельта сравнивает байты), а на
        // ключевом кадре это ~1 КБ на тысячу объектов.
        var contentsCount = o.Contents.Count > byte.MaxValue ? byte.MaxValue : o.Contents.Count;
        w.Write((byte)contentsCount);
        for (var i = 0; i < contentsCount; i++)
        {
            var slot = o.Contents[i];
            w.Write(slot.Index);
            w.Write(slot.SourceIndex);
            WriteDefinitionId(w, slot.ItemDefinitionId);
            w.Write(slot.StackCount);
            w.Write(slot.ResourceAmount);
            w.Write((byte)slot.WaterKind);
            w.Write((byte)slot.LastAddedWaterKind);
            // У мешка типизированных ячеек не бывает, и соблазн «не писать
            // заведомо пустое» здесь неверный: тип ячейки один на человека и на
            // вещь, а через провод тип едет ЦЕЛИКОМ или не едет никак. Пустая
            // строка стоит байт, а поле, забытое ради байта, живёт до
            // следующего ключевого кадра (гейт покрытия провода ловит ровно это).
            WireIo.WriteString(w, slot.AcceptedItemDefinitionId);
        }
        // §151: always present beside Contents for the same reason as its
        // count. Zero is the legacy/unbounded container projection.
        w.Write(o.ContainerCapacity);

        if ((parts & ObjectParts.Owner) != 0)
        {
            w.Write(o.OwnerNpcId.Value);
        }

        if ((parts & ObjectParts.Variant) != 0)
        {
            WireIo.WriteString(w, o.Variant);
        }

        if ((parts & ObjectParts.BuildProduct) != 0)
        {
            WireIo.WriteString(w, o.BuildProduct);
        }

        if ((parts & ObjectParts.BuildSite) != 0)
        {
            w.Write(o.BillLogs);
            w.Write(o.BillStones);
            w.Write(o.BillLeaves);
            w.Write(o.DeliveredLogs);
            w.Write(o.DeliveredStones);
            w.Write(o.DeliveredLeaves);
            w.Write(o.BillSticks);
            w.Write(o.BillRope);
            w.Write(o.DeliveredSticks);
            w.Write(o.DeliveredRope);
            w.Write(o.RoastingRaw);
            w.Write(o.RoastingCooked);
            w.Write(o.BillBoards);
            w.Write(o.DeliveredBoards);
        }

        if ((parts & ObjectParts.Architecture) != 0)
        {
            w.Write(o.ArchitectureElements.Count);
            foreach (var element in o.ArchitectureElements)
            {
                w.Write(element.ElementId);
                WireIo.WriteString(w, element.DefinitionId);
                WireIo.WriteString(w, element.SlotKey);
                w.Write(element.SlotIndex);
                w.Write(element.Layer);
                w.Write(element.LocalX);
                w.Write(element.LocalZ);
                w.Write(element.LocalYaw);
                w.Write(element.RequiredSticks);
                w.Write(element.RequiredBoards);
                w.Write(element.RequiredRope);
                w.Write(element.RequiredLeaves);
                w.Write(element.DeliveredSticks);
                w.Write(element.DeliveredBoards);
                w.Write(element.DeliveredRope);
                w.Write(element.DeliveredLeaves);
                w.Write(element.Buildable);
                w.Write(element.WorkRequired);
                w.Write(element.WorkDone);
                w.Write(element.DemolitionPlanned);
                WireIo.WriteString(w, element.ReplacementDefinitionId);
            }
        }

        if ((parts & ObjectParts.ArchitectureOwner) != 0)
            w.Write(o.ArchitectureOwnerObjectId.Value);

        if ((parts & ObjectParts.CraftProject) != 0)
        {
            w.Write(o.CraftWorkRequired);
            w.Write(o.CraftWorkDone);
            w.Write(o.CraftBatchCount);
            w.Write(o.CraftActive);
            w.Write(o.CraftStationObjectId.HasValue);
            if (o.CraftStationObjectId.HasValue) w.Write(o.CraftStationObjectId.Value);
            w.Write(o.CraftCurrentWorkerId.HasValue);
            if (o.CraftCurrentWorkerId.HasValue) w.Write(o.CraftCurrentWorkerId.Value);
            w.Write(o.CraftLastWorkerId.HasValue);
            if (o.CraftLastWorkerId.HasValue) w.Write(o.CraftLastWorkerId.Value);
            w.Write(o.CraftIngredients.Count);
            foreach (var ingredient in o.CraftIngredients) WireIo.WriteString(w, ingredient);
        }
    }

    private static void ReadObjects(BinaryReader r, WorldSnapshot into)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.Objects, count);
        for (var i = 0; i < count; i++)
        {
            ReadObjectRecord(r, into.Objects[i]);
        }
    }

    /// <summary>ONE object — the mirror of <see cref="WriteObjectRecord"/>.</summary>
    internal static void ReadObjectRecord(BinaryReader r, ObjectSnapshot o)
    {
        {
            var parts = (ObjectParts)r.ReadByte();

            o.Id = new ObjectId(r.ReadInt32());
            o.DefinitionId = ReadDefinitionId(r);
            o.Tile = WireIo.ReadTile(r);
            o.RotationDegrees = r.ReadSingle();
            o.ResourceAmount = r.ReadSingle();
            o.WaterKind = (Agents.WaterKind)r.ReadByte();
            o.LastAddedWaterKind = (Agents.WaterKind)r.ReadByte();
            o.Wetness = r.ReadSingle();
            o.Durability = r.ReadSingle();
            o.Dirtiness = r.ReadSingle();
            o.Bloodiness = r.ReadSingle();
            o.SpawnTick = r.ReadInt32();
            WireIo.ReadJunctions(r, o.Junctions);
            o.BuildingBlueprintJson = r.ReadString();

            // §128.5: содержимое вещи — счётчик пишется всегда (см. writer).
            o.Contents.Clear();
            var contentsCount = r.ReadByte();
            for (var i = 0; i < contentsCount; i++)
            {
                o.Contents.Add(new InventorySlotSnapshot
                {
                    Index = r.ReadInt32(),
                    SourceIndex = r.ReadInt32(),
                    ItemDefinitionId = ReadDefinitionId(r),
                    StackCount = r.ReadInt32(),
                    ResourceAmount = r.ReadSingle(),
                    WaterKind = (Agents.WaterKind)r.ReadByte(),
                    LastAddedWaterKind = (Agents.WaterKind)r.ReadByte(),
                    AcceptedItemDefinitionId = r.ReadString()
                });
            }
            o.ContainerCapacity = r.ReadInt32();

            // Absent parts must be CLEARED, not left alone: these records are
            // reused across frames, so an object that stops being a build site
            // would otherwise keep last frame's bill forever.
            o.OwnerNpcId = (parts & ObjectParts.Owner) != 0 ? r.ReadInt32() : (int?)null;
            o.Variant = (parts & ObjectParts.Variant) != 0 ? r.ReadString() : string.Empty;
            o.BuildProduct = (parts & ObjectParts.BuildProduct) != 0 ? r.ReadString() : string.Empty;

            if ((parts & ObjectParts.BuildSite) != 0)
            {
                o.BillLogs = r.ReadInt32();
                o.BillStones = r.ReadInt32();
                o.BillLeaves = r.ReadInt32();
                o.DeliveredLogs = r.ReadInt32();
                o.DeliveredStones = r.ReadInt32();
                o.DeliveredLeaves = r.ReadInt32();
                o.BillSticks = r.ReadInt32();
                o.BillRope = r.ReadInt32();
                o.DeliveredSticks = r.ReadInt32();
                o.DeliveredRope = r.ReadInt32();
                o.RoastingRaw = r.ReadInt32();
                o.RoastingCooked = r.ReadInt32();
                o.BillBoards = r.ReadInt32();
                o.DeliveredBoards = r.ReadInt32();
            }
            else
            {
                o.BillLogs = 0;
                o.BillStones = 0;
                o.BillLeaves = 0;
                o.DeliveredLogs = 0;
                o.DeliveredStones = 0;
                o.DeliveredLeaves = 0;
                o.BillSticks = 0;
                o.BillRope = 0;
                o.DeliveredSticks = 0;
                o.DeliveredRope = 0;
                o.RoastingRaw = 0;
                o.RoastingCooked = 0;
                o.BillBoards = 0;
                o.DeliveredBoards = 0;
            }

            o.ArchitectureElements.Clear();
            if ((parts & ObjectParts.Architecture) != 0)
            {
                var architectureCount = r.ReadInt32();
                for (var i = 0; i < architectureCount; i++)
                {
                    o.ArchitectureElements.Add(new ArchitectureElementSnapshot
                    {
                        ElementId = r.ReadInt32(),
                        DefinitionId = r.ReadString(),
                        SlotKey = r.ReadString(),
                        SlotIndex = r.ReadInt32(),
                        Layer = r.ReadInt32(),
                        LocalX = r.ReadSingle(),
                        LocalZ = r.ReadSingle(),
                        LocalYaw = r.ReadSingle(),
                        RequiredSticks = r.ReadInt32(),
                        RequiredBoards = r.ReadInt32(),
                        RequiredRope = r.ReadInt32(),
                        RequiredLeaves = r.ReadInt32(),
                        DeliveredSticks = r.ReadInt32(),
                        DeliveredBoards = r.ReadInt32(),
                        DeliveredRope = r.ReadInt32(),
                        DeliveredLeaves = r.ReadInt32(),
                        Buildable = r.ReadBoolean(),
                        WorkRequired = r.ReadInt32(),
                        WorkDone = r.ReadInt32(),
                        DemolitionPlanned = r.ReadBoolean(),
                        ReplacementDefinitionId = r.ReadString()
                    });
                }
            }

            o.ArchitectureOwnerObjectId = (parts & ObjectParts.ArchitectureOwner) != 0
                ? r.ReadInt32()
                : (int?)null;
            o.IsDoorOpen = (parts & ObjectParts.DoorClosed) == 0;

            o.CraftIngredients.Clear();
            if ((parts & ObjectParts.CraftProject) != 0)
            {
                o.CraftWorkRequired = r.ReadInt32();
                o.CraftWorkDone = r.ReadInt32();
                o.CraftBatchCount = r.ReadInt32();
                o.CraftActive = r.ReadBoolean();
                o.CraftStationObjectId = r.ReadBoolean() ? r.ReadInt32() : (int?)null;
                o.CraftCurrentWorkerId = r.ReadBoolean() ? r.ReadInt32() : (int?)null;
                o.CraftLastWorkerId = r.ReadBoolean() ? r.ReadInt32() : (int?)null;
                var count = r.ReadInt32();
                for (var i = 0; i < count; i++) o.CraftIngredients.Add(r.ReadString());
            }
            else
            {
                o.CraftWorkRequired = 0;
                o.CraftWorkDone = 0;
                o.CraftBatchCount = 1;
                o.CraftActive = false;
                o.CraftStationObjectId = null;
                o.CraftCurrentWorkerId = null;
                o.CraftLastWorkerId = null;
            }
        }
    }

    // ── NPCs ──────────────────────────────────────────────────────────────

    private static void WriteNpcs(WorldSnapshot snapshot, BinaryWriter w, bool includeDebugDetails)
    {
        var npcs = snapshot.Npcs;
        w.Write(npcs.Count);
        for (var i = 0; i < npcs.Count; i++)
        {
            WriteNpcRecord(w, npcs[i], includeDebugDetails);
        }
    }

    // §28.15C v3: тела едут ТОЙ ЖЕ записью, что и живые — WriteNpcRecord один на
    // оба списка. Своя, урезанная запись для трупа была бы вторым местом, где
    // можно забыть поле, а забытое поле в дельте портит зеркало до следующего
    // ключевого кадра (и рефлексионный гейт покрытия сторожит именно одну).
    private static void WriteCorpses(WorldSnapshot snapshot, BinaryWriter w, bool includeDebugDetails)
    {
        var corpses = snapshot.Corpses;
        w.Write(corpses.Count);
        for (var i = 0; i < corpses.Count; i++)
        {
            WriteNpcRecord(w, corpses[i], includeDebugDetails);
        }
    }

    private static void ReadCorpses(BinaryReader r, WorldSnapshot into, bool includeDebugDetails)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.Corpses, count);
        for (var i = 0; i < count; i++)
        {
            ReadNpcRecord(r, into.Corpses[i], includeDebugDetails);
        }
    }

    /// <summary>ONE colonist, self-contained — see <see cref="WriteObjectRecord"/>.</summary>
    /// <summary>
    /// §83.2 r12: запись колонистки, разложенная на ГРУППЫ ПОЛЕЙ.
    /// <para>
    /// Замер, из-за которого это появилось: 96.5% дельты — записи NPC, медиана
    /// 3.6 КБ на запись, 39-69 КБ/с на зрителя. Дельта сравнивает БАЙТЫ записи
    /// целиком, а колонистка каждый тик делает шаг — значит вместе с двадцатью
    /// байтами позиции каждый раз уезжали полтора килобайта прочности, грязи,
    /// влаги и крови по каждой надетой и лежащей в рюкзаке вещи, плюс атрибуты и
    /// навыки, которые не меняются вообще.
    /// </para>
    /// <para>
    /// Группа — это диапазон полей, которые меняются ВМЕСТЕ и с одной частотой.
    /// Перед записью едет маска: бит на группу, и в дельте включены только те,
    /// чьи байты отличаются от базовой линии.
    /// </para>
    /// <para>
    /// ⭐ Свойство §83, ради которого всё это устроено именно так, не нарушено:
    /// кодировщик дельты по-прежнему НЕ СМОТРИТ НА ПОЛЕ. Он просит у этих же
    /// методов байты группы — тех самых, которыми пишется ключевой кадр, — и
    /// сравнивает их с прошлыми. Поле по-прежнему можно забыть ровно в одном
    /// месте, и это место сторожит гейт покрытия. Поэтому группы обязаны быть
    /// разбиением: каждое поле ровно в одной, ни одного вне их.
    /// </para>
    /// </summary>
    internal static class NpcGroup
    {
        public const int Identity = 0;    // имя, меш, причёска, фракция — не меняется никогда
        public const int Transform = 1;   // тайл, позиция, поворот — каждый тик
        public const int Locomotion = 2;  // поза, прыжок, обмороки, стресс — часто
        public const int Vitals = 3;      // здоровье и нужды — на среднем слое
        public const int Combat = 4;      // замах, удар, оружие — только в бою
        public const int Body = 5;        // конечности, раны, протезы, перенос
        public const int Mind = 6;        // цель, план, реплики, цель взаимодействия
        public const int Inventory = 7;   // рюкзак и контейнеры — редко, но толсто
        public const int Wear = 8;        // надетое и его состояние — редко, но толсто
        public const int Character = 9;   // §76 атрибуты, навыки, перки, черты, эффекты
        public const int Relations = 10;  // §57 отношения
        public const int Debug = 11;      // дампы панели — только под --debug-details

        public const int Count = 12;
        public const int All = (1 << Count) - 1;
        public const int AllButDebug = All & ~(1 << Debug);
    }

    /// <summary>
    /// Полная запись — как её пишет ключевой кадр: маска со всеми группами,
    /// затем все группы подряд. Дельта пишет ту же маску с меньшим числом битов
    /// и только выбранные группы; читатель у обоих ОДИН.
    /// </summary>
    internal static void WriteNpcRecord(BinaryWriter w, NpcSnapshot n, bool includeDebugDetails)
    {
        var mask = includeDebugDetails ? NpcGroup.All : NpcGroup.AllButDebug;
        w.Write((ushort)mask);
        for (var group = 0; group < NpcGroup.Count; group++)
        {
            if ((mask & (1 << group)) != 0)
            {
                WriteNpcGroup(w, n, group);
            }
        }
    }

    internal static void WriteNpcGroup(BinaryWriter w, NpcSnapshot n, int group)
    {
        switch (group)
        {
            case NpcGroup.Identity: WriteNpcIdentity(w, n); break;
            case NpcGroup.Transform: WriteNpcTransform(w, n); break;
            case NpcGroup.Locomotion: WriteNpcLocomotion(w, n); break;
            case NpcGroup.Vitals: WriteNpcVitals(w, n); break;
            case NpcGroup.Combat: WriteNpcCombat(w, n); break;
            case NpcGroup.Body: WriteNpcBody(w, n); break;
            case NpcGroup.Mind: WriteNpcMind(w, n); break;
            case NpcGroup.Inventory: WriteNpcInventory(w, n); break;
            case NpcGroup.Wear: WriteNpcWear(w, n); break;
            case NpcGroup.Character: WriteNpcCharacter(w, n); break;
            case NpcGroup.Relations: WriteNpcRelations(w, n); break;
            case NpcGroup.Debug: WriteNpcDebug(w, n); break;
            default: throw new InvalidDataException($"Unknown NPC field group {group}.");
        }
    }

    private static void WriteNpcIdentity(BinaryWriter w, NpcSnapshot n)
    {
        w.Write(n.Id.Value);
        WireIo.WriteString(w, n.ProfileId);
        w.Write(n.UseAuthoredAppearance);
        WireIo.WriteString(w, n.DisplayName);
        WireIo.WriteString(w, n.ActorMesh);
        // §74 composition + §72 faction. Faction travels as a byte: the enum is
        // tiny and both ends link the same definition.
        WireIo.WriteString(w, n.HairColour);
        WireIo.WriteString(w, n.SkinSet);
        WireIo.WriteString(w, n.EyeColor);
        WireIo.WriteString(w, n.Hairstyle);
        WireIo.WriteString(w, n.VoiceBank);
        w.Write((byte)n.Faction);
        w.Write(n.IsHostileToColony);
        w.Write(n.HexkufaExposure);
    }

    private static void WriteNpcTransform(BinaryWriter w, NpcSnapshot n)
    {
        WireIo.WriteTile(w, n.Tile);
        WireIo.WriteFloat2(w, n.Position);
        w.Write(n.RotationDegrees);
    }

    private static void WriteNpcCombat(BinaryWriter w, NpcSnapshot n)
    {
        w.Write(n.MeleeStats.LimbMultiplier);
        w.Write(n.MeleeStats.StrengthMultiplier);
        w.Write(n.MeleeStats.CombatMultiplier);
        w.Write(n.MeleeStats.AgilityRecoveryMultiplier);
        w.Write(n.IsFighting);
        w.Write(n.CombatOpponentNpcId);
        w.Write(n.IsSwinging);
        w.Write(n.SwingStartTick);
        w.Write(n.MeleeWeaponId ?? string.Empty);
        w.Write(n.StrikeIndex);
        w.Write(n.HitStampTick);
        w.Write(n.HitWeaponId ?? string.Empty);
        w.Write(n.HitPart ?? string.Empty);
        WireIo.WriteFloat2(w, n.HitFrom);
    }

    private static void WriteNpcVitals(BinaryWriter w, NpcSnapshot n)
    {
        w.Write(n.Health);
        w.Write(n.CompassionTrait);
        w.Write(n.Hunger);
        w.Write(n.Thirst);
        w.Write(n.Energy);
        w.Write(n.Comfort);
        w.Write(n.Social);
        w.Write(n.Compassion);
        w.Write(n.ThermalDiscomfort);
        w.Write(n.ThermalComfort);
        w.Write(n.Stamina);
        w.Write(n.Hygiene);
        w.Write(n.Blood);
        w.Write(n.TanLevel);
        w.Write(n.Bandages);
        w.Write(n.Pills);
        w.Write(n.Sunburn);
        w.Write(n.EffectiveUv);
        w.Write(n.IsShaded);
        w.Write(n.WoundLockedHp);
        w.Write(n.VitalHealth); // §105 r2
        w.Write(n.DisplayHealth); // §105 r3
        w.Write(n.BloodDeficit);
    }

    private static void WriteNpcLocomotion(BinaryWriter w, NpcSnapshot n)
    {
        WireIo.WriteString(w, n.PostureHint);
        w.Write(n.Winded);
        w.Write(n.IsRunning);
        w.Write(n.Breath);
        WireIo.WriteString(w, n.HopKind);
        WireIo.WriteTile(w, n.HopTargetTile);
        WireIo.WriteTile(w, n.HopFromTile);
        w.Write(n.HopStartTick);
        w.Write(n.IsFainted);
        w.Write(n.IsUnconscious);
        w.Write(n.IsDying); // §105
        w.Write(n.IsCrying); // §110
        w.Write(n.IsSadWalk); // §81.10
        w.Write(n.IsPlayingDead); // §105.14
        w.Write(n.LegsLost); // §50: ноги в ноль — вставать нечем
        w.Write(n.AidTargetLyingDown);
        w.Write(n.LyingStationSlot); // §111.13
        w.Write(n.IsLedgeSit);
        w.Write(n.LedgeSeatStepsUp);
        w.Write(n.IsWaking);
        w.Write(n.Stress);
    }

    private static void WriteNpcMind(BinaryWriter w, NpcSnapshot n)
    {
        // goal / plan / execution
        WireIo.WriteString(w, n.CurrentGoal);
        WireIo.WriteString(w, n.CurrentDream);
        WireIo.WriteString(w, n.PlanStatus);
        WireIo.WriteString(w, n.MovementStatus);
        WireIo.WriteString(w, n.ExecutionStatus);
        WireIo.WriteString(w, n.CurrentInteraction);
        WireIo.WriteString(w, n.HeldItemId);
        WireIo.WriteString(w, n.OffhandItemId); // §55.4 (bug #317)
        WireIo.WriteNullableInt(w, n.RomancePartnerNpcId);
        WireIo.WriteString(w, n.RomanceClipKey);
        w.Write(n.RomanceForced);
        w.Write(n.RomanceAnchorX);
        w.Write(n.RomanceAnchorY);
        w.Write(n.RomanceFacingDegrees);

        // social cues
        WireIo.WriteString(w, n.TalkTopic);
        WireIo.WriteNullableInt(w, n.TalkTopicPeerId);
        w.Write(n.TalkResultTick);
        w.Write(n.TalkResultDelta);
        w.Write(n.SocialCueTick);
        WireIo.WriteString(w, n.SocialCueKind);
        WireIo.WriteNullableInt(w, n.SocialCuePeerId);
        WireIo.WriteString(w, n.SocialCueItemId);

        // wardrobe beat
        w.Write(n.ExecutionStartTick);
        w.Write(n.ExecutionEndTick);
        w.Write(n.InteractionSeconds);
        WireIo.WriteString(w, n.HeldGarmentId);
        w.Write(n.HeldGarmentDirt);
        w.Write(n.HeldGarmentBlood);
        w.Write(n.HeldGarmentWet);
        w.Write(n.HeldGarmentDurability);
        WireIo.WriteNullableInt(w, n.TargetObjectId);
        WireIo.WriteNullableTile(w, n.TargetTile);
        w.Write(n.IsStarving);
        w.Write(n.KnownObjectCount);
        WireIo.WriteNullableInt(w, n.GoalLockEndTick);
        w.Write(n.IsManualControl); // §121
        w.Write(n.RunByDefault); // §121.11
        w.Write(n.OutfitLocked); // §133.9
    }

    private static void WriteNpcInventory(BinaryWriter w, NpcSnapshot n)
    {
        WireIo.WriteStrings(w, n.InventoryItems);
        WireIo.WriteInts(w, n.InventoryOwnerIds);
        w.Write(n.InventoryUsedSlots);
        WireIo.WriteStrings(w, n.InventoryStacks);
        WireIo.WriteStrings(w, n.InventoryDurability);
        WireIo.WriteStrings(w, n.InventoryWetness);
        WireIo.WriteStrings(w, n.InventoryDirtiness);
        WireIo.WriteStrings(w, n.InventoryBloodiness);
        WireIo.WriteStrings(w, n.InventoryWater);
        w.Write((byte)n.BottleWaterKind);
        w.Write(n.HeldBottleFill);
        w.Write((byte)n.HeldBottleAppearance);
        w.Write(n.InventoryCapacity);
        WireIo.WriteString(w, n.FavoriteWeaponId);
        w.Write(n.InventoryContainers.Count);
        foreach (var container in n.InventoryContainers)
        {
            WireIo.WriteString(w, container.Id);
            w.Write((byte)container.Kind);
            WireIo.WriteString(w, container.OwnerItemDefinitionId);
            w.Write(container.OwnerSourceIndex);
            w.Write((byte)container.BodyAnchor);
            w.Write(container.Capacity);
            w.Write(container.BaseCapacity);
            w.Write(container.StrengthBonus);
            w.Write(container.BackpackCapacity);
            w.Write(container.Slots.Count);
            foreach (var slot in container.Slots)
            {
                w.Write(slot.Index);
                w.Write(slot.SourceIndex);
                WireIo.WriteString(w, slot.ItemDefinitionId);
                w.Write(slot.StackCount);
                w.Write(slot.ResourceAmount);
                w.Write((byte)slot.WaterKind);
                w.Write((byte)slot.LastAddedWaterKind);
                WireIo.WriteString(w, slot.AcceptedItemDefinitionId);
            }
        }
    }

    private static void WriteNpcWear(BinaryWriter w, NpcSnapshot n)
    {
        WireIo.WriteStrings(w, n.WornItems);
        WireIo.WriteInts(w, n.WornOwnerIds);
        WireIo.WriteStrings(w, n.HolsteredItems);
        WireIo.WriteStrings(w, n.WornDurability);
        WireIo.WriteStrings(w, n.WornWetness);
        WireIo.WriteStrings(w, n.WornDirtiness);
        WireIo.WriteStrings(w, n.WornBloodiness);
    }

    private static void WriteNpcCharacter(BinaryWriter w, NpcSnapshot n)
    {
        WireIo.WriteStrings(w, n.Effects);
        WireIo.WriteStrings(w, n.EffectImpacts); // §48.7
        // §76: innate attributes, learned skills, perks.
        WireIo.WriteStrings(w, n.Attributes);
        WireIo.WriteStrings(w, n.Skills);
        w.Write(n.PerceptionRadiusTiles);
        WireIo.WriteStrings(w, n.Perks);
        // §126/v19: черты характера.
        WireIo.WriteStrings(w, n.Traits);
    }

    private static void WriteNpcBody(BinaryWriter w, NpcSnapshot n)
    {
        WireIo.WriteStrings(w, n.BodyParts);
        WireIo.WriteStrings(w, n.PartArmor);
        WireIo.WriteStrings(w, n.SeveredParts);
        WireIo.WriteString(w, n.WorstBodyPart);
        WireIo.WriteStrings(w, n.UncoveredParts);
        WireIo.WriteStrings(w, n.BandagedZones);
        WireIo.WriteStrings(w, n.Wounds);

        // §28.15C v3: каким клипом она упала.
        w.Write(n.DeathAnimVariant);

        // §116/v12: typed conditions, wounds, prosthetics and carry links.
        WireIo.WriteNullableInt(w, n.CarriedNpcId);
        WireIo.WriteNullableInt(w, n.CarriedByNpcId);
        WireIo.WriteNullableInt(w, n.RescueDestinationObjectId);
        w.Write(n.BodyPartConditions.Count);
        foreach (var part in n.BodyPartConditions)
        {
            w.Write((byte)part.Part);
            w.Write(part.Health);
            w.Write(part.Armor);
            w.Write(part.CriticalTrauma);
            w.Write(part.BluntDamage);
            w.Write(part.SplintSupport);
            w.Write(part.HitBias);
            w.Write(part.BloodSoil);
            w.Write(part.IntimacySoil);
            w.Write(part.Severed);
            WireIo.WriteString(w, part.BandageKind);
            w.Write(part.Prosthetic != null);
            if (part.Prosthetic is { } prosthetic)
            {
                WireIo.WriteString(w, prosthetic.DefinitionId);
                w.Write((byte)prosthetic.Part);
                w.Write(prosthetic.Condition);
                w.Write(prosthetic.MaxCondition);
                w.Write(prosthetic.Function);
                w.Write(prosthetic.Mechanical);
            }
        }

        w.Write(n.OpenWounds.Count);
        foreach (var wound in n.OpenWounds)
        {
            w.Write(wound.Id);
            w.Write((byte)wound.Part);
            w.Write(wound.Severity);
            w.Write(wound.Heal01);
            w.Write(wound.Clot01);
            w.Write(wound.Stabilized);
            w.Write(wound.Plastered); // §118.2
            w.Write(wound.BleedFactor);
            w.Write(wound.Seed);
        }
    }

    /// <summary>
    /// §57: отношения — ИГРОВЫЕ данные, вкладка «Отношения» на листе персонажа,
    /// и едут всегда. Когда-то они лежали в отладочном блоке, и это был молчаливый
    /// отказ ровно того сорта, который §83 обещает ловить: локально панель читает
    /// снапшот напрямую, мимо кодека, поэтому всё работало, — а на сервере вкладка
    /// оказывалась пустой, если хост не запущен с --debug-details.
    /// </summary>
    private static void WriteNpcRelations(BinaryWriter w, NpcSnapshot n)
    {
        w.Write(n.PlayerVoiceFamiliarity);
        w.Write(n.PlayerVoiceTrust);
        w.Write(n.PlayerVoiceAffinity);
        w.Write(n.PlayerVoiceLastInteractionTick);
        w.Write(n.RelationshipDetails.Count);
        for (var j = 0; j < n.RelationshipDetails.Count; j++)
        {
            var rel = n.RelationshipDetails[j];
            w.Write(rel.OtherId);
            WireIo.WriteString(w, rel.OtherName);
            w.Write(rel.Trust);
            w.Write(rel.Familiarity);
            w.Write(rel.Affinity);
            w.Write(rel.LastInteractionTick);
            w.Write(rel.Authority);
        }

        // §167: указание — хвост группы Mind.
        WireIo.WriteString(w, n.DirectiveKind);
        WireIo.WriteNullableInt(w, n.DirectiveFromNpcId);
        w.Write(n.DirectiveUntilTick);
    }

    /// <summary>
    /// Debug-panel payload: the string dumps (memory, cooldowns, the raw
    /// relationship line) and goal scores. Off by default for the same reason
    /// the exporter gates them — megabytes a tick that only the panel reads.
    /// </summary>
    private static void WriteNpcDebug(BinaryWriter w, NpcSnapshot n)
    {
        WireIo.WriteStrings(w, n.CooldownGoals);
        WireIo.WriteStrings(w, n.Relationships);
        WireIo.WriteStrings(w, n.KnownObjects);
        WireIo.WriteJunctions(w, n.Path);

        w.Write(n.GoalScores.Count);
        for (var j = 0; j < n.GoalScores.Count; j++)
        {
            var score = n.GoalScores[j];
            WireIo.WriteString(w, score.Goal);
            w.Write(score.FinalScore);
        }
    }

    private static void ReadNpcs(BinaryReader r, WorldSnapshot into, bool includeDebugDetails)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.Npcs, count);
        for (var i = 0; i < count; i++)
        {
            ReadNpcRecord(r, into.Npcs[i], includeDebugDetails);
        }
    }

    /// <summary>ONE colonist — the mirror of <see cref="WriteNpcRecord"/>.</summary>
    internal static void ReadNpcRecord(BinaryReader r, NpcSnapshot n, bool includeDebugDetails)
    {
        // ОДИН читатель на ключевой кадр и на дельту: разница между ними ровно в
        // том, сколько битов стоит в маске. Группа, чей бит не стоит, не тронута
        // — зеркало сохраняет то, что уже держало, и это весь смысл затеи.
        var mask = r.ReadUInt16();
        for (var group = 0; group < NpcGroup.Count; group++)
        {
            if ((mask & (1 << group)) != 0)
            {
                ReadNpcGroup(r, n, group);
            }
        }

        if (!includeDebugDetails)
        {
            // Ничего не писали, но чистим: иначе выключенная панель показывала бы
            // дампы последнего кадра, в котором была открыта.
            n.CooldownGoals.Clear();
            n.Relationships.Clear();
            n.KnownObjects.Clear();
            n.Path.Clear();
            n.GoalScores.Clear();
        }
    }

    internal static void ReadNpcGroup(BinaryReader r, NpcSnapshot n, int group)
    {
        switch (group)
        {
            case NpcGroup.Identity: ReadNpcIdentity(r, n); break;
            case NpcGroup.Transform: ReadNpcTransform(r, n); break;
            case NpcGroup.Locomotion: ReadNpcLocomotion(r, n); break;
            case NpcGroup.Vitals: ReadNpcVitals(r, n); break;
            case NpcGroup.Combat: ReadNpcCombat(r, n); break;
            case NpcGroup.Body: ReadNpcBody(r, n); break;
            case NpcGroup.Mind: ReadNpcMind(r, n); break;
            case NpcGroup.Inventory: ReadNpcInventory(r, n); break;
            case NpcGroup.Wear: ReadNpcWear(r, n); break;
            case NpcGroup.Character: ReadNpcCharacter(r, n); break;
            case NpcGroup.Relations: ReadNpcRelations(r, n); break;
            case NpcGroup.Debug: ReadNpcDebug(r, n); break;
            default: throw new InvalidDataException($"Unknown NPC field group {group}.");
        }
    }

    private static void ReadNpcIdentity(BinaryReader r, NpcSnapshot n)
    {
        n.Id = new EntityId(r.ReadInt32());
        n.ProfileId = r.ReadString();
        n.UseAuthoredAppearance = r.ReadBoolean();
        n.DisplayName = r.ReadString();
        n.ActorMesh = r.ReadString();
        n.HairColour = r.ReadString();
        n.SkinSet = r.ReadString();
        n.EyeColor = r.ReadString();
        n.Hairstyle = r.ReadString();
        n.VoiceBank = r.ReadString();
        n.Faction = (Agents.Faction)r.ReadByte();
        n.IsHostileToColony = r.ReadBoolean();
        n.HexkufaExposure = r.ReadInt32();
    }

    private static void ReadNpcTransform(BinaryReader r, NpcSnapshot n)
    {
        n.Tile = WireIo.ReadTile(r);
        n.Position = WireIo.ReadFloat2(r);
        n.RotationDegrees = r.ReadSingle();
    }

    private static void ReadNpcCombat(BinaryReader r, NpcSnapshot n)
    {
        n.MeleeStats ??= new MeleeStatsSnapshot();
        n.MeleeStats.LimbMultiplier = r.ReadSingle();
        n.MeleeStats.StrengthMultiplier = r.ReadSingle();
        n.MeleeStats.CombatMultiplier = r.ReadSingle();
        n.MeleeStats.AgilityRecoveryMultiplier = r.ReadSingle();
        n.IsFighting = r.ReadBoolean();
        n.CombatOpponentNpcId = r.ReadInt32();
        n.IsSwinging = r.ReadBoolean();
        n.SwingStartTick = r.ReadInt32();
        n.MeleeWeaponId = r.ReadString();
        n.StrikeIndex = r.ReadInt32();
        n.HitStampTick = r.ReadInt32();
        n.HitWeaponId = r.ReadString();
        n.HitPart = r.ReadString();
        n.HitFrom = WireIo.ReadFloat2(r);
    }

    private static void ReadNpcVitals(BinaryReader r, NpcSnapshot n)
    {
        n.Health = r.ReadSingle();
        n.CompassionTrait = r.ReadSingle();
        n.Hunger = r.ReadSingle();
        n.Thirst = r.ReadSingle();
        n.Energy = r.ReadSingle();
        n.Comfort = r.ReadSingle();
        n.Social = r.ReadSingle();
        n.Compassion = r.ReadSingle();
        n.ThermalDiscomfort = r.ReadSingle();
        n.ThermalComfort = r.ReadSingle();
        n.Stamina = r.ReadSingle();
        n.Hygiene = r.ReadSingle();
        n.Blood = r.ReadSingle();
        n.TanLevel = r.ReadSingle();
        n.Bandages = r.ReadInt32();
        n.Pills = r.ReadInt32();
        n.Sunburn = r.ReadSingle();
        n.EffectiveUv = r.ReadSingle();
        n.IsShaded = r.ReadBoolean();
        n.WoundLockedHp = r.ReadSingle();
        n.VitalHealth = r.ReadSingle(); // §105 r2
        n.DisplayHealth = r.ReadSingle(); // §105 r3
        n.BloodDeficit = r.ReadSingle();
    }

    private static void ReadNpcLocomotion(BinaryReader r, NpcSnapshot n)
    {
        n.PostureHint = r.ReadString();
        n.Winded = r.ReadBoolean();
        n.IsRunning = r.ReadBoolean();
        n.Breath = r.ReadSingle();
        n.HopKind = r.ReadString();
        n.HopTargetTile = WireIo.ReadTile(r);
        n.HopFromTile = WireIo.ReadTile(r);
        n.HopStartTick = r.ReadInt32();
        n.IsFainted = r.ReadBoolean();
        n.IsUnconscious = r.ReadBoolean();
        n.IsDying = r.ReadBoolean(); // §105
        n.IsCrying = r.ReadBoolean(); // §110
        n.IsSadWalk = r.ReadBoolean(); // §81.10
        n.IsPlayingDead = r.ReadBoolean(); // §105.14
        n.LegsLost = r.ReadBoolean(); // §50
        n.AidTargetLyingDown = r.ReadBoolean();
        n.LyingStationSlot = r.ReadInt32(); // §111.13
        n.IsLedgeSit = r.ReadBoolean();
        n.LedgeSeatStepsUp = r.ReadInt32();
        n.IsWaking = r.ReadBoolean();
        n.Stress = r.ReadSingle();
    }

    private static void ReadNpcMind(BinaryReader r, NpcSnapshot n)
    {
        n.CurrentGoal = r.ReadString();
        n.CurrentDream = r.ReadString();
        n.PlanStatus = r.ReadString();
        n.MovementStatus = r.ReadString();
        n.ExecutionStatus = r.ReadString();
        n.CurrentInteraction = r.ReadString();
        n.HeldItemId = r.ReadString();
        n.OffhandItemId = r.ReadString(); // §55.4 (bug #317)
        n.RomancePartnerNpcId = WireIo.ReadNullableInt(r);
        n.RomanceClipKey = r.ReadString();
        n.RomanceForced = r.ReadBoolean();
        n.RomanceAnchorX = r.ReadSingle();
        n.RomanceAnchorY = r.ReadSingle();
        n.RomanceFacingDegrees = r.ReadSingle();

        n.TalkTopic = r.ReadString();
        n.TalkTopicPeerId = WireIo.ReadNullableInt(r);
        n.TalkResultTick = r.ReadInt32();
        n.TalkResultDelta = r.ReadSingle();
        n.SocialCueTick = r.ReadInt32();
        n.SocialCueKind = r.ReadString();
        n.SocialCuePeerId = WireIo.ReadNullableInt(r);
        n.SocialCueItemId = r.ReadString();

        n.ExecutionStartTick = r.ReadInt32();
        n.ExecutionEndTick = r.ReadInt32();
        n.InteractionSeconds = r.ReadSingle();
        n.HeldGarmentId = r.ReadString();
        n.HeldGarmentDirt = r.ReadSingle();
        n.HeldGarmentBlood = r.ReadSingle();
        n.HeldGarmentWet = r.ReadSingle();
        n.HeldGarmentDurability = r.ReadSingle();
        n.TargetObjectId = WireIo.ReadNullableInt(r);
        n.TargetTile = WireIo.ReadNullableTile(r);
        n.IsStarving = r.ReadBoolean();
        n.KnownObjectCount = r.ReadInt32();
        n.GoalLockEndTick = WireIo.ReadNullableInt(r);
        n.IsManualControl = r.ReadBoolean(); // §121
        n.RunByDefault = r.ReadBoolean(); // §121.11
        n.OutfitLocked = r.ReadBoolean(); // §133.9
    }

    private static void ReadNpcInventory(BinaryReader r, NpcSnapshot n)
    {
        WireIo.ReadStrings(r, n.InventoryItems);
        WireIo.ReadInts(r, n.InventoryOwnerIds);
        n.InventoryUsedSlots = r.ReadInt32();
        WireIo.ReadStrings(r, n.InventoryStacks);
        WireIo.ReadStrings(r, n.InventoryDurability);
        WireIo.ReadStrings(r, n.InventoryWetness);
        WireIo.ReadStrings(r, n.InventoryDirtiness);
        WireIo.ReadStrings(r, n.InventoryBloodiness);
        WireIo.ReadStrings(r, n.InventoryWater);
        n.BottleWaterKind = (Agents.WaterKind)r.ReadByte();
        n.HeldBottleFill = r.ReadSingle();
        n.HeldBottleAppearance = (Agents.WaterKind)r.ReadByte();
        n.InventoryCapacity = r.ReadInt32();
        n.FavoriteWeaponId = r.ReadString();
        var inventoryContainerCount = r.ReadInt32();
        WireIo.Resize(n.InventoryContainers, inventoryContainerCount);
        for (var i = 0; i < inventoryContainerCount; i++)
        {
            var container = n.InventoryContainers[i];
            container.Id = r.ReadString();
            container.Kind = (HexLive.Simulation.Runtime.InventoryContainerKind)r.ReadByte();
            container.OwnerItemDefinitionId = r.ReadString();
            container.OwnerSourceIndex = r.ReadInt32();
            container.BodyAnchor = (HexLive.Simulation.Runtime.InventoryBodyAnchor)r.ReadByte();
            container.Capacity = r.ReadInt32();
            container.BaseCapacity = r.ReadInt32();
            container.StrengthBonus = r.ReadInt32();
            container.BackpackCapacity = r.ReadInt32();
            var inventorySlotCount = r.ReadInt32();
            WireIo.Resize(container.Slots, inventorySlotCount);
            for (var j = 0; j < inventorySlotCount; j++)
            {
                var slot = container.Slots[j];
                slot.Index = r.ReadInt32();
                slot.SourceIndex = r.ReadInt32();
                slot.ItemDefinitionId = r.ReadString();
                slot.StackCount = r.ReadInt32();
                slot.ResourceAmount = r.ReadSingle();
                slot.WaterKind = (Agents.WaterKind)r.ReadByte();
                slot.LastAddedWaterKind = (Agents.WaterKind)r.ReadByte();
                slot.AcceptedItemDefinitionId = r.ReadString();
            }
        }
    }

    private static void ReadNpcWear(BinaryReader r, NpcSnapshot n)
    {
        WireIo.ReadStrings(r, n.WornItems);
        WireIo.ReadInts(r, n.WornOwnerIds);
        WireIo.ReadStrings(r, n.HolsteredItems);
        WireIo.ReadStrings(r, n.WornDurability);
        WireIo.ReadStrings(r, n.WornWetness);
        WireIo.ReadStrings(r, n.WornDirtiness);
        WireIo.ReadStrings(r, n.WornBloodiness);
    }

    private static void ReadNpcCharacter(BinaryReader r, NpcSnapshot n)
    {
        WireIo.ReadStrings(r, n.Effects);
        WireIo.ReadStrings(r, n.EffectImpacts); // §48.7
        WireIo.ReadStrings(r, n.Attributes);
        WireIo.ReadStrings(r, n.Skills);
        n.PerceptionRadiusTiles = r.ReadInt32();
        WireIo.ReadStrings(r, n.Perks);
        WireIo.ReadStrings(r, n.Traits);
    }

    private static void ReadNpcBody(BinaryReader r, NpcSnapshot n)
    {
        WireIo.ReadStrings(r, n.BodyParts);
        WireIo.ReadStrings(r, n.PartArmor);
        WireIo.ReadStrings(r, n.SeveredParts);
        n.WorstBodyPart = r.ReadString();
        WireIo.ReadStrings(r, n.UncoveredParts);
        WireIo.ReadStrings(r, n.BandagedZones);
        WireIo.ReadStrings(r, n.Wounds);

        n.DeathAnimVariant = r.ReadInt32();

        n.CarriedNpcId = WireIo.ReadNullableInt(r);
        n.CarriedByNpcId = WireIo.ReadNullableInt(r);
        n.RescueDestinationObjectId = WireIo.ReadNullableInt(r);
        var bodyConditionCount = r.ReadInt32();
        WireIo.Resize(n.BodyPartConditions, bodyConditionCount);
        for (var i = 0; i < bodyConditionCount; i++)
        {
            var part = n.BodyPartConditions[i];
            part.Part = (BodyPart)r.ReadByte();
            part.Health = r.ReadSingle();
            part.Armor = r.ReadSingle();
            part.CriticalTrauma = r.ReadSingle();
            part.BluntDamage = r.ReadSingle();
            part.SplintSupport = r.ReadSingle();
            part.HitBias = r.ReadSingle();
            part.BloodSoil = r.ReadSingle();
            part.IntimacySoil = r.ReadSingle();
            part.Severed = r.ReadBoolean();
            part.BandageKind = r.ReadString();
            part.Prosthetic = r.ReadBoolean()
                ? new ProstheticSnapshot
                {
                    DefinitionId = r.ReadString(),
                    Part = (BodyPart)r.ReadByte(),
                    Condition = r.ReadSingle(),
                    MaxCondition = r.ReadSingle(),
                    Function = r.ReadSingle(),
                    Mechanical = r.ReadBoolean()
                }
                : null;
        }

        var openWoundCount = r.ReadInt32();
        WireIo.Resize(n.OpenWounds, openWoundCount);
        for (var i = 0; i < openWoundCount; i++)
        {
            var wound = n.OpenWounds[i];
            wound.Id = r.ReadInt32();
            wound.Part = (BodyPart)r.ReadByte();
            wound.Severity = r.ReadSingle();
            wound.Heal01 = r.ReadSingle();
            wound.Clot01 = r.ReadSingle();
            wound.Stabilized = r.ReadBoolean();
            wound.Plastered = r.ReadBoolean(); // §118.2
            wound.BleedFactor = r.ReadSingle();
            wound.Seed = r.ReadInt32();
        }
    }

    private static void ReadNpcRelations(BinaryReader r, NpcSnapshot n)
    {
        n.PlayerVoiceFamiliarity = r.ReadSingle();
        n.PlayerVoiceTrust = r.ReadSingle();
        n.PlayerVoiceAffinity = r.ReadSingle();
        n.PlayerVoiceLastInteractionTick = r.ReadInt32();
        var relCount = r.ReadInt32();
        WireIo.Resize(n.RelationshipDetails, relCount);
        for (var j = 0; j < relCount; j++)
        {
            var rel = n.RelationshipDetails[j];
            rel.OtherId = r.ReadInt32();
            rel.OtherName = r.ReadString();
            rel.Trust = r.ReadSingle();
            rel.Familiarity = r.ReadSingle();
            rel.Affinity = r.ReadSingle();
            rel.LastInteractionTick = r.ReadInt32();
            rel.Authority = r.ReadSingle();
        }

        n.DirectiveKind = r.ReadString();
        n.DirectiveFromNpcId = WireIo.ReadNullableInt(r);
        n.DirectiveUntilTick = r.ReadInt32();
    }

    private static void ReadNpcDebug(BinaryReader r, NpcSnapshot n)
    {
        WireIo.ReadStrings(r, n.CooldownGoals);
        WireIo.ReadStrings(r, n.Relationships);
        WireIo.ReadStrings(r, n.KnownObjects);
        WireIo.ReadJunctions(r, n.Path);

        var scoreCount = r.ReadInt32();
        WireIo.Resize(n.GoalScores, scoreCount);
        for (var j = 0; j < scoreCount; j++)
        {
            var score = n.GoalScores[j];
            score.Goal = r.ReadString();
            score.FinalScore = r.ReadSingle();
        }
    }

    // ── wildlife ──────────────────────────────────────────────────────────

    private static void WriteMobs(WorldSnapshot snapshot, BinaryWriter w)
    {
        var items = snapshot.Mobs;
        w.Write(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            WriteMobRecord(w, items[i]);
        }
    }

    internal static void WriteMobRecord(BinaryWriter w, MobSnapshot m)
    {
        w.Write(m.Id);
        WireIo.WriteString(w, m.MobId);
        WireIo.WriteTile(w, m.Tile);
        WireIo.WriteFloat2(w, m.Position);
        w.Write(m.Health);
        WireIo.WriteString(w, m.Status);
        w.Write(m.TargetNpcId);
        w.Write(m.IsAttacking);
        w.Write(m.AttackStartTick);
        w.Write(m.CarriedLimbOwnerNpcId);
        WireIo.WriteString(w, m.CarriedLimbPart);
    }

    private static void ReadMobs(BinaryReader r, WorldSnapshot into)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.Mobs, count);
        for (var i = 0; i < count; i++)
        {
            ReadMobRecord(r, into.Mobs[i]);
        }
    }

    internal static void ReadMobRecord(BinaryReader r, MobSnapshot m)
    {
        m.Id = r.ReadInt32();
        m.MobId = r.ReadString();
        m.Tile = WireIo.ReadTile(r);
        m.Position = WireIo.ReadFloat2(r);
        m.Health = r.ReadSingle();
        m.Status = r.ReadString();
        m.TargetNpcId = r.ReadInt32();
        m.IsAttacking = r.ReadBoolean();
        m.AttackStartTick = r.ReadInt32();
        m.CarriedLimbOwnerNpcId = r.ReadInt32();
        m.CarriedLimbPart = r.ReadString();
    }

    // §147.5: патрульные слоты. Запись меняется только на переходах состояний,
    // так что дельта-секция в стедистейте не шлёт ни байта.
    private static void WriteMobSlots(WorldSnapshot snapshot, BinaryWriter w)
    {
        var items = snapshot.MobSlots;
        w.Write(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            WriteMobSlotRecord(w, items[i]);
        }
    }

    internal static void WriteMobSlotRecord(BinaryWriter w, MobSlotSnapshot s)
    {
        w.Write(s.SlotId);
        WireIo.WriteString(w, s.MobId);
        w.Write(s.State);
        w.Write(s.ReservedMobId);
        w.Write(s.CycleIndex);
        w.Write(s.Ring.Count);
        for (var i = 0; i < s.Ring.Count; i++)
        {
            var waypoint = s.Ring[i];
            w.Write(waypoint.JunctionId);
            WireIo.WriteTile(w, waypoint.Tile);
            WireIo.WriteFloat2(w, waypoint.Position);
        }
    }

    private static void ReadMobSlots(BinaryReader r, WorldSnapshot into)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.MobSlots, count);
        for (var i = 0; i < count; i++)
        {
            ReadMobSlotRecord(r, into.MobSlots[i]);
        }
    }

    internal static void ReadMobSlotRecord(BinaryReader r, MobSlotSnapshot s)
    {
        s.SlotId = r.ReadInt32();
        s.MobId = r.ReadString();
        s.State = r.ReadInt32();
        s.ReservedMobId = r.ReadInt32();
        s.CycleIndex = r.ReadInt32();
        var waypoints = r.ReadInt32();
        WireIo.Resize(s.Ring, waypoints);
        for (var i = 0; i < waypoints; i++)
        {
            var waypoint = s.Ring[i];
            waypoint.JunctionId = r.ReadInt32();
            waypoint.Tile = WireIo.ReadTile(r);
            waypoint.Position = WireIo.ReadFloat2(r);
        }
    }

    private static void WriteCrabs(WorldSnapshot snapshot, BinaryWriter w)
    {
        var items = snapshot.Crabs;
        w.Write(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            WriteCrabRecord(w, items[i]);
        }
    }

    internal static void WriteCrabRecord(BinaryWriter w, CrabSnapshot c)
    {
        w.Write(c.Id);
        WireIo.WriteTile(w, c.Tile);
        WireIo.WriteFloat2(w, c.Position);
    }

    private static void ReadCrabs(BinaryReader r, WorldSnapshot into)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.Crabs, count);
        for (var i = 0; i < count; i++)
        {
            ReadCrabRecord(r, into.Crabs[i]);
        }
    }

    internal static void ReadCrabRecord(BinaryReader r, CrabSnapshot c)
    {
        c.Id = r.ReadInt32();
        c.Tile = WireIo.ReadTile(r);
        c.Position = WireIo.ReadFloat2(r);
    }

    private static void WriteDeathRecords(WorldSnapshot snapshot, BinaryWriter w)
    {
        var items = snapshot.DeathRecords;
        w.Write(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            WriteDeathRecordRecord(w, items[i]);
        }
    }

    internal static void WriteDeathRecordRecord(BinaryWriter w, DeathRecordSnapshot d)
    {
        w.Write(d.EntityId);
        WireIo.WriteString(w, d.DisplayName);
        w.Write(d.Tick);
        WireIo.WriteTile(w, d.Tile);
        WireIo.WriteString(w, d.Cause);
    }

    private static void ReadDeathRecords(BinaryReader r, WorldSnapshot into)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.DeathRecords, count);
        for (var i = 0; i < count; i++)
        {
            ReadDeathRecordRecord(r, into.DeathRecords[i]);
        }
    }

    internal static void ReadDeathRecordRecord(BinaryReader r, DeathRecordSnapshot d)
    {
        d.EntityId = r.ReadInt32();
        d.DisplayName = r.ReadString();
        d.Tick = r.ReadInt32();
        d.Tile = WireIo.ReadTile(r);
        d.Cause = r.ReadString();
    }

    // ── §136: дневники ──────────────────────────────────────────────────────
    // Своя секция, потому что меняется раз в игровой час, а запись NPC — каждый
    // тик; подробности в комментарии к WorldSnapshot.Journals.

    private static void WriteJournals(WorldSnapshot snapshot, BinaryWriter w)
    {
        var items = snapshot.Journals;
        w.Write(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            WriteJournalRecord(w, items[i]);
        }
    }

    internal static void WriteJournalRecord(BinaryWriter w, NpcJournalSnapshot j)
    {
        w.Write(j.NpcId);
        w.Write(j.Entries.Count);
        for (var i = 0; i < j.Entries.Count; i++)
        {
            var e = j.Entries[i];
            w.Write(e.Tick);
            WireIo.WriteString(w, e.Type);
            w.Write((byte)e.Register);
            w.Write((byte)e.Bond);
            w.Write((byte)e.Perspective);
            WireIo.WriteString(w, e.SubjectNameId);
            WireIo.WriteString(w, e.Extra);
            w.Write((byte)e.Variant);
            w.Write((byte)e.QuietHours);
            WireIo.WriteString(w, e.Chore0);
            WireIo.WriteString(w, e.Chore1);
            WireIo.WriteString(w, e.Chore2);
        }
    }

    private static void ReadJournals(BinaryReader r, WorldSnapshot into)
    {
        var count = r.ReadInt32();
        WireIo.Resize(into.Journals, count);
        for (var i = 0; i < count; i++)
        {
            ReadJournalRecord(r, into.Journals[i]);
        }
    }

    internal static void ReadJournalRecord(BinaryReader r, NpcJournalSnapshot j)
    {
        j.NpcId = r.ReadInt32();
        var count = r.ReadInt32();
        WireIo.Resize(j.Entries, count);
        for (var i = 0; i < count; i++)
        {
            var e = j.Entries[i];
            e.Tick = r.ReadInt32();
            e.Type = r.ReadString();
            e.Register = r.ReadByte();
            e.Bond = r.ReadByte();
            e.Perspective = r.ReadByte();
            e.SubjectNameId = r.ReadString();
            e.Extra = r.ReadString();
            e.Variant = r.ReadByte();
            e.QuietHours = r.ReadByte();
            e.Chore0 = r.ReadString();
            e.Chore1 = r.ReadString();
            e.Chore2 = r.ReadString();
        }
    }
}

}

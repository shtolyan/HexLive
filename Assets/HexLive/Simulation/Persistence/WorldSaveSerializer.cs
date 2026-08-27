using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Journal;
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
//   on load, rebuilt on first pathfind),
// - Mind scene-of-§81 fields (AbuseBeat/AbuseBlows/SceneBlowsPlanned/
//   SceneBlowSpacingClips/AbuseVerdictTick/SceneStartHealth/
//   SceneLastBlowRestTick/ForcedMeleeWeaponId) — a scene lasts ≤60 ticks and
//   never survives a save; SaveGoal already folds the Abuse goal itself to None.
public static class WorldSaveSerializer
{
    // v16 was §71 Breath; §72 lands on top of it, so the faction fields are a
    // SEPARATE version — two features that each bumped to 16 on their own
    // branch describe two different formats, and a reader must be able to tell
    // "has breath" from "has breath AND factions".
    // v19 (§76) follows the same discipline: appended after the v18 strings,
    // never inserted mid-record, and read behind its own gate.
    // v20 (§28.15C v3): тела. Смерть больше не удаляет колонистку — её NPCState
    // целиком переезжает в Entities.Corpses и лежит там до конца игры, вместе с
    // одеждой и карманами. Значит сейв обязан нести ВТОРОЙ список людей: без
    // него загруженный мир — это остров, с которого мёртвые исчезли вместе со
    // своими вещами, то есть ровно то, от чего уходили.
    // v23 (§105): умирание переживает сохранение — причина, остаток запаса и
    // окно штрафа «едва живая». Иначе перезагрузка «лечила» бы лежащую на
    // грани, ровно как когда-то чинила культю (§50).
    // v24 (§85): цвет глаз. Отдельным полем, потому что до §85 он ехал внутри
    // SkinSet — материалы актрисы несли и её глаза тоже.
    // v26 (§81.10): понурая походка. Стала состоянием сима (режет скорость
    // вдвое), а была таймером вида — и потому обнулялась перезагрузкой молча.
    // v27 (§72.14): число уже высаженных трёхдневных волн. Без него убитая
    // волна возвращалась бы после загрузки, если выводить прогресс из ростера.
    // v28 (§116): критическая глубина, типы ран, шины, протезы и взаимные
    // ссылки переноски. Расширение добавлено в хвост NPC-записи.
    // v29 (§30): незавершённый человеческий замах и выбранная часть тела.
    // Два legacy-слота mercy остаются в binary layout как false/0.
    // v30 (§119): workbench bills, persistent craft projects and aid pledges.
    // v31 (§121): ручное управление. В блоб едет ОДИН флаг — под чьим
    // управлением персонаж; недоигранный приказ едет сам собой, потому что
    // план и исполнение сериализуются целиком и после загрузки просто
    // продолжаются. (Ветка §121 приехала со своим v28 — номер занят §116,
    // поэтому поле переехало в хвост под v31.)
    // v32 (§120 constructor): persistent per-building architecture elements.
    // v33: canonical Bay_00 geometry and topology come from the same persisted
    // door element; v32 hut slots are migrated from the mirrored prototype.
    // v34: every constructor piece is a top-level WorldObject linked to its
    // footprint aggregate by ArchitectureOwnerId.
    // v36 (§126): черты характера — одна битовая маска в хвост NPC-записи.
    // Блоб v35 и старше черт не знает, поэтому при чтении их ВЫВОДЯТ из
    // фракции: до §126 «абьюзер» и «неряха» и были синонимами Faction !=
    // Colony, так что чужак из старого сейва обязан очнуться собой, а не
    // безобидным. Без этой миграции загрузка тихо разоружила бы врага.
    // v37 (§120.2): architectural door open/closed state on each LEGO piece.
    // v39 (§121.1): unfinished manual movement plans retain Walk/Run pace.
    // v40 (§132): processed weekly colony-arrival boundaries. Without this
    // cursor a loaded full camp would accumulate a hidden replacement backlog.
    // v41 (§40.8-H r10): накопительная кровяная подложка per-zone (BloodSoil)
    // в хвост NPC-записи. Старый блоб читается чистым — прежние спеклы были
    // производной от HP и в сейве не жили.
    // v42 (§133): у каждой вещи есть владелец (ItemInstance.OwnerId), зеркало
    // WorldObjectState.Owner по ту сторону границы «надето ↔ лежит». Старый блоб
    // читается так: НАДЕТОЕ принадлежит носительнице (что на ней — её), а
    // носимое в карманах и лежащее на земле остаётся ничейным и обретает
    // владельца при первом надевании. Иначе пришлось бы гадать, какая палка в
    // рюкзаке «чья», а ничейное — ровно то состояние, которое умеет claim.
    // v43 (§135): добыча в зубах зверя — чья конечность, какая, срок еды и
    // сытость. Объекта `body.limb_severed` в мире на это время НЕТ (его забрали
    // с земли), так что без этих полей загрузка молча уничтожала бы ногу вместе
    // со сценой; старый блоб читается «пасть пуста, зверь не сыт».
    // v45 (§136): дневник колонистки — кольцо закрытых записей на каждую NPC.
    // Старый блоб читается «дневник пуст», и это не потеря: до v45 его никто не
    // вёл, а первая запись появится через игровой час. Кандидат текущего часа
    // не пишется намеренно — он черновик, а не состояние мира.
    // 47 (§118.2): у раны появился флаг Plastered — пластырь заклеивает ОДНУ
    // рану, в отличие от бинта на всю зону. Читается под гейтом версии, поэтому
    // сейвы 46 и старше грузятся как раньше (в них пластырей просто нет).
    // v48 (§40.6 r13): persistent phase и настоящий берег отличают общую
    // стирку, купание и финальное переодевание одной personal-care транзакции.
    // v49 (§35.4 r2, #167): у тайла появился флаг Roofed. Indoor остался
    // санктуарием — им размечены двор колонии и стоянка чужака, где крыши нет,
    // — а солнце и тень теперь смотрят только на перекрытие. Старый блоб
    // читается «крыша там, где есть настил».
    // v50 (§120.8): произвольные чертежи игрока. Реестр
    // WorldState.PlayerBlueprints едет JSON'ом собственного формата чертежа
    // (BuildingBlueprintJson — тот же, что в команде и в редакторе), а каждый
    // объект несёт BlueprintId (0 = встроенный committed-план). Без реестра
    // загрузчик переписал бы геометрию слотов произвольного дома из
    // committed-плана — молча и навсегда.
    // v51 (§146.2): режим мира + ревизия генератора большого острова сразу за
    // сидом. Проверяются как сид: блоб, записанный на чужой геометрии (другой
    // режим или выросшая карта BigIsland), отклоняется, а не применяется молча
    // на несуществующие тайлы. Блобы ≤50 — всегда Feud.
    // v52 (§146.6): недельные курсоры прибытий по лагерям — словарь в хвосте;
    // старое одиночное поле v40 остаётся и читается в шим (запись Colony).
    // v53 (§147.1): патрульные слоты виртуальных зверей вместе с испечёнными
    // кольцами — урок §46 v4: всё состояние слота в сейве, ничего не
    // выводится из сида задним числом. Блоб ≤52 читает пустой список, и
    // ленивая генерация отстраивает слоты заново, если ручки включены.
    // v55 (§133.9, #193): сохраняемый запрет смены одежды per NPC.
    // v56 (§127): парная сцена, её cooldown и смываемый IntimacySoil.
    // v57 (§133.10): выбранный комплект и точные ObjectId снятых вещей. Без
    // этого загрузка посреди сушки оставляла закреплённого NPC голым навсегда.
    // v59 (#218): последний прямой социальный контакт в каждой направленной
    // записи отношений; старые записи получают 0 и стабильный fallback-порядок.
    public const int BlobVersion = 59;
    private const int OldestReadableBlobVersion = 3;

    private const int EndMarker = unchecked((int)0x454E4421); // "END!"

    private static readonly BodyPart[] BodyPartOrder =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    public static void Write(WorldState world, BinaryWriter w)
    {
        w.Write(BlobVersion);
        w.Write(world.Seed);
        // §146.2 (v51): режим и ревизия worldgen'а — вторая половина ключа
        // «на какой геометрии написан этот блоб» (первая — сид).
        w.Write((int)world.Mode);
        w.Write(world.Mode switch
        {
            Bootstrap.GameMode.BigIsland =>
                Bootstrap.PrototypeWorldDefinitionFactory.BigIslandWorldGenRevision,
            Bootstrap.GameMode.HugeIsland =>
                Bootstrap.PrototypeWorldDefinitionFactory.HugeIslandWorldGenRevision,
            Bootstrap.GameMode.Maniac =>
                Bootstrap.PrototypeWorldDefinitionFactory.HugeIslandWorldGenRevision,
            _ => 0
        });
        w.Write(world.Tick);
        w.Write(world.NextRuntimeObjectId);
        w.Write(world.RaftProgress);
        w.Write(world.Completed);
        w.Write(world.CampfireDreamDone); // §64: the campfire-dream latch survives a reload
        w.Write(world.DeathRecords.Count);
        foreach (var death in world.DeathRecords)
        {
            WriteDeathRecord(w, death);
        }

        // §72: the per-faction camp anchors. Authored at bootstrap, never
        // derived, so they must survive a reload.
        w.Write(world.FactionHomes.Count);
        foreach (var pair in world.FactionHomes)
        {
            w.Write((int)pair.Key);
            WriteTile(w, pair.Value);
        }

        w.Write(world.ColonyInDireStraits);
        w.Write(world.NextMobId);
        w.Write(world.NextRabbitId);
        w.Write(world.NextMobSpawnCheckTick);
        w.Write(world.NextRabbitSpawnCheckTick);
        w.Write(world.TopologyVersion);
        w.Write(world.RaidWavesSpawned); // §72.14, v27
        w.Write(world.ColonyArrivalsProcessed); // §132, v40

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

        // §148: разведанные гексы. Без них перезапуск возвращал бы игроку
        // чёрный остров, уже пройденный ногами колонии, — а разведка это
        // достижение партии, ровно как построенный дом.
        w.Write(world.ExploredTiles.Count);
        foreach (var coord in world.ExploredTiles)
        {
            WriteTile(w, coord);
        }

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

        // §28.15C v3 (v20): тела — тем же куском записи, что и живые. Читатель
        // раскладывает их в ДРУГОЙ реестр, и это единственная разница: труп
        // отличается от живой не набором полей, а тем, кто его тикает.
        w.Write(world.Entities.Corpses.Count);
        foreach (var body in world.Entities.Corpses.Values)
        {
            WriteNpc(w, body);
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
            w.Write(dog.LeavesAtTick); // v38: гость рейда остаётся гостем
            // v43 (§135): добыча в зубах. Объекта конечности в мире на это
            // время нет — он живёт здесь, и без записи загрузка стирала бы его
            // насовсем вместе с отходом и сытостью.
            WriteNullableEntity(w, dog.CarriedLimbOwner);
            w.Write(dog.CarriedLimbPart ?? string.Empty);
            w.Write(dog.LimbTakenAtTick);
            w.Write(dog.LimbEatenAtTick);
            w.Write(dog.SatedUntilTick);
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

        // Keep the removed water-predator section as an empty legacy slot so
        // existing saves retain the byte layout of every section after it.
        w.Write(0);

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

        // v50 (§120.8): реестр произвольных чертежей — тем же JSON, что и
        // команда/редактор, чтобы формат чертежа жил ровно в одном месте.
        w.Write(world.NextPlayerBlueprintId);
        w.Write(world.PlayerBlueprints.Count);
        foreach (var pair in world.PlayerBlueprints)
        {
            w.Write(pair.Key);
            w.Write(Runtime.Blueprints.BuildingBlueprintJson.Serialize(
                pair.Value, pretty: false));
        }

        // §146.6 (v52): недельные курсоры прибытий ПО ЛАГЕРЯМ. Colony уехала
        // выше в старом одиночном поле (v40, через шим) — здесь весь словарь,
        // отсортированный по ординалу, чтобы байты сейва не зависели от
        // порядка словаря.
        var arrivalCamps = new List<Agents.Faction>(
            world.ColonyArrivalsProcessedByFaction.Keys);
        arrivalCamps.Sort((a, b) => ((int)a).CompareTo((int)b));
        w.Write(arrivalCamps.Count);
        foreach (var faction in arrivalCamps)
        {
            w.Write((int)faction);
            w.Write(world.ColonyArrivalsProcessedByFaction[faction]);
        }

        // §147.1 (v53): патрульные слоты — целиком, с кольцами.
        w.Write(world.MobSpawnSlots.Count);
        foreach (var slot in world.MobSpawnSlots)
        {
            w.Write(slot.SlotId);
            w.Write(slot.MobId ?? string.Empty);
            w.Write((int)slot.State);
            w.Write(slot.ReservedMobId);
            w.Write(slot.HomeJunction.Value);
            w.Write(slot.CooldownUntilTick);
            w.Write(slot.StoredHealth);
            w.Write(slot.CycleIndex);
            w.Write(slot.Ring.Count);
            foreach (var waypoint in slot.Ring)
            {
                w.Write(waypoint.Junction.Value);
                w.Write(waypoint.Tile.Q);
                w.Write(waypoint.Tile.R);
                w.Write(waypoint.Position.X);
                w.Write(waypoint.Position.Y);
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

        // §146.2 (v51): блоб обязан лечь на worldgen СВОЕГО режима и своей
        // ревизии. Блобы ≤50 писались до режимов — они всегда Feud.
        var savedMode = version >= 51 ? (Bootstrap.GameMode)r.ReadInt32() : Bootstrap.GameMode.Feud;
        if (savedMode != world.Mode)
        {
            throw new InvalidDataException(
                $"Save mode {savedMode} does not match world mode {world.Mode}.");
        }

        if (version >= 51)
        {
            var revision = r.ReadInt32();
            var expected = world.Mode switch
            {
                Bootstrap.GameMode.BigIsland =>
                    Bootstrap.PrototypeWorldDefinitionFactory.BigIslandWorldGenRevision,
                Bootstrap.GameMode.HugeIsland =>
                    Bootstrap.PrototypeWorldDefinitionFactory.HugeIslandWorldGenRevision,
                Bootstrap.GameMode.Maniac =>
                    Bootstrap.PrototypeWorldDefinitionFactory.HugeIslandWorldGenRevision,
                _ => 0
            };
            if (revision != expected)
            {
                throw new InvalidDataException(
                    $"Save worldgen revision {revision}, this build generates {expected}.");
            }
        }

        world.Tick = r.ReadInt32();
        world.NextRuntimeObjectId = r.ReadInt32();
        world.RaftProgress = r.ReadInt32();
        world.Completed = version >= 4
            ? r.ReadBoolean()
            : world.RaftProgress >= WorldState.RaftTarget;
        world.CampfireDreamDone = version >= 11 && r.ReadBoolean(); // §64
        world.DeathRecords.Clear();
        if (version >= 4)
        {
            var deathCount = r.ReadInt32();
            for (var i = 0; i < deathCount; i++)
            {
                world.DeathRecords.Add(ReadDeathRecord(r));
            }
        }

        // §72: camp anchors. A pre-v17 save has none — WorldStateFactory has
        // already seeded them from the same bootstrap definition, so leaving
        // the factory's values in place is exactly right.
        if (version >= 17)
        {
            world.FactionHomes.Clear();
            var homeCount = r.ReadInt32();
            for (var i = 0; i < homeCount; i++)
            {
                var faction = (Faction)r.ReadInt32();
                world.FactionHomes[faction] = ReadTile(r);
            }
        }

        world.ColonyInDireStraits = r.ReadBoolean();
        world.NextMobId = r.ReadInt32();
        world.NextRabbitId = r.ReadInt32();
        world.NextMobSpawnCheckTick = r.ReadInt32();
        world.NextRabbitSpawnCheckTick = r.ReadInt32();
        world.TopologyVersion = r.ReadInt32();
        // An old save never had recurring waves. Adopt the elapsed boundaries
        // as already handled so loading on day 30 does not dump ten attackers
        // into the camp at once; the next boundary proceeds normally.
        world.RaidWavesSpawned = version >= 27
            ? r.ReadInt32()
            // Same boundary formula as RaidWaveSystem (calendar days), or the
            // adoption would mark a different set of waves as already handled.
            : HexLive.Simulation.Runtime.Spec72.RaidWaveIntervalDays > 0
                ? HexLive.Simulation.Runtime.EnvironmentSystem.CalendarDay(world.Tick) /
                  HexLive.Simulation.Runtime.Spec72.RaidWaveIntervalDays
                : 0;
        // Old saves adopt elapsed weekly boundaries as already handled: a day
        // 50 colony must not receive seven women at once merely because the
        // feature (and its cursor) did not exist when that save was written.
        world.ColonyArrivalsProcessed = version >= 40
            ? r.ReadInt32()
            : HexLive.Simulation.Runtime.WorldBalance.ColonyArrivalIntervalDays > 0
                ? HexLive.Simulation.Runtime.EnvironmentSystem.CalendarDay(world.Tick) /
                  HexLive.Simulation.Runtime.WorldBalance.ColonyArrivalIntervalDays
                : 0;

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

            // §35.4 r2 (v49): у крыши появился собственный флаг. В старом сейве
            // его нет, а достроенный дом узнаётся по настилу: HasFloor ставится
            // ровно там, где легло перекрытие. Санктуарные тайлы без постройки
            // (двор колонии, стоянка чужака) настила не имеют и крышу не
            // получают — ради этого различия всё и затевалось.
            if (version < 49 && flags.HasFlag(TileFlags.HasFloor))
            {
                flags |= TileFlags.Roofed;
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

        // §148: разведанное. Старый сейв (до v54) его не нёс — тогда остров
        // открывается заново, но ходить по нему уже некуда торопиться.
        world.ExploredTiles.Clear();
        if (version >= 54)
        {
            var exploredCount = r.ReadInt32();
            for (var i = 0; i < exploredCount; i++)
            {
                world.ExploredTiles.Add(ReadTile(r));
            }
        }

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

        // §28.15C v3: тела. У сейва до v20 список пуст — там мёртвых не было как
        // сущностей вовсе, они истлевали в объект и исчезали.
        world.Entities.Corpses.Clear();
        if (version >= 20)
        {
            var corpseCount = r.ReadInt32();
            for (var i = 0; i < corpseCount; i++)
            {
                var body = ReadNpc(r, version);
                world.Entities.Corpses[body.Id] = body;
            }
        }

        RepairCarryLinks(world);

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
            // v38: срок ухода гостя рейда. Блобы до v38 его не знают — там
            // все собаки читаются жителями, и лишних разберёт правило потолка
            // в MobSystem.EnforceResidentCap (ровно так чинится сейв, в
            // котором стая накопилась по старому багу).
            dog.LeavesAtTick = version >= 38 ? r.ReadInt32() : 0;
            // v43 (§135): добыча в зубах. Блобы до v43 её не знают — там зверь
            // читается с пустой пастью и несытым, что и есть правда старого мира.
            if (version >= 43)
            {
                dog.CarriedLimbOwner = ReadNullableEntity(r);
                dog.CarriedLimbPart = r.ReadString();
                dog.LimbTakenAtTick = r.ReadInt32();
                dog.LimbEatenAtTick = r.ReadInt32();
                dog.SatedUntilTick = r.ReadInt32();
            }
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

        // Removed water-predator records are consumed and discarded solely for
        // backward-compatible loading of existing saves.
        var removedPredatorCount = r.ReadInt32();
        for (var i = 0; i < removedPredatorCount; i++)
        {
            _ = r.ReadInt32();
            _ = ReadTile(r);
            _ = r.ReadInt32();
            _ = ReadFloat2(r);
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

        // v50 (§120.8): реестр чертежей читается ДО MigrateRetiredContent —
        // именно миграция переписывает геометрию слотов plan-зданий, и делать
        // это она обязана уже по чертежу площадки, не по committed-плану.
        if (version >= 50)
        {
            world.NextPlayerBlueprintId = r.ReadInt32();
            var blueprintCount = r.ReadInt32();
            world.PlayerBlueprints.Clear();
            for (var i = 0; i < blueprintCount; i++)
            {
                var id = r.ReadInt32();
                var json = r.ReadString();
                if (!Runtime.Blueprints.BuildingBlueprintJson.TryDeserialize(
                        json, out var draft, out var error))
                {
                    throw new InvalidDataException(
                        $"Save blob player blueprint {id} is unreadable: {error}");
                }

                world.PlayerBlueprints[id] = draft;
            }
        }

        // §146.6 (v52): по-лагерные курсоры прибытий. Старый int (v40) уже
        // прочитан выше в шим (ляжет в Colony); словарь целиком его перекрывает.
        if (version >= 52)
        {
            world.ColonyArrivalsProcessedByFaction.Clear();
            var arrivalCampCount = r.ReadInt32();
            for (var i = 0; i < arrivalCampCount; i++)
            {
                var faction = (Agents.Faction)r.ReadInt32();
                world.ColonyArrivalsProcessedByFaction[faction] = r.ReadInt32();
            }
        }

        // §147.1 (v53): патрульные слоты с кольцами.
        world.MobSpawnSlots.Clear();
        if (version >= 53)
        {
            var slotCount = r.ReadInt32();
            for (var i = 0; i < slotCount; i++)
            {
                var slot = new Wildlife.MobSpawnSlot
                {
                    SlotId = r.ReadInt32(),
                    MobId = r.ReadString(),
                    State = (Wildlife.MobSlotState)r.ReadInt32(),
                    ReservedMobId = r.ReadInt32(),
                    HomeJunction = new JunctionId(r.ReadInt32()),
                    CooldownUntilTick = r.ReadInt32(),
                    StoredHealth = r.ReadSingle(),
                    CycleIndex = r.ReadInt32(),
                };
                var waypoints = r.ReadInt32();
                for (var j = 0; j < waypoints; j++)
                {
                    slot.Ring.Add(new Wildlife.PatrolWaypoint
                    {
                        Junction = new JunctionId(r.ReadInt32()),
                        Tile = new TileCoord(r.ReadInt32(), r.ReadInt32()),
                        Position = new Float2(r.ReadSingle(), r.ReadSingle()),
                    });
                }

                world.MobSpawnSlots.Add(slot);
            }
        }

        if (r.ReadInt32() != EndMarker)
        {
            throw new InvalidDataException("Save blob end marker missing — truncated or corrupt save.");
        }

        MigrateRetiredContent(world, version);
    }

    // Save migration: content retired from the bootstrap still lives inside
    // older saves' entity lists — despawn it on load or the girls keep using
    // ghosts (e.g. the water.pond anchors: removed from the world, invisible
    // to the renderer, yet loaded NPCs kept hiking to them for water).
    private static void MigrateRetiredContent(WorldState world, int loadedVersion)
    {
        // v32-v33 stored all LEGO components inside one building object. Explode
        // them exactly once into normal world objects before spatial repair.
        BuildingRules.MaterializeLegacyElements(world);
        var retired = new List<ObjectId>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.HutBed)
            {
                obj.DefinitionId = ContentIds.BedBasic;
                obj.Variant = ContentIds.HutBedVariant;
            }
            else if (obj.DefinitionId == ContentIds.BedLeaf)
            {
                obj.DefinitionId = ContentIds.BedBasic;
            }

            if (obj.BuildProduct is ContentIds.BedLeaf or ContentIds.HutBed)
            {
                obj.BuildProduct = ContentIds.BedBasic;
                obj.BillLogs = System.Math.Max(obj.BillLogs, SimBalance.BedBasicBillLogs);
                obj.BillSticks = System.Math.Max(obj.BillSticks, SimBalance.BedBasicBillSticks);
                obj.BillRope = System.Math.Max(obj.BillRope, SimBalance.BedBasicBillRope);
                obj.BillLeaves = System.Math.Max(obj.BillLeaves, SimBalance.BedBasicBillLeaves);
            }

            if (obj.DefinitionId is "water.pond" or "armor.leather" or "armor.heavy")
            {
                retired.Add(obj.Id);
            }
        }

        foreach (var id in retired)
        {
            WorldObjectMutations.DespawnObject(world, id);
        }

        // Migration is spatial too: both legacy hut cots could retain one
        // anchor and render collapsed after becoming bed.basic.
        HexLive.Simulation.Bootstrap.BuildingBootstrap.RepairIntegratedCotAnchors(world);
        // §120: a committed player plan re-derives its own topology from the
        // modules it actually raised. Both shapes are repaired — the unbuilt
        // site (walls half up, doorway already open) and the finished building —
        // because junction Blocked/Door flags are worldgen state the loader
        // rebuilds, not save data.
        var plansToRepair = new List<WorldObjectState>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.HutPlan || obj.BuildProduct == ContentIds.HutPlan)
                plansToRepair.Add(obj);
        }
        foreach (var plan in plansToRepair)
        {
            if (plan.DefinitionId == ContentIds.HutPlan)
                BuildingRules.ReconcileCompletedHutElements(world, plan);
            else
                BuildingRules.RefreshHutElementGeometry(world, plan);
            HexLive.Simulation.Bootstrap.BuildingBootstrap.RepairPlanTopology(world, plan);
            if (plan.DefinitionId == ContentIds.HutPlan)
                HexLive.Simulation.Bootstrap.BuildingBootstrap.StakePlanFurnitureSites(world, plan);
        }

        var hutsToRepair = new List<WorldObjectState>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.Hut1Hex) hutsToRepair.Add(obj);
        }
        foreach (var hut in hutsToRepair)
        {
            // Slot geometry belongs to the current blueprint catalog, not
            // to instance save data. A v33 save made by a broken build can
            // otherwise preserve the wrong door forever merely because its
            // version already equals the reader's version.
            BuildingRules.RefreshHutElementGeometry(world, hut);
            HexLive.Simulation.Bootstrap.BuildingBootstrap.RepairHutTopology(world, hut);
            HexLive.Simulation.Bootstrap.BuildingBootstrap.RepairIntegratedHearthAnchor(world, hut);
            // §133: гардероб — новая мебель, поэтому в домах из старых сейвов
            // его нет; та же функция и ставит его впервые, и пересаживает.
            HexLive.Simulation.Bootstrap.BuildingBootstrap.RepairWardrobeAnchor(world, hut);
        }

        // §120.10: direct architecture has no blueprint owner to appear in the
        // two repair loops above. Its absolute SlotKeys and per-piece bills are
        // already saved; rebuild derived geometry, roof gates and topology.
        Runtime.Blueprints.FreeArchitectureRules.RefreshAll(world);

        MigrateRetiredGarments(world, world.Entities.Npcs.Values);
        MigrateRetiredGarments(world, world.Entities.Corpses.Values);
    }

    // These armor ids never had shipping art or localization. Strip them from
    // old saves as well as new content, otherwise a saved wearer remains an
    // invisible, overpowered ghost item after the catalog has retired it.
    private static void MigrateRetiredGarments(
        WorldState world,
        IEnumerable<NPCState> npcs)
    {
        foreach (var npc in npcs)
        {
            npc.WornItems.RemoveAll(IsRetiredGarment);
            npc.Inventory.Items.RemoveAll(IsRetiredGarment);
            npc.Mind.RedressGarments.RemoveAll(id => !world.Entities.Objects.ContainsKey(id));
            // §52.9 / баг #124: сейвы, сделанные до сужения стартового пула,
            // несут пару, дерущуюся за один (слой, слот) — вид пересобирал обе
            // каждый тик вечно. Чинить надо и старый мир, а не только новый:
            // проигравшая уходит в рюкзак, надетой остаётся первая.
            EquipmentMath.StripConflictingWorn(world, npc);
            EquipmentMath.Recalculate(world, npc);
        }
    }

    private static bool IsRetiredGarment(ItemInstance item) =>
        item.DefinitionId is "armor.leather" or "armor.heavy";

    private static bool IsRetiredGarment(string definitionId) =>
        definitionId is "armor.leather" or "armor.heavy";

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

        WriteNullableEntity(w, obj.Owner); // §64: personal-bed ownership

        // v12: build-site payload — WHAT the site becomes, its material bill and
        // the materials already hauled in (Contents). Without this an in-progress
        // build (a half-raised bed) lost its BuildProduct on reload, became an
        // empty "build.site", and BedSiteSystem swept it as cruft — so a bed
        // could never survive a save/load, let alone finish (see §52/§54.2).
        w.Write(obj.BuildProduct);
        w.Write(obj.BillLogs);
        w.Write(obj.BillStones);
        w.Write(obj.BillLeaves);
        w.Write(obj.BillSticks);
        w.Write(obj.BillRope);
        WriteItemList(w, obj.Contents);

        // v13 (§66): the yaw a built piece stands at. Pre-v13 saves read 0 —
        // an old world's furniture keeps facing exactly where it always did.
        w.Write(obj.RotationDegrees);

        // v30 (§119), append-only object extension.
        w.Write(obj.BillBoards);
        WriteNullableJunction(w, obj.CraftJunction);
        w.Write(obj.CraftWorkRequired);
        w.Write(obj.CraftWorkDone);
        w.Write(obj.CraftBatchCount);
        WriteNullableObject(w, obj.CraftStationObjectId);

        w.Write(obj.ArchitectureElements.Count);
        foreach (var element in obj.ArchitectureElements)
        {
            w.Write(element.ElementId);
            w.Write(element.DefinitionId);
            w.Write(element.SlotKey);
            w.Write(element.SlotIndex);
            w.Write((int)element.Layer);
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
        }
        WriteNullableObject(w, obj.ArchitectureOwnerId);
        w.Write(obj.IsDoorOpen);

        // v50 (§120.8): чей чертёж строит этот plan-объект (0 = committed).
        w.Write(obj.BlueprintId);
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
        if (version == 7)
        {
            obj.Dirtiness = MathUtil.Clamp01(obj.Dirtiness + obj.Bloodiness);
        }
        obj.SpawnTick = r.ReadInt32();
        ReadJunctionList(r, obj.BlockedJunctions);
        obj.NextProductionTick = r.ReadInt32();
        var producedCount = r.ReadInt32();
        for (var i = 0; i < producedCount; i++)
        {
            obj.ProducedItems.Add(new ObjectId(r.ReadInt32()));
        }

        obj.Owner = version >= 11 ? ReadNullableEntity(r) : null; // §64

        // v12: build-site payload (product + bill + delivered materials). Pre-v12
        // saves never stored it, so those in-progress sites still load empty and
        // BedSiteSystem sweeps them (the old behaviour) — only NEW builds persist.
        if (version >= 12)
        {
            obj.BuildProduct = r.ReadString();
            obj.BillLogs = r.ReadInt32();
            obj.BillStones = r.ReadInt32();
            obj.BillLeaves = r.ReadInt32();
            obj.BillSticks = r.ReadInt32();
            obj.BillRope = r.ReadInt32();
            ReadItemList(r, obj.Contents, version);
        }

        // v13 (§66): built-piece yaw; absent before v13 ⇒ 0 (old placement).
        obj.RotationDegrees = version >= 13 ? r.ReadSingle() : 0f;

        if (version >= 30)
        {
            obj.BillBoards = r.ReadInt32();
            obj.CraftJunction = ReadNullableJunction(r);
            obj.CraftWorkRequired = r.ReadInt32();
            obj.CraftWorkDone = r.ReadInt32();
            obj.CraftBatchCount = r.ReadInt32();
            obj.CraftStationObjectId = ReadNullableObject(r);
        }

        if (version >= 32)
        {
            var architectureCount = r.ReadInt32();
            for (var i = 0; i < architectureCount; i++)
            {
                obj.ArchitectureElements.Add(new ArchitectureElementState
                {
                    ElementId = r.ReadInt32(),
                    DefinitionId = r.ReadString(),
                    SlotKey = r.ReadString(),
                    SlotIndex = r.ReadInt32(),
                    Layer = (PlacementLayer)r.ReadInt32(),
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
                    WorkDone = r.ReadInt32()
                });
            }
        }
        else if (Runtime.BuildSiteMath.IsArchitecturalBuilding(obj.BuildProduct) ||
                 BuildingRules.IsCompletedBuilding(obj))
        {
            // §120: both hut products, not just the canonical one — an
            // element-less plan site restored from an old blob would otherwise
            // come back with no modules at all.
            BuildingRules.EnsureHutElements(obj, completed: BuildingRules.IsCompletedBuilding(obj));
            if (Runtime.BuildSiteMath.IsArchitecturalBuilding(obj.BuildProduct))
                BuildingRules.SyncHutElements(obj);
        }

        obj.ArchitectureOwnerId = version >= 34 ? ReadNullableObject(r) : null;
        obj.IsDoorOpen = version >= 37 ? r.ReadBoolean() : true;
        obj.BlueprintId = version >= 50 ? r.ReadInt32() : 0;

        // Rotation is a placement contract, not decorative save data. Repair
        // legacy arbitrary/30-degree poses on every save version, including
        // current v32 files that already carry architecture elements.
        if (Runtime.BuildSiteMath.IsArchitecturalBuilding(obj.BuildProduct) ||
            BuildingRules.IsCompletedBuilding(obj))
        {
            obj.RotationDegrees = StructurePlacement.QuantizeHexSymmetryYaw(obj.RotationDegrees);
        }
        else if (UsesHexFurnitureYaw(obj.DefinitionId) ||
                 UsesHexFurnitureYaw(obj.BuildProduct))
        {
            obj.RotationDegrees = StructurePlacement.QuantizeHexYaw(obj.RotationDegrees);
        }

        return obj;
    }

    private static bool UsesHexFurnitureYaw(string definitionId) =>
        definitionId == ContentIds.BedBasic ||
        definitionId == ContentIds.Workbench ||
        definitionId == ContentIds.DryingRack ||
        definitionId == ContentIds.WaterCollector;

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
        w.Write(npc.BodyWetness);
        w.Write(npc.CompassionTrait);
        w.Write((int)npc.Faction); // §72: ordinal — the enum is APPEND-ONLY
        // §74: the rest of the composition. DisplayName/ActorMesh above have
        // been written since v3; these three join them so a girl keeps her
        // face, hair and voice across a reload even if the pools change later.
        w.Write(npc.SkinSet);
        w.Write(npc.Hairstyle);
        w.Write(npc.VoiceBank);

        // §76: who she is and what she has learned. Written by NAME, not as a
        // count-prefixed (kind, value) loop like Body.Parts — BodyPart predates
        // the save and is shared with content, whereas AttributeKind/SkillKind
        // are §76-private and fixed. A loop would buy forward-compat we don't
        // need at the price of a silent mis-read if either enum is reordered.
        w.Write(npc.Attributes.Strength);
        w.Write(npc.Attributes.Agility);
        w.Write(npc.Attributes.Endurance);
        w.Write(npc.Attributes.Toughness);
        w.Write(npc.Attributes.Hardiness);
        w.Write(npc.Attributes.Wits);
        w.Write(npc.Skills.Combat);
        w.Write(npc.Skills.Harvesting);
        w.Write(npc.Skills.Crafting);
        w.Write(npc.Skills.Building);
        w.Write(npc.Skills.Cooking);
        w.Write(npc.Skills.Medicine);
        w.Write(npc.Skills.Survival);
        w.Write(npc.Skills.Social);

        // §28.15C v3 (v20): каким клипом она упала. Пишется для КАЖДОГО тела,
        // живого и мёртвого, одним и тем же куском — тело мёртвой это тот же
        // NPCState, только в другом реестре, и раздваивать запись значило бы
        // завести второе место, где можно забыть поле.
        w.Write(npc.DeathAnimVariant);

        // §85 (v24): цвет глаз. В КОНЕЦ скалярной череды, а не рядом с SkinSet
        // выше, ровно по причине §74.7 — вставка в середину переписала бы
        // раскладку, которую уже читают сейвы v18-v23.
        w.Write(npc.EyeColor);

        WriteItemList(w, npc.WornItems);
        WriteJunctionList(w, npc.ClaimedJunctions);

        w.Write(npc.Body.Parts.Count);
        foreach (var pair in npc.Body.Parts)
        {
            w.Write((int)pair.Key);
            w.Write(pair.Value);
        }

        // §50: severed is a SET apart from part HP — a 0-HP mauled zone heals
        // back, a severed one is gone for good. Before v14 this set was not
        // saved, so a reloaded amputee regrew the limb and stood back up.
        w.Write(npc.Body.Severed.Count);
        foreach (var part in npc.Body.Severed)
        {
            w.Write((int)part);
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

        // §44: which zones wear a herbal leaf wrap vs a medkit gauze wrap.
        w.Write(npc.BandagedZones.Count);
        foreach (var zone in npc.BandagedZones)
        {
            w.Write((int)zone);
        }

        w.Write(npc.GauzeZones.Count);
        foreach (var zone in npc.GauzeZones)
        {
            w.Write((int)zone);
        }

        var needs = npc.Needs;
        w.Write(needs.Hunger);
        w.Write(needs.Thirst);
        w.Write(needs.Energy);
        w.Write(needs.Comfort);
        w.Write(needs.Social);
        w.Write(needs.Compassion); // v15, spec §53
        w.Write(needs.ThermalDiscomfort);
        w.Write(needs.ThermalComfort);
        w.Write(needs.Stamina);
        w.Write(needs.Breath); // v16, §71
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
        // §49 water sickness v2: the open 🤢 window and the torso's unpaid
        // damage budget. Unsaved (pre-v15), a reload cured the sickness.
        w.Write(mind.SickUntilTick);
        w.Write(mind.SicknessDamageRemaining);
        // §105 (v23): умирание переживает сохранение — иначе перезагрузка
        // «лечила» бы лежащую на грани, ровно как когда-то чинила культю.
        // DyingTickStamp НЕ пишется намеренно: он транзиентен, а сохранённая
        // пара «запас + старый штамп» подарила бы ей при загрузке целое окно
        // дрейна одним куском. Нулевой штамп значит «перештамповать».
        w.Write((int)mind.DyingCause);
        w.Write(mind.DyingReserve);
        w.Write(mind.ConvalescentUntilTick);
        // §110 (v25): слёзы переживают сохранение — иначе перезагрузка
        // мгновенно «утешала» бы лежащую и рыдающую.
        w.Write(mind.CryingUntilTick);
        // §105.14: PlayDeadUntilTick / PlayDeadSinceTick НЕ пишутся намеренно —
        // притворство транзиентно (как DyingTickStamp): после загрузки она
        // просто встанет, это безобидно, а блоб трогать не приходится.
        // §81.10 (v26): и понурая походка тоже — иначе загрузка выпрямляла бы
        // ей спину и возвращала полную скорость на середине минуты после сцены.
        w.Write(mind.SadWalkUntilTick);
        // §121 (v31): под чьим управлением персонаж. Единственное поле ручного
        // режима в блобе — цель приказа едет своим ходом (план сериализуется
        // целиком), а сцепка PlayerAttack складывается в SaveGoal.
        w.Write(mind.ManualControl);
        // §121.7 (v46 legacy): прежний потиковый штамп остаётся в блобе только
        // ради бинарной совместимости. Реальный lease процесса не сохраняется.
        w.Write(mind.LastManualInputTick);
        w.Write(mind.WakeGraceUntilTick);
        w.Write(mind.AdrenalineUntilTick);
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

        // v15: more mutable mind state that silently reset on load — the §35.4
        // overheat latch + its dwell re-arm counter, and the §40.6 mid-bathe
        // redress list (which EXACT shore garments to put back on, and where).
        w.Write(mind.IsOverheated);
        w.Write(mind.CoolRearmCount);
        w.Write(mind.RedressGarments.Count);
        foreach (var id in mind.RedressGarments)
        {
            w.Write(id.Value);
        }

        WriteNullableJunction(w, mind.RedressShore);
        // v48 / #147: the pile alone cannot say whether she still owes the
        // batch wash, the body bath, or only the final re-dress.
        w.Write((int)mind.PersonalCarePhase);
        WriteNullableJunction(w, mind.PersonalCareBathShore);

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
        w.Write(plan.RunRequested); // v39, §121.1
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
            w.Write(step.LaundryFromInventory); // v58, §40.6 r16
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

        // §111.13 (v44): чью станцию и какую держит. Цели Aid и LootHelpless
        // переживают сохранение, поэтому без этих двух полей все после загрузки
        // перебирали бы станции заново и держатель ног менялся — видимый скачок.
        WriteNullableEntity(w, execution.LyingStationTargetId);
        w.Write(execution.LyingStationSlot);

        // §gear-craft v2: the staged craft's laid-out ground items (inputs
        // during the work beat, the finished output during the take beat).
        w.Write(execution.CraftLayout.Count);
        foreach (var laid in execution.CraftLayout)
        {
            w.Write(laid.Value);
        }

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
            w.Write(pair.Value.LastInteractionTick);
        }

        w.Write(npc.Inventory.Capacity);
        WriteItemList(w, npc.Inventory.Items);

        // §116 / v28 append-only extension.
        w.Write(npc.Body.BloodDeficit);
        w.Write(BodyPartOrder.Length);
        foreach (var part in BodyPartOrder)
        {
            var condition = npc.Body.Condition(part);
            w.Write((int)part);
            w.Write(condition.BluntDamage);
            w.Write(condition.CriticalTrauma);
            w.Write(condition.SplintSupport);
            w.Write(condition.HitBias);
            w.Write(condition.HitBiasChangedTick);
            w.Write(condition.Prosthetic != null);
            if (condition.Prosthetic is { } prosthetic)
            {
                w.Write(prosthetic.DefinitionId ?? string.Empty);
                w.Write((int)prosthetic.Part);
                w.Write(prosthetic.Condition);
                w.Write(prosthetic.MaxCondition);
                w.Write(prosthetic.Function);
                w.Write(prosthetic.Mechanical);
            }
        }

        w.Write(npc.Wounds.Count);
        foreach (var wound in npc.Wounds)
        {
            w.Write(wound.Id);
            w.Write(wound.Clot01);
            w.Write(wound.Stabilized);
            w.Write(wound.BleedFactor);
            w.Write(wound.Plastered); // blob 47
        }

        WriteNullableEntity(w, npc.CarriedNpcId);
        WriteNullableEntity(w, npc.CarriedByNpcId);
        WriteNullableObject(w, npc.RescueDestinationObjectId);

        // §30 / v29: save/load during wind-up preserves the selected part.
        w.Write(npc.StrikeLandsAtTick);
        w.Write(npc.StrikeReadyAtTick);
        w.Write(npc.AttackAnimUntilTick);
        w.Write(npc.SwingStartTick);
        w.Write(npc.SwingStrikeIndex);
        WriteNullableEntity(w, npc.PendingHumanStrikeTargetId);
        w.Write((int)npc.PendingHumanStrikePart);
        w.Write(false); // retired v29 mercy flag; preserve blob layout
        w.Write(0f); // retired v29 kill-intent scalar; preserve blob layout
        WriteNullableEntity(w, npc.Mind.CombatOpponentNpcId);
        w.Write(npc.Mind.ForcedMeleeWeaponId != null);
        if (npc.Mind.ForcedMeleeWeaponId != null)
        {
            w.Write(npc.Mind.ForcedMeleeWeaponId);
        }

        // §119 / v30: the compassionate promise and current persistent project.
        WriteNullableEntity(w, npc.Mind.ProstheticAidTargetId);
        w.Write(npc.Mind.ProstheticAidPart.HasValue);
        if (npc.Mind.ProstheticAidPart.HasValue)
        {
            w.Write((int)npc.Mind.ProstheticAidPart.Value);
        }
        w.Write(npc.Mind.ProstheticAidRetryAfterTick);
        WriteNullableObject(w, npc.Execution.CraftProjectId);
        w.Write(npc.Execution.CraftCycleStartWork);

        // §125 / v35: седьмая характеристика — в конец записи, не рядом с
        // шестёркой v19 (append-only; вставка в середину сломала бы чтение
        // любого блоба v19..v34).
        w.Write(npc.Attributes.Perception);

        // §125.3: память последней встречи. Живые списки восприятия
        // пересобираются на первом же тике и не пишутся, а эта память — знание,
        // которое обязано пережить загрузку.
        w.Write(npc.Memory.KnownAgents.Count);
        foreach (var met in npc.Memory.KnownAgents.Values)
        {
            w.Write(met.Id.Value);
            w.Write((int)met.Faction);
            WriteTile(w, met.Tile);
            w.Write(met.LastSeenTick);
            // §125.7: вердикт последней встречи — им живёт помощь по памяти.
            WriteNullableJunction(w, met.Junction);
            w.Write(met.Suffering);
            w.Write((int)met.AidKind);
            w.Write(met.Helpless);
        }

        // §126 / v36: черты характера — ОДНО число, ординал каждой черты это
        // номер бита (TraitKind — append-only ровно поэтому). Список имён здесь
        // был бы двумя ошибками сразу: длина записи поехала бы от состава, а
        // переименование черты в коде молча потеряло бы её у всех сохранённых.
        w.Write(npc.Traits.Bits);

        // §40.8-H r10 / v41: кровяная подложка per-zone — в конец записи
        // (append-only). Порядок зон фиксирован BodyPartOrder, как в v28.
        w.Write(BodyPartOrder.Length);
        foreach (var part in BodyPartOrder)
        {
            w.Write((int)part);
            w.Write(npc.Body.Condition(part).BloodSoil);
            w.Write(npc.Body.Condition(part).IntimacySoil);
        }

        // §136 / v45: дневник. Пишется ТОЛЬКО кольцо закрытых записей —
        // недописанный кандидат текущего часа намеренно теряется: он не
        // состояние мира, а полминуты накопления, и восстанавливать его значило
        // бы хранить в сейве черновик.
        var entries = npc.Journal.Entries;
        w.Write(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            w.Write(entry.Tick);
            WriteNullableString(w, entry.Type);
            w.Write((byte)entry.Register);
            w.Write((byte)entry.Bond);
            w.Write((byte)entry.Perspective);
            WriteNullableString(w, entry.SubjectNameId);
            WriteNullableString(w, entry.Extra);
            w.Write(entry.Variant);
            w.Write(entry.QuietHours);
            WriteNullableString(w, entry.Chore0);
            WriteNullableString(w, entry.Chore1);
            WriteNullableString(w, entry.Chore2);
        }

        // §133.9 / v55: append-only хвост NPC-записи.
        w.Write(npc.Mind.OutfitLocked);

        // §127 / v56: paired scene must survive a save on either participant;
        // the leader's active plan owns completion and clears both mirrors.
        WriteNullableEntity(w, npc.Mind.RomancePartnerNpcId);
        WriteNullableEntity(w, npc.Mind.RomanceLeaderNpcId);
        WriteNullableString(w, npc.Mind.RomanceClipKey);
        w.Write(npc.Mind.RomanceForced);
        w.Write(npc.Mind.RomanceAnchorX);
        w.Write(npc.Mind.RomanceAnchorY);
        w.Write(npc.Mind.RomanceFacingDegrees);
        w.Write(npc.Mind.RomanceCooldownUntilTick);

        // §133.10 / v57: persistent selected outfit. Audit cadence and the
        // armed maintenance target are derived and intentionally not saved.
        w.Write(npc.Mind.DesiredOutfit.Count);
        foreach (var piece in npc.Mind.DesiredOutfit)
        {
            w.Write(piece.DefinitionId);
            WriteNullableObject(w, piece.GroundObjectId);
        }
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

        if (version >= 14)
        {
            npc.BodyWetness = r.ReadSingle();
            npc.CompassionTrait = r.ReadSingle();
        }

        // §72: absent before v17 — an old save is a single-faction world, which
        // is exactly what it was, so the field default (Colony) is the answer.
        if (version >= 17)
        {
            npc.Faction = (Faction)r.ReadInt32();
        }

        // §74: absent before v18 — a pre-§74 save is a world where the body's
        // own materials, its prefab hairstyle and its mesh-named voice bank
        // WERE the look, and empty means exactly that. So an old colony still
        // loads as Marta/Molly/Jana/Jolly, unchanged.
        if (version >= 18)
        {
            npc.SkinSet = r.ReadString();
            npc.Hairstyle = r.ReadString();
            npc.VoiceBank = r.ReadString();
        }

        // §76: absent before v19 — a pre-§76 save is a colony of average bodies
        // with no trade learned, and the field defaults (0.5 attributes, 0
        // skills) say exactly that: every multiplier at 1.0, i.e. the game the
        // save was written by.
        if (version >= 19)
        {
            npc.Attributes.Strength = r.ReadSingle();
            npc.Attributes.Agility = r.ReadSingle();
            npc.Attributes.Endurance = r.ReadSingle();
            npc.Attributes.Toughness = r.ReadSingle();
            npc.Attributes.Hardiness = r.ReadSingle();
            npc.Attributes.Wits = r.ReadSingle();
            npc.Skills.Combat = r.ReadSingle();
            npc.Skills.Harvesting = r.ReadSingle();
            npc.Skills.Crafting = r.ReadSingle();
            npc.Skills.Building = r.ReadSingle();
            npc.Skills.Cooking = r.ReadSingle();
            npc.Skills.Medicine = r.ReadSingle();
            npc.Skills.Survival = r.ReadSingle();
            npc.Skills.Social = r.ReadSingle();
        }

        // §28.15C v3: до v20 мёртвых не существовало как сущностей — тело
        // истлевало, и грузить было нечего. Ноль здесь и означает «жива».
        if (version >= 20)
        {
            npc.DeathAnimVariant = r.ReadInt32();
        }

        // §85: до v24 цвет глаз ехал внутри SkinSet — материалы актрисы несли
        // и глаза тоже. Пустая строка здесь и означает «глаза с префаба тела»,
        // то есть ровно тот вид, в котором сейв писался.
        if (version >= 24)
        {
            npc.EyeColor = r.ReadString();
        }

        ReadItemList(r, npc.WornItems, version);
        if (version < 42)
        {
            // §133: то, что на ней надето в старом сейве, — её собственное.
            foreach (var worn in npc.WornItems)
            {
                worn.OwnerId = npc.Id.Value;
            }
        }

        ReadJunctionList(r, npc.ClaimedJunctions);

        var partCount = r.ReadInt32();
        for (var i = 0; i < partCount; i++)
        {
            var part = (BodyPart)r.ReadInt32();
            npc.Body.Parts[part] = r.ReadSingle();
        }

        if (version >= 14)
        {
            var severedCount = r.ReadInt32();
            for (var i = 0; i < severedCount; i++)
            {
                // Sever() re-pins the part at 0 HP — idempotent with the
                // Parts values read just above.
                npc.Body.Sever((BodyPart)r.ReadInt32());
            }
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

        if (version >= 14)
        {
            var bandagedCount = r.ReadInt32();
            for (var i = 0; i < bandagedCount; i++)
            {
                npc.BandagedZones.Add((BodyPart)r.ReadInt32());
            }

            var gauzeCount = r.ReadInt32();
            for (var i = 0; i < gauzeCount; i++)
            {
                npc.GauzeZones.Add((BodyPart)r.ReadInt32());
            }
        }

        var needs = npc.Needs;
        needs.Hunger = r.ReadSingle();
        needs.Thirst = r.ReadSingle();
        needs.Energy = r.ReadSingle();
        needs.Comfort = r.ReadSingle();
        needs.Social = r.ReadSingle();
        if (version >= 15)
        {
            needs.Compassion = r.ReadSingle();
        }

        needs.ThermalDiscomfort = r.ReadSingle();
        needs.ThermalComfort = r.ReadSingle();
        needs.Stamina = r.ReadSingle();
        needs.Breath = version >= 16 ? r.ReadSingle() : 1f; // §71: older saves start rested
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
        if (version >= 15)
        {
            mind.SickUntilTick = r.ReadInt32();
            mind.SicknessDamageRemaining = r.ReadSingle();
        }

        if (version >= 23)
        {
            // §105: причина и остаток запаса. Штамп не читается — его нет в
            // блобе (см. запись); ноль означает «перештамповать на первом
            // тике», то есть загрузка стоит ей одного пропущенного вычета.
            mind.DyingCause = (DyingCause)r.ReadInt32();
            mind.DyingReserve = r.ReadSingle();
            mind.ConvalescentUntilTick = r.ReadInt32();
        }

        // §110: старый сейв просто не плачет — 0 значит «не плачет».
        mind.CryingUntilTick = version >= 25 ? r.ReadInt32() : 0;
        // §81.10: старый сейв просто не грустит.
        mind.SadWalkUntilTick = version >= 26 ? r.ReadInt32() : 0;
        // §121: в старом сейве ручного режима не было — все под ИИ.
        mind.ManualControl = version >= 31 && r.ReadBoolean();
        // §121.7 (v46 legacy): читаем прежний потиковый штамп, сохраняя layout;
        // действующий real-time lease начинается заново на первом idle-проходе.
        mind.LastManualInputTick = version >= 46 ? r.ReadInt32() : 0;

        mind.WakeGraceUntilTick = r.ReadInt32();
        mind.AdrenalineUntilTick = version >= 9 ? r.ReadInt32() : 0;
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

        if (version >= 15)
        {
            mind.IsOverheated = r.ReadBoolean();
            mind.CoolRearmCount = r.ReadInt32();
            var redressCount = r.ReadInt32();
            for (var i = 0; i < redressCount; i++)
            {
                mind.RedressGarments.Add(new ObjectId(r.ReadInt32()));
            }

            mind.RedressShore = ReadNullableJunction(r);
            if (version >= 48)
            {
                mind.PersonalCarePhase = (PersonalCarePhase)r.ReadInt32();
                mind.PersonalCareBathShore = ReadNullableJunction(r);
            }
            else if (mind.RedressGarments.Count > 0)
            {
                // v15-v47 had only one resumable meaning for a remembered
                // pile: walk back and put it on.
                mind.PersonalCarePhase = PersonalCarePhase.Redress;
            }
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
        plan.RunRequested = version >= 39 && r.ReadBoolean();
        var stepCount = r.ReadInt32();
        for (var i = 0; i < stepCount; i++)
        {
            plan.Steps.Add(new PlanStep
            {
                Type = (PlanStepType)r.ReadInt32(),
                TargetJunction = ReadNullableJunction(r),
                TargetObject = ReadNullableObject(r),
                Interaction = r.ReadBoolean() ? (InteractionType)r.ReadInt32() : null,
                TimeoutEndTick = r.ReadBoolean() ? r.ReadInt32() : null,
                LaundryFromInventory = version >= 58 && r.ReadBoolean()
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

        if (version >= 44)
        {
            execution.LyingStationTargetId = ReadNullableEntity(r);
            execution.LyingStationSlot = r.ReadInt32();
        }
        else
        {
            execution.LyingStationTargetId = null;
            execution.LyingStationSlot = -1;
        }

        execution.CraftLayout.Clear();
        if (version >= 10)
        {
            var craftLayoutCount = r.ReadInt32();
            for (var i = 0; i < craftLayoutCount; i++)
            {
                execution.CraftLayout.Add(new ObjectId(r.ReadInt32()));
            }
        }

        var movement = npc.Movement;
        movement.BlockedWaitTicks = r.ReadInt32();
        movement.IsMoving = r.ReadBoolean();
        ReadJunctionList(r, movement.JunctionPath);
        movement.PathIndex = r.ReadInt32();
        movement.DesiredDirection = ReadFloat2(r);
        movement.DesiredRotationDegrees = r.ReadSingle();
        movement.MoveSpeed = r.ReadSingle();
        movement.TurnSpeed = r.ReadSingle();
        movement.SetStatus((MovementStatus)r.ReadInt32());
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
                Affinity = r.ReadSingle(),
                LastInteractionTick = version >= 59 ? r.ReadInt32() : 0
            };
        }

        npc.Inventory.Capacity = r.ReadInt32();
        ReadItemList(r, npc.Inventory.Items, version);
        MedicalSupplyMath.MaterializeLegacyPouch(npc);

        if (version >= 28)
        {
            npc.Body.BloodDeficit = r.ReadSingle();
            var conditionCount = r.ReadInt32();
            for (var i = 0; i < conditionCount; i++)
            {
                var part = (BodyPart)r.ReadInt32();
                var condition = npc.Body.Condition(part);
                condition.BluntDamage = r.ReadSingle();
                condition.CriticalTrauma = r.ReadSingle();
                condition.SplintSupport = r.ReadSingle();
                condition.HitBias = r.ReadSingle();
                condition.HitBiasChangedTick = r.ReadInt32();
                if (r.ReadBoolean())
                {
                    condition.Prosthetic = new ProstheticState
                    {
                        DefinitionId = r.ReadString(),
                        Part = (BodyPart)r.ReadInt32(),
                        Condition = r.ReadSingle(),
                        MaxCondition = r.ReadSingle(),
                        Function = r.ReadSingle(),
                        Mechanical = r.ReadBoolean()
                    };
                }
            }

            var woundExtensionCount = r.ReadInt32();
            for (var i = 0; i < woundExtensionCount; i++)
            {
                var id = r.ReadInt32();
                var clot = r.ReadSingle();
                var stabilized = r.ReadBoolean();
                var bleedFactor = r.ReadSingle();
                var plastered = version >= 47 && r.ReadBoolean();
                var wound = npc.Wounds.Find(candidate => candidate.Id == id);
                if (wound != null)
                {
                    wound.Clot01 = clot;
                    wound.Stabilized = stabilized;
                    wound.BleedFactor = bleedFactor;
                    wound.Plastered = plastered;
                }
            }

            npc.CarriedNpcId = ReadNullableEntity(r);
            npc.CarriedByNpcId = ReadNullableEntity(r);
            npc.RescueDestinationObjectId = ReadNullableObject(r);

            if (version >= 29)
            {
                var strikeLandsAtTick = r.ReadInt32();
                var strikeReadyAtTick = r.ReadInt32();
                var attackAnimUntilTick = r.ReadInt32();
                var swingStartTick = r.ReadInt32();
                var swingStrikeIndex = r.ReadInt32();
                var pendingStrikeTarget = ReadNullableEntity(r);
                var pendingStrikePart = (BodyPart)r.ReadInt32();
                _ = r.ReadBoolean(); // retired v29 mercy flag
                _ = r.ReadSingle(); // retired v29 kill-intent scalar
                FightScene.RestoreSwingSlot(
                    npc,
                    strikeLandsAtTick,
                    strikeReadyAtTick,
                    attackAnimUntilTick,
                    swingStartTick,
                    swingStrikeIndex,
                    pendingStrikeTarget,
                    pendingStrikePart);
                npc.Mind.CombatOpponentNpcId = ReadNullableEntity(r);
                npc.Mind.ForcedMeleeWeaponId = r.ReadBoolean() ? r.ReadString() : null;
            }

            if (version >= 30)
            {
                npc.Mind.ProstheticAidTargetId = ReadNullableEntity(r);
                npc.Mind.ProstheticAidPart = r.ReadBoolean() ? (BodyPart)r.ReadInt32() : null;
                npc.Mind.ProstheticAidRetryAfterTick = r.ReadInt32();
                npc.Execution.CraftProjectId = ReadNullableObject(r);
                npc.Execution.CraftCycleStartWork = r.ReadInt32();
            }
        }
        else
        {
            MigrateKenshiState(npc);
        }

        if (version >= 35)
        {
            // §125: Восприятие. Старый блоб — дефолт 0.5 (радиус 5, до-§125 мир).
            npc.Attributes.Perception = r.ReadSingle();

            // §125.3: память встреч. У старого блоба её нет — пустая память
            // честно означает «никого пока не видела», сенсор наполнит.
            var metCount = r.ReadInt32();
            for (var i = 0; i < metCount; i++)
            {
                var met = new Memory.AgentMemory
                {
                    Id = new EntityId(r.ReadInt32()),
                    Faction = (Faction)r.ReadInt32(),
                    Tile = ReadTile(r),
                    LastSeenTick = r.ReadInt32(),
                    Junction = ReadNullableJunction(r),
                    Suffering = r.ReadSingle(),
                    AidKind = (AidKind)r.ReadInt32(),
                    Helpless = r.ReadBoolean()
                };
                npc.Memory.KnownAgents[met.Id] = met;
            }
        }

        if (version >= 36)
        {
            // §126: черты характера одним числом.
            npc.Traits.Bits = r.ReadUInt64();
        }
        else
        {
            // §126 миграция: до v36 «гнобит» и «не моется» БЫЛИ фракцией.
            // Правило живёт в TraitMath, чтобы его можно было проверить гейтом,
            // а не подделкой двоичного блоба.
            HexLive.Simulation.Runtime.TraitMath.ApplyPreTraitDefaults(npc);
        }

        if (version >= 41)
        {
            // §40.8-H r10: кровяная подложка. Старый блоб — чистая кожа (0):
            // прежние спеклы были производной от HP и не сохранялись.
            var soilCount = r.ReadInt32();
            for (var i = 0; i < soilCount; i++)
            {
                var part = (BodyPart)r.ReadInt32();
                var soil = r.ReadSingle();
                var intimacySoil = version >= 56 ? r.ReadSingle() : 0f;
                if (npc.Body.Conditions.TryGetValue(part, out var condition))
                {
                    condition.BloodSoil = soil;
                    condition.IntimacySoil = intimacySoil;
                }
            }
        }

        if (version >= 45)
        {
            // §136: дневник. Старый блоб — пустой дневник, и это правда: до
            // этой версии его никто не вёл. Первая запись появится через час.
            var entryCount = r.ReadInt32();
            var entries = new List<JournalEntry>(entryCount);
            for (var i = 0; i < entryCount; i++)
            {
                entries.Add(new JournalEntry
                {
                    Tick = r.ReadInt32(),
                    Type = ReadNullableString(r),
                    Register = (JournalRegister)r.ReadByte(),
                    Bond = (JournalBond)r.ReadByte(),
                    Perspective = (JournalPerspective)r.ReadByte(),
                    SubjectNameId = ReadNullableString(r),
                    Extra = ReadNullableString(r),
                    Variant = r.ReadByte(),
                    QuietHours = r.ReadByte(),
                    Chore0 = ReadNullableString(r),
                    Chore1 = ReadNullableString(r),
                    Chore2 = ReadNullableString(r)
                });
            }

            npc.Journal.LoadFrom(entries);
        }

        // §133.9: старый сейв не содержал пользовательского запрета, поэтому
        // его одежда остаётся в прежнем свободном режиме.
        npc.Mind.OutfitLocked = version >= 55 && r.ReadBoolean();

        if (version >= 56)
        {
            npc.Mind.RomancePartnerNpcId = ReadNullableEntity(r);
            npc.Mind.RomanceLeaderNpcId = ReadNullableEntity(r);
            npc.Mind.RomanceClipKey = ReadNullableString(r) ?? string.Empty;
            npc.Mind.RomanceForced = r.ReadBoolean();
            npc.Mind.RomanceAnchorX = r.ReadSingle();
            npc.Mind.RomanceAnchorY = r.ReadSingle();
            npc.Mind.RomanceFacingDegrees = r.ReadSingle();
            npc.Mind.RomanceCooldownUntilTick = r.ReadInt32();
        }

        if (version >= 57)
        {
            var desiredCount = r.ReadInt32();
            for (var i = 0; i < desiredCount; i++)
            {
                npc.Mind.DesiredOutfit.Add(new DesiredOutfitPiece
                {
                    DefinitionId = r.ReadString(),
                    GroundObjectId = ReadNullableObject(r)
                });
            }
        }
        else if (npc.Mind.OutfitLocked)
        {
            // v55-v56 knew only the veto. Preserve the visible current set as
            // the initial desired outfit; off-body drying pieces had no saved
            // identity in that format and cannot be guessed without stealing
            // a spare from the wardrobe.
            foreach (var worn in npc.WornItems)
            {
                npc.Mind.DesiredOutfit.Add(new DesiredOutfitPiece
                {
                    DefinitionId = worn.DefinitionId
                });
            }
        }

        return npc;
    }

    private static void MigrateKenshiState(NPCState npc)
    {
        foreach (var wound in npc.Wounds)
        {
            wound.BleedFactor = 1f;
            wound.Stabilized = npc.BandagedZones.Contains(wound.Zone) ||
                npc.GauzeZones.Contains(wound.Zone);
            wound.Clot01 = wound.Stabilized ? 1f : 0f;
        }

        var depth = MathUtil.Clamp01(1f - npc.Mind.DyingReserve);
        if (npc.Mind.DyingCause == DyingCause.BloodLoss)
        {
            npc.Body.BloodDeficit = depth;
        }
        else if (npc.Mind.DyingCause == DyingCause.VitalCrushed)
        {
            var worstPart = BodyPart.Head;
            var worstHealth = float.MaxValue;
            foreach (var part in BodyState.VitalParts)
            {
                if (npc.Body.Parts[part] < worstHealth)
                {
                    worstHealth = npc.Body.Parts[part];
                    worstPart = part;
                }
            }

            npc.Body.Condition(worstPart).CriticalTrauma = depth;
        }
    }

    private static void RepairCarryLinks(WorldState world)
    {
        var safeLiveDrops = new HashSet<EntityId>();
        var safeCorpseDrops = new HashSet<EntityId>();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.CarriedNpcId is { } carriedId)
            {
                KenshiRescueMath.TryGetPerson(world, carriedId, out var patient, out var dead);
                if (carriedId.Equals(npc.Id) ||
                    patient == null ||
                    patient.CarriedByNpcId is not { } carrierId || !carrierId.Equals(npc.Id))
                {
                    if (patient != null)
                    {
                        (dead ? safeCorpseDrops : safeLiveDrops).Add(patient.Id);
                    }
                    npc.CarriedNpcId = null;
                    npc.RescueDestinationObjectId = null;
                }
                else if (dead)
                {
                    CorpseMath.SuspendAnchor(world, patient);
                }
            }

            if (npc.CarriedByNpcId is { } carrierId2)
            {
                if (carrierId2.Equals(npc.Id) ||
                    !world.Entities.Npcs.TryGetValue(carrierId2, out var carrier) ||
                    carrier.CarriedNpcId is not { } patientId || !patientId.Equals(npc.Id))
                {
                    npc.CarriedByNpcId = null;
                    safeLiveDrops.Add(npc.Id);
                }
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            if (corpse.CarriedByNpcId is not { } carrierId)
            {
                continue;
            }

            if (!world.Entities.Npcs.TryGetValue(carrierId, out var carrier) ||
                carrier.CarriedNpcId is not { } bodyId || !bodyId.Equals(corpse.Id))
            {
                corpse.CarriedByNpcId = null;
                safeCorpseDrops.Add(corpse.Id);
            }
            else
            {
                CorpseMath.SuspendAnchor(world, corpse);
            }
        }

        // A half-link from a corrupt/interrupted save must not leave a patient
        // with no junction forever. Re-anchor only bodies that should be lying;
        // a healthy standing NPC with an unrelated stale id keeps her pose.
        foreach (var id in safeLiveDrops)
        {
            if (world.Entities.Npcs.TryGetValue(id, out var patient) &&
                patient.Health > 0f && patient.CurrentJunction is null &&
                (patient.IsUnconscious(world.Tick) || patient.IsDying || patient.Body.IsProne))
            {
                HexLive.Simulation.Runtime.MortalityHelpers.AnchorLyingBody(world, patient);
            }
        }


        foreach (var id in safeCorpseDrops)
        {
            if (world.Entities.Corpses.TryGetValue(id, out var body) && !body.IsBeingCarried)
            {
                CorpseMath.AnchorBody(world, body);
            }
        }
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
            w.Write(item.OwnerId);
        }
    }

    private static void ReadItemList(BinaryReader r, List<ItemInstance> items, int version)
    {
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var item = new ItemInstance(r.ReadString())
            {
                Wetness = r.ReadSingle(),
                Durability = r.ReadSingle(),
                ResourceAmount = r.ReadSingle(),
                Dirtiness = version >= 7 ? r.ReadSingle() : 0f,
                Bloodiness = version >= 7 ? r.ReadSingle() : 0f,
                OwnerId = version >= 42 ? r.ReadInt32() : 0
            };
            if (version == 7)
            {
                item.Dirtiness = MathUtil.Clamp01(item.Dirtiness + item.Bloodiness);
            }

            items.Add(item);
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

    // §121: PlayerAttack складывается вместе со сценными целями — сцепка живёт
    // в полях, которых в блобе нет. А вот PlayerOrder НЕ складывается: план
    // приказа (шаги, цели, исполнение) сериализуется целиком, поэтому
    // недошедшая колонистка после загрузки просто идёт дальше.
    private static GoalType SaveGoal(GoalType goal) =>
        goal is GoalType.Defend or GoalType.Abuse or GoalType.GroupHunt or GoalType.Expel or
            GoalType.Rescue or GoalType.PickUpPerson or GoalType.PutInBed or
            GoalType.Splint or GoalType.FitProsthetic or GoalType.PlayerAttack
            ? GoalType.None
            : goal;

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

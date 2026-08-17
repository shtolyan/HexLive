using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Server.Mcp
{

/// <summary>
/// §144.3/§144.5: что агент умеет сделать с колонией и как это исполняется.
/// <para>
/// ⭐ Ни один инструмент не трогает мир напрямую. Приказы уходят в
/// <c>WorldHost.SubmitManualCommand</c> — тот же шов, которым ходят клики
/// игрока и решения LLM, — и получают тот же типизированный вердикт
/// accepted/rejected. Из этого следует главное свойство: агент НЕ МОЖЕТ
/// сделать того, чего не может игрок. Таблица разрешений §121.5 одна на всех,
/// и её не обходит ни одна дверь.
/// </para>
/// <para>
/// Смотрит агент тоже не своими глазами: <c>describe_colonist</c> отдаёт ровно
/// те три сводки, которые строит <see cref="LlmDecisionContextBuilder"/> для
/// HTTP-контура (§32.15). Один сборщик на оба пути управления — иначе два
/// описания мира разъедутся, и отладка «почему он решил иначе» превратится в
/// сравнение двух текстов, которых никто не писал вместе.
/// </para>
/// </summary>
public sealed class McpTools
{
    private readonly WorldHost _host;
    private readonly ControlLeases _leases;
    private readonly SpecLibrary _spec;

    public McpTools(WorldHost host, ControlLeases leases, SpecLibrary? spec = null)
    {
        _host = host;
        _leases = leases;
        _spec = spec ?? SpecLibrary.Discover(null);
    }

    // ── каталог ───────────────────────────────────────────────────────────

    public static IReadOnlyList<ToolSpec> Catalog { get; } = new List<ToolSpec>
    {
        new("world_status",
            "Состояние мира: тик, часы, погода, сколько колонисток живо, темп сервера. " +
            "Ничего не меняет.",
            Schema()),

        new("list_colonists",
            "Все колонистки: id, имя, фракция, тайл, здоровье, текущая цель, под ручным ли " +
            "управлением и кто держит лиз. С этого начинают.",
            Schema()),

        new("describe_colonist",
            "Полная картина по одной колонистке: состояние, восприятие (объекты рядом с их " +
            "id и доступными взаимодействиями, союзницы, враги и звери) и память. Ровно " +
            "тот текст, который получает LLM-контур. Отсюда берут objectId для interact " +
            "и mobId для attack_mob.",
            Schema(("npcId", "integer", "id колонистки", true))),

        new("read_spec",
            "Спецификация мира — та же, по которой он написан. Начните с неё: правила, " +
            "решающие, сработает приказ или нет, живут здесь, а не в описаниях " +
            "инструментов. Без аргументов — оглавление всех разделов. section=«121» — " +
            "раздел целиком (управление и ручной режим), «144» — этот шов, «64» — вода " +
            "и мечты, «54» — стройка и припасы. Длинные разделы читаются кусками: " +
            "передайте offset из поля nextOffset.",
            Schema(("section", "string", "номер раздела, например 121 или 29E", false),
                   ("offset", "integer", "с какого символа продолжить чтение длинного раздела", false))),

        new("read_events",
            "Что произошло в мире: разговоры, удары, ранения, обмороки, смерти, ссоры. " +
            "Без него видно только числа состояния — падающее здоровье приходит без " +
            "причины, и причину приходится домысливать. Вызывать с sinceSeq=watermark " +
            "из прошлого ответа; первый вызов без него отдаёт свежий хвост. " +
            "gap=true — кольцо подрезало, пропущенное потеряно навсегда и переспрашивать " +
            "его нельзя. truncated=true — упёрлись в limit, позовите ещё раз.",
            Schema(("sinceSeq", "integer", "вотермарка прошлого ответа; без неё — свежий хвост", false),
                   ("limit", "integer", "сколько событий максимум (по умолчанию 100)", false),
                   ("npcId", "integer", "оставить только события про эту колонистку", false))),

        new("list_leases",
            "Кто кем сейчас владеет и сколько секунд простаивает.",
            Schema()),

        new("acquire_control",
            "Взять колонистку под управление: занимает лиз и включает ручной режим (§121). " +
            "Пока лиз ваш, ИИ ей не распоряжается, а другой агент не может отдать ей приказ. " +
            "Повторный вызов тем же владельцем продлевает лиз.",
            Schema(("npcId", "integer", "id колонистки", true))),

        new("release_control",
            "Вернуть колонистку ИИ и отпустить лиз. Вызывать, закончив работу: иначе она " +
            "простоит под управлением до истечения лиза.",
            Schema(("npcId", "integer", "id колонистки", true))),

        new("move_to",
            "Идти в точку мира (координаты, а не узел — ближайший узел найдёт симуляция).",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("x", "number", "мировая координата X", true),
                   ("y", "number", "мировая координата Y", true),
                   ("run", "boolean", "бежать вместо шага", false))),

        new("interact",
            "Подойти к объекту и сделать с ним ровно это. objectId и допустимые глаголы — " +
            "из describe_colonist (поле interactions у объекта).",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("objectId", "integer", "id объекта", true),
                   ("interaction", "string", "глагол взаимодействия, например PickUp/Drink/Sleep", true))),

        new("craft_item",
            "Заказать крафт по цели рецепта (§138), например CookMeat или CraftCloth. " +
            "Список целей — в ошибке при неверном значении.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("recipeGoal", "string", "цель рецепта", true))),

        new("attack_npc",
            "Напасть на человека.",
            Schema(("npcId", "integer", "id нападающей", true),
                   ("targetNpcId", "integer", "id цели", true))),

        new("attack_mob",
            "Напасть на зверя. mobId — из поля mobs в describe_colonist: там ровно те, " +
            "кого она сейчас видит. targetsMe=true значит зверь идёт именно за ней.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("mobId", "integer", "id зверя", true))),

        new("stop",
            "Отставить текущий приказ. Не выключает ручной режим — это разные вещи (§121).",
            Schema(("npcId", "integer", "id колонистки", true))),

        new("talk_to",
            "Подойти и поговорить с колонисткой (§121.9/§28). Занятая или не в духе цель " +
            "откажет ПО ПРИБЫТИИ — это штатный исход, не ошибка инструмента.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("targetNpcId", "integer", "с кем говорить", true))),

        new("aid_person",
            "Помочь конкретной колонистке ЯВНЫМ видом помощи (§53): Feed/Hydrate/Treat/" +
            "Medicate/Console. Помощь стоит припаса ПОМОЩНИЦЫ (§53.7); отказ NoSupplies " +
            "значит «нечем» — сначала добудьте еду/воду/бинт.",
            Schema(("npcId", "integer", "id помощницы", true),
                   ("targetNpcId", "integer", "кому помочь", true),
                   ("kind", "string", "вид помощи: Feed/Hydrate/Treat/Medicate/Console", true))),

        new("treat_limbs",
            "Наложить шину или приладить протез лежащей (§116/§118). Что именно — решает " +
            "симуляция (шина первой). NoLimbDamage — конечности целы; NoSupplies — нет " +
            "шины/протеза или пациентка не в кровати для протеза.",
            Schema(("npcId", "integer", "id лекарки", true),
                   ("targetNpcId", "integer", "id пациентки", true))),

        new("self_action",
            "Самодействие (§121.9): CallForHelp (крик о помощи в бою, не сносит план), " +
            "TreatSelf (перевязаться), GroundSit/GroundSleep (сесть/лечь на землю), " +
            "Bathe/WashClothes (купание/стирка), EatFromPack/DrinkFromPack (из рюкзака).",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("kind", "string",
                    "вид: CallForHelp/TreatSelf/GroundSit/GroundSleep/Bathe/WashClothes/" +
                    "EatFromPack/DrinkFromPack", true))),

        new("carry_person",
            "Взять на руки лежащую (или свою — и стоящую) колонистку (§124). Руки должны " +
            "быть свободны.",
            Schema(("npcId", "integer", "id носильщицы", true),
                   ("targetNpcId", "integer", "кого поднять", true))),

        new("put_down_person",
            "Положить переносимого человека у ног (§124).",
            Schema(("npcId", "integer", "id носильщицы", true))),

        new("put_person_in_bed",
            "Донести переносимого человека до кровати и уложить (§124.1). objectId кровати — " +
            "из describe_colonist.",
            Schema(("npcId", "integer", "id носильщицы", true),
                   ("bedObjectId", "integer", "id кровати", true))),

        new("manage_inventory",
            "Надеть/убрать/выбросить вещь из инвентаря (§52): source=Carried|Worn, index — " +
            "номер ячейки из describe_colonist, expectedDefinitionId защищает от протухшей " +
            "картинки (id не совпал — приказ честно отклоняется).",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("source", "string", "Carried или Worn", true),
                   ("index", "integer", "номер ячейки", true),
                   ("expectedDefinitionId", "string", "ожидаемый id предмета в ячейке", true),
                   ("action", "string", "Wear/Stow/Drop", true))),

        new("transfer_inventory",
            "Обмен с лежащим человеком (§128): взять или отдать одну ячейку.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("otherNpcId", "integer", "id второй стороны (лежащей)", true),
                   ("source", "string", "Carried или Worn — чья ячейка описывается", true),
                   ("index", "integer", "номер ячейки", true),
                   ("expectedDefinitionId", "string", "ожидаемый id предмета", true),
                   ("count", "integer", "сколько штук (по умолчанию 1)", false),
                   ("direction", "string", "Take (себе) или Give (отдать)", true))),

        new("transfer_container",
            "Обыск вещи (§128.5): истлевшее тело, снятый рюкзак, аптечка. slotIndex и " +
            "expectedDefinitionId — из содержимого объекта в describe_colonist.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("containerObjectId", "integer", "id объекта-контейнера", true),
                   ("slotIndex", "integer", "номер ячейки содержимого", true),
                   ("expectedDefinitionId", "string", "ожидаемый id предмета", true),
                   ("count", "integer", "сколько штук (по умолчанию 1)", false),
                   ("direction", "string", "Take или Give", true))),

        new("prey_person",
            "«Тёмный» приказ §56 (за выключателем Spec121.ManualDarkOrdersEnabled): " +
            "выследить СОСЕДКУ ради мяса. Нужен разделочный нож. Необратимо и с полными " +
            "последствиями §56 — свидетельницы, страх, метка убийцы.",
            Schema(("npcId", "integer", "id охотницы", true),
                   ("targetNpcId", "integer", "id жертвы (союзница)", true))),

        new("abuse_person",
            "«Тёмный» приказ §81 (за тем же выключателем): затеять сцену травли против " +
            "ВРАЖДЕБНОГО чужака — отжать припас. Свидетельницы жертвы впишутся, доверие " +
            "к агрессорше упадёт у всех, кто видел.",
            Schema(("npcId", "integer", "id зачинщицы", true),
                   ("targetNpcId", "integer", "id жертвы (враждебной)", true))),
    };

    public sealed record ToolSpec(string Name, string Description, JsonElement InputSchema);

    // ── исполнение ────────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает текст ответа. Отказ — это тоже ответ (<paramref name="isError"/>),
    /// а не исключение: агенту нужна причина, чтобы попробовать иначе.
    /// </summary>
    public string Call(string name, JsonElement arguments, string owner, out bool isError)
    {
        isError = false;
        try
        {
            switch (name)
            {
                case "world_status": return WorldStatus();
                case "list_colonists": return ListColonists();
                case "list_leases": return ListLeases();
                case "read_events": return ReadEvents(arguments);
                case "read_spec": return ReadSpec(arguments, out isError);
                case "describe_colonist": return Describe(Int(arguments, "npcId"), out isError);
                case "acquire_control": return Acquire(Int(arguments, "npcId"), owner, out isError);
                case "release_control": return Release(Int(arguments, "npcId"), owner, out isError);
                case "move_to": return MoveTo(arguments, owner, out isError);
                case "interact": return Interact(arguments, owner, out isError);
                case "craft_item": return Craft(arguments, owner, out isError);
                case "attack_npc": return AttackNpc(arguments, owner, out isError);
                case "attack_mob": return AttackMob(arguments, owner, out isError);
                case "stop": return Simple(arguments, owner, npc => new StopCommand(npc), out isError);
                // ⭐ Обязательные аргументы читаются ДО Submit (вне лямбды):
                // отказ «нет параметра X» обязан прийти и без лиза — за этим
                // следит контрактный гейт каталога.
                case "talk_to":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(arguments, owner,
                        npc => new TalkToCommand(npc, target), out isError);
                }

                case "aid_person": return AidPerson(arguments, owner, out isError);
                case "treat_limbs":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(arguments, owner,
                        npc => new TreatLimbsCommand(npc, target), out isError);
                }

                case "self_action": return SelfAction(arguments, owner, out isError);
                case "carry_person":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(arguments, owner,
                        npc => new CarryPersonCommand(npc, target), out isError);
                }

                case "put_down_person":
                    return Simple(arguments, owner, npc => new PutDownPersonCommand(npc), out isError);
                case "put_person_in_bed":
                {
                    var bed = new ObjectId(Int(arguments, "bedObjectId"));
                    return Simple(arguments, owner,
                        npc => new PutPersonInBedCommand(npc, bed), out isError);
                }

                case "manage_inventory": return ManageInventory(arguments, owner, out isError);
                case "transfer_inventory": return TransferInventory(arguments, owner, out isError);
                case "transfer_container": return TransferContainer(arguments, owner, out isError);
                case "prey_person":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(arguments, owner,
                        npc => new PreyPersonCommand(npc, target), out isError);
                }

                case "abuse_person":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(arguments, owner,
                        npc => new AbusePersonCommand(npc, target), out isError);
                }

                default:
                    isError = true;
                    return $"Нет такого инструмента: {name}";
            }
        }
        catch (McpArgumentException ex)
        {
            isError = true;
            return ex.Message;
        }
    }

    private string WorldStatus()
    {
        var census = _host.Census();
        return _host.Read(world => Json(new Dictionary<string, object?>
        {
            ["tick"] = world.Tick,
            ["seed"] = world.Seed,
            ["clock"] = world.Environment.TimeOfDayNormalized,
            ["phase"] = world.Environment.Phase.ToString(),
            ["temperature"] = world.Environment.GlobalTemperature,
            ["raining"] = world.Environment.IsRaining,
            ["colonistsAlive"] = census.alive,
            ["colonistsTotal"] = census.total,
            ["objects"] = census.objects,
            ["paused"] = _host.IsPaused,
            ["speedMultiplier"] = _host.SpeedMultiplier,
            ["ticksPerSecond"] = Math.Round(_host.MeasuredTicksPerSecond, 2),
            ["averageTickMs"] = Math.Round(_host.AverageTickMs, 2),
        }));
    }

    /// <summary>
    /// §144.9. Спека по сети: у агента нет ни репозитория, ни файлов рядом.
    /// </summary>
    private string ReadSpec(JsonElement arguments, out bool isError)
    {
        isError = false;

        var section = arguments.ValueKind == JsonValueKind.Object &&
                      arguments.TryGetProperty("section", out var value) &&
                      value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(section))
        {
            var index = _spec.ReadIndex();
            if (index == null)
            {
                isError = true;
                return "Спека не поставлена с этим сервером. Запустите с --spec-dir <путь>.";
            }

            var sections = _spec.Sections();
            return Json(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["sections"] = sections,
                ["hint"] = "read_spec с section=«121» отдаёт раздел целиком. " +
                           "Ссылка вида §105.14 значит раздел 105 — подпункты живут внутри файла.",
            });
        }

        var offset = OptionalInt(arguments, "offset") ?? 0;
        if (!_spec.TryReadSection(section, offset, out var text, out var total, out var error))
        {
            isError = true;
            return error;
        }

        var end = offset + text.Length;
        return Json(new Dictionary<string, object?>
        {
            ["section"] = section.Trim().TrimStart('§'),
            ["text"] = text,
            ["offset"] = offset,
            ["totalChars"] = total,
            // Усечение — элемент ответа, а не тишина: обрыв на полуслове,
            // прочитанный как конец раздела, хуже отсутствия раздела.
            ["truncated"] = end < total,
            ["nextOffset"] = end < total ? end : (object?)null,
        });
    }

    /// <summary>
    /// §144.6. Тянущее чтение хроники — сервер по-прежнему ничего не проталкивает
    /// (§144.4), агент сам спрашивает «что было после Seq=N».
    /// <para>
    /// Первый вызов без <c>sinceSeq</c> отдаёт свежий хвост, а не всю ленту с
    /// начала мира: агент, подключившийся на 50-тысячном тике, интересуется тем,
    /// что происходит сейчас, а не двумя тысячами событий из кольца, каждое из
    /// которых он всё равно не сможет соотнести с текущим состоянием.
    /// </para>
    /// </summary>
    private string ReadEvents(JsonElement arguments)
    {
        var limit = OptionalInt(arguments, "limit") ?? 100;
        if (limit is <= 0 or > 500)
        {
            limit = limit <= 0 ? 100 : 500;
        }

        var entityId = OptionalInt(arguments, "npcId");

        // Без вотермарки — «с этого момента», а не «всё, что храним». Свалить на
        // первый же вызов сотни накопленных событий значит забить агенту контекст
        // тем, чего он не вызывал и с текущим состоянием соотнести не может.
        // Кому нужна предыстория — передаёт sinceSeq=1 и получает gap=true.
        var batch = _host.ReadEvents(OptionalLong(arguments, "sinceSeq"), limit, entityId);

        var rows = new List<object>(batch.Events.Count);
        foreach (var e in batch.Events)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["seq"] = e.Seq,
                ["tick"] = e.Tick,
                ["type"] = e.Type,
                // Дословно: из Message вытаскивают id зверя и разбирают Kind=/Cause=[…].
                ["message"] = e.Message,
                ["entityId"] = e.EntityId,
            });
        }

        return Json(new Dictionary<string, object?>
        {
            ["events"] = rows,
            ["watermark"] = batch.Watermark,
            ["oldestRetainedSeq"] = batch.OldestRetainedSeq,
            ["gap"] = batch.Gap,
            ["sessionReset"] = batch.SessionReset,
            ["truncated"] = batch.Truncated,
            // Меняется при перезапуске процесса и при загрузке сейва. Без него
            // чужой мир, успевший дойти до seq 900, неотличим от нашего.
            ["sessionEpoch"] = _host.McpSessionEpoch,
        });
    }

    private string ListColonists()
    {
        return _host.Read(world =>
        {
            var rows = new List<object>();
            foreach (var npc in world.Entities.Npcs.Values)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["npcId"] = npc.Id.Value,
                    ["name"] = npc.DisplayName ?? string.Empty,
                    ["faction"] = npc.Faction.ToString(),
                    ["tile"] = $"{npc.Tile.Q},{npc.Tile.R}",
                    ["position"] = new Dictionary<string, object?>
                    {
                        ["x"] = Round(npc.Position.X),
                        ["y"] = Round(npc.Position.Y),
                    },
                    ["health"] = Round(npc.Health),
                    ["goal"] = npc.Mind.CurrentGoal.ToString(),
                    ["planStatus"] = npc.Plan.Status.ToString(),
                    ["manualControl"] = npc.Mind.ManualControl,
                    ["unconscious"] = npc.IsUnconscious(world.Tick),
                    ["leaseHolder"] = Holder(npc.Id.Value),
                });
            }

            rows.Sort((a, b) => Id(a).CompareTo(Id(b)));
            return Json(new Dictionary<string, object?>
            {
                ["tick"] = world.Tick,
                ["colonists"] = rows,
            });
        });

        static int Id(object row) => (int)((Dictionary<string, object?>)row)["npcId"]!;
    }

    private string ListLeases()
    {
        var rows = new List<object>();
        foreach (var (npcId, owner, idle) in _leases.Snapshot())
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["npcId"] = npcId,
                ["owner"] = owner,
                ["idleSeconds"] = idle,
            });
        }

        return Json(new Dictionary<string, object?>
        {
            ["timeoutSeconds"] = _leases.TimeoutSeconds,
            ["leases"] = rows,
        });
    }

    private string Describe(int npcId, out bool isError)
    {
        var text = _host.Read(world =>
        {
            if (!world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc))
            {
                return null;
            }

            // ⭐ Тот же сборщик, что кормит §32.15. Не копия — он сам.
            var context = LlmDecisionContextBuilder.Build(world, npc);
            return Json(new Dictionary<string, object?>
            {
                ["npcId"] = npc.Id.Value,
                ["name"] = npc.DisplayName ?? string.Empty,
                ["tick"] = context.Tick,
                ["position"] = new Dictionary<string, object?>
                {
                    ["x"] = Round(context.Position.X),
                    ["y"] = Round(context.Position.Y),
                },
                ["tile"] = $"{npc.Tile.Q},{npc.Tile.R}",
                ["manualControl"] = npc.Mind.ManualControl,
                ["leaseHolder"] = Holder(npc.Id.Value),
                ["stateSummary"] = context.StateSummary,
                ["perceptionSummary"] = context.PerceptionSummary,
                ["memorySummary"] = context.MemorySummary,
                ["inventory"] = Inventory(npc),
            });
        });

        if (text == null)
        {
            isError = true;
            return $"Нет колонистки с id {npcId} — сверься со списком (list_colonists).";
        }

        isError = false;
        return text;
    }

    private static List<object> Inventory(NPCState npc)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in npc.Inventory.Items)
        {
            counts.TryGetValue(item.DefinitionId, out var had);
            counts[item.DefinitionId] = had + 1;
        }

        var rows = new List<object>();
        foreach (var pair in counts)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["itemId"] = pair.Key,
                ["count"] = pair.Value,
            });
        }

        return rows;
    }

    private string Acquire(int npcId, string owner, out bool isError)
    {
        var known = _host.Read(world =>
            world.Entities.Npcs.ContainsKey(new EntityId(npcId)));
        if (!known)
        {
            isError = true;
            return $"Нет колонистки с id {npcId}.";
        }

        if (!_leases.TryAcquire(npcId, owner, out var leaseId, out var heldBy))
        {
            isError = true;
            return $"Колонистка {npcId} уже под управлением другого агента ({heldBy}). " +
                   $"Лиз освободится сам после {_leases.TimeoutSeconds} с без команд.";
        }

        // Лиз — это право говорить; ручной режим — это то, что мир слышит.
        // Второе без первого пустило бы к ней ИИ, первое без второго оставило
        // бы приказы без исполнителя, поэтому они всегда вместе.
        var admission = _host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(npcId), true));

        isError = !admission.Accepted;
        if (!admission.Accepted)
        {
            _leases.Release(npcId, owner);
            return $"Мир отказал во включении ручного режима: {admission.Reason}";
        }

        return Json(new Dictionary<string, object?>
        {
            ["npcId"] = npcId,
            ["leaseId"] = leaseId,
            ["owner"] = owner,
            ["timeoutSeconds"] = _leases.TimeoutSeconds,
            ["note"] = "Лиз продлевается каждой командой. release_control по окончании работы.",
        });
    }

    private string Release(int npcId, string owner, out bool isError)
    {
        if (!_leases.TryRenew(npcId, owner, out var heldBy))
        {
            isError = true;
            return heldBy.Length == 0
                ? $"Колонистка {npcId} и так свободна."
                : $"Колонисткой {npcId} владеет другой агент ({heldBy}).";
        }

        var admission = _host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(npcId), false));
        _leases.Release(npcId, owner);

        isError = false;
        return Json(new Dictionary<string, object?>
        {
            ["npcId"] = npcId,
            ["released"] = true,
            ["worldAccepted"] = admission.Accepted,
            ["reason"] = admission.Reason,
        });
    }

    private string MoveTo(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var x = Number(arguments, "x");
        var y = Number(arguments, "y");
        var run = Bool(arguments, "run", false);
        return Submit(npcId, owner,
            npc => new MoveToCommand(npc, new Float2(x, y), run), out isError);
    }

    private string Interact(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var objectId = Int(arguments, "objectId");
        var verb = Text(arguments, "interaction");
        if (!Enum.TryParse<InteractionType>(verb, ignoreCase: true, out var interaction))
        {
            isError = true;
            return $"Неизвестное взаимодействие «{verb}». Допустимые: " +
                   string.Join(", ", Enum.GetNames(typeof(InteractionType)));
        }

        return Submit(npcId, owner,
            npc => new InteractCommand(npc, new ObjectId(objectId), interaction), out isError);
    }

    private string Craft(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var goalName = Text(arguments, "recipeGoal");
        if (!Enum.TryParse<GoalType>(goalName, ignoreCase: true, out var goal) ||
            !RecipeCatalog.ByGoal.ContainsKey(goal))
        {
            isError = true;
            var known = new List<string>();
            foreach (var pair in RecipeCatalog.ByGoal)
            {
                known.Add(pair.Key.ToString());
            }

            known.Sort(StringComparer.Ordinal);
            return $"«{goalName}» — не цель рецепта. Доступные: {string.Join(", ", known)}";
        }

        return Submit(npcId, owner, npc => new CraftItemCommand(npc, goal), out isError);
    }

    private string AttackNpc(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var target = Int(arguments, "targetNpcId");
        return Submit(npcId, owner,
            npc => new AttackNpcCommand(npc, new EntityId(target)), out isError);
    }

    private string AttackMob(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var mobId = Int(arguments, "mobId");
        return Submit(npcId, owner, npc => new AttackMobCommand(npc, mobId), out isError);
    }

    // ⭐ Во всех хелперах ниже СНАЧАЛА читаются все обязательные аргументы
    // (Int/Text бросают именной отказ на недостающий), и только ПОТОМ
    // валидируются значения енумов: отказ «нет параметра X» обязан приходить
    // независимо от мусора в остальных — за этим следит контрактный гейт.

    // §121.9: вид помощи выбирает агент явно — как игрок в подменю.
    private string AidPerson(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var wardId = Int(arguments, "targetNpcId");
        var kindName = Text(arguments, "kind");
        if (!Enum.TryParse<AidKind>(kindName, ignoreCase: true, out var kind) ||
            kind == AidKind.None)
        {
            isError = true;
            return $"Неизвестный вид помощи «{kindName}». Допустимые: " +
                   "Feed, Hydrate, Treat, Medicate, Console.";
        }

        var ward = new EntityId(wardId);
        return Submit(npcId, owner,
            npc => new AidPersonCommand(npc, ward, kind), out isError);
    }

    private string SelfAction(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var kindName = Text(arguments, "kind");
        if (!Enum.TryParse<SelfActionKind>(kindName, ignoreCase: true, out var kind))
        {
            isError = true;
            return $"Неизвестное самодействие «{kindName}». Допустимые: " +
                   string.Join(", ", Enum.GetNames(typeof(SelfActionKind)));
        }

        return Submit(npcId, owner,
            npc => new SelfActionCommand(npc, kind), out isError);
    }

    private string ManageInventory(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var sourceName = Text(arguments, "source");
        var index = Int(arguments, "index");
        var expected = Text(arguments, "expectedDefinitionId");
        var actionName = Text(arguments, "action");
        if (!TryEnum<InventoryItemSource>(sourceName, "source", out var source, out var error) ||
            !TryEnum<InventoryAction>(actionName, "action", out var action, out error))
        {
            isError = true;
            return error;
        }

        var item = new InventoryItemRef(source, index, expected);
        return Submit(npcId, owner,
            npc => new ManageInventoryCommand(npc, item, action), out isError);
    }

    private string TransferInventory(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var otherId = Int(arguments, "otherNpcId");
        var sourceName = Text(arguments, "source");
        var index = Int(arguments, "index");
        var expected = Text(arguments, "expectedDefinitionId");
        var directionName = Text(arguments, "direction");
        if (!TryEnum<InventoryItemSource>(sourceName, "source", out var source, out var error) ||
            !TryEnum<InventoryTransferDirection>(
                directionName, "direction", out var direction, out error))
        {
            isError = true;
            return error;
        }

        var count = OptionalInt(arguments, "count") ?? 1;
        var item = new InventoryItemRef(source, index, expected);
        var other = new EntityId(otherId);
        return Submit(npcId, owner,
            npc => new TransferInventoryCommand(npc, other, item, count, direction),
            out isError);
    }

    private string TransferContainer(JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var containerId = Int(arguments, "containerObjectId");
        var slotIndex = Int(arguments, "slotIndex");
        var expected = Text(arguments, "expectedDefinitionId");
        var directionName = Text(arguments, "direction");
        if (!TryEnum<InventoryTransferDirection>(
                directionName, "direction", out var direction, out var error))
        {
            isError = true;
            return error;
        }

        var count = OptionalInt(arguments, "count") ?? 1;
        var container = new ObjectId(containerId);
        return Submit(npcId, owner,
            npc => new TransferContainerCommand(
                npc, container, slotIndex, expected, count, direction),
            out isError);
    }

    private static bool TryEnum<T>(
        string text, string name, out T value, out string error)
        where T : struct, Enum
    {
        error = string.Empty;
        if (Enum.TryParse(text, ignoreCase: true, out value))
        {
            return true;
        }

        error = $"Неизвестное значение «{text}» для {name}. Допустимые: " +
                string.Join(", ", Enum.GetNames(typeof(T)));
        return false;
    }

    private string Simple(JsonElement arguments, string owner,
        Func<EntityId, ISimulationCommand> build, out bool isError) =>
        Submit(Int(arguments, "npcId"), owner, build, out isError);

    /// <summary>
    /// Единственная дорога от инструмента до мира: сверить лиз, отдать команду
    /// шву, вернуть его вердикт как есть. Ни одного «а вот тут можно и без
    /// лиза» — иначе весь смысл владения теряется на первом же исключении.
    /// </summary>
    private string Submit(int npcId, string owner,
        Func<EntityId, ISimulationCommand> build, out bool isError)
    {
        if (!_leases.TryRenew(npcId, owner, out var heldBy))
        {
            isError = true;
            return heldBy.Length == 0
                ? $"Колонистка {npcId} не под вашим управлением — сначала acquire_control."
                : $"Колонисткой {npcId} владеет другой агент ({heldBy}).";
        }

        var admission = _host.SubmitManualCommand(build(new EntityId(npcId)));
        isError = !admission.Accepted;
        return Json(new Dictionary<string, object?>
        {
            ["npcId"] = npcId,
            ["order"] = admission.Order,
            ["status"] = admission.Status.ToString(),
            ["reason"] = admission.Reason,
        });
    }

    private string Holder(int npcId)
    {
        var holder = _leases.HolderOf(npcId);
        return holder.Length == 0 ? string.Empty : holder;
    }

    // ── аргументы и вывод ─────────────────────────────────────────────────

    private sealed class McpArgumentException : Exception
    {
        public McpArgumentException(string message) : base(message)
        {
        }
    }

    private static int Int(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        throw new McpArgumentException($"Нужен целочисленный параметр «{name}».");
    }

    /// <summary>
    /// Необязательное целое: отсутствует — <c>null</c>, а не ноль. Ноль здесь
    /// значащий (вотермарка 0 = «с самого начала кольца»), поэтому подменять им
    /// пропуск нельзя.
    /// </summary>
    private static int? OptionalInt(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long? OptionalLong(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static float Number(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return (float)number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed))
            {
                return (float)parsed;
            }
        }

        throw new McpArgumentException($"Нужен числовой параметр «{name}».");
    }

    private static bool Bool(JsonElement arguments, string name, bool fallback)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
        }

        return fallback;
    }

    private static string Text(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        throw new McpArgumentException($"Нужен строковый параметр «{name}».");
    }

    private static double Round(float value) => Math.Round(value, 3);

    private static string Json(object payload) =>
        JsonSerializer.Serialize(payload, McpJson.Options);

    /// <summary>
    /// Схема инструмента. Пишется здесь руками и намеренно узкой: описание
    /// каждого параметра — это то единственное, что агент прочитает перед
    /// первым вызовом.
    /// </summary>
    private static JsonElement Schema(
        params (string name, string type, string description, bool required)[] properties)
    {
        var builder = new StringBuilder();
        builder.Append("{\"type\":\"object\",\"properties\":{");
        var required = new List<string>();
        for (var i = 0; i < properties.Length; i++)
        {
            var (name, type, description, isRequired) = properties[i];
            if (i > 0) builder.Append(',');
            builder.Append(JsonSerializer.Serialize(name)).Append(":{\"type\":")
                .Append(JsonSerializer.Serialize(type)).Append(",\"description\":")
                .Append(JsonSerializer.Serialize(description)).Append('}');
            if (isRequired) required.Add(name);
        }

        builder.Append("},\"required\":[");
        for (var i = 0; i < required.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(JsonSerializer.Serialize(required[i]));
        }

        builder.Append("]}");
        return JsonDocument.Parse(builder.ToString()).RootElement.Clone();
    }
}

internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

}

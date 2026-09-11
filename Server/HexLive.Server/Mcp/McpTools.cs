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
using HexLive.Simulation.Social;
using HexLive.Simulation.Wire;

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
    private readonly Func<WorldHost> _currentHost;
    private readonly Func<int> _currentWorldGeneration;
    private readonly ControlLeases _leases;
    private readonly AgentSessionRegistry _agents;
    private readonly SpecLibrary _spec;
    private readonly System.Threading.AsyncLocal<AgentCommandRequest?> _trackedCommand = new();
    private readonly System.Threading.AsyncLocal<AgentCommandResult?> _trackedResult = new();

    public McpTools(WorldHost host, ControlLeases leases, SpecLibrary? spec = null)
        : this(() => host, () => 0, leases, new AgentSessionRegistry(), spec)
    {
    }

    /// <summary>
    /// §144.4/§145.5: HTTP endpoint lives longer than any one colony. Resolve
    /// the current host at the start of every tool call, rather than retaining
    /// the host that existed when the process mapped <c>/mcp</c>.
    /// </summary>
    public McpTools(Func<WorldHost> currentHost, ControlLeases leases, SpecLibrary? spec = null)
        : this(currentHost, () => 0, leases, new AgentSessionRegistry(), spec)
    {
    }

    public McpTools(Func<WorldHost> currentHost, Func<int> currentWorldGeneration,
        ControlLeases leases, AgentSessionRegistry agents, SpecLibrary? spec = null)
    {
        _currentHost = currentHost ?? throw new ArgumentNullException(nameof(currentHost));
        _currentWorldGeneration = currentWorldGeneration ??
            throw new ArgumentNullException(nameof(currentWorldGeneration));
        _leases = leases;
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _spec = spec ?? SpecLibrary.Discover(null);
    }

    // ── каталог ───────────────────────────────────────────────────────────

    public static IReadOnlyList<ToolSpec> Catalog { get; } = new List<ToolSpec>
    {
        new("world_status",
            "Состояние мира: тик, часы, погода, сколько колонисток живо, темп сервера. " +
            "Ничего не меняет.",
            Schema()),

        new("read_agent_command", "Квитанция команды и последний принятый номер. unknown не означает разрешение повторить команду.",
            Schema(("npcId", "integer", "исполнитель", true), ("sequence", "integer", "номер команды; 0 для чтения верхней границы", true),
                ("commandId", "string", "ID команды; пусто при sequence=0", true))),
        new("execute_agent_command", "Идемпотентная обёртка штатной команды: сохраняет номер и результат в том же мире. Требует control lease.",
            Schema(("npcId", "integer", "исполнитель", true), ("sequence", "integer", "следующий номер из read_agent_command", true),
                ("commandId", "string", "уникальный ID шага от контроллера", true), ("tool", "string", "штатный tool действия", true),
                ("arguments", "object", "аргументы действия", true))),

        new("list_colonists",
            "Все колонистки: id, имя, фракция, тайл, здоровье, текущая цель, под ручным ли " +
            "управлением и кто держит лиз. С этого начинают.",
            Schema()),

        new("describe_colonist",
            "Полная картина по одной колонистке: состояние, восприятие (объекты рядом с их " +
            "id и доступными взаимодействиями, союзницы, враги и звери) и память. Ровно " +
            "тот текст, который получает LLM-контур. Отсюда берут objectId для interact " +
            "и mobId для attack_mob. effects и effectImpacts — статусы и текущие причины " +
            "изменения параметров этого тела, как в UI; effectDefinitions/effectTerms " +
            "дают их канонические объяснения EN/RU.",
            Schema(("npcId", "integer", "id колонистки", true),
                ("perceptionEpoch", "string", "epoch из recentPerception предыдущего завершённого хода", false),
                ("perceptionSince", "integer", "watermark из recentPerception завершённого хода", false))),

        new("query_known_objects",
            "Поиск в личной памяти NPC, включая предметы вне текущего восприятия. " +
            "Не меняет игру и не требует control lease. definitionPrefix: например food.coconut. " +
            "Возвращает последние известные координаты, давность и источник знания; предмет мог исчезнуть. " +
            "catalogInteractions — возможности типа, не обещание текущей доступности. " +
            "В Agent Studio выбор этого tool запрашивает данные перед финальным решением, без публикации предварительной речи. " +
            "Пустой результат означает отсутствие знания, а не отсутствие предметов в мире.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("definitionPrefix", "string", "префикс definition id, до 128 символов; пустой — любые", false),
                   ("interaction", "string", "имя InteractionType для фильтра по каталогу; пустой — любые", false),
                   ("limit", "integer", "1..64, по умолчанию 16", false))),

        new("attach_agent",
            "Прикрепить эту MCP-сессию к одному живому NPC без включения manual mode (§160).",
            Schema(("npcId", "integer", "id NPC", true),
                   ("displayName", "string", "имя агента для UI, до 48 символов", true),
                   ("capabilities", "array", "playerText/speech/worldActions/relationView/journal", true),
                   ("ttlSeconds", "integer", "TTL attachment 15..120, по умолчанию 45", false),
                   ("inboxResumeKey", "string", "закрытый ключ восстановления inbox после разрыва", false))),

        new("agent_heartbeat",
            "Продлить attachment и узнать playerPresent/phase без платного model call.",
            Schema(("attachmentId", "string", "id из attach_agent", true))),

        new("read_agent_inbox",
            "Финальные текстовые реплики игрока после sinceSeq; аудио сюда не попадает.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("sinceSeq", "integer", "последняя обработанная seq", false),
                   ("limit", "integer", "1..16", false))),

        new("ack_agent_inbox",
            "Подтвердить непрерывный прочитанный префикс inbox после durable commit хода.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("throughSeq", "integer", "последняя обработанная seq", true))),

        new("publish_agent_phase",
            "Опубликовать Ready/Thinking/Acting/Speaking/Sleeping/Error и текущий turnId.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("turnId", "string", "id текущего хода", true),
                   ("phase", "string", "публичная фаза", true))),

        new("commit_agent_turn",
            "Идемпотентно завершить ход: короткая мысль, реакция и временный UI read-model.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("turnId", "string", "уникальный id хода", true),
                   ("reaction", "string", "None/Warm/Neutral/Tense/Hostile", true),
                   ("voiceTurn", "boolean", "true только для завершённого голосового хода; фиксирует и None. По умолчанию false", false),
                   ("intentSummary", "string", "до 240 символов, без chain-of-thought", true),
                   ("relationView", "string", "необязательный публичный вид отношений до 1024 символов", false),
                   ("journalEntry", "string", "необязательная свежая запись до 400", false))),

        new("begin_agent_utterance",
            "Начать строгую chunked-загрузку готового PCM16 mono 44.1 kHz WAV.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("utteranceId", "string", "идемпотентный id реплики", true),
                   ("turnId", "string", "id хода", true),
                   ("language", "string", "язык текста", true),
                   ("text", "string", "точный субтитр до 600", true),
                   ("emotion", "string", "эмоция", true),
                   ("delivery", "string", "player_reply или world", true),
                   ("priority", "string", "Talk или Ambient", true),
                   ("totalBytes", "integer", "размер WAV до 3 MiB", true),
                   ("durationMs", "integer", "0..60000", true),
                   ("sha256", "string", "SHA-256 WAV hex", true))),

        new("append_agent_utterance",
            "Добавить следующий base64 chunk, максимум 192 KiB после декодирования.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("utteranceId", "string", "id реплики", true),
                   ("chunkIndex", "integer", "строго последовательный индекс с нуля", true),
                   ("base64", "string", "байты WAV", true))),

        new("commit_agent_utterance",
            "Проверить размер/SHA/WAV и передать реплику назначенному viewer.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("utteranceId", "string", "id реплики", true),
                   ("sha256", "string", "итоговый SHA-256", true))),

        new("detach_agent",
            "Снять attachment, очистить временные данные и освободить action lease.",
            Schema(("attachmentId", "string", "id attachment", true),
                   ("preserveInbox", "boolean", "сохранить inbox для сетевого переподключения владельца", false))),

        new("read_build_catalog",
            "Каталог мебели и её ведомости из штатного сборщика площадок. Для продолжения используйте ведомость конкретной видимой стройки.",
            Schema(("definitionId", "string", "id мебели, например bed.basic; без него — весь каталог мебели", false))),

        new("read_recipes",
            "Действующие рецепты: ингредиенты, результат, станция и работа. Без definitionId — список рецептов; с ним — рецепт предмета.",
            Schema(("definitionId", "string", "id результата, например resource.rope", false))),

        new("read_inventory_drop",
            "Проверить место для одного carried-предмета по штатной геометрии Drop. При NoDropSpot ищет подход в радиусе двух тайлов. Не двигает NPC и не резервирует место; маршрут не проверен. После движения перечитать инвентарь и место.",
            Schema(("npcId", "integer", "id колонистки", true),
                ("index", "integer", "актуальный физический sourceIndex carried-предмета", true),
                ("expectedDefinitionId", "string", "definitionId выбранного экземпляра", true))),

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
            Schema(("npcId", "integer", "id колонистки", true),
                   ("ttlSeconds", "integer", "TTL аренды 15..120 секунд", false))),

        new("acquire_npc_control",
            "Имя §159 для той же единой аренды §121: взять NPC под управление только " +
            "на время одного действия компаньона. TTL 15..120 секунд.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("ttlSeconds", "integer", "TTL аренды 15..120 секунд", false))),

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
            "откажет ПО ПРИБЫТИИ — это штатный исход, не ошибка инструмента. " +
            "Необязательная topic выбирает общую тему и штатную реплику Hexkufa, без TTS.",
            TalkToSchema()),

        new("request_item",
            "Попросить у конкретного NPC один предмет по definitionId (§153.4). Владелец добровольно " +
            "соглашается или отказывает. Сначала подойдите: радиус подарка 1.95 wu и проверка " +
            "препятствий; автоматического подхода нет, вне досягаемости TooFar. " +
            "Completed/Transferred означает реальную передачу; отказ не меняет вещи владельца.",
            Schema(("npcId", "integer", "id просящей колонистки", true),
                   ("targetNpcId", "integer", "id владельца предмета", true),
                   ("definitionId", "string", "определение нужного предмета; передаётся ровно один экземпляр", true))),

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
            "Bathe/WashClothes (купание/стирка), EatFromPack/DrinkFromPack (из рюкзака), " +
            "GoHome (бежать в собственный домашний лагерь; координаты не нужны, " +
            "маршрут выбирает симуляция; NoRouteToCamp означает отсутствие маршрута).",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("kind", "string",
                    "вид: " + string.Join("/", Enum.GetNames(typeof(SelfActionKind))), true))),

        new("rest_until", "Ограниченный отдых только внутри execute_agent_command: Energy — сон на земле, Stamina — отдых сидя. Сервер сам завершит отдых при достижении target; прерывание считается сбоем.",
            Schema(("npcId", "integer", "id колонистки", true),
                ("need", "string", "Energy или Stamina", true),
                ("target", "number", "целевой показатель больше 0 и не больше 1", true))),

        new("merge_camps",
            "Добровольно объединить два женских лагеря (§146.12), включая всех их жителей. " +
            "useTargetCamp=false оставляет общий дом в лагере npcId, true — в лагере targetNpcId. " +
            "Нужно подойти для разговора; обе направленные Affinity строго выше 0.50. " +
            "Нельзя пригласить бессознательную, лежащую, переносимую или сражающуюся NPC. " +
            "Это не принудительный перевод одной девушки: отказы TargetUnavailable, " +
            "RelationshipTooLow, TooFarToTalk сохраняют мир без изменений.",
            Schema(("npcId", "integer", "кто предлагает объединение", true),
                   ("targetNpcId", "integer", "кому предлагает", true),
                   ("useTargetCamp", "boolean", "true — её дом; false — наш дом (по умолчанию)", false))),

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
            "картинки (id не совпал — приказ честно отклоняется). Удаление сдвигает следующие " +
            "физические индексы. Для очереди Drop отдельных обычных предметов используй " +
            "убывающие исходные индексы; после Wear/Stow/изменения контейнера перечитай инвентарь.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("source", "string", "Carried или Worn", true),
                   ("index", "integer", "номер ячейки", true),
                   ("expectedDefinitionId", "string", "ожидаемый id предмета в ячейке", true),
                   ("action", "string", "Wear/Stow/Drop", true))),

        new("transfer_inventory",
            "Give: подарить предмет живому человеку (§153); Take: взять у беспомощного по §128. Проверки цели и предмета выполняются при исполнении.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("otherNpcId", "integer", "id получателя подарка или цели обыска", true),
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

    public sealed record ToolSpec(string Name, string Description,
        [property: System.Text.Json.Serialization.JsonIgnore] JsonElement RawInputSchema)
    {
        public JsonElement InputSchema => McpActionSchemas.Enrich(Name, RawInputSchema);
    }

    // ── исполнение ────────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает текст ответа. Отказ — это тоже ответ (<paramref name="isError"/>),
    /// а не исключение: агенту нужна причина, чтобы попробовать иначе.
    /// </summary>
    public string Call(string name, JsonElement arguments, string owner, out bool isError,
        Func<int, bool>? canAccessNpc = null)
    {
        // Bind exactly once per JSON-RPC tool invocation. A world swap between
        // calls must be visible; a single call must never mix two worlds.
        var host = _currentHost();
        isError = false;
        try
        {
            // §163: scope is supplied by the authenticated transport, never by tool arguments.
            // Unknown/new tools fail closed until assigned an explicit scope below.
            if (canAccessNpc != null && !IsWithinPlayerScope(name, arguments, owner, canAccessNpc))
            {
                isError = true;
                return Json(new { error = "NpcAccessDenied" });
            }
            switch (name)
            {
                case "read_agent_command": return Json(host.ReadAgentCommand(Int(arguments, "npcId"),
                    OptionalLong(arguments, "sequence") ?? throw new McpArgumentException("Нужен параметр sequence"), Text(arguments, "commandId")));
                case "execute_agent_command": return ExecuteAgentCommand(arguments, owner, out isError);
                case "world_status": return WorldStatus(host);
                case "list_colonists": return ListColonists(host, canAccessNpc);
                case "list_leases": return ListLeases(canAccessNpc);
                case "read_events": return ReadEvents(host, arguments);
                case "read_spec": return ReadSpec(arguments, out isError);
                case "read_recipes": return host.Read(_ => McpPlanningObservations.Recipes(OptionalText(arguments, "definitionId") ?? ""));
                case "read_build_catalog": return host.Read(_ => McpPlanningObservations.BuildCatalog(OptionalText(arguments, "definitionId") ?? ""));
                case "read_inventory_drop": return host.Read(w => McpPlanningObservations.InventoryDrop(w,
                    Int(arguments, "npcId"), Int(arguments, "index"), Text(arguments, "expectedDefinitionId")));
                case "query_known_objects": return QueryKnownObjects(host, arguments, out isError);
                case "describe_colonist": return Describe(host, Int(arguments, "npcId"), out isError,
                    canAccessNpc != null, owner, OptionalText(arguments, "perceptionEpoch") ?? "",
                    OptionalLong(arguments, "perceptionSince") ?? 0);
                case "attach_agent": return AttachAgent(host, arguments, owner, out isError);
                case "agent_heartbeat": return AgentHeartbeat(arguments, owner, out isError);
                case "read_agent_inbox": return ReadAgentInbox(arguments, owner, out isError);
                case "ack_agent_inbox":
                    var throughSeq = OptionalLong(arguments, "throughSeq") ??
                        throw new McpArgumentException("Нужен целочисленный параметр throughSeq");
                    var acknowledged = _agents.TryAcknowledgeInbox(Text(arguments, "attachmentId"), owner,
                        _currentWorldGeneration(), throughSeq, out var ackReason);
                    isError = !acknowledged;
                    return acknowledged ? Json(new { accepted = true }) : ackReason;
                case "publish_agent_phase": return PublishAgentPhase(arguments, owner, out isError);
                case "commit_agent_turn": return CommitAgentTurn(host, arguments, owner, out isError);
                case "begin_agent_utterance": return BeginAgentUtterance(arguments, owner, out isError);
                case "append_agent_utterance": return AppendAgentUtterance(arguments, owner, out isError);
                case "commit_agent_utterance": return CommitAgentUtterance(arguments, owner, out isError);
                case "detach_agent": return DetachAgent(host, arguments, owner, out isError);
                case "acquire_control":
                case "acquire_npc_control":
                    return Acquire(host, arguments, owner, out isError);
                case "release_control": return Release(host, Int(arguments, "npcId"), owner, out isError);
                case "move_to": return MoveTo(host, arguments, owner, out isError);
                case "interact": return Interact(host, arguments, owner, out isError);
                case "craft_item": return Craft(host, arguments, owner, out isError);
                case "attack_npc": return AttackNpc(host, arguments, owner, out isError);
                case "attack_mob": return AttackMob(host, arguments, owner, out isError);
                case "stop": return Simple(host, arguments, owner, npc => new StopCommand(npc), out isError);
                // ⭐ Обязательные аргументы читаются ДО Submit (вне лямбды):
                // отказ «нет параметра X» обязан прийти и без лиза — за этим
                // следит контрактный гейт каталога.
                case "talk_to":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    TalkTopic? topic = null;
                    if (arguments.TryGetProperty("topic", out _))
                    {
                        var requested = Text(arguments, "topic");
                        if (!Enum.TryParse<TalkTopic>(requested, out var parsed) ||
                            !TalkTopicRequest.IsAllowed(parsed) || parsed.ToString() != requested)
                            throw new McpArgumentException("InvalidTalkTopic: topic должен быть одним из enum схемы talk_to.");
                        topic = parsed;
                    }
                    return Simple(host, arguments, owner,
                        npc => new TalkToCommand(npc, target, topic), out isError);
                }

                case "aid_person": return AidPerson(host, arguments, owner, out isError);
                case "treat_limbs":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(host, arguments, owner,
                        npc => new TreatLimbsCommand(npc, target), out isError);
                }

                case "self_action": return SelfAction(host, arguments, owner, out isError);
                case "rest_until":
                    var restNpc = Int(arguments, "npcId");
                    var restNeed = Text(arguments, "need");
                    var restTarget = Number(arguments, "target");
                    if (_trackedCommand.Value is not { RestNeed.Length: > 0 } rest)
                        throw new McpArgumentException("RestRequiresTrackedExecution");
                    if (rest.RestNeed != restNeed || rest.RestTarget != restTarget)
                        throw new McpArgumentException("RestTargetMismatch");
                    return Submit(host, restNpc, owner,
                        npc => new SelfActionCommand(npc, rest.RestNeed == "Energy" ? SelfActionKind.GroundSleep : SelfActionKind.GroundSit), out isError);
                case "merge_camps":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    var useTargetCamp = Bool(arguments, "useTargetCamp", false);
                    return Simple(host, arguments, owner,
                        npc => new MergeCampsCommand(npc, target, useTargetCamp), out isError);
                }
                case "carry_person":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(host, arguments, owner,
                        npc => new CarryPersonCommand(npc, target), out isError);
                }

                case "put_down_person":
                    return Simple(host, arguments, owner, npc => new PutDownPersonCommand(npc), out isError);
                case "put_person_in_bed":
                {
                    var bed = new ObjectId(Int(arguments, "bedObjectId"));
                    return Simple(host, arguments, owner,
                        npc => new PutPersonInBedCommand(npc, bed), out isError);
                }

                case "manage_inventory": return ManageInventory(host, arguments, owner, out isError);
                case "request_item": return RequestItem(host, arguments, owner, out isError);
                case "transfer_inventory": return TransferInventory(host, arguments, owner, out isError);
                case "transfer_container": return TransferContainer(host, arguments, owner, out isError);
                case "prey_person":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(host, arguments, owner,
                        npc => new PreyPersonCommand(npc, target), out isError);
                }

                case "abuse_person":
                {
                    var target = new EntityId(Int(arguments, "targetNpcId"));
                    return Simple(host, arguments, owner,
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

    private static string WorldStatus(WorldHost host)
    {
        var census = host.Census();
        return host.Read(world => Json(new Dictionary<string, object?>
        {
            ["tick"] = world.Tick,
            ["worldId"] = host.WorldId,
            ["seed"] = world.Seed,
            ["mode"] = world.Mode.ToString(),
            ["clock"] = world.Environment.TimeOfDayNormalized,
            ["dayLengthTicks"] = WorldBalance.DayLengthTicks,
            ["gameHour"] = (long)(world.Tick * 24d / WorldBalance.DayLengthTicks),
            ["phase"] = world.Environment.Phase.ToString(),
            ["temperature"] = world.Environment.GlobalTemperature,
            ["raining"] = world.Environment.IsRaining,
            ["colonistsAlive"] = census.alive,
            ["colonistsTotal"] = census.total,
            ["objects"] = census.objects,
            ["paused"] = host.IsPaused,
            ["speedMultiplier"] = host.SpeedMultiplier,
            ["ticksPerSecond"] = Math.Round(host.MeasuredTicksPerSecond, 2),
            ["averageTickMs"] = Math.Round(host.AverageTickMs, 2),
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
    private static string ReadEvents(WorldHost host, JsonElement arguments)
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
        var batch = host.ReadEvents(OptionalLong(arguments, "sinceSeq"), limit, entityId);

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
            ["sessionEpoch"] = host.McpSessionEpoch,
        });
    }

    private bool IsWithinPlayerScope(string name, JsonElement arguments, string owner, Func<int, bool> allowed)
    {
        if (name is "world_status" or "read_spec" or "read_recipes" or "read_build_catalog" or "list_colonists" or "list_leases") return true;
        if (name is "agent_heartbeat" or "read_agent_inbox" or "ack_agent_inbox" or "publish_agent_phase" or
            "commit_agent_turn" or "begin_agent_utterance" or "append_agent_utterance" or
            "commit_agent_utterance" or "detach_agent")
            return arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty("attachmentId", out var attachment) && attachment.ValueKind == JsonValueKind.String &&
                _agents.TryGetOwnedNpcId(attachment.GetString()!, owner, _currentWorldGeneration(), out var npc) && allowed(npc);
        // Catalog membership prevents an unreviewed future global tool from gaining access by
        // accepting an otherwise irrelevant npcId. Every remaining current tool is NPC-scoped.
        var npcScoped = false;
        foreach (var tool in Catalog)
            if (tool.Name == name && tool.InputSchema.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("npcId", out _)) { npcScoped = true; break; }
        return npcScoped && arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("npcId", out var id) && id.ValueKind == JsonValueKind.Number &&
            id.TryGetInt32(out var value) && allowed(value);
    }

    private string ListColonists(WorldHost host, Func<int, bool>? canAccessNpc = null)
    {
        // Assignment reconciliation takes assignments -> world locks. Never invert that
        // order by calling its predicate while holding the world read lock.
        if (canAccessNpc != null)
        {
            var ids = host.Read(world =>
            {
                var result = new List<int>();
                foreach (var npc in world.Entities.Npcs.Values) result.Add(npc.Id.Value);
                return result;
            });
            var allowed = new HashSet<int>();
            foreach (var id in ids) if (canAccessNpc(id)) allowed.Add(id);
            canAccessNpc = allowed.Contains;
        }
        return host.Read(world =>
        {
            var rows = new List<object>();
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (canAccessNpc != null && !canAccessNpc(npc.Id.Value)) continue;
                rows.Add(new Dictionary<string, object?>
                {
                    ["npcId"] = npc.Id.Value,
                    ["profileId"] = npc.ProfileId ?? string.Empty,
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
                    ["carriedNpcId"] = npc.CarriedNpcId?.Value,
                    ["carriedByNpcId"] = npc.CarriedByNpcId?.Value,
                    ["unconscious"] = npc.IsUnconscious(world.Tick),
                    ["leaseHolder"] = canAccessNpc == null ? Holder(npc.Id.Value) : (Holder(npc.Id.Value).Length == 0 ? string.Empty : "occupied"),
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

    private string ListLeases(Func<int, bool>? canAccessNpc = null)
    {
        var rows = new List<object>();
        foreach (var (npcId, owner, idle) in _leases.Snapshot())
        {
            if (canAccessNpc != null && !canAccessNpc(npcId)) continue;
            rows.Add(new Dictionary<string, object?>
            {
                ["npcId"] = npcId,
                ["owner"] = canAccessNpc == null ? owner : "occupied",
                ["idleSeconds"] = idle,
            });
        }

        return Json(new Dictionary<string, object?>
        {
            ["timeoutSeconds"] = _leases.TimeoutSeconds,
            ["leases"] = rows,
        });
    }

    private static string QueryKnownObjects(WorldHost host, JsonElement arguments, out bool isError)
    {
        isError = true;
        const string invalid = "{\"error\":\"InvalidKnowledgeQueryArguments\"}";
        if (arguments.ValueKind != JsonValueKind.Object) return invalid;
        if (!arguments.TryGetProperty("npcId", out _))
            return "{\"error\":\"MissingKnowledgeQueryArgument\",\"parameter\":\"npcId\"}";
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var npcId = 0;
        var prefix = "";
        InteractionType? interaction = null;
        var limit = McpKnownObjectObservations.DefaultLimit;
        foreach (var field in arguments.EnumerateObject())
        {
            if (!fields.Add(field.Name)) return invalid;
            switch (field.Name)
            {
                case "npcId":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out npcId) || npcId < 1) return invalid;
                    break;
                case "definitionPrefix":
                    if (field.Value.ValueKind != JsonValueKind.String) return invalid;
                    prefix = field.Value.GetString()!;
                    if (prefix.Length > 128) return invalid;
                    break;
                case "interaction":
                    if (field.Value.ValueKind != JsonValueKind.String) return invalid;
                    // Optional filters emitted by structured model responses may be empty.
                    // Only an empty string means no filter; unknown names still fail closed.
                    if (field.Value.GetString()!.Length == 0) break;
                    if (!Enum.TryParse<InteractionType>(field.Value.GetString(), true, out var parsed) ||
                        !string.Equals(Enum.GetName(parsed), field.Value.GetString(), StringComparison.OrdinalIgnoreCase)) return invalid;
                    interaction = parsed;
                    break;
                case "limit":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out limit) ||
                        limit < 1 || limit > McpKnownObjectObservations.MaximumLimit) return invalid;
                    break;
                default: return invalid;
            }
        }
        if (!fields.Contains("npcId")) return invalid;
        var query = new McpKnownObjectObservations.Query(prefix, interaction, limit);
        var result = host.Read(world => world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc)
            ? McpKnownObjectObservations.Read(world, npc, query, host.WorldId) : null);
        if (result == null) return "{\"error\":\"NpcMissing\"}";
        isError = false;
        return Json(result);
    }

    private string Describe(WorldHost host, int npcId, out bool isError, bool redactOwner = false,
        string owner = "", string perceptionEpoch = "", long perceptionSince = 0)
    {
        var observations = _agents.GetPerception(npcId, owner, _currentWorldGeneration());
        var text = host.Read(world =>
        {
            if (!world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc))
            {
                return null;
            }

            // ⭐ Тот же сборщик, что кормит §32.15. Не копия — он сам.
            var context = LlmDecisionContextBuilder.Build(world, npc);
            var effects = McpEffectObservations.Read(world, npc);
            return Json(new Dictionary<string, object?>
            {
                ["npcId"] = npc.Id.Value,
                ["profileId"] = npc.ProfileId ?? string.Empty,
                ["name"] = npc.DisplayName ?? string.Empty,
                ["tick"] = context.Tick,
                ["position"] = new Dictionary<string, object?>
                {
                    ["x"] = Round(context.Position.X),
                    ["y"] = Round(context.Position.Y),
                },
                ["tile"] = $"{npc.Tile.Q},{npc.Tile.R}",
                ["manualControl"] = npc.Mind.ManualControl,
                ["carriedNpcId"] = npc.CarriedNpcId?.Value,
                ["carriedByNpcId"] = npc.CarriedByNpcId?.Value,
                ["leaseHolder"] = redactOwner ? (Holder(npc.Id.Value).Length == 0 ? string.Empty : "occupied") : Holder(npc.Id.Value),
                // Gameplay-language progress is part of the body/world adapter;
                // §159's personal memories remain in the local Masha archive.
                ["hexkufaExposure"] = npc.HexkufaExposure,
                // One-release migration envelope. AgentHost imports it into
                // its local workspace; no generic turn writes these fields.
                ["legacyAgentState"] = LegacyAgentState(npc),
                ["stateSummary"] = context.StateSummary,
                ["bodyNeeds"] = McpPlanningObservations.Needs(npc),
                ["inOwnCamp"] = ColonyQueries.InCamp(world, npc.Tile, npc.Faction),
                ["execution"] = McpPlanningObservations.Execution(npc),
                ["effects"] = effects.Effects,
                ["effectImpacts"] = effects.Impacts,
                ["effectDefinitions"] = effects.Definitions,
                ["effectTerms"] = effects.Terms,
                ["perceptionSummary"] = context.PerceptionSummary,
                ["memorySummary"] = context.MemorySummary,
                ["inventory"] = Inventory(npc),
                ["inventorySummary"] = McpInventoryObservations.Read(world, npc),
                ["visibleItems"] = McpItemObservations.Visible(world, npc),
                ["visibleNpcs"] = McpNpcObservations.Visible(world, npc),
                ["recentPerception"] = McpPerceptionObservations.Recent(
                    observations?.Read(perceptionEpoch, perceptionSince, world.Tick)),
                ["inventoryItems"] = McpItemObservations.Carried(world, npc),
                ["wornItems"] = McpItemObservations.Worn(world, npc),
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

    private static object? LegacyAgentState(NPCState npc)
    {
        var legacy = npc.Companion;
        if (legacy.Memories.Count == 0 && legacy.NarrativeJournal.Count == 0 &&
            legacy.LastIntentSummary.Length == 0 && legacy.HexkufaExposure == 0 &&
            legacy.PlayerVoiceBond.Familiarity <= 0f && legacy.PlayerVoiceBond.Trust <= 0f &&
            legacy.PlayerVoiceBond.Affinity <= 0f)
            return null;

        var memories = new List<object>();
        foreach (var memory in legacy.Memories)
        {
            memories.Add(new Dictionary<string, object?>
            {
                ["key"] = memory.Key,
                ["value"] = memory.Value,
                ["importance"] = Round(memory.Importance),
                ["lastUpdatedTick"] = memory.LastUpdatedTick,
            });
        }
        var journal = new List<object>();
        foreach (var entry in legacy.NarrativeJournal)
            journal.Add(new Dictionary<string, object?>
                { ["tick"] = entry.Tick, ["text"] = entry.Text });
        return new Dictionary<string, object?>
        {
            ["hexkufaExposure"] = legacy.HexkufaExposure,
            ["lastIntentSummary"] = legacy.LastIntentSummary,
            ["playerVoiceBond"] = new Dictionary<string, object?>
            {
                ["familiarity"] = Round(legacy.PlayerVoiceBond.Familiarity),
                ["trust"] = Round(legacy.PlayerVoiceBond.Trust),
                ["affinity"] = Round(legacy.PlayerVoiceBond.Affinity),
                ["lastInteractionTick"] = legacy.PlayerVoiceBond.LastInteractionTick,
            },
            ["memories"] = memories,
            ["journal"] = journal,
        };
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

    // ── §160 generic agent attachment ────────────────────────────────────

    private string AttachAgent(WorldHost host, JsonElement arguments, string owner,
        out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var displayName = Text(arguments, "displayName").Trim();
        var ttl = OptionalInt(arguments, "ttlSeconds") ?? AgentSessionRegistry.DefaultTtlSeconds;
        if (displayName.Length is < 1 or > 48)
        {
            isError = true;
            return "displayName должен содержать 1..48 символов.";
        }
        if (ttl is < 15 or > 120)
        {
            isError = true;
            return "ttlSeconds должен быть в диапазоне 15..120.";
        }
        if (!TryCapabilities(arguments, out var capabilities, out var capabilityError))
        {
            isError = true;
            return capabilityError;
        }

        var alive = host.Read(world => world.Entities.Npcs.TryGetValue(
            new EntityId(npcId), out var npc) && npc.Health > 0f && !npc.IsDying);
        if (!alive)
        {
            isError = true;
            return $"Нет живого NPC с id {npcId}.";
        }

        // An attachment may take over the assigned player's manual controls,
        // but must not steal another MCP session's in-flight physical action.
        foreach (var lease in _leases.Snapshot())
        {
            if (lease.npcId == npcId && lease.owner != owner &&
                !lease.owner.StartsWith("ws:", StringComparison.Ordinal))
            {
                isError = true;
                return "ActionLeaseBusy";
            }
        }

        var initialEventWatermark = host.ReadEvents(null, 1, npcId).Watermark;
        var accepted = _agents.TryAttach(npcId, owner, _currentWorldGeneration(), displayName,
            capabilities, ttl, out var snapshot, out var reason, OptionalText(arguments, "inboxResumeKey"));
        if (accepted)
        {
            var observations = _agents.BindPerception(snapshot.AttachmentId, owner, _currentWorldGeneration());
            var control = _agents.BindControl(snapshot.AttachmentId, owner, _currentWorldGeneration());
            var ownAction = false;
            foreach (var lease in _leases.Snapshot())
            {
                if (lease.npcId != npcId) continue;
                if (lease.owner == owner) ownAction = true;
                else if (control != null && lease.owner.StartsWith("ws:", StringComparison.Ordinal))
                    _leases.Release(npcId, lease.owner);
            }
            host.Read(world =>
            {
                // A concurrent detach may have disposed this attachment after
                // lookup. Never overwrite a replacement's live buffer with it.
                if (observations != null &&
                    world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc) &&
                    observations.Capture(world, npc)) npc.Perception.Observations = observations;
                if (control != null && world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var controlled))
                    control.Bind(world, controlled, ownAction);
                return true;
            });
        }
        isError = !accepted;
        return accepted ? AttachmentJson(snapshot, initialEventWatermark,
            _agents.InboxResumeKey(snapshot.AttachmentId, owner, _currentWorldGeneration())) : reason;
    }

    private string AgentHeartbeat(JsonElement arguments, string owner, out bool isError)
    {
        var accepted = _agents.TryHeartbeat(Text(arguments, "attachmentId"), owner,
            _currentWorldGeneration(), out var snapshot, out var reason);
        isError = !accepted;
        return accepted ? AttachmentJson(snapshot) : reason;
    }

    private string ReadAgentInbox(JsonElement arguments, string owner, out bool isError)
    {
        var since = OptionalLong(arguments, "sinceSeq") ?? 0;
        var limit = OptionalInt(arguments, "limit") ?? AgentSessionRegistry.MaxReadMessages;
        if (since < 0 || limit is < 1 or > AgentSessionRegistry.MaxReadMessages)
        {
            isError = true;
            return "sinceSeq должен быть >=0, limit — 1..16.";
        }
        if (!_agents.TryReadInbox(Text(arguments, "attachmentId"), owner,
                _currentWorldGeneration(), since, limit, out var inbox, out var reason))
        {
            isError = true;
            return reason;
        }

        var messages = new List<object>();
        for (var i = 0; i < inbox.Messages.Length; i++)
        {
            var message = inbox.Messages[i];
            messages.Add(new Dictionary<string, object?>
            {
                ["seq"] = message.Sequence,
                ["messageId"] = message.MessageId,
                ["senderId"] = message.SenderId,
                ["language"] = message.Language,
                ["text"] = message.Text,
                ["createdUtc"] = message.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        isError = false;
        return Json(new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["watermark"] = inbox.Watermark,
            ["gap"] = inbox.Gap,
            ["truncated"] = inbox.Truncated,
        });
    }

    private string PublishAgentPhase(JsonElement arguments, string owner, out bool isError)
    {
        var attachmentId = Text(arguments, "attachmentId");
        var turnId = Bounded(Text(arguments, "turnId"), 80, "turnId");
        var phaseText = Text(arguments, "phase");
        if (!TryEnum<AgentPhase>(phaseText, "phase", out var phase, out var error))
        {
            isError = true;
            return error;
        }
        var accepted = _agents.TryPublishPhase(attachmentId, owner,
            _currentWorldGeneration(), turnId, phase, out var reason);
        isError = !accepted;
        return accepted ? Json(new { turnId, phase = phase.ToString() }) : reason;
    }

    private string CommitAgentTurn(WorldHost host, JsonElement arguments, string owner,
        out bool isError)
    {
        var attachmentId = Text(arguments, "attachmentId");
        var turnId = Bounded(Text(arguments, "turnId").Trim(), 80, "turnId");
        var intent = Bounded(Text(arguments, "intentSummary").Trim(),
            AgentWire.MaxTextCharacters, "intentSummary");
        var relation = Bounded(OptionalText(arguments, "relationView").Trim(),
            AgentWire.MaxRelationCharacters, "relationView");
        var journal = Bounded(OptionalText(arguments, "journalEntry").Trim(),
            AgentWire.MaxJournalCharacters, "journalEntry");
        var voiceTurn = Bool(arguments, "voiceTurn", false);
        if (turnId.Length == 0)
        {
            isError = true;
            return "turnId не может быть пустым.";
        }
        if (!TryEnum<CompanionReaction>(Text(arguments, "reaction"), "reaction",
                out var reaction, out var enumError))
        {
            isError = true;
            return enumError;
        }

        if (!_agents.TryHeartbeat(attachmentId, owner, _currentWorldGeneration(),
                out var attachment, out var reason))
        {
            isError = true;
            return reason;
        }

        // World-side idempotency is committed first. If the process dies before
        // the ephemeral UI commit, retrying applies Social as a no-op and then
        // republishes the view; the inverse order could permanently lose Social.
        if (voiceTurn || reaction != CompanionReaction.None)
        {
            var admission = host.SubmitManualCommand(new RecordAgentSocialCommand(
                new EntityId(attachment.NpcId), turnId, reaction));
            if (!admission.Accepted)
            {
                isError = true;
                return admission.Reason;
            }
        }
        if (!_agents.TryCommitTurn(attachmentId, owner, _currentWorldGeneration(), turnId,
                intent, relation, journal, out var duplicate, out var npcId, out reason))
        {
            isError = true;
            return reason;
        }
        isError = false;
        return Json(new Dictionary<string, object?>
        {
            ["npcId"] = npcId,
            ["turnId"] = turnId,
            ["duplicate"] = duplicate,
            ["status"] = "Committed",
        });
    }

    private string BeginAgentUtterance(JsonElement arguments, string owner, out bool isError)
    {
        // Read every required field before semantic validation: an omitted
        // field must always be the refusal the MCP client sees (§144 gate).
        var attachmentId = Text(arguments, "attachmentId");
        var utteranceId = Text(arguments, "utteranceId");
        var turnId = Text(arguments, "turnId");
        var language = Text(arguments, "language");
        var text = Text(arguments, "text");
        var emotion = Text(arguments, "emotion");
        var deliveryText = Text(arguments, "delivery").Replace("_", string.Empty);
        var priorityText = Text(arguments, "priority");
        var totalBytes = Int(arguments, "totalBytes");
        var durationMs = Int(arguments, "durationMs");
        var sha256 = Text(arguments, "sha256");
        if (!TryEnum<AgentSpeechDelivery>(deliveryText, "delivery", out var delivery,
                out var deliveryError))
        {
            isError = true;
            return deliveryError;
        }
        if (!TryEnum<AgentSpeechPriority>(priorityText, "priority",
                out var priority, out var priorityError))
        {
            isError = true;
            return priorityError;
        }
        if (totalBytes is < 0 or > AgentWire.MaxAudioBytes || durationMs is < 0 or > AgentWire.MaxSpeechDurationMs)
        {
            isError = true;
            return "WAV должен быть <=6 MiB и <=60000 ms.";
        }

        var metadata = new AgentUtteranceMetadata
        {
            UtteranceId = Bounded(utteranceId.Trim(), 80, "utteranceId"),
            TurnId = Bounded(turnId.Trim(), 80, "turnId"),
            Language = Bounded(language.Trim(), 16, "language"),
            Text = Bounded(text.Trim(), AgentWire.MaxSpeechCharacters, "text"),
            Emotion = Bounded(emotion.Trim(), 32, "emotion"),
            Delivery = delivery,
            Priority = priority,
            TotalBytes = totalBytes,
            DurationMilliseconds = durationMs,
            Sha256 = Bounded(sha256.Trim(), 64, "sha256"),
        };
        if (metadata.UtteranceId.Length == 0 || metadata.TurnId.Length == 0 ||
            metadata.Text.Length == 0 || metadata.Sha256.Length != 64)
        {
            isError = true;
            return "utteranceId/turnId/text обязательны, sha256 содержит 64 hex-символа.";
        }
        if ((delivery == AgentSpeechDelivery.PlayerReply && priority != AgentSpeechPriority.Talk) ||
            (delivery == AgentSpeechDelivery.World && priority != AgentSpeechPriority.Ambient))
        {
            isError = true;
            return "player_reply требует Talk, world требует Ambient.";
        }

        var accepted = _agents.TryBeginUtterance(attachmentId, owner,
            _currentWorldGeneration(), metadata, out var duplicate, out var reason);
        isError = !accepted;
        return accepted ? Json(new { metadata.UtteranceId, duplicate }) : reason;
    }

    private string AppendAgentUtterance(JsonElement arguments, string owner, out bool isError)
    {
        var attachmentId = Text(arguments, "attachmentId");
        var utteranceId = Text(arguments, "utteranceId");
        var chunkIndex = Int(arguments, "chunkIndex");
        var encoded = Text(arguments, "base64");
        if (encoded.Length > 4 * ((AgentWire.MaxChunkBytes + 2) / 3))
        {
            isError = true;
            return "AudioChunkTooLarge";
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            isError = true;
            return "base64 chunk повреждён.";
        }
        var accepted = _agents.TryAppendUtterance(attachmentId, owner,
            _currentWorldGeneration(), utteranceId, chunkIndex, bytes, out var reason);
        isError = !accepted;
        return accepted ? Json(new { acceptedBytes = bytes.Length }) : reason;
    }

    private string CommitAgentUtterance(JsonElement arguments, string owner, out bool isError)
    {
        var accepted = _agents.TryCommitUtterance(Text(arguments, "attachmentId"), owner,
            _currentWorldGeneration(), Text(arguments, "utteranceId"),
            Text(arguments, "sha256"), out var duplicate, out var utterance, out var reason);
        isError = !accepted;
        return accepted ? Json(new
        {
            duplicate,
            sequence = utterance?.Sequence ?? 0,
            bytes = utterance?.Bytes.Length ?? 0,
        }) : reason;
    }

    private string DetachAgent(WorldHost host, JsonElement arguments, string owner, out bool isError)
    {
        if (arguments.TryGetProperty("preserveInbox", out var preserve) && preserve.ValueKind == JsonValueKind.True)
            _agents.PreserveInbox(Text(arguments, "attachmentId"), owner, _currentWorldGeneration());
        var accepted = _agents.TryDetach(Text(arguments, "attachmentId"), owner,
            _currentWorldGeneration(), out var npcId, out var reason);
        if (!accepted)
        {
            isError = true;
            return reason;
        }
        if (_leases.Release(npcId, owner))
        {
            AgentControlActions.Release(host, npcId);
        }
        isError = false;
        return Json(new { npcId, status = "Detached" });
    }

    private static string AttachmentJson(AgentAttachmentSnapshot snapshot, long? eventWatermark = null, string? inboxResumeKey = null) =>
        Json(new Dictionary<string, object?>
        {
            ["attachmentId"] = snapshot.AttachmentId,
            ["inboxResumeKey"] = inboxResumeKey,
            ["eventWatermark"] = eventWatermark,
            ["npcId"] = snapshot.NpcId,
            ["displayName"] = snapshot.DisplayName,
            ["capabilities"] = snapshot.Capabilities.ToString(),
            ["phase"] = snapshot.Phase.ToString(),
            ["playerPresent"] = snapshot.PlayerPresent,
            ["presentSpeakerIds"] = snapshot.PresentSpeakerIds,
            ["intentSummary"] = snapshot.IntentSummary,
            ["relationView"] = snapshot.RelationView,
            ["journalEntry"] = snapshot.JournalEntry,
            ["ttlSeconds"] = snapshot.TtlSeconds,
        });

    private static bool TryCapabilities(JsonElement arguments,
        out AgentCapabilities capabilities, out string error)
    {
        capabilities = AgentCapabilities.None;
        error = string.Empty;
        if (!arguments.TryGetProperty("capabilities", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            error = "capabilities должен быть массивом строк.";
            return false;
        }
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = "capabilities должен быть массивом строк.";
                return false;
            }
            var name = (item.GetString() ?? string.Empty).Replace("_", string.Empty);
            if (!TryEnum<AgentCapabilities>(name, "capabilities", out var capability,
                    out error) || capability == AgentCapabilities.None)
                return false;
            capabilities |= capability;
        }
        return true;
    }

    private static string Bounded(string value, int max, string name)
    {
        if (value.Length > max)
            throw new McpArgumentException($"{name} длиннее {max} символов.");
        return value;
    }


    private string Acquire(WorldHost host, JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var timeoutSeconds = OptionalInt(arguments, "ttlSeconds") ?? _leases.TimeoutSeconds;
        if (timeoutSeconds is < 15 or > 120)
        {
            isError = true;
            return "ttlSeconds должен быть в диапазоне 15..120.";
        }
        if (!_agents.CanIssueWorldCommands(npcId, owner))
        {
            isError = true;
            return "ControlledByAgent";
        }

        var known = host.Read(world =>
            world.Entities.Npcs.ContainsKey(new EntityId(npcId)));
        if (!known)
        {
            isError = true;
            return $"Нет колонистки с id {npcId}.";
        }

        if (!_leases.TryAcquire(npcId, owner, timeoutSeconds, out var leaseId, out var heldBy))
        {
            isError = true;
            return $"Колонистка {npcId} уже под управлением другого агента ({heldBy}). " +
                   $"Лиз освободится сам после {timeoutSeconds} с без команд.";
        }

        // Лиз — это право говорить; ручной режим — это то, что мир слышит.
        // Второе без первого пустило бы к ней ИИ, первое без второго оставило
        // бы приказы без исполнителя, поэтому они всегда вместе.
        host.Read(world =>
        {
            if (world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc) &&
                npc.Mind.ExternalControl?.IsActive == false) npc.Mind.ExternalControl = null;
            return true;
        });
        var admission = host.SubmitManualCommand(
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
            ["timeoutSeconds"] = timeoutSeconds,
            ["note"] = "Лиз продлевается каждой командой. release_control по окончании работы.",
        });
    }

    private string Release(WorldHost host, int npcId, string owner, out bool isError)
    {
        if (!_leases.TryRenew(npcId, owner, out var heldBy))
        {
            isError = true;
            return heldBy.Length == 0
                ? $"Колонистка {npcId} и так свободна."
                : $"Колонисткой {npcId} владеет другой агент ({heldBy}).";
        }

        var admission = AgentControlActions.Release(host, npcId);
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

    private string MoveTo(WorldHost host, JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var x = Number(arguments, "x");
        var y = Number(arguments, "y");
        var run = Bool(arguments, "run", false);
        return Submit(host, npcId, owner,
            npc => new MoveToCommand(npc, new Float2(x, y), run), out isError);
    }

    private string Interact(WorldHost host, JsonElement arguments, string owner, out bool isError)
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

        return Submit(host, npcId, owner,
            npc => new InteractCommand(npc, new ObjectId(objectId), interaction), out isError);
    }

    private string Craft(WorldHost host, JsonElement arguments, string owner, out bool isError)
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

        return Submit(host, npcId, owner, npc => new CraftItemCommand(npc, goal), out isError);
    }

    private string AttackNpc(WorldHost host, JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var target = Int(arguments, "targetNpcId");
        return Submit(host, npcId, owner,
            npc => new AttackNpcCommand(npc, new EntityId(target)), out isError);
    }

    private string AttackMob(WorldHost host, JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var mobId = Int(arguments, "mobId");
        return Submit(host, npcId, owner, npc => new AttackMobCommand(npc, mobId), out isError);
    }

    // ⭐ Во всех хелперах ниже СНАЧАЛА читаются все обязательные аргументы
    // (Int/Text бросают именной отказ на недостающий), и только ПОТОМ
    // валидируются значения енумов: отказ «нет параметра X» обязан приходить
    // независимо от мусора в остальных — за этим следит контрактный гейт.

    // §121.9: вид помощи выбирает агент явно — как игрок в подменю.
    private string AidPerson(WorldHost host, JsonElement arguments, string owner, out bool isError)
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
        return Submit(host, npcId, owner,
            npc => new AidPersonCommand(npc, ward, kind), out isError);
    }

    private string SelfAction(WorldHost host, JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var kindName = Text(arguments, "kind");
        if (!Enum.TryParse<SelfActionKind>(kindName, ignoreCase: true, out var kind))
        {
            isError = true;
            return $"Неизвестное самодействие «{kindName}». Допустимые: " +
                   string.Join(", ", Enum.GetNames(typeof(SelfActionKind)));
        }

        return Submit(host, npcId, owner,
            npc => new SelfActionCommand(npc, kind), out isError);
    }

    private string ManageInventory(WorldHost host, JsonElement arguments, string owner, out bool isError)
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
        return Submit(host, npcId, owner,
            npc => new ManageInventoryCommand(npc, item, action), out isError);
    }

    private string RequestItem(WorldHost host, JsonElement arguments, string owner, out bool isError)
    {
        var npcId = Int(arguments, "npcId");
        var targetNpcId = Int(arguments, "targetNpcId");
        var definitionId = Text(arguments, "definitionId");
        var answer = Submit(host, npcId, owner,
            npc => new RequestItemCommand(npc, new EntityId(targetNpcId), definitionId), out isError);
        if (isError) return answer;
        return Json(new { npcId, targetNpcId, definitionId, count = 1,
            status = "Completed", outcome = "Transferred" });
    }

    private string TransferInventory(WorldHost host, JsonElement arguments, string owner, out bool isError)
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
        return Submit(host, npcId, owner,
            npc => new TransferInventoryCommand(npc, other, item, count, direction),
            out isError);
    }

    private string TransferContainer(WorldHost host, JsonElement arguments, string owner, out bool isError)
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
        return Submit(host, npcId, owner,
            npc => new TransferContainerCommand(
                npc, container, slotIndex, expected, count, direction),
            out isError);
    }

    private static bool TryEnum<T>(
        string text, string name, out T value, out string error)
        where T : struct, Enum
    {
        error = string.Empty;
        if (Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(typeof(T), value))
        {
            return true;
        }

        error = $"Неизвестное значение «{text}» для {name}. Допустимые: " +
                string.Join(", ", Enum.GetNames(typeof(T)));
        return false;
    }

    private string Simple(WorldHost host, JsonElement arguments, string owner,
        Func<EntityId, ISimulationCommand> build, out bool isError) =>
        Submit(host, Int(arguments, "npcId"), owner, build, out isError);

    /// <summary>
    /// Единственная дорога от инструмента до мира: сверить лиз, отдать команду
    /// шву, вернуть его вердикт как есть. Ни одного «а вот тут можно и без
    /// лиза» — иначе весь смысл владения теряется на первом же исключении.
    /// </summary>
    private string Submit(WorldHost host, int npcId, string owner,
        Func<EntityId, ISimulationCommand> build, out bool isError)
    {
        if (!_agents.CanIssueWorldCommands(npcId, owner))
        {
            isError = true;
            return "ControlledByAgent";
        }
        if (!_leases.TryRenew(npcId, owner, out var heldBy))
        {
            isError = true;
            return heldBy.Length == 0
                ? $"Колонистка {npcId} не под вашим управлением — сначала acquire_control."
                : $"Колонисткой {npcId} владеет другой агент ({heldBy}).";
        }

        if (_trackedCommand.Value is { } tracked)
        {
            if (tracked.NpcId != npcId) throw new McpArgumentException("TrackedActorMismatch");
            var receipt = host.SubmitTrackedManualCommand(tracked, build(new EntityId(npcId)));
            _trackedResult.Value = receipt;
            isError = receipt.Outcome == "failed";
            return Json(receipt);
        }
        var admission = host.SubmitManualCommand(build(new EntityId(npcId)));
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

    private string ExecuteAgentCommand(JsonElement args, string owner, out bool isError)
    {
        var npcId = Int(args, "npcId");
        var sequence = OptionalLong(args, "sequence") ?? throw new McpArgumentException("Нужен параметр sequence");
        var commandId = Text(args, "commandId");
        var tool = Text(args, "tool");
        if (!args.TryGetProperty("arguments", out var nested) || nested.ValueKind != JsonValueKind.Object || nested.GetRawText().Length > 8192)
            throw new McpArgumentException("Нужен объект arguments до 8192 символов");
        if (tool is not ("move_to" or "interact" or "craft_item" or "stop" or "talk_to" or
            "aid_person" or "treat_limbs" or "self_action" or "carry_person" or "put_down_person" or
            "put_person_in_bed" or "manage_inventory" or "attack_mob" or "merge_camps" or "request_item" or "transfer_inventory" or "rest_until"))
            throw new McpArgumentException("TrackedToolUnavailable");
        var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in nested.EnumerateObject())
            if (!properties.TryAdd(property.Name, property.Value.Clone())) throw new McpArgumentException("DuplicateArgument");
        properties["npcId"] = JsonSerializer.SerializeToElement(npcId);
        var bound = JsonSerializer.SerializeToElement(properties);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(tool + "\n" + bound.GetRawText()))).ToLowerInvariant();
        var previous = _trackedCommand.Value;
        var previousResult = _trackedResult.Value;
        try
        {
            _trackedCommand.Value = new(npcId, sequence, commandId, fingerprint);
            if (tool == "rest_until")
            {
                var need = Text(bound, "need"); var target = Number(bound, "target");
                if (need is not ("Energy" or "Stamina") || !float.IsFinite(target) || target <= 0 || target > 1)
                    throw new McpArgumentException("InvalidRestTarget");
                _trackedCommand.Value = _trackedCommand.Value with { RestNeed = need, RestTarget = target };
            }
            _trackedResult.Value = null;
            var result = Call(tool, bound, owner, out isError);
            if (_trackedResult.Value is { } receipt)
            {
                // A recorded failure is a valid receipt, not an unknown transport
                // error. Let the executor persist failed rather than lose its outcome.
                isError = false;
                return Json(receipt);
            }
            return result;
        }
        finally { _trackedCommand.Value = previous; _trackedResult.Value = previousResult; }
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

    private static string OptionalText(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static double Round(float value) => Math.Round(value, 3);

    private static string Json(object payload) =>
        JsonSerializer.Serialize(payload, McpJson.Options);

    /// <summary>
    /// Схема инструмента. Пишется здесь руками и намеренно узкой: описание
    /// каждого параметра — это то единственное, что агент прочитает перед
    /// первым вызовом.
    /// </summary>
    private static JsonElement TalkToSchema()
    {
        var topics = new List<string>();
        foreach (var topic in TalkTopicRequest.Allowed) topics.Add(topic.ToString());
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                npcId = new { type = "integer", description = "id колонистки" },
                targetNpcId = new { type = "integer", description = "с кем говорить" },
                topic = new
                {
                    type = "string",
                    description = "Общая тема (§28.15E): SmallTalk 💬, Escape ⛵, Dogs 🐕, Weather 🌧, " +
                        "Food 🥥, Fire 🔥, Home 🏠, Gossip 👀, Flirt 💗, Joke 😂, Grumble 😠. " +
                        "Без topic симуляция выбирает сама. Реальные жалобы участниц и исход разговора остаются штатными.",
                    @enum = topics,
                },
            },
            required = new[] { "npcId", "targetNpcId" },
        });
    }

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

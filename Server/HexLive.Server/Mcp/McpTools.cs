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
    private readonly McpControlLeases _leases;

    public McpTools(WorldHost host, McpControlLeases leases)
    {
        _host = host;
        _leases = leases;
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
            "id и доступными взаимодействиями) и память. Ровно тот текст, который получает " +
            "LLM-контур. Отсюда берут objectId для interact.",
            Schema(("npcId", "integer", "id колонистки", true))),

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
            "Напасть на зверя. mobId — из describe_colonist.",
            Schema(("npcId", "integer", "id колонистки", true),
                   ("mobId", "integer", "id зверя", true))),

        new("stop",
            "Отставить текущий приказ. Не выключает ручной режим — это разные вещи (§121).",
            Schema(("npcId", "integer", "id колонистки", true))),
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
                case "describe_colonist": return Describe(Int(arguments, "npcId"), out isError);
                case "acquire_control": return Acquire(Int(arguments, "npcId"), owner, out isError);
                case "release_control": return Release(Int(arguments, "npcId"), owner, out isError);
                case "move_to": return MoveTo(arguments, owner, out isError);
                case "interact": return Interact(arguments, owner, out isError);
                case "craft_item": return Craft(arguments, owner, out isError);
                case "attack_npc": return AttackNpc(arguments, owner, out isError);
                case "attack_mob": return AttackMob(arguments, owner, out isError);
                case "stop": return Simple(arguments, owner, npc => new StopCommand(npc), out isError);
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

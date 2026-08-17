using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp
{

/// <summary>
/// Каталог инструментов не должен врать (§144.5).
/// <para>
/// Гейт написан по следу конкретного бага. Инструмент <c>attack_mob</c>
/// заявлял в своём описании «mobId — из describe_colonist», и это было
/// неправдой: поля <c>mobs</c> в ответе не существовало вовсе. Прожило
/// обещание до первого настоящего агента — на колонистку напал зверь, и
/// ударить в ответ было нечем, потому что взять id было неоткуда.
/// </para>
/// <para>
/// Обещание в описании — такой же контракт, как сигнатура метода, только
/// компилятор его не проверяет. Здесь проверяется он.
/// </para>
/// </summary>
[NonParallelizable]
public sealed class McpToolContractGateTests
{
    /// <summary>
    /// Поля, которые описания обещают агенту прямо текстом. Список ведётся
    /// руками СОЗНАТЕЛЬНО: обещание — решение автора описания, и вписать сюда
    /// строку должно быть таким же осознанным действием, как дать обещание.
    /// </summary>
    private static readonly (string Tool, string Field, string Promise)[] Promises =
    {
        ("attack_mob", "mobs", "«mobId — из поля mobs в describe_colonist»"),
        ("describe_colonist", "objects", "«objectId … из describe_colonist»"),
        ("describe_colonist", "inventory", "полная картина по колонистке"),
        ("read_events", "watermark", "«sinceSeq=watermark из прошлого ответа»"),
        ("read_events", "gap", "«gap=true — кольцо подрезало»"),
        ("read_events", "truncated", "«truncated=true — упёрлись в limit»"),
        ("read_spec", "nextOffset", "«передайте offset из поля nextOffset»"),
    };

    [Test]
    public void EveryCatalogEntryDispatches()
    {
        var tools = CreateTools();

        foreach (var spec in McpTools.Catalog)
        {
            var answer = tools.Call(spec.Name, EmptyArguments(), owner: "gate", out _);

            // Каталог и switch — два независимых списка, и ничто, кроме этого
            // теста, не заставляет их совпадать. Инструмент, объявленный и не
            // подключённый, виден агенту в tools/list и падает при вызове.
            Assert.That(answer, Does.Not.Contain("Нет такого инструмента"),
                $"«{spec.Name}» объявлен в каталоге, но у диспетчера для него нет ветки.");
        }
    }

    [Test]
    public void EveryRequiredParameterIsActuallyRead()
    {
        var tools = CreateTools();

        foreach (var spec in McpTools.Catalog)
        {
            var required = RequiredProperties(spec.InputSchema).ToArray();
            foreach (var omitted in required)
            {
                // Пустого объекта мало: параметры читаются по порядку, и жалоба
                // всегда будет про первый недостающий. Чтобы проверить КАЖДЫЙ,
                // подставляем все остальные и убираем ровно один.
                var arguments = ArgumentsWithout(spec.InputSchema, required, omitted);
                var answer = tools.Call(spec.Name, arguments, owner: "gate", out var isError);

                // Обязательный параметр, который обработчик не читает, — это
                // мёртвая строка в схеме: агент её заполняет, а она ни на что
                // не влияет.
                Assert.That(isError, Is.True,
                    $"«{spec.Name}» объявляет обязательный «{omitted}», но без него не спорит.");
                Assert.That(answer, Does.Contain(omitted),
                    $"Отказ «{spec.Name}» обязан назвать недостающий «{omitted}», " +
                    "иначе агенту нечего чинить.");
            }
        }
    }

    [Test]
    public void EveryPromisedFieldExistsInTheAnswer()
    {
        var tools = CreateTools();
        var responses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (tool, field, promise) in Promises)
        {
            var source = SourceToolFor(tool);
            if (!responses.TryGetValue(source, out var text))
            {
                text = tools.Call(source, ArgumentsFor(source), owner: "gate", out var isError);
                Assert.That(isError, Is.False, $"{source} ответил ошибкой: {text}");
                responses[source] = text;
            }

            // Ищется так, как читает агент. Часть полей — ключи JSON
            // (`inventory`, `watermark`), часть живёт внутри текстовых сводок
            // восприятия (`objects=[…]`, `mobs=[…]`): §144.3 требует, чтобы мир
            // описывал один сборщик, и он отдаёт строку, а не структуру.
            var asJsonKey = $"\"{field}\"";
            var asSummaryField = $"{field}=[";

            Assert.That(text.Contains(asJsonKey, StringComparison.Ordinal) ||
                        text.Contains(asSummaryField, StringComparison.Ordinal), Is.True,
                $"Описание «{tool}» обещает {promise}, а «{field}» в ответе " +
                $"«{source}» нет ни ключом, ни полем сводки. Ровно так жило обещание mobId.");
        }
    }

    [Test]
    public void HandshakeSendsTheAgentToTheSpecFirst()
    {
        // Единственный текст, который агент читает ДО первого вызова. Если он
        // не назовёт спеку, агент никогда не узнает, что у мира есть писаные
        // правила, и будет выяснять их методом отказов.
        var endpoint = File.ReadAllText(RepoFile("Server", "HexLive.Server", "Mcp", "McpEndpoint.cs"));
        var instructions = endpoint[endpoint.IndexOf("\"instructions\"", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(instructions, Does.Contain("read_spec"));
            Assert.That(instructions, Does.Contain("read_events"));
            Assert.That(McpTools.Catalog.Any(t => t.Name == "read_spec"), Is.True);
        });
    }

    /// <summary>Какой инструмент отдаёт ответ, в котором обещано поле.</summary>
    private static string SourceToolFor(string tool) => tool switch
    {
        "attack_mob" => "describe_colonist",
        _ => tool,
    };

    private static JsonElement ArgumentsFor(string tool)
    {
        if (tool == "describe_colonist")
        {
            using var host = CreateHost();
            var npcId = FirstNpcId(host);
            return JsonDocument.Parse($"{{\"npcId\":{npcId}}}").RootElement.Clone();
        }

        // Без section это оглавление, а nextOffset появляется при чтении
        // раздела — обещание относится именно к нему.
        if (tool == "read_spec")
        {
            return JsonDocument.Parse("{\"section\":\"40\"}").RootElement.Clone();
        }

        return EmptyArguments();
    }

    private static McpTools CreateTools()
    {
        var host = CreateHost();
        host.EnableMcpEventLog();
        _hosts.Add(host);
        return new McpTools(host, new ControlLeases(120), SpecLibrary.Discover(null));
    }

    private static readonly List<WorldHost> _hosts = new();

    [TearDown]
    public void DisposeHosts()
    {
        foreach (var host in _hosts) host.Dispose();
        _hosts.Clear();
    }

    private static int FirstNpcId(WorldHost host) =>
        host.Read(world =>
        {
            var min = int.MaxValue;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.Id.Value < min) min = npc.Id.Value;
            }
            return min;
        });

    private static IEnumerable<string> RequiredProperties(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in required.EnumerateArray())
        {
            var name = item.GetString();
            if (!string.IsNullOrEmpty(name)) yield return name!;
        }
    }

    private static JsonElement EmptyArguments() =>
        JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Все обязательные параметры, кроме одного, — правдоподобными значениями по типу.</summary>
    private static JsonElement ArgumentsWithout(
        JsonElement schema, IReadOnlyList<string> required, string omitted)
    {
        var parts = new List<string>();
        foreach (var name in required)
        {
            if (name == omitted) continue;
            parts.Add($"\"{name}\":{SampleValue(schema, name)}");
        }

        return JsonDocument.Parse("{" + string.Join(",", parts) + "}").RootElement.Clone();
    }

    private static string SampleValue(JsonElement schema, string property)
    {
        var type = schema.TryGetProperty("properties", out var properties) &&
                   properties.TryGetProperty(property, out var declared) &&
                   declared.TryGetProperty("type", out var kind)
            ? kind.GetString()
            : "string";

        return type switch
        {
            "integer" or "number" => "1",
            "boolean" => "false",
            _ => "\"x\"",
        };
    }

    private static WorldHost CreateHost() => new(
        seed: 12345,
        Path.Combine(Path.GetTempPath(), $"hexlive-mcp-gate-{Guid.NewGuid():N}.sav"),
        RepoFile("SimData", "simdata.json"),
        verboseTrace: false);

    private static string RepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var parts = new string[relativePath.Length + 1];
            parts[0] = directory.FullName;
            Array.Copy(relativePath, 0, parts, 1, relativePath.Length);
            var candidate = Path.Combine(parts);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(string.Join('/', relativePath));
    }
}

}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Mcp
{

/// <summary>
/// §144: MCP поверх HTTP — `POST /mcp`, JSON-RPC 2.0, один запрос — один ответ.
/// <para>
/// Написан руками, и это не упрямство. В csproj сервера прямым текстом стоит
/// «ZERO NuGet packages», и стоит там ради свойства, которое дороже удобства:
/// сервер и игра компилируют ОДИН И ТОТ ЖЕ исходник симуляции, а пакет,
/// приехавший сюда, немедленно оказался бы в зависимостях того, что должно
/// собираться в Unity. Нужная часть протокола — рукопожатие, список
/// инструментов и вызов — это триста строк разбора JSON, а не фреймворк.
/// </para>
/// <para>
/// Реализован ровно необходимый минимум транспорта: <c>initialize</c>,
/// <c>notifications/initialized</c>, <c>ping</c>, <c>tools/list</c>,
/// <c>tools/call</c>. Нет SSE-канала (сервер сам ничего не проталкивает —
/// смотреть мир агент может тем же <c>world_status</c>), нет ресурсов и
/// промптов: их пришлось бы придумывать, а придуманный интерфейс хуже
/// отсутствующего.
/// </para>
/// </summary>
public static class McpEndpoint
{
    /// <summary>Версия протокола, на которую отвечаем, если клиент не назвал свою.</summary>
    private const string DefaultProtocolVersion = "2025-06-18";

    private const string SessionHeader = "Mcp-Session-Id";

    public static void Map(WebApplication app, WorldHost host, McpAccessToken token,
        McpControlLeases leases)
    {
        var tools = new McpTools(host, leases);

        app.MapPost("/mcp", async (HttpContext context) =>
        {
            if (!Authorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await context.Response.WriteAsync("MCP: нужен заголовок Authorization: Bearer <токен>.");
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                await WriteJson(context, Error(null, -32700, "Parse error: " + ex.Message));
                return;
            }

            using (document)
            {
                // Идентичность агента = сессия рукопожатия. По спецификации
                // клиент обязан возвращать выданный Mcp-Session-Id в каждом
                // последующем запросе — на этом и стоит владение лизом. Клиент
                // без сессии не отвергается, но все такие попадают в одного
                // владельца: два безымянных агента — это, с точки зрения мира,
                // один, и пусть лучше они мешают друг другу явно, чем тихо
                // перехватывают колонисток.
                var session = context.Request.Headers[SessionHeader].ToString();
                var owner = string.IsNullOrWhiteSpace(session) ? "anonymous" : session;

                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var batch = new List<object>();
                    foreach (var item in root.EnumerateArray())
                    {
                        var answer = Handle(item, tools, owner, context);
                        if (answer != null) batch.Add(answer);
                    }

                    if (batch.Count == 0)
                    {
                        context.Response.StatusCode = StatusCodes.Status202Accepted;
                        return;
                    }

                    await WriteJson(context, batch);
                    return;
                }

                var response = Handle(root, tools, owner, context);
                if (response == null)
                {
                    // Уведомление: по JSON-RPC ответа нет вообще.
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                await WriteJson(context, response);
            }
        });

        // GET на тот же адрес — чтобы «а он вообще живой?» отвечало по-человечески,
        // а не 405-й строкой из фреймворка.
        app.MapGet("/mcp", (HttpContext context) => Results.Text(
            "HexLive MCP endpoint.\n" +
            "POST JSON-RPC 2.0 here with: Authorization: Bearer <токен из hexlive-mcp.txt>\n" +
            $"Методы: initialize, ping, tools/list, tools/call. Инструментов: {McpTools.Catalog.Count}.\n",
            "text/plain; charset=utf-8"));
    }

    private static object? Handle(JsonElement request, McpTools tools, string owner,
        HttpContext context)
    {
        var id = request.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null
            ? (object?)Id(idElement)
            : null;
        var method = request.TryGetProperty("method", out var methodElement) &&
                     methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString() ?? string.Empty
            : string.Empty;

        switch (method)
        {
            case "initialize":
            {
                var requested = request.TryGetProperty("params", out var initParams) &&
                                initParams.TryGetProperty("protocolVersion", out var version) &&
                                version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : null;

                // Сессию выдаём в заголовке: она же — владелец лизов.
                if (string.IsNullOrWhiteSpace(context.Request.Headers[SessionHeader].ToString()))
                {
                    context.Response.Headers[SessionHeader] = Guid.NewGuid().ToString("N");
                }

                return Result(id, new Dictionary<string, object?>
                {
                    ["protocolVersion"] = string.IsNullOrWhiteSpace(requested)
                        ? DefaultProtocolVersion
                        : requested,
                    ["capabilities"] = new Dictionary<string, object?>
                    {
                        ["tools"] = new Dictionary<string, object?>(),
                    },
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "hexlive",
                        ["version"] = "1",
                    },
                    ["instructions"] =
                        "Колония живёт своей жизнью и без тебя. Порядок работы: list_colonists → " +
                        "describe_colonist → acquire_control → приказы → release_control. " +
                        "Приказ проходит ту же проверку, что клик игрока: отказ приходит с причиной.",
                });
            }

            case "notifications/initialized":
            case "notifications/cancelled":
                return null;

            case "ping":
                return Result(id, new Dictionary<string, object?>());

            case "tools/list":
            {
                var list = new List<object>();
                foreach (var spec in McpTools.Catalog)
                {
                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = spec.Name,
                        ["description"] = spec.Description,
                        ["inputSchema"] = spec.InputSchema,
                    });
                }

                return Result(id, new Dictionary<string, object?> { ["tools"] = list });
            }

            case "tools/call":
            {
                if (!request.TryGetProperty("params", out var callParams) ||
                    !callParams.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    return Error(id, -32602, "tools/call без имени инструмента.");
                }

                var arguments = callParams.TryGetProperty("arguments", out var argumentsElement)
                    ? argumentsElement
                    : default;

                var text = tools.Call(nameElement.GetString() ?? string.Empty, arguments, owner,
                    out var isError);

                // Ошибка инструмента — это НЕ ошибка протокола: агент должен
                // прочитать причину и попробовать иначе, а не получить обрыв.
                return Result(id, new Dictionary<string, object?>
                {
                    ["content"] = new List<object>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text,
                        },
                    },
                    ["isError"] = isError,
                });
            }

            default:
                return id == null ? null : Error(id, -32601, $"Метод не поддержан: {method}");
        }
    }

    private static bool Authorized(HttpContext context, McpAccessToken token)
    {
        var header = context.Request.Headers["Authorization"].ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return token.Matches(header.Substring(prefix.Length).Trim());
        }

        return false;
    }

    private static object Id(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number)
            ? number
            : element.GetRawText().Trim('"');

    private static Dictionary<string, object?> Result(object? id, object payload) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = payload,
        };

    private static Dictionary<string, object?> Error(object? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

    private static async Task WriteJson(HttpContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, McpJson.Options), Encoding.UTF8);
    }
}

}

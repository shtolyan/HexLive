using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server
{

/// <summary>
/// §168.10: <c>--web-root DIR</c> раздаёт WebGL-сборку на <c>/play/</c> тем же
/// процессом, что держит <c>/watch</c> и <c>/api/assets</c>. Один origin —
/// значит браузеру не нужен CORS, а клиент выводит адрес сокета прямо из
/// адреса страницы (<c>WebPage.PageServerUrl</c>). В проде то же самое может
/// делать Caddy (<c>Server/Caddy/webgl-play.caddy</c>); этот путь — для
/// локальной проверки и для хоста без Caddy.
/// <para>
/// Unity собирает Brotli без JS-фолбэка: <c>*.br</c> уходит как есть, с
/// <c>Content-Encoding: br</c> и НАСТОЯЩИМ типом внутреннего файла —
/// <c>application/wasm</c> нужен браузеру для потоковой компиляции.
/// <c>UseResponseCompression</c> ответ с уже выставленным
/// <c>Content-Encoding</c> не пережимает.
/// </para>
/// </summary>
public static class WebClientEndpoints
{
    public const string Prefix = "/play";

    public static void Map(WebApplication app, string root)
    {
        var fullRoot = Path.GetFullPath(root);
        // Один маршрут: шаблон "/play/{**path}" ловит и "/play", и "/play/"
        // (маршрутизатор не различает хвостовой слеш), поэтому редирект на
        // слеш — только для голого "/play", иначе это вечный 302 сам на себя.
        // Слеш нужен: index.html грузит "Build/…" относительными путями.
        app.MapGet(Prefix + "/{**path}", async context =>
        {
            if (string.Equals(context.Request.Path.Value, Prefix, StringComparison.Ordinal))
            {
                context.Response.Redirect(Prefix + "/", permanent: false);
                return;
            }

            var relative = context.Request.RouteValues["path"] as string;
            if (!WebClientFiles.TryResolve(fullRoot, relative, out var file))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = file.ContentType;
            if (file.ContentEncoding is not null)
            {
                context.Response.Headers.ContentEncoding = file.ContentEncoding;
            }
            // Имена файлов сборки не хешированы — только с перепроверкой.
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentLength = new FileInfo(file.Path).Length;
            await context.Response.SendFileAsync(file.Path);
        });
    }
}

/// <summary>Pure part of <see cref="WebClientEndpoints"/>: path → file, type, encoding.</summary>
public static class WebClientFiles
{
    public readonly struct Resolved
    {
        public Resolved(string path, string contentType, string? contentEncoding)
        {
            Path = path;
            ContentType = contentType;
            ContentEncoding = contentEncoding;
        }

        public string Path { get; }
        public string ContentType { get; }
        public string? ContentEncoding { get; }
    }

    public static bool TryResolve(string fullRoot, string? relative, out Resolved file)
    {
        file = default;
        relative = string.IsNullOrEmpty(relative) ? "index.html" : relative;
        if (relative.Contains('\\') || relative.Contains('\0'))
        {
            return false;
        }

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        }
        catch (Exception)
        {
            return false;
        }

        var rootWithSeparator = fullRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(full))
        {
            return false;
        }

        var name = full;
        string? encoding = null;
        if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
        {
            encoding = "br";
            name = name.Substring(0, name.Length - 3);
        }
        else if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            encoding = "gzip";
            name = name.Substring(0, name.Length - 3);
        }

        file = new Resolved(full, ContentTypeOf(name), encoding);
        return true;
    }

    private static string ContentTypeOf(string name)
    {
        switch (System.IO.Path.GetExtension(name).ToLowerInvariant())
        {
            case ".html": return "text/html; charset=utf-8";
            case ".js": return "application/javascript";
            case ".wasm": return "application/wasm";
            case ".json": return "application/json";
            case ".css": return "text/css";
            case ".png": return "image/png";
            case ".jpg":
            case ".jpeg": return "image/jpeg";
            case ".ico": return "image/x-icon";
            case ".svg": return "image/svg+xml";
            default: return "application/octet-stream";
        }
    }
}

}

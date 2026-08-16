using System;
using System.IO;
using System.Security.Cryptography;

namespace HexLive.Server.Mcp
{

/// <summary>
/// §144.1: секрет, которым агент доказывает право говорить с миром.
/// <para>
/// Устроен как одноразовый пароль админки и по той же причине: дефолтного
/// значения не существует, поэтому нечему утечь и нечего забыть сменить. При
/// первом запуске токен генерируется, печатается в консоль ОДИН раз и ложится
/// рядом с сейвом в файл 0600. Дальше он читается оттуда.
/// </para>
/// <para>
/// В отличие от пароля админки хеша здесь нет намеренно: пароль вводит человек
/// и его надо защищать от чтения файла, а этот токен — машинный, его всё равно
/// придётся отдать агенту целиком. Хеш создал бы видимость защиты, не добавив
/// её: кто прочитал файл, тот прочитал бы и токен в конфиге агента рядом.
/// </para>
/// <para>
/// ⚠️ По простому HTTP токен едет открытым текстом — ровно как пароль админки
/// (§83). Наружу с машины — только за TLS-прокси.
/// </para>
/// </summary>
public sealed class McpAccessToken
{
    private McpAccessToken(string value, string path)
    {
        Value = value;
        Path = path;
    }

    public string Value { get; }

    public string Path { get; }

    public static McpAccessToken LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16)
            {
                return new McpAccessToken(existing, path);
            }
        }

        var token = Generate();
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, token + Environment.NewLine);
        TryRestrictPermissions(path);

        Console.WriteLine();
        Console.WriteLine("  ┌──────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  MCP CONTROL — first run                                 │");
        Console.WriteLine("  │  Bearer token for /mcp (also saved to the file below):    │");
        Console.WriteLine($"  │      {token,-52}│");
        Console.WriteLine("  └──────────────────────────────────────────────────────────┘");
        Console.WriteLine($"  {path}");
        Console.WriteLine();
        return new McpAccessToken(token, path);
    }

    /// <summary>
    /// Сравнение постоянного времени: токен приходит с каждым вызовом, и
    /// обычное сравнение строк сдаёт его побайтово тому, кто умеет мерить.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expected = System.Text.Encoding.UTF8.GetBytes(Value);
        var actual = System.Text.Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return "hexmcp_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void TryRestrictPermissions(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
            // Windows: прав файловой системы здесь не выставить тем же вызовом.
            // Секрет остаётся в профиле пользователя, как и сейв рядом.
        }
        catch (IOException)
        {
        }
    }
}

}

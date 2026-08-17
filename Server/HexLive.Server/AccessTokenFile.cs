using System;
using System.IO;
using System.Security.Cryptography;

namespace HexLive.Server
{

/// <summary>
/// §144.1/§121.9: секрет, которым внешний контур доказывает право говорить с
/// миром — обобщение <c>McpAccessToken</c> для второй двери (токен ИГРОКА на
/// <c>/watch</c>). Дефолтного значения не существует: при первом запуске токен
/// генерируется, печатается в консоль ОДИН раз и ложится рядом с сейвом в файл
/// 0600. Хеша нет намеренно: токен машинный, его всё равно отдают клиенту
/// целиком — хеш создал бы видимость защиты, не добавив её.
/// <para>
/// ⚠️ По простому HTTP/WS токен едет открытым текстом — ровно как пароль
/// админки (§83). Наружу с машины — только за TLS-прокси.
/// </para>
/// </summary>
public sealed class AccessTokenFile
{
    private AccessTokenFile(string value, string path)
    {
        Value = value;
        Path = path;
    }

    public string Value { get; }

    public string Path { get; }

    public static AccessTokenFile LoadOrCreate(string path, string banner, string prefix)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16)
            {
                return new AccessTokenFile(existing, path);
            }
        }

        var token = Generate(prefix);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, token + Environment.NewLine);
        TryRestrictPermissions(path);

        Console.WriteLine();
        Console.WriteLine("  ┌──────────────────────────────────────────────────────────┐");
        Console.WriteLine($"  │  {banner,-56}│");
        Console.WriteLine("  │  Bearer token (also saved to the file below):             │");
        Console.WriteLine($"  │      {token,-52}│");
        Console.WriteLine("  └──────────────────────────────────────────────────────────┘");
        Console.WriteLine($"  {path}");
        Console.WriteLine();
        return new AccessTokenFile(token, path);
    }

    /// <summary>
    /// Сравнение постоянного времени: токен приходит с каждым подключением, и
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

    private static string Generate(string prefix)
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
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

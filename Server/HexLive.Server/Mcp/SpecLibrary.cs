using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace HexLive.Server.Mcp
{

/// <summary>
/// Спека, доступная агенту по сети (§144.9).
/// <para>
/// ⭐ Зачем это вообще нужно. Агент приходит по HTTP и может стоять на другой
/// машине: ни репозитория, ни чекаута, ни файлов рядом у него нет. Всё, что он
/// знает о мире, — двенадцать описаний инструментов. А правила, которые
/// РЕШАЮТ, сработает приказ или нет, живут в спеке: «ручная ест только своё»
/// (§121.6), «пальма — это вода колонии» (§64.9), «обыскать можно труп, а не
/// лежащего» (§111). Не дать их — значит заставить его выяснять устройство мира
/// методом отказов, а половину так и не выяснить.
/// </para>
/// </summary>
public sealed class SpecLibrary
{
    /// <summary>
    /// Разделы называются числом с необязательной буквой (<c>144</c>, <c>29E</c>,
    /// <c>75A</c>) плюс один именованный — <c>preamble</c>.
    /// <para>
    /// ⚠️ Это не косметика, а граница безопасности: имя приходит из сети и идёт
    /// в путь. Без строгой формы <c>../../etc/passwd</c> прочиталось бы через
    /// тот же вызов. Проверяется ФОРМА, а не «нет ли в строке точек»: чёрные
    /// списки обходят, белые — нет.
    /// </para>
    /// </summary>
    private static readonly Regex SectionPattern =
        new("^(?:preamble|[0-9]{1,4}[A-Z]?)$", RegexOptions.Compiled);

    /// <summary>
    /// Потолок одного ответа. Крупнейший раздел (§40) весит 143 КБ — одним
    /// куском он занял бы больше контекста, чем всё, ради чего его читают.
    /// </summary>
    public const int MaxChunkChars = 24_000;

    private readonly string? _root;

    public SpecLibrary(string? root)
    {
        _root = string.IsNullOrWhiteSpace(root) ? null : root;
    }

    /// <summary>
    /// Каталог рядом со сборкой — туда его кладёт csproj. Явный
    /// <c>--spec-dir</c> перебивает: сервер может быть запущен из чужого места.
    /// </summary>
    public static SpecLibrary Discover(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return new SpecLibrary(explicitRoot);
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "Spec");
        return new SpecLibrary(Directory.Exists(beside) ? beside : null);
    }

    public bool Available => _root != null && Directory.Exists(_root);

    /// <summary>Оглавление целиком — 14.5 КБ, точка входа для агента.</summary>
    public string? ReadIndex()
    {
        if (!Available)
        {
            return null;
        }

        var path = Path.Combine(_root!, "spec.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public IReadOnlyList<string> Sections()
    {
        if (!Available)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var file in Directory.GetFiles(_root!, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name != "spec.md" && SectionPattern.IsMatch(name))
            {
                names.Add(name);
            }
        }

        names.Sort(CompareSections);
        return names;
    }

    /// <summary>
    /// Кусок раздела. Возвращает <c>false</c>, когда раздела нет или имя не
    /// прошло проверку формы — отказ, а не исключение: агенту нужна причина.
    /// </summary>
    public bool TryReadSection(
        string section, int offset, out string text, out int totalChars, out string error)
    {
        text = string.Empty;
        totalChars = 0;
        error = string.Empty;

        if (!Available)
        {
            error = "Спека не поставлена с этим сервером (нет каталога Spec). " +
                    "Запустите с --spec-dir <путь>.";
            return false;
        }

        var trimmed = (section ?? string.Empty).Trim().TrimStart('§');
        if (!SectionPattern.IsMatch(trimmed))
        {
            error = $"«{section}» не похоже на номер раздела. Ожидается число с " +
                    "необязательной буквой (144, 29E, 75A) или preamble.";
            return false;
        }

        var path = Path.Combine(_root!, trimmed + ".md");
        if (!File.Exists(path))
        {
            error = $"Раздела §{trimmed} нет. Список — в оглавлении (read_spec без аргументов).";
            return false;
        }

        var whole = File.ReadAllText(path);
        totalChars = whole.Length;

        if (offset < 0) offset = 0;
        if (offset >= whole.Length)
        {
            text = string.Empty;
            return true;
        }

        var length = Math.Min(MaxChunkChars, whole.Length - offset);
        text = whole.Substring(offset, length);
        return true;
    }

    /// <summary>Числа по величине, а не по алфавиту: иначе §10 встаёт перед §9.</summary>
    private static int CompareSections(string left, string right)
    {
        var l = SplitSection(left);
        var r = SplitSection(right);
        var number = l.Number.CompareTo(r.Number);
        return number != 0
            ? number
            : string.CompareOrdinal(l.Suffix, r.Suffix);
    }

    private static (int Number, string Suffix) SplitSection(string name)
    {
        if (name == "preamble")
        {
            return (-1, string.Empty);
        }

        var digits = 0;
        while (digits < name.Length && char.IsDigit(name[digits]))
        {
            digits++;
        }

        return (int.Parse(name.Substring(0, digits)), name.Substring(digits));
    }
}

}

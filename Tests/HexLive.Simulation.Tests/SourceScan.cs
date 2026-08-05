using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HexLive.Simulation.Tests
{

/// <summary>Одно место в исходнике: файл, строка, что там нашли.</summary>
public sealed class SourceHit
{
    public string RelativePath;
    public int Line;
    public string Value;

    public override string ToString() => RelativePath + ":" + Line + "  " + Value;
}

/// <summary>
/// Чтение исходников симуляции как ДАННЫХ — на этом стоят линты и гейт дрейфа
/// вайтлиста.
/// <para>
/// Почему сканом текста, а не рефлексией по собранной сборке: типы событий —
/// это строковые литералы в аргументах вызова, их в метаданных нет вообще.
/// Единственная альтернатива — прогнать симуляцию и собрать то, что она успела
/// эмитить, но так виден лишь путь, по которому прошёл конкретный прогон:
/// событие смерти от кровопотери не эмитится, пока кто-нибудь не истечёт кровью.
/// Скан видит ВСЕ 384 точки эмита независимо от сценария.
/// </para>
/// </summary>
public static class SourceScan
{
    /// <summary>Все .cs симуляции. Пути — относительные от корня репо, чтобы
    /// сообщения об ошибках были кликабельными и одинаковыми на любой машине.</summary>
    public static IReadOnlyList<string> SimulationFiles()
    {
        return Directory
            .EnumerateFiles(RepoPaths.SimulationSources, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    public static string Relative(string absolutePath) =>
        Path.GetRelativePath(RepoPaths.Root, absolutePath).Replace('\\', '/');

    /// <summary>
    /// Литеральные типы событий, которые симуляция МОЖЕТ эмитить: третий аргумент
    /// <c>Trace.Emit</c>, второй у <c>Trace.EmitSystem</c>, плюс <c>Type = "…"</c>
    /// в прямых инициализаторах <c>new SimulationEvent</c> (их три штуки мимо
    /// хелперов: TickStart движка и две мутации мира).
    /// </summary>
    public static List<SourceHit> EmittedEventTypes()
    {
        var hits = new List<SourceHit>();

        foreach (var file in SimulationFiles())
        {
            var relative = Relative(file);
            if (IsInfrastructure(relative))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var call in FindCalls(text, "Trace.Emit"))
            {
                AddLiteralsOf(hits, relative, text, call, argumentIndex: 2);
            }

            foreach (var call in FindCalls(text, "Trace.EmitSystem"))
            {
                AddLiteralsOf(hits, relative, text, call, argumentIndex: 1);
            }

            foreach (var hit in FindObjectInitializerTypes(text, relative))
            {
                hits.Add(hit);
            }
        }

        return hits;
    }

    /// <summary>
    /// Точки эмита, где тип СОБИРАЕТСЯ во время работы — интерполяция или склейка.
    /// Именно это ловушка §63: id вещи ушёл В САМ ТИП
    /// (<c>"ClothesWashed underwear.bra …"</c>), и событие стало невидимым для
    /// вайтлиста, счётчиков и grep — оно как бы есть, и его как бы нет.
    /// <para>
    /// Тернарник из двух литералов (<c>raining ? "RainStarted" : "RainStopped"</c>)
    /// НЕ нарушение: оба имени константны и грепаются. Голое имя переменной или
    /// вызов хелпера — тоже не нарушение: это лишний прыжок для читателя, но
    /// множество имён остаётся конечным и записанным литералами.
    /// </para>
    /// </summary>
    public static List<SourceHit> ComposedEventTypes()
    {
        var hits = new List<SourceHit>();

        foreach (var file in SimulationFiles())
        {
            var relative = Relative(file);
            if (IsInfrastructure(relative))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var call in FindCalls(text, "Trace.Emit"))
            {
                AddIfComposed(hits, relative, text, call, argumentIndex: 2);
            }

            foreach (var call in FindCalls(text, "Trace.EmitSystem"))
            {
                AddIfComposed(hits, relative, text, call, argumentIndex: 1);
            }
        }

        return hits;
    }

    /// <summary>
    /// Все строковые литералы симуляции. Грубая сеть для вопроса «а существует ли
    /// такое имя вообще» — им закрывается косвенность: <c>CraftedTraceName(goal)</c>
    /// возвращает литералы из switch, <c>FinishPersonalCare(…, "Bathed")</c> получает
    /// литерал у вызывающего. Точный скан таких имён не видит, а вайтлист-гейт
    /// обязан отличать «имя живёт за одним прыжком» от «имени нет нигде» — второе и
    /// было настоящим багом (<c>Collapsed</c>, <c>MeatCooked</c>).
    /// </summary>
    public static HashSet<string> AllStringLiterals()
    {
        var literals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in SimulationFiles())
        {
            foreach (var line in File.ReadAllLines(file))
            {
                foreach (var literal in StringLiterals(line))
                {
                    literals.Add(literal);
                }
            }
        }

        return literals;
    }

    /// <summary>
    /// Литералы вида <c>"food.coconut"</c> — идентификаторы контента, зашитые в код.
    /// Ищутся только в системах: каталоги контента И ЕСТЬ место, где id должны жить.
    /// </summary>
    public static List<SourceHit> ContentIdLiterals()
    {
        var hits = new List<SourceHit>();
        var systemsRoot = Path.Combine(RepoPaths.SimulationSources, "Runtime", "Systems")
            .Replace('\\', '/');

        foreach (var file in SimulationFiles())
        {
            if (!file.Replace('\\', '/').StartsWith(systemsRoot, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Relative(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var literal in StringLiterals(lines[i]))
                {
                    if (LooksLikeContentId(literal))
                    {
                        hits.Add(new SourceHit
                        {
                            RelativePath = relative,
                            Line = i + 1,
                            Value = literal
                        });
                    }
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Сырой текст аргумента номер <paramref name="argumentIndex"/> у каждого вызова
    /// <paramref name="callee"/>. Разбор идёт по скобкам, поэтому перенос строки
    /// внутри вызова и вложенные вызовы разбор не ломают.
    /// </summary>
    public static List<SourceHit> CallArguments(string callee, int argumentIndex,
        string underRelativeDir = null)
    {
        var hits = new List<SourceHit>();

        foreach (var file in SimulationFiles())
        {
            var relative = Relative(file);
            if (underRelativeDir != null &&
                !relative.Contains(underRelativeDir, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var call in FindCalls(text, callee))
            {
                var args = SplitArguments(text, call);
                if (args == null || args.Count <= argumentIndex)
                {
                    continue;
                }

                hits.Add(new SourceHit
                {
                    RelativePath = relative,
                    Line = LineOf(text, call),
                    Value = args[argumentIndex].Trim()
                });
            }
        }

        return hits;
    }

    /// <summary>
    /// Присваивания вида <c>&lt;что-то&gt;.CurrentGoal = GoalType.X</c> — цели, которые
    /// ставятся системами НАПРЯМУЮ, минуя аукцион.
    /// </summary>
    public static List<SourceHit> DirectGoalAssignments()
    {
        var hits = new List<SourceHit>();
        const string marker = "CurrentGoal = GoalType.";

        foreach (var file in SimulationFiles())
        {
            var relative = Relative(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var at = lines[i].IndexOf(marker, StringComparison.Ordinal);
                if (at < 0 || IsCommented(lines[i], at))
                {
                    continue;
                }

                var start = at + marker.Length;
                var end = start;
                while (end < lines[i].Length && (char.IsLetterOrDigit(lines[i][end]) || lines[i][end] == '_'))
                {
                    end++;
                }

                hits.Add(new SourceHit
                {
                    RelativePath = relative,
                    Line = i + 1,
                    Value = lines[i].Substring(start, end - start)
                });
            }
        }

        return hits;
    }

    private static bool IsCommented(string line, int index)
    {
        var comment = IndexOfLineComment(line);
        return comment >= 0 && comment < index;
    }

    /// <summary>
    /// Файлы, которые сами и есть механизм трассировки — у них <c>Type</c> это
    /// параметр, а не литерал, и в скане они дали бы ложные срабатывания.
    /// </summary>
    private static bool IsInfrastructure(string relativePath) =>
        relativePath.EndsWith("Runtime/Diagnostics/SimTrace.cs", StringComparison.Ordinal) ||
        relativePath.EndsWith("Wire/SimulationEventCodec.cs", StringComparison.Ordinal);

    /// <summary>Позиции открывающей скобки каждого вызова <paramref name="callee"/>.</summary>
    private static IEnumerable<int> FindCalls(string text, string callee)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(callee, from, StringComparison.Ordinal);
            if (at < 0)
            {
                yield break;
            }

            from = at + callee.Length;

            // "Trace.Emit" не должен ловить "Trace.EmitSystem".
            var after = from;
            while (after < text.Length && char.IsWhiteSpace(text[after]))
            {
                after++;
            }

            if (after < text.Length && text[after] == '(')
            {
                yield return after;
            }
        }
    }

    /// <summary>
    /// Все литералы из выражения-типа: голый <c>"X"</c> даёт один, тернарник
    /// <c>c ? "A" : "B"</c> — оба.
    /// </summary>
    private static void AddLiteralsOf(List<SourceHit> into, string relative, string text,
        int openParen, int argumentIndex)
    {
        var args = SplitArguments(text, openParen);
        if (args == null || args.Count <= argumentIndex)
        {
            return;
        }

        var line = LineOf(text, openParen);
        foreach (var literal in StringLiterals(args[argumentIndex]))
        {
            into.Add(new SourceHit
            {
                RelativePath = relative,
                Line = line,
                Value = literal
            });
        }
    }

    private static void AddIfComposed(List<SourceHit> into, string relative, string text,
        int openParen, int argumentIndex)
    {
        var args = SplitArguments(text, openParen);
        if (args == null || args.Count <= argumentIndex)
        {
            return;
        }

        var raw = args[argumentIndex].Trim();
        var interpolated = raw.Contains("$\"", StringComparison.Ordinal);
        var concatenated = raw.Contains('"') && raw.Contains('+');
        if (!interpolated && !concatenated)
        {
            return;
        }

        into.Add(new SourceHit
        {
            RelativePath = relative,
            Line = LineOf(text, openParen),
            Value = raw
        });
    }

    /// <summary>Инициализаторы <c>new SimulationEvent { … Type = "X" … }</c>.</summary>
    private static IEnumerable<SourceHit> FindObjectInitializerTypes(string text, string relative)
    {
        foreach (var start in FindAll(text, "new SimulationEvent"))
        {
            var brace = text.IndexOf('{', start);
            if (brace < 0)
            {
                continue;
            }

            var end = MatchingBrace(text, brace);
            if (end < 0)
            {
                continue;
            }

            var body = text.Substring(brace, end - brace);
            var typeAt = body.IndexOf("Type", StringComparison.Ordinal);
            if (typeAt < 0)
            {
                continue;
            }

            var eq = body.IndexOf('=', typeAt);
            if (eq < 0)
            {
                continue;
            }

            var comma = body.IndexOf(',', eq);
            var value = comma < 0 ? body.Substring(eq + 1) : body.Substring(eq + 1, comma - eq - 1);
            var literal = AsPlainLiteral(value.Trim());
            if (literal == null)
            {
                continue;
            }

            yield return new SourceHit
            {
                RelativePath = relative,
                Line = LineOf(text, brace + typeAt),
                Value = literal
            };
        }
    }

    private static IEnumerable<int> FindAll(string text, string needle)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
            {
                yield break;
            }

            from = at + needle.Length;
            yield return at;
        }
    }

    /// <summary>
    /// Аргументы вызова, разрезанные по запятым ВЕРХНЕГО уровня — вложенные вызовы,
    /// строки с запятыми и интерполяция не должны разваливать разбор.
    /// </summary>
    private static List<string> SplitArguments(string text, int openParen)
    {
        var args = new List<string>();
        var depth = 0;
        var start = openParen + 1;

        for (var i = openParen; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipChar(text, i);
                continue;
            }

            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
                continue;
            }

            if (c == ')' || c == ']' || c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    args.Add(text.Substring(start, i - start));
                    return args;
                }

                continue;
            }

            if (c == ',' && depth == 1)
            {
                args.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        return null;
    }

    /// <summary>Индекс закрывающей кавычки строки, начинающейся на <paramref name="quote"/>.</summary>
    private static int SkipString(string text, int quote)
    {
        // Verbatim (@"…") и raw-строки в симуляции не встречаются; интерполяция
        // ($"…") с точки зрения пропуска ведёт себя как обычная строка, лишь бы
        // вложенные фигурные скобки внутри не считались за блок — сюда мы попадаем
        // ДО их обработки, поэтому они и не считаются.
        for (var i = quote + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static int SkipChar(string text, int quote)
    {
        for (var i = quote + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '\'')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipString(text, i);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Содержимое аргумента, если он — ОДИН обычный строковый литерал.
    /// Интерполяция, склейка и любое выражение дают null.
    /// </summary>
    private static string AsPlainLiteral(string argument)
    {
        var s = argument.Trim();
        if (s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"')
        {
            return null;
        }

        var inner = s.Substring(1, s.Length - 2);
        if (inner.Contains('"') || inner.Contains('\\'))
        {
            return null;
        }

        return inner;
    }

    /// <summary>
    /// Строковые литералы, комментарии пропускаются. Разбор построчный: обычный
    /// строковый литерал C# не может пересечь конец строки, поэтому так корректно
    /// и для многострочного выражения-аргумента.
    /// </summary>
    private static IEnumerable<string> StringLiterals(string source)
    {
        foreach (var line in source.Split('\n'))
        {
            foreach (var literal in LiteralsOfLine(line))
            {
                yield return literal;
            }
        }
    }

    private static IEnumerable<string> LiteralsOfLine(string line)
    {
        var comment = IndexOfLineComment(line);
        var text = comment < 0 ? line : line.Substring(0, comment);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }

            var end = SkipString(text, i);
            if (end <= i)
            {
                yield break;
            }

            yield return text.Substring(i + 1, end - i - 1);
            i = end;
        }
    }

    private static int IndexOfLineComment(string line)
    {
        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '"')
            {
                i = SkipString(line, i);
                continue;
            }

            if (line[i] == '/' && line[i + 1] == '/')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Похоже ли на id контента: <c>группа.имя</c> строчными, без пробелов.
    /// Формат намеренно узкий — <c>"H={0:F2}"</c>, пути и ключи трассы мимо.
    /// </summary>
    private static bool LooksLikeContentId(string literal)
    {
        if (literal.Length < 3 || literal.Contains(' ') || literal.Contains('=') ||
            literal.Contains('/') || literal.Contains('{'))
        {
            return false;
        }

        var dot = literal.IndexOf('.');
        if (dot <= 0 || dot == literal.Length - 1)
        {
            return false;
        }

        foreach (var c in literal)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        // Ровно одна точка: "food.coconut" — да, "a.b.c" — это уже не id контента.
        return literal.IndexOf('.', dot + 1) < 0;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}

}

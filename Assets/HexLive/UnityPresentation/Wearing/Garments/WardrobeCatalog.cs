using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace HexLive.UnityPresentation.Wearing.Garments
{

// Какие вещи и причёски вообще есть — СПИСОК ИМЁН, и ничего больше.
//
// Список берётся из каталога Addressables, который игра и так читает на старте:
// это перечень адресов, то есть по сути имён бандлов. Никакого своего файла
// рядом не нужно — положил бандл, и его имя появилось в перечне.
//
// Что НЕ делается здесь и делается намеренно: не грузится ни один ассет.
// Перечислить адреса — операция над таблицей строк; арт, материалы и всё
// прочее приезжает потом и только для той вещи, которую действительно надели.
//
// Отсюда и рабочий сценарий: заспавнилась девушка → алгоритм видит в перечне
// такие-то id → выдал ей одежду и причёску → загрузились ровно эти бандлы.
// Положили рядом новую вещь — она появилась в перечне, и следующая девушка
// может выйти уже в ней, без пересборки игры.
public static class WardrobeCatalog
{
    private const string WearPrefix = "wear/";
    private const string HairPrefix = "hair/";

    private static List<string> _wear;
    private static List<string> _hair;

    /// <summary>Id арта всех вещей, что лежат рядом с игрой.</summary>
    public static IReadOnlyList<string> Wear => _wear ??= Collect(WearPrefix, nested: false);

    /// <summary>Имена всех причёсок.</summary>
    public static IReadOnlyList<string> Hair => _hair ??= Collect(HairPrefix, nested: false);

    /// <summary>Перечитать перечень — контент положили, пока игра шла.</summary>
    public static void Forget()
    {
        _wear = null;
        _hair = null;
    }

    private static List<string> Collect(string prefix, bool nested)
    {
        var result = new List<string>();

        // Дождаться подъёма Addressables ОБЯЗАТЕЛЬНО: до инициализации список
        // локаторов пуст, и перечень молча выходит нулевым — ни ошибки, ни
        // подсказки, просто «вещей 0». Именно так и случилось на первом
        // прогоне: старт зовёт нас раньше, чем Addressables успевает встать.
        Addressables.InitializeAsync(false).WaitForCompletion();
        foreach (var locator in Addressables.ResourceLocators)
        {
            if (locator?.Keys == null)
            {
                continue;
            }

            foreach (var key in locator.Keys)
            {
                if (key is not string address || !address.StartsWith(prefix))
                {
                    continue;
                }

                var name = address.Substring(prefix.Length);
                // У волос адреса двух видов: сама причёска «hair/X» и её
                // материалы «hair/X/Цвет/Поверхность». Нужны только первые —
                // отсюда отсечение по вложенности, а не по длине или догадке.
                if (!nested && name.Contains('/'))
                {
                    continue;
                }

                if (!result.Contains(name))
                {
                    result.Add(name);
                }
            }
        }

        // Порядок — по имени: перечень участвует в seeded-розыгрыше одежды, а
        // порядок ключей в каталоге ничем не гарантирован.
        result.Sort(string.CompareOrdinal);
        return result;
    }

    /// <summary>Что нашлось — одной строкой в лог, для проверки глазами.</summary>
    public static void Report()
    {
        Debug.Log($"[Гардероб] в каталоге: вещей {Wear.Count}, причёсок {Hair.Count}. " +
                  "Это ИМЕНА бандлов; содержимое грузится, когда вещь надевают.");
    }
}

}

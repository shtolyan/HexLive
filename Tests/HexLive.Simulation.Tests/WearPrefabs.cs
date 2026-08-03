using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Tests
{

/// <summary>
/// Что НА САМОМ ДЕЛЕ написано в префабе одежды — слой и слоты, прочитанные с
/// диска, без Unity.
/// <para>
/// Спек §52.9: занятость места решает ПРЕФАБ (<c>BodyBones.Equip</c> держит одну
/// вещь на (слой, слот)), а симуляция его зеркалит. Зеркало проверяется только
/// против оригинала — поэтому гейт читает сам <c>*.prefab</c>, а не вторую копию
/// тех же данных.
/// </para>
/// <para>
/// Формат: Unity сериализует <c>int[]</c> (у нас — массив enum'а
/// <c>VisualWearSlot</c>) как HEX-строку, по 4 БАЙТА little-endian на элемент:
/// <c>slots: 1300000014000000</c> = FootR, FootL. Пусто = слотов нет.
/// <c>layer:</c> — порядковый номер <c>VisualWearLayer</c>, который совпадает с
/// <see cref="WearLayer"/> имя-в-имя (Underwear / Wear / Outerwear).
/// </para>
/// </summary>
public static class WearPrefabs
{
    /// <summary>Один файл префаба: где он лежит и что занимает на теле.</summary>
    public sealed class Prefab
    {
        public string File;
        public WearLayer Layer;
        public List<WearSlot> Slots = new List<WearSlot>();
    }

    /// <summary>Одна вещь гардероба. Бельё — два префаба (лифчик и трусы).</summary>
    public sealed class Entry
    {
        public List<Prefab> Prefabs = new List<Prefab>();

        /// <summary>Слои всех префабов вещи. Больше одного — уже поломка.</summary>
        public IEnumerable<WearLayer> Layers
        {
            get
            {
                var seen = new List<WearLayer>();
                foreach (var p in Prefabs)
                {
                    if (!seen.Contains(p.Layer))
                    {
                        seen.Add(p.Layer);
                    }
                }

                return seen;
            }
        }

        /// <summary>Объединение слотов по всем префабам вещи.</summary>
        public List<WearSlot> Slots
        {
            get
            {
                var all = new List<WearSlot>();
                foreach (var p in Prefabs)
                {
                    foreach (var s in p.Slots)
                    {
                        if (!all.Contains(s))
                        {
                            all.Add(s);
                        }
                    }
                }

                return all;
            }
        }
    }

    private static Dictionary<string, Entry> _byId;

    /// <summary>Папка арта: <c>Assets/Resources/HexLive/Wear/&lt;id&gt;/*.prefab</c>.</summary>
    public static string Root =>
        Path.Combine(RepoPaths.Root, "Assets", "Resources", "HexLive", "Wear");

    /// <summary>id вещи → её префабы. Читается один раз на прогон.</summary>
    public static IReadOnlyDictionary<string, Entry> ById => _byId ??= Scan();

    /// <summary>
    /// Столкнутся ли эти две вещи на теле — ровно правило <c>BodyBones.Equip</c>:
    /// какой-нибудь префаб одной и какой-нибудь префаб другой в ОДНОМ слое делят
    /// хотя бы один слот. Это то, что произойдёт в игре, чего бы ни думал сим.
    /// </summary>
    public static bool Collide(Entry a, Entry b)
    {
        foreach (var pa in a.Prefabs)
        {
            foreach (var pb in b.Prefabs)
            {
                if (pa.Layer != pb.Layer)
                {
                    continue;
                }

                foreach (var slot in pa.Slots)
                {
                    if (pb.Slots.Contains(slot))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Кто именно с кем столкнётся — для текста падения.</summary>
    public static string DescribeCollision(Entry a, Entry b)
    {
        foreach (var pa in a.Prefabs)
        {
            foreach (var pb in b.Prefabs)
            {
                if (pa.Layer != pb.Layer)
                {
                    continue;
                }

                foreach (var slot in pa.Slots)
                {
                    if (pb.Slots.Contains(slot))
                    {
                        return pa.Layer + "/" + slot;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, Entry> Scan()
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var entry = new Entry();
            foreach (var file in Directory.EnumerateFiles(dir, "*.prefab"))
            {
                entry.Prefabs.Add(Read(file));
            }

            if (entry.Prefabs.Count > 0)
            {
                result[Path.GetFileName(dir)] = entry;
            }
        }

        return result;
    }

    private static Prefab Read(string prefabPath)
    {
        var prefab = new Prefab { File = prefabPath };
        foreach (var line in File.ReadAllLines(prefabPath))
        {
            var slots = Regex.Match(line, @"^  slots: ?([0-9a-fA-F]*)\s*$");
            if (slots.Success)
            {
                foreach (var slot in Decode(slots.Groups[1].Value))
                {
                    if (!prefab.Slots.Contains(slot))
                    {
                        prefab.Slots.Add(slot);
                    }
                }

                continue;
            }

            var layer = Regex.Match(line, @"^  layer: (\d+)\s*$");
            if (layer.Success)
            {
                prefab.Layer = (WearLayer)int.Parse(layer.Groups[1].Value);
            }
        }

        return prefab;
    }

    private static IEnumerable<WearSlot> Decode(string hex)
    {
        for (var i = 0; i + 8 <= hex.Length; i += 8)
        {
            // little-endian: "13000000" -> 0x13 -> FootR
            var value = 0;
            for (var b = 3; b >= 0; b--)
            {
                value = (value << 8) | Convert.ToInt32(hex.Substring(i + b * 2, 2), 16);
            }

            yield return (WearSlot)value;
        }
    }
}

}

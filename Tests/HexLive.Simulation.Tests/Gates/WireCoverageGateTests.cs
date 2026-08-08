using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Поле, забытое в кодеке снапшота, — молчаливый отказ: колонистка просто
/// рендерится без раны.
/// <para>
/// Ловится не глазами, а рефлексией: каждому свойству записи проставляется
/// ЗАВЕДОМО НЕ-ДЕФОЛТНОЕ значение, снапшот кодируется, декодируется и
/// сравнивается свойство за свойством. Сравнение «как есть», без подстановки,
/// пропустило бы поле, случайно равное дефолту на обоих концах — а именно так
/// выглядит новое поле в первый день.
/// </para>
/// <para>
/// ⚠️ В дельта-режиме забытое поле портит зеркало НЕ на один кадр, а до
/// следующего ключевого — поэтому цена промаха здесь выше обычной.
/// </para>
/// </summary>
public sealed class WireCoverageGateTests
{
    /// <summary>
    /// По проводу НЕ едет намеренно, и это не забывчивость:
    /// <list type="bullet">
    /// <item>Tiles / Junctions — чистый выхлоп генерации мира (~14 000 узлов);
    /// приёмник строит их из сида, а совпадение доказывает контрольная сумма
    /// топологии в рукопожатии.</item>
    /// <item>TraceEvents — отдельный поток со своим watermark'ом.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> DeliberatelyNotTransported =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(WorldSnapshot.Tiles),
            nameof(WorldSnapshot.Junctions),
            nameof(WorldSnapshot.TraceEvents),
        };

    [Test]
    public void EveryTransportedPropertySurvivesRoundTrip()
    {
        var world = TestWorld.CreateWorld();
        WorldSnapshotExporter.IncludeDebugDetails = true;

        var sent = WorldSnapshotExporter.Export(world);
        Assert.That(sent.Npcs, Is.Not.Empty, "В прототипном мире нет NPC — нечего проверять.");
        Assert.That(sent.Objects, Is.Not.Empty, "В прототипном мире нет объектов.");

        var salt = 0;
        StampNonDefaults(sent, ref salt);

        var received = RoundTrip(sent);

        var differences = new List<string>();
        Compare(nameof(WorldSnapshot), sent, received, differences);

        Assert.That(differences, Is.Empty,
            "Свойство не пережило кодирование — значит WorldSnapshotCodec о нём не " +
            "знает. Добавь его в Write/Read (и помни: в дельтах промах живёт до " +
            "следующего ключевого кадра):\n  " + string.Join("\n  ", differences));
    }

    private static WorldSnapshot RoundTrip(WorldSnapshot snapshot)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSnapshotCodec.Write(snapshot, writer, includeDebugDetails: true);
        }

        buffer.Position = 0;
        var received = new WorldSnapshot();
        using var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, leaveOpen: true);
        WorldSnapshotCodec.Read(reader, received);
        return received;
    }

    /// <summary>
    /// Проставляет каждому записываемому свойству значение, отличное от дефолта и
    /// от соседей: иначе забытое поле совпало бы по обе стороны само собой.
    /// </summary>
    private static void StampNonDefaults(object record, ref int salt)
    {
        foreach (var property in Transported(record.GetType()))
        {
            var value = property.GetValue(record);

            if (value is IList list && property.GetSetMethod() == null)
            {
                foreach (var item in list)
                {
                    if (item != null && !IsLeaf(item.GetType()))
                    {
                        StampNonDefaults(item, ref salt);
                    }
                }

                continue;
            }

            if (property.GetSetMethod() == null)
            {
                continue;
            }

            salt++;
            var stamped = Stamp(property.PropertyType, salt);
            if (stamped != null)
            {
                property.SetValue(record, stamped);
            }
        }
    }

    private static object Stamp(Type type, int salt)
    {
        if (type == typeof(int))
        {
            return 1000 + salt;
        }

        if (type == typeof(float))
        {
            // Кратно 1/64: значение обязано пережить любое разумное сжатие
            // координат без потерь, иначе тест ловил бы точность, а не покрытие.
            return (float)(salt % 64) / 64f + 1f;
        }

        if (type == typeof(bool))
        {
            return salt % 2 == 0;
        }

        if (type == typeof(string))
        {
            return "v" + salt;
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.Length == 0 ? null : values.GetValue(salt % values.Length);
        }

        // Составные типы (TileCoord, Float2 и прочее) оставляем как есть: их
        // экспорт уже дал осмысленное значение, а собрать их вслепую нельзя.
        return null;
    }

    private static void Compare(string path, object sent, object received, List<string> into)
    {
        if (sent == null || received == null)
        {
            if (!ReferenceEquals(sent, received))
            {
                into.Add(path + ": одна из сторон null");
            }

            return;
        }

        foreach (var property in Transported(sent.GetType()))
        {
            var here = path + "." + property.Name;
            var a = property.GetValue(sent);
            var b = property.GetValue(received);

            if (a is IList listA && b is IList listB)
            {
                if (listA.Count != listB.Count)
                {
                    into.Add(here + ": длина " + listA.Count + " → " + listB.Count);
                    continue;
                }

                for (var i = 0; i < listA.Count; i++)
                {
                    if (listA[i] != null && !IsLeaf(listA[i].GetType()))
                    {
                        Compare(here + "[" + i + "]", listA[i], listB[i], into);
                    }
                    else if (!Equals(listA[i], listB[i]))
                    {
                        into.Add(here + "[" + i + "]: " + listA[i] + " → " + listB[i]);
                    }
                }

                continue;
            }

            // Nested record properties (MeleeStats, and future typed payloads)
            // are transported field-by-field just like list elements. Reference
            // equality would report a false failure even when every scalar made
            // the trip, so recurse into the record instead.
            if (a != null && b != null && !a.GetType().IsValueType && !IsLeaf(a.GetType()))
            {
                Compare(here, a, b, into);
                continue;
            }

            if (!Equals(a, b))
            {
                into.Add(here + ": " + Describe(a) + " → " + Describe(b));
            }
        }
    }

    private static IEnumerable<PropertyInfo> Transported(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => !DeliberatelyNotTransported.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);

    private static string Describe(object value) => value == null ? "null" : value.ToString();
}

}

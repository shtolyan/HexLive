using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §52.9: занятость места на теле решает ПРЕФАБ — и сим обязан отвечать так же.
///
/// <para>
/// Две модели одного и того же. Вид: <c>BodyBones.Equip</c> держит ОДНУ вещь на
/// (слой, слот) префаба и молча вытесняет чужую. Сим:
/// <c>WearSlotCatalog.Occupies</c> = слой из <see cref="ObjectDefinition.Layer"/>
/// + слоты из <see cref="WearSlotCatalog"/>. Между ними нет ни компилятора, ни
/// ссылки — только договорённость, что таблица зеркалит префаб.
/// </para>
/// <para>
/// ⭐ Расхождение стоило ~100 мс/тик. Если сим считает две вещи совместимыми, а
/// префабы дерутся за один (слой, слот), вещь надевается и вытесняется в тот же
/// кадр — навсегда: <c>SyncWorn</c> видел «в симе надето, на теле нет», чинил
/// это заново каждый тик (Instantiate + ~57 костей), и утекал набор материалов
/// на каждой попытке. Сейчас вид сдаётся после первой попытки и пишет
/// <c>[Wear] '&lt;id&gt;' was evicted the instant it was equipped</c> — то есть цена
/// расхождения теперь не кадр, а ПРОПАВШАЯ ВЕЩЬ. Гейт держит расхождение на нуле.
/// </para>
/// <para>
/// Чинить всегда В СТОРОНУ ПРЕФАБА (это записано кровью: однажды «починили»
/// наоборот и рубашка лишилась слотов запястий). Слоты префаба — истина; строка
/// каталога и <c>WearLayer</c> вещи подгоняются под неё.
/// </para>
/// </summary>
public sealed class WearSlotGateTests
{
    private static IReadOnlyDictionary<string, ObjectDefinition> _defs;

    private static IReadOnlyDictionary<string, ObjectDefinition> Defs =>
        _defs ??= PrototypeContentCatalog.CreateDefaults();

    /// <summary>Вещи гардероба так, как их видит симуляция (оттюненный экспорт).</summary>
    private static IEnumerable<ObjectDefinition> Garments =>
        GarmentLibrary.Active
            .Select(g => Defs.TryGetValue(g.Id, out var def) ? def : null)
            .Where(def => def is not null && def.Layer is not null);

    /// <summary>
    /// ⭐ ТОТ САМЫЙ инвариант: ни одна пара вещей, которые сим разрешает носить
    /// ОДНОВРЕМЕННО, не претендует на один (слой, слот) префаба.
    /// </summary>
    [Test]
    public void NoPairTheSimAllowsTogetherFightsForTheSameSpot()
    {
        var clashes = new List<string>();

        foreach (var (a, b) in Pairs())
        {
            if (WearSlotCatalog.Occupies(a, b))
            {
                continue; // сим сам снимет одну — драки не будет
            }

            var pa = WearPrefabs.ById[a.Id];
            var pb = WearPrefabs.ById[b.Id];
            if (WearPrefabs.Collide(pa, pb))
            {
                clashes.Add($"{a.Id} <-> {b.Id}: префабы дерутся за " +
                            $"{WearPrefabs.DescribeCollision(pa, pb)}, " +
                            $"а сим держит их в слоях {a.Layer}/{b.Layer} со слотами " +
                            $"[{string.Join(",", WearSlotCatalog.For(a.Id))}] / " +
                            $"[{string.Join(",", WearSlotCatalog.For(b.Id))}]");
            }
        }

        Assert.That(clashes, Is.Empty,
            "Сим разрешает носить вместе то, что тело носить вместе не может. " +
            "Каждая такая пара — ВЕЩЬ, КОТОРАЯ НЕ НАДЕВАЕТСЯ: её вытесняют в тот же " +
            "тик, и в консоли висит [Wear] ... was evicted the instant it was equipped. " +
            "Чинить в сторону ПРЕФАБА — строку WearSlotCatalog и/или WearLayer вещи, " +
            "никогда не огрублять слоты префаба (§52.9):\n  " +
            string.Join("\n  ", clashes));
    }

    /// <summary>
    /// Обратная сторона той же монеты: сим НЕ снимает то, что на теле уживается.
    /// Не падение кадра, а §52.9 в чистом виде — ложное вытеснение раздевает
    /// девушку без причины и бросает свежевыстиранное на песке.
    /// </summary>
    [Test]
    public void NoPairTheSimStripsActuallyFitsTogether()
    {
        var falseStrips = new List<string>();

        foreach (var (a, b) in Pairs())
        {
            if (!WearSlotCatalog.Occupies(a, b))
            {
                continue;
            }

            if (!WearPrefabs.Collide(WearPrefabs.ById[a.Id], WearPrefabs.ById[b.Id]))
            {
                falseStrips.Add($"{a.Id} <-> {b.Id}: сим снимает одну, а на теле они " +
                                "не пересекаются вовсе");
            }
        }

        Assert.That(falseStrips, Is.Empty,
            "Сим раздевает без причины: вещи занимают разные места на теле, но " +
            "вытесняют друг друга в симуляции (§52.9 — 61 такая пара уже была). " +
            "Строка каталога либо слой разъехались с префабом:\n  " +
            string.Join("\n  ", falseStrips));
    }

    /// <summary>
    /// Строка каталога — ЗЕРКАЛО слотов префаба, буква в букву. Без этого
    /// предыдущие два теста ловят только те расхождения, что уже дошли до пары.
    /// </summary>
    [Test]
    public void CatalogMirrorsThePrefabSlots()
    {
        var drift = new List<string>();

        foreach (var def in Garments)
        {
            if (!WearPrefabs.ById.TryGetValue(def.Id, out var prefab))
            {
                drift.Add($"{def.Id}: вещь есть в симе, а папки арта " +
                          $"Resources/HexLive/Wear/{def.Id}/ нет — рисовать нечем");
                continue;
            }

            var authored = prefab.Slots;
            if (authored.Count == 0)
            {
                drift.Add($"{def.Id}: у префаба НЕ ЗАПОЛНЕНЫ слоты, поэтому сим " +
                          "падает на грубый Covers и вытесняет по зоне защиты");
                continue;
            }

            var mirrored = WearSlotCatalog.For(def.Id);
            if (mirrored.Count == 0)
            {
                drift.Add($"{def.Id}: нет строки в WearSlotCatalog — сим падает на " +
                          $"Covers, хотя префаб знает точно: [{string.Join(",", authored)}]");
                continue;
            }

            var missing = authored.Where(s => !mirrored.Contains(s)).ToList();
            var extra = mirrored.Where(s => !authored.Contains(s)).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                drift.Add($"{def.Id}: префаб [{string.Join(",", authored)}], " +
                          $"каталог [{string.Join(",", mirrored)}]" +
                          (missing.Count > 0 ? $"; не хватает {string.Join(",", missing)}" : "") +
                          (extra.Count > 0 ? $"; лишние {string.Join(",", extra)}" : ""));
            }
        }

        Assert.That(drift, Is.Empty,
            "WearSlotCatalog разъехался с префабами. Править КАТАЛОГ под префаб, " +
            "не наоборот (§52.9):\n  " + string.Join("\n  ", drift));
    }

    /// <summary>
    /// ⭐ Баг #124: гейты выше проверяют КАТАЛОГ, а мир одевает девушек САМ —
    /// стартовый наряд пишет в <c>WornItems</c> напрямую, мимо
    /// <c>ResolveWearConflicts</c>. Пара, дерущаяся за один (слой, слот), там и
    /// заводилась: 20.3% колонисток на 201 сиде выходили на берег в ДВУХ низах
    /// (пул «лифчиков» ловил трусы с завышенной талией — у них Covers = Torso +
    /// Pelvis). Вид от этого не оправляется: вещи пересоздают друг друга каждый
    /// тик вечно, кадр уезжает в 4-6 FPS. Проверять надо РОЖДЁННЫЙ МИР, а не
    /// таблицу: таблица была верна всё это время.
    /// </summary>
    [Test]
    public void NoColonistIsBornWearingAClashingPair()
    {
        var offenders = new List<string>();
        var seeds = new List<int> { 476005489 }; // мир из отчёта #124
        var rng = new System.Random(20260813);
        for (var i = 0; i < 40; i++)
        {
            seeds.Add(rng.Next(1, int.MaxValue));
        }

        foreach (var seed in seeds)
        {
            var world = new WorldStateFactory().Create(PrototypeWorldDefinitionFactory.Create(seed));
            foreach (var npc in world.Entities.Npcs.Values)
            {
                var worn = npc.WornItems.Select(w => w.DefinitionId).ToList();
                for (var a = 0; a < worn.Count; a++)
                for (var b = a + 1; b < worn.Count; b++)
                {
                    Defs.TryGetValue(worn[a], out var da);
                    Defs.TryGetValue(worn[b], out var db);
                    if (WearSlotCatalog.Occupies(da, db))
                    {
                        offenders.Add($"seed {seed} NPC {npc.Id.Value}: {worn[a]} <-> {worn[b]} " +
                                      $"(слоты [{string.Join(",", WearSlotCatalog.For(worn[a]))}] / " +
                                      $"[{string.Join(",", WearSlotCatalog.For(worn[b]))}])");
                    }
                }
            }
        }

        Assert.That(offenders, Is.Empty,
            "Мир рождает девушку в паре, которую тело носить не может (§52.9, баг #124). " +
            "Это не опечатка в каталоге, а стартовый наряд: сузить пул в " +
            "WorldStateFactory и/или довериться EquipmentMath.StripConflictingWorn:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Слой — вторая половина того же ключа, и разъезжается он так же тихо.
    /// <c>armor.heavy</c> числился Outerwear при префабе Wear, и одно это поле
    /// давало 22 из 30 конфликтующих пар.
    /// </summary>
    [Test]
    public void SimLayerMatchesThePrefabLayer()
    {
        var drift = new List<string>();

        foreach (var def in Garments)
        {
            if (!WearPrefabs.ById.TryGetValue(def.Id, out var prefab))
            {
                continue; // об этом ругается CatalogMirrorsThePrefabSlots
            }

            var layers = prefab.Layers.ToList();
            if (layers.Count > 1)
            {
                drift.Add($"{def.Id}: префабы вещи лежат в РАЗНЫХ слоях " +
                          $"({string.Join(",", layers)}) — вытеснение перестаёт быть определённым");
                continue;
            }

            if (layers[0] != def.Layer)
            {
                drift.Add($"{def.Id}: префаб в слое {layers[0]}, сим считает {def.Layer}");
            }
        }

        Assert.That(drift, Is.Empty,
            "Слой одежды в симе разошёлся с префабом (§52.9). Менять WearLayer вещи " +
            "(GarmentLibrary.Defaults + ассет + simdata.json — все три), либо, если " +
            "прав всё-таки сим, слой САМОГО ПРЕФАБА:\n  " + string.Join("\n  ", drift));
    }

    /// <summary>
    /// Тот же слой живёт в ЧЕТЫРЁХ местах, и у них разный приоритет: ассет
    /// (что читает Unity) → simdata.json (что читают headless и сервер) →
    /// умолчание в коде (запасное). Гейт выше сверяет с префабом только сим;
    /// этот держит вместе остальные, иначе правка в одном месте выглядит как
    /// «не работает» — ровно грабли <see cref="SimDataFreshnessGateTests"/>,
    /// у которого секции garments нет.
    /// </summary>
    [Test]
    public void EveryHomeOfTheLayerAgrees()
    {
        var assets = Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Wearing", "Garments", "Assets");
        if (!Directory.Exists(assets))
        {
            Assert.Ignore("Каталог ассетов одежды не найден.");
        }

        var exported = GarmentLibrary.Active.ToDictionary(g => g.Id, g => g.Layer);
        var defaults = GarmentLibrary.Defaults.ToDictionary(g => g.Id, g => g.Layer);
        var drift = new List<string>();
        var compared = 0;

        foreach (var path in Directory.EnumerateFiles(assets, "*.asset", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            var id = Regex.Match(text, @"^  id: (.+)$", RegexOptions.Multiline);
            var layer = Regex.Match(text, @"^  layer: (\d+)$", RegexOptions.Multiline);
            if (!id.Success || !layer.Success)
            {
                continue;
            }

            var key = id.Groups[1].Value.Trim();
            var fromAsset = (WearLayer)int.Parse(layer.Groups[1].Value);
            compared++;

            if (exported.TryGetValue(key, out var fromExport) && fromExport != fromAsset)
            {
                drift.Add($"{key}: ассет даёт {fromAsset}, simdata.json — {fromExport}");
            }

            if (defaults.TryGetValue(key, out var fromCode) && fromCode != fromAsset)
            {
                drift.Add($"{key}: ассет даёт {fromAsset}, умолчание в коде — {fromCode}");
            }

            // Генератор каталога (GarmentCatalogBuilder) ищет ассет по пути
            // Assets/<Слой>/<slug>.asset и берёт слой ИЗ КОДА. Ассет, лежащий не
            // в своей папке, при следующем Rebuild не найдётся — рядом появится
            // второй с тем же id, а тюнинг старого потеряется.
            var folder = Path.GetFileName(Path.GetDirectoryName(path));
            if (folder != fromAsset.ToString())
            {
                drift.Add($"{key}: ассет со слоем {fromAsset} лежит в папке {folder}/ — " +
                          "Rebuild Catalog создаст рядом второй и потеряет тюнинг");
            }
        }

        Assert.That(compared, Is.GreaterThan(50),
            "Сверено подозрительно мало ассетов одежды — сломан разбор, а не данные.");

        Assert.That(drift, Is.Empty,
            "Слой одежды разъехался между ассетом, экспортом и кодом. В игре выигрывает " +
            "АССЕТ, в headless — simdata.json, поэтому правка одного из них выглядит как " +
            "«не работает»:\n  " + string.Join("\n  ", drift));
    }

    /// <summary>Все пары вещей гардероба, каждая по разу.</summary>
    private static IEnumerable<(ObjectDefinition A, ObjectDefinition B)> Pairs()
    {
        var all = Garments.Where(def => WearPrefabs.ById.ContainsKey(def.Id))
            .OrderBy(def => def.Id, System.StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                yield return (all[i], all[j]);
            }
        }
    }
}

}

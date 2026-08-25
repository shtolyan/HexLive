using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ ДВА ЧИСЛА, ОБЯЗАННЫЕ СОВПАДАТЬ: окно замаха в модели и длина клипа.
///
/// <para>
/// Сим открывает окно на <c>AttackDurationSeconds</c> (у кулака — на
/// длительность ВАРИАНТА удара) и в конце его ждёт следующий замах. Вид играет
/// клип, у которого своя, авторская длина. Совпадать они обязаны, а не
/// совпадало НИ ОДНО: кулак Punch A — 2.17 с против окна 1.5 с, копьё — 3.27
/// против 2.0. Снаружи это «удар оборвался на середине» или «второй замах
/// поверх первого», и найти это можно было только глазами.
/// </para>
/// <para>
/// §104 r4 подгоняет ТЕМП клипа под окно (AttackSpeed), поэтому небольшое
/// расхождение больше не видно. Но подгонка клампится
/// (<c>NpcActorView.ActionSpeedMin/Max</c> = 0.6…1.6): за этими границами
/// кламп срезает, рассинхрон возвращается — и заодно ускорение перестаёт
/// читаться как удар и начинает читаться как перемотка. Гейт следит именно за
/// границей, а не за точным равенством.
/// </para>
/// <para>
/// Длина клипа берётся из его <c>.meta</c> (<c>lastFrame − firstFrame</c> при
/// 30 fps) — формула сверена с живым Unity: 65 кадров = 2.1667 с ровно.
/// </para>
/// </summary>
public sealed class ClipWindowGateTests
{
    // Те же границы, что клампят подгонку в NpcActorView (ActionSpeedMin/Max).
    private const double MinFit = 0.6;
    private const double MaxFit = 1.6;

    /// <summary>
    /// Клипы, чья длина ЗАВЕДОМО не влезает в подгонку, и почему это пока
    /// терпимо. Список — ратчет: новое расхождение обязано либо чиниться, либо
    /// попадать сюда с объяснением, а не проскакивать молча.
    /// </summary>
    /// <remarks>
    /// Сейчас список ПУСТ, и это состояние по умолчанию: §104.8 выровнял окна
    /// по кадру контакта клипа, и последняя запись (копьё: 3.27 с против окна
    /// 2.0, подгонка 1.63 за клампом) ушла вместе с ней — окно копья стало
    /// 2.62 с, подгонка 1.25.
    /// </remarks>
    private static readonly Dictionary<string, string> Known =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Test]
    public void EveryAttackClipFitsItsSimWindow()
    {
        var gearDir = Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "RuntimeSource", "Gear");
        var assetsRoot = Path.Combine(RepoPaths.Root, "Assets");
        Assert.That(Directory.Exists(gearDir), Is.True, "Не найден каталог ассетов снаряжения: " + gearDir);

        var offenders = new List<string>();
        var staleKnown = new HashSet<string>(Known.Keys, StringComparer.Ordinal);
        var checkedClips = 0;

        foreach (var asset in Directory.EnumerateFiles(gearDir, "*.asset").OrderBy(p => p, StringComparer.Ordinal))
        {
            var gear = Path.GetFileNameWithoutExtension(asset);
            var scalars = UnityAsset.Scalars(asset);

            // Вариантные удары (кулаки): длительность собирается из строки —
            // замах + доигрыш, ровно как в GearConfig.ToStats.
            var rows = UnityAsset.StrikeRows(asset);
            for (var i = 0; i < rows.Count; i++)
            {
                Inspect($"{gear}.strike[{i}]", rows[i].ClipGuid, rows[i].HitDelay + rows[i].Follow);
            }

            // Плоское снаряжение: длительность лежит отдельным полем.
            var flat = scalars.TryGetValue("attackDurationSeconds", out var d) ? d : 0;
            var clips = UnityAsset.AttackClipGuids(asset);
            for (var i = 0; i < clips.Count; i++)
            {
                Inspect($"{gear}.attack[{i}]", clips[i], flat);
            }

            void Inspect(string label, string guid, double windowSeconds)
            {
                if (guid == null || windowSeconds <= 0.01)
                {
                    return;
                }

                var clipSeconds = UnityAsset.ClipSecondsByGuid(assetsRoot, guid);
                if (clipSeconds <= 0.01)
                {
                    return; // не нарезанный FBX — не наше дело
                }

                checkedClips++;
                var fit = clipSeconds / windowSeconds;
                if (fit >= MinFit && fit <= MaxFit)
                {
                    // Расхождение в пределах подгонки — но если оно записано в
                    // Known, запись протухла.
                    return;
                }

                if (Known.ContainsKey(label))
                {
                    staleKnown.Remove(label);
                    return;
                }

                offenders.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}: клип {1:F2} с против окна {2:F2} с (подгонка ×{3:F2}, " +
                    "допустимо {4:F2}…{5:F2})",
                    label, clipSeconds, windowSeconds, fit, MinFit, MaxFit));
            }
        }

        // Страховка от «сломан разбор, а не данные»: молчащий парсер дал бы
        // зелёный тест на любом наборе ассетов.
        Assert.That(checkedClips, Is.GreaterThan(5),
            $"Проверено всего {checkedClips} клипов — похоже, сломан разбор ассетов " +
            "или мет, а не данные.");

        Assert.That(offenders, Is.Empty,
            "Клип удара не влезает в окно, которое открывает симуляция:\n  " +
            string.Join("\n  ", offenders) +
            "\nЛибо поправь тайминги в ассете снаряжения (hitDelay+follow, " +
            "attackDurationSeconds), либо возьми клип подходящей длины. Подгонка " +
            "темпа (AttackSpeed) закрывает расхождение только внутри клампа.");

        Assert.That(staleKnown, Is.Empty,
            "Запись в Known больше не нужна — расхождение исчезло, удали строку:\n  " +
            string.Join("\n  ", staleKnown));
    }
}

}

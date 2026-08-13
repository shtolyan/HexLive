using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ УРОН ПАДАЕТ ТОГДА ЖЕ, КОГДА КУЛАК КАСАЕТСЯ ЦЕЛИ (spec §104.8).
///
/// <para>
/// <c>hitDelaySeconds</c> обещает ровно одно: через сколько секунд от начала
/// анимации ложится урон — а с ним кровь, вздрагивание и звук удара. Вид
/// растягивает клип точно на <c>attackDurationSeconds</c>, значит проверяемое
/// равенство одно:
/// </para>
/// <code>
///     hitDelaySeconds ≈ contactFraction × attackDurationSeconds
/// </code>
/// <para>
/// Доля контакта — ЗАМЕР по самому клипу
/// (<c>Tools/measure_strike_contact.py</c> → <c>Tools/strike_contacts.json</c>),
/// а не мнение. До §104.8 замах стоял «на глаз, ~75% окна» — число от
/// процедурного взмаха, которого в бою давно нет: настоящий контакт живёт на
/// 24-40% клипа, и брызга опаздывала за видимым ударом на 0.4-0.95 с. Симптом
/// («кровь с опозданием») ловится только глазами и только в бою, поэтому здесь
/// стоит гейт: два числа, обязанные совпадать, — ровно тот класс, что дал §102,
/// §103 и §104.4.
/// </para>
/// <para>
/// Второе равенство — КВАНТОВАНИЕ: сим кладёт удар на сетку тиков
/// (<c>SecondsToTicks</c>, 4 тика в секунду), поэтому замах обязан сам стоять
/// на четверти секунды. Иначе тот же рассинхрон возвращается, только мельче.
/// </para>
/// </summary>
public sealed class StrikeContactGateTests
{
    /// <summary>Полкадра при 30 fps ≈ 0.017 с; берём с запасом на округление
    /// авторских чисел до сотой. Старая ошибка была в 25 раз больше.</summary>
    private const double ToleranceSeconds = 0.05;

    private const double TicksPerSecond = 4.0;

    private sealed class ContactRow
    {
        public string Clip { get; init; }
        public double Fraction { get; init; }
        public int ContactFrame { get; init; }
        public int Frames { get; init; }
    }

    [Test]
    public void HitDelayMatchesTheClipContactFrame()
    {
        var contacts = LoadContacts();
        var gearDir = Path.Combine(RepoPaths.Root, "Assets", "Resources", "HexLive", "Gear");
        var assetsRoot = Path.Combine(RepoPaths.Root, "Assets");

        var offenders = new List<string>();
        var offGrid = new List<string>();
        var unmeasured = new List<string>();
        var checkedClips = 0;

        foreach (var asset in Directory.EnumerateFiles(gearDir, "*.asset")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var gear = Path.GetFileNameWithoutExtension(asset);

            var rows = UnityAsset.StrikeRows(asset);
            for (var i = 0; i < rows.Count; i++)
            {
                // Длительность варианта собирается ровно как в GearConfig.ToStats.
                Inspect($"{gear}.strike[{i}]", rows[i].ClipGuid,
                    rows[i].HitDelay, rows[i].HitDelay + rows[i].Follow);
            }

            var scalars = UnityAsset.Scalars(asset);
            var flatDuration = scalars.TryGetValue("attackDurationSeconds", out var d) ? d : 0;
            var flatHit = scalars.TryGetValue("hitDelaySeconds", out var h) ? h : 0;
            var clips = UnityAsset.AttackClipGuids(asset);
            for (var i = 0; i < clips.Count; i++)
            {
                Inspect($"{gear}.attack[{i}]", clips[i], flatHit, flatDuration);
            }

            void Inspect(string label, string guid, double hitDelay, double duration)
            {
                if (guid == null || duration <= 0.01 || hitDelay <= 0.001)
                {
                    return;
                }

                var meta = UnityAsset.MetaByGuid(assetsRoot, guid);
                if (meta == null)
                {
                    return; // клипа нет в проекте — не наше дело
                }

                var clip = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(meta)); // <имя>.fbx.meta
                if (!contacts.TryGetValue(clip, out var row))
                {
                    unmeasured.Add($"{label}: клип «{clip}»");
                    return;
                }

                checkedClips++;

                var expected = row.Fraction * duration;
                if (Math.Abs(expected - hitDelay) > ToleranceSeconds)
                {
                    offenders.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}: замах {1:F2} с, а клип «{2}» касается цели на {3:P1} окна " +
                        "({4:F2} с из {5:F2}) — разъезд {6:F2} с",
                        label, hitDelay, clip, row.Fraction, expected, duration,
                        Math.Abs(expected - hitDelay)));
                }

                var ticks = Math.Max(1, Math.Round(hitDelay * TicksPerSecond,
                    MidpointRounding.ToEven));
                if (Math.Abs(ticks / TicksPerSecond - hitDelay) > ToleranceSeconds)
                {
                    offGrid.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}: замах {1:F2} с, сим положит удар на {2:F2} с (сетка 0.25)",
                        label, hitDelay, ticks / TicksPerSecond));
                }
            }
        }

        Assert.That(checkedClips, Is.GreaterThan(5),
            $"Проверено всего {checkedClips} клипов — похоже, сломан разбор ассетов, " +
            "а не данные.");

        Assert.That(unmeasured, Is.Empty,
            "Клип удара не измерен — гейт про него ничего не знает:\n  " +
            string.Join("\n  ", unmeasured) +
            "\nПерезапусти замер:\n  /Applications/Blender.app/Contents/MacOS/Blender " +
            "-b -P Tools/measure_strike_contact.py -- --write");

        Assert.That(offenders, Is.Empty,
            "Урон падает не тогда, когда клип касается цели — кровь и звук " +
            "разъедутся с видимым ударом:\n  " + string.Join("\n  ", offenders) +
            "\nПравь hitDelaySeconds/attackDurationSeconds в ассете снаряжения " +
            "так, чтобы hitDelay = доля × длительность (цикл duration+cooldown " +
            "держи прежним — это баланс).");

        Assert.That(offGrid, Is.Empty,
            "Замах не стоит на сетке тиков — квантование сдвинет удар:\n  " +
            string.Join("\n  ", offGrid));
    }

    private static Dictionary<string, ContactRow> LoadContacts()
    {
        var path = Path.Combine(RepoPaths.Root, "Tools", "strike_contacts.json");
        Assert.That(File.Exists(path), Is.True,
            "Нет замера кадров контакта: " + path +
            "\nСними его: /Applications/Blender.app/Contents/MacOS/Blender -b " +
            "-P Tools/measure_strike_contact.py -- --write");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = new Dictionary<string, ContactRow>(StringComparer.Ordinal);
        foreach (var clip in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            var name = clip.GetProperty("clip").GetString();
            rows[name] = new ContactRow
            {
                Clip = name,
                Fraction = clip.GetProperty("contactFraction").GetDouble(),
                ContactFrame = clip.GetProperty("contactFrame").GetInt32(),
                Frames = clip.GetProperty("frames").GetInt32(),
            };
        }

        return rows;
    }
}

}

using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// «Вечный блеск / круглая марля до перезапуска» (bug #288 + жалоба на
/// нестынущий глянец): четыре независимых замка, каждый из которых в
/// одиночку замораживал раскраску кожи до перезапуска игры.
///
/// 1. §155-вытеснение выдёргивало карты покраски вместе с телом — возврат
///    девушки в восприятие пересобирал вью раньше, чем карта успевала
///    доехать («skin_Molly not found»), и painter жил без карты вечно.
/// 2. Ретрай карты жил в PlaceNewStamps и случался только при новых метках —
///    актриса без свежих ран не переспрашивала карту никогда.
/// 3. Исчезновение штампа (высохшая капля, снятый бинт) не будило fresh-
///    полосу — перепечатка ждала полного оборота кольца, а кольцо
///    планировщика раздували «вечные трупы»: painter на объекте, умершем
///    до первой активации, не получает OnDestroy/Unregister.
/// 4. Живой глянцевый слот с базой, запечённой при другой мокроте,
///    пропускался per-slot гейтом подписи — винил при сухом теле.
/// </summary>
public sealed class SkinPaintLivenessContractTests
{
    private static string Wearing(string file) => File.ReadAllText(Path.Combine(
        RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
        "Wearing", file));

    [Test]
    public void EvictionKeepsSkinPaintMapsResident()
    {
        var residency = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Content", "ContentResidency.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(residency, Does.Not.Contain(
                "PaintPointMap.Evict"),
                "Карты покраски кожи переживают вытеснение тела: их выдёргивание " +
                "рождало «skin_<актриса> not found» на возврате в восприятие.");
            Assert.That(residency, Does.Not.Contain(
                "EvictPath(\"HexLive/PaintMaps/"),
                "AtomicResources-кэш карт кожи тоже остаётся резидентным.");
        });
    }

    [Test]
    public void MapRetryLivesInSyncNotBehindPlacement()
    {
        var painter = Wearing("SkinTexturePainter.cs");

        var syncStart = painter.IndexOf("public void Sync(", System.StringComparison.Ordinal);
        var placeAt = painter.IndexOf(
            "private void PlaceNewStamps(", System.StringComparison.Ordinal);
        var sync = painter.Substring(syncStart, placeAt - syncStart);

        Assert.Multiple(() =>
        {
            Assert.That(syncStart, Is.GreaterThan(0));
            Assert.That(sync, Does.Contain(
                "RefreshPointMap(bypassRetryDelay: false)"),
                "Ретрай карты вызывается внутри Sync.");
            Assert.That(painter, Does.Contain("PaintPointMap.Request("),
                "Асинхронный первый miss нельзя считать terminal отсутствием карты.");
            Assert.That(painter, Does.Not.Contain("PaintPointMap.Load("));
            Assert.That(syncStart, Is.LessThan(placeAt),
                "Ретрай стоит ДО PlaceNewStamps: спрятанный за needsPlacement, " +
                "он не случался у актрисы без свежих меток.");
            Assert.That(painter, Does.Contain("_tombstoneScratch"),
                "Поздний прилёт карты снимает надгробия неразмещённых штампов.");
        });
    }

    [Test]
    public void StaleStampsAndWetBaseWakeTheFreshLane()
    {
        var painter = Wearing("SkinTexturePainter.cs");

        Assert.Multiple(() =>
        {
            Assert.That(painter, Does.Contain(
                "needsPlacement || targetsLost || _stale.Count > 0"),
                "Исчезнувшая метка будит fresh-полосу — сушка не ждёт оборота кольца.");
            Assert.That(painter, Does.Contain(
                "Mathf.Abs(_paintedWet[slot] - _wetSmoothness) > 0.05f"),
                "Сторожок мокрой базы: глянцевый слот с чужой базой перепекается.");
            Assert.That(painter, Does.Contain(
                "_paintedSig[slot] = 0;"),
                "Сторожок пробивает per-slot гейт подписи, не только гейт Sync.");
        });
    }

    // Вердикты игрока 2026-08-30: сухая кожа — матовая в НОЛЬ, а раны —
    // блестят. Стендовые замеры вскрыли настоящего убийцу канала: per-index
    // MaterialPropertyBlock на SkinnedMeshRenderer глушит сэмплинг
    // _MetallicGlossMap (probe_no_mpb: карта ожила ровно в момент снятия
    // MPB) — потому гладкость/тинт кожи пишутся В МАТЕРИАЛЫ (per-NPC
    // инстансы), а пин «1 на слот с картой» безопасен: per-pixel правду
    // несёт живая карта (base = мокрота, ядро крови = глянец).
    [Test]
    public void SkinWritesGoToMaterialsAndTheGlossChannelLives()
    {
        var view = Wearing("NpcActorView.cs");
        var painter = Wearing("SkinTexturePainter.cs");

        Assert.Multiple(() =>
        {
            Assert.That(view, Does.Contain(
                "internal const float DrySkinSmoothness = 0f;"),
                "Сухая кожа матовая в ноль — блеск продаёт мокроту и кровь.");
            Assert.That(view, Does.Not.Contain("SetPropertyBlock(_skinMpb"),
                "Per-index MPB на коже глушит карту гладкости — писать в материалы.");
            Assert.That(view, Does.Contain("_skinTintMaterials"),
                "Инстансы материалов кэшируются, а не аллоцируются геттером.");
            Assert.That(painter, Does.Contain(
                "private const bool WoundGlossEnabled = true;"),
                "Канал глянца ран жив — карта маскирует пин per-pixel.");
            Assert.That(painter, Does.Contain("material.shader = globalLit;"),
                "Бандловая копия URP/Lit без варианта карты пересаживается на " +
                "глобальный шейдер — иначе карта глянца молча игнорируется.");
            // Bug #307: пересадка шла по ВСЕМ материалам тела и делала ресницы
            // сплошными (их transparent-вариант живёт только в бандловой копии
            // шейдера). Пересаживаются только крашеные слоты кожи.
            Assert.That(painter, Does.Contain("foreach (var slot in _paintSlots)"),
                "Пересадка шейдера ограничена слотами кожи — ресницы/волосы " +
                "остаются на своей бандловой копии с прозрачным вариантом.");
        });
    }

    [Test]
    public void SchedulerPrunesDeadPainters()
    {
        var scheduler = Wearing("SkinPaintScheduler.cs");

        Assert.That(scheduler, Does.Contain(
            "is Object unityTarget && unityTarget == null"),
            "Кольцо чистит Unity-мёртвых по жизни объекта, а не по ссылке: " +
            "painter, умерший до активации, не получает OnDestroy и раньше " +
            "съедал ход кольца вечно.");
    }

    [Test]
    public void RepaintFailureIsNotRecordedAsSuccess()
    {
        var painter = Wearing("SkinTexturePainter.cs");

        // Каждый catch перепечатки обязан взводить _slotDeferred: иначе
        // RepaintAll записывал подпись как успех и слот пропускался вечно.
        var catches = 0;
        var index = 0;
        while (true)
        {
            index = painter.IndexOf("repaint failed", index, System.StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var windowStart = System.Math.Max(0, index - 700);
            var window = painter.Substring(windowStart, index - windowStart);
            Assert.That(window, Does.Contain("_slotDeferred = true;"),
                "Catch перепечатки не должен оставлять целую подпись слота.");
            catches++;
            index++;
        }

        Assert.That(catches, Is.GreaterThanOrEqualTo(3),
            "Ожидаются catch-и albedo/gloss/normal перепечаток.");
    }
}

}

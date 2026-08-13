using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.AbuseTest;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ КОНТРАКТ СНАПШОТА: сигналы, по которым вид рисует бой, обязаны доезжать
/// до снапшота И реально в нём меняться.
///
/// <para>
/// §103 стоил ЧЕТЫРЁХ кругов отладки, и каждый круг находил настоящий дефект —
/// это само по себе диагноз. Удары человека против человека не рисовались
/// никогда, потому что <c>MeleeSwing</c> (копия собачьего замаха) потеряла одну
/// строку: <c>SwingStartTick = world.Tick</c>. Модель была верна, события были
/// верны, урон был верен — молчала только картинка.
/// </para>
/// <para>
/// Почему это не ловилось ничем: весь харнесс кончается НА снапшоте.
/// <c>WireCoverageGate</c> проверяет кодек, но штампует поля ПОСЛЕ экспорта,
/// поэтому связка <c>NPCState → NpcSnapshot</c> не покрыта вообще. А соак и
/// golden-трассы смотрят в модель и слепы к тому, что получает вид.
/// </para>
/// <para>
/// Этот гейт закрывает ровно тот зазор, и проверяется он так: закомментируй
/// <c>actor.SwingStartTick = world.Tick</c> в <c>MeleeSwing</c> — тест обязан
/// покраснеть.
/// </para>
/// </summary>
public sealed class SnapshotContractGateTests
{
    // Сколько тиков арены смотрим. Сцена §81 повторяется примерно раз в 1000
    // тиков, а перед первой ему надо ещё дойти и захотеть — с запасом.
    private const int ArenaTicks = 6000;

    /// <summary>
    /// ⭐ МАППИНГ экспортера: поле состояния → поле снапшота.
    ///
    /// <para>
    /// Дешёвая половина гейта и та, что ловит §103 за миллисекунды: если
    /// экспортер разойдётся с полями (переименование <c>SwingStrikeIndex</c> →
    /// <c>StrikeIndex</c>, производная <c>IsSwinging</c>), это видно здесь, без
    /// прогона мира.
    /// </para>
    /// </summary>
    [Test]
    public void ExporterCarriesEveryCombatSignal()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();

        npc.IsFighting = true;
        npc.SwingStartTick = 4242;
        npc.SwingStrikeIndex = 2;
        npc.AttackAnimUntilTick = world.Tick + 5;
        npc.Mind.ForcedMeleeWeaponId = ContentIds.Knife;
        npc.HitStampTick = 4247;
        npc.HitWeaponId = "bite";
        npc.CompassionTrait = 0.17f;

        var open = Find(WorldSnapshotExporter.Export(world), npc.Id.Value);

        Assert.That(open.IsFighting, Is.True,
            "IsFighting не доехал — вид рисует мирную позу посреди драки.");
        Assert.That(open.SwingStartTick, Is.EqualTo(4242),
            "Штамп начала замаха не доехал. Вид опознаёт новый удар по СМЕНЕ " +
            "этого числа, и без него не играет НИЧЕГО (§103 r4).");
        Assert.That(open.StrikeIndex, Is.EqualTo(2),
            "Вариант удара не доехал — вид сыграет случайный клип вместо того, " +
            "который выбрала симуляция.");
        Assert.That(open.MeleeWeaponId, Is.EqualTo(ContentIds.Knife),
            "Оружие не доехало. Вид считал его САМ («лучшее из рюкзака») и не " +
            "знал про назначенное сценой — «бьёт ножом, а урон как рукой» (§103 r5).");
        Assert.That(open.IsSwinging, Is.True,
            "Окно анимации открыто (Tick < AttackAnimUntilTick), а IsSwinging " +
            "говорит обратное — проверь производную в WorldSnapshotExporter.");
        Assert.That(open.HitStampTick, Is.EqualTo(4247),
            "Штамп попадания не доехал — вид не даст ни брызги, ни флинча, ни " +
            "звука удара в момент, когда удар лёг (§104 r5).");
        Assert.That(open.HitWeaponId, Is.EqualTo("bite"),
            "Чем попали — не доехало: звук удара выбирается по этому полю " +
            "(кулак, клинок, зубы).");
        Assert.That(open.CompassionTrait, Is.EqualTo(0.17f).Within(0.0001f),
            "Постоянное сострадание не доехало — карточка показывает запас заботы вместо черты.");
        Assert.That(open.MeleeStats.LimbMultiplier,
            Is.EqualTo(npc.Body.LimbStrikeFactor()).Within(0.0001f));
        Assert.That(open.MeleeStats.StrengthMultiplier,
            Is.EqualTo(AttributeMath.MeleeStrengthMult(npc)).Within(0.0001f));
        Assert.That(open.MeleeStats.CombatMultiplier,
            Is.EqualTo(AttributeMath.MeleeCombatMult(npc)).Within(0.0001f));
        Assert.That(open.MeleeStats.AgilityRecoveryMultiplier,
            Is.EqualTo(AttributeMath.AttackCooldownMult(npc)).Within(0.0001f));

        // Окно закрылось — флаг обязан погаснуть, иначе процедурный замах
        // вида зависнет навсегда.
        npc.AttackAnimUntilTick = world.Tick;
        var closed = Find(WorldSnapshotExporter.Export(world), npc.Id.Value);
        Assert.That(closed.IsSwinging, Is.False,
            "Окно анимации истекло, а IsSwinging всё ещё поднят.");
    }

    /// <summary>
    /// §111.13: станция едет ЧИСЛОМ и реально меняется. Вид не имеет права
    /// выводить её из позиций — рендер интерполирует кадры, и производная
    /// станция мигала бы на границах.
    /// </summary>
    [Test]
    public void ExporterCarriesTheLyingStationSlot()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();

        var idle = Find(WorldSnapshotExporter.Export(world), npc.Id.Value);
        Assert.That(idle.LyingStationSlot, Is.EqualTo(-1),
            "Никакой станции не занято — слот обязан быть пустым.");

        npc.Execution.LyingStationTargetId = npc.Id;
        npc.Execution.LyingStationSlot = 3;
        var working = Find(WorldSnapshotExporter.Export(world), npc.Id.Value);
        Assert.That(working.LyingStationSlot, Is.EqualTo(3),
            "Номер станции не доехал — вид сыграет клип у ног, стоя у плеча.");
    }

    [Test]
    public void ExporterCarriesDerivedInventoryContainersWithoutCreatingASecondStore()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        npc.Inventory.Items.Add("tool.knife");
        npc.Inventory.Items.Add("resource.stick");
        npc.Inventory.Items.Add("resource.stick");
        EquipmentMath.RecalculateCapacity(world, npc);

        var exported = Find(WorldSnapshotExporter.Export(world), npc.Id.Value);
        var regular = exported.InventoryContainers.Where(c =>
            c.Kind is InventoryContainerKind.HandLeft or
                InventoryContainerKind.HandRight or
                InventoryContainerKind.Carry or
                InventoryContainerKind.Garment).ToArray();

        Assert.That(regular.Sum(c => c.Capacity), Is.EqualTo(exported.InventoryCapacity));
        Assert.That(regular.SelectMany(c => c.Slots).Count(s => s.StackCount > 0),
            Is.EqualTo(exported.InventoryUsedSlots));
        Assert.That(regular.SelectMany(c => c.Slots)
            .Single(s => s.ItemDefinitionId == "resource.stick").StackCount, Is.EqualTo(2));
        Assert.That(exported.FavoriteWeaponId, Is.EqualTo("tool.knife"));
        Assert.That(exported.InventoryItems, Does.Contain("tool.knife"),
            "Legacy item fields stay until the existing detail card has fully migrated.");
    }

    /// <summary>
    /// ⭐ СЦЕНАРНЫЙ инвариант: в живом бою сигналы ДЕЙСТВИТЕЛЬНО меняются.
    ///
    /// <para>
    /// Поле может доезжать и при этом всегда быть нулём — ровно так и выглядел
    /// §103. Поэтому здесь прогоняется настоящая арена абьюза (тот же класс
    /// строит сцену Unity и соак), снимается снапшот КАЖДЫЙ тик, и
    /// утверждается то, что нужно виду: штамп сменился несколько раз, каждый
    /// замах хоть один тик был виден как <c>IsSwinging</c>, вариант удара лежит
    /// в границах листа оружия, а боевой флаг не мигает посреди сцены.
    /// </para>
    /// </summary>
    [Test]
    public void AbuseSceneMovesEveryCombatSignal()
    {
        var definition = AbuseTestWorld.Build(313);
        var world = new WorldStateFactory().Create(definition);
        AbuseTestWorld.Prepare(world);

        var settings = new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval,
        };

        var clock = new SimulationClock();
        clock.Resume();

        var engine = new SimulationEngine(world, settings, clock);
        SimulationSystemRegistry.RegisterDefaults(engine);
        DefinitionIdTable.Build(world.Content);

        var stamps = new List<int>();          // все РАЗНЫЕ штампы замаха чужака
        var swingingStamps = new HashSet<int>(); // штампы, которые вид мог увидеть
        var badIndex = new List<string>();
        var fightingFlips = 0;
        var wasFighting = false;
        var sawFight = false;
        var lowestVictimHealth = 1f;
        var hits = new List<int>();            // все РАЗНЫЕ штампы попадания по жертвам
        var hitWeapons = new HashSet<string>();
        var lastHitByNpc = new Dictionary<int, int>();

        for (var i = 0; i < ArenaTicks && !world.Completed; i++)
        {
            engine.Step();

            var snapshot = WorldSnapshotExporter.Export(world);

            // §109: с честным ответным боем чужак может ПАСТЬ в арене — она
            // без лагерей и укрытий, клапан отхода §109.8 вхолостую, и трое с
            // ножами добивают его до конца окна. Всё, что гейту нужно, сцены
            // к этому моменту уже сказали — дальше мерить некого.
            var outsider = snapshot.Npcs.FirstOrDefault(
                n => n.Id.Value == AbuseTestWorld.OutsiderId);
            if (outsider is null)
            {
                break;
            }

            if (outsider.SwingStartTick > 0 &&
                (stamps.Count == 0 || stamps[^1] != outsider.SwingStartTick))
            {
                stamps.Add(outsider.SwingStartTick);
            }

            if (outsider.IsSwinging)
            {
                swingingStamps.Add(outsider.SwingStartTick);

                // Вариант удара индексирует клипы того же оружия: выход за
                // границы — молчаливый откат вида к случайному клипу, то есть
                // «сим выбрал ногу, показали кулак».
                var variants = GearCatalog.For(outsider.MeleeWeaponId ?? string.Empty)
                    .StrikeVariants;
                var length = variants?.Length ?? 0;
                if (outsider.StrikeIndex >= 0 &&
                    (length == 0 || outsider.StrikeIndex >= length))
                {
                    badIndex.Add($"тик {world.Tick}: StrikeIndex={outsider.StrikeIndex} " +
                        $"при оружии '{outsider.MeleeWeaponId}' с {length} вариантами");
                }
            }

            if (outsider.IsFighting != wasFighting)
            {
                fightingFlips++;
                wasFighting = outsider.IsFighting;
                sawFight |= outsider.IsFighting;
            }

            foreach (var girl in snapshot.Npcs)
            {
                if (girl.Id.Value != AbuseTestWorld.OutsiderId &&
                    girl.Health < lowestVictimHealth)
                {
                    lowestVictimHealth = girl.Health;
                }

                // Штамп попадания — по ЛЮБОМУ, кого ударили: сцена бьёт
                // девушку, она отвечает, помощницы вмешиваются.
                if (girl.HitStampTick <= 0)
                {
                    continue;
                }

                var seen = lastHitByNpc.TryGetValue(girl.Id.Value, out var prev) ? prev : 0;
                if (girl.HitStampTick == seen)
                {
                    continue;
                }

                lastHitByNpc[girl.Id.Value] = girl.HitStampTick;
                hits.Add(girl.HitStampTick);
                hitWeapons.Add(girl.HitWeaponId ?? string.Empty);

                // Штамп ставится в тик удара, а SimulationEngine.Step двигает
                // часы ПОСЛЕ систем — значит свежий штамп это ровно тик,
                // который только что отработал. Иначе кто-то ставит его задним
                // числом (или наперёд), и вид даст кровь со звуком не в тот
                // момент, когда бьют.
                Assert.That(girl.HitStampTick, Is.EqualTo(world.Tick - 1),
                    $"NPC{girl.Id.Value}: штамп попадания {girl.HitStampTick} после " +
                    $"тика {world.Tick - 1} — момент удара смещён.");
            }
        }

        // Страховка от вакуумного зелёного: без сцены все проверки ниже
        // выполняются сами собой и гейт врёт.
        Assert.That(sawFight, Is.True,
            $"За {ArenaTicks} тиков арены сцена абьюза не случилась ни разу — " +
            "проверять нечего. Смотри AbuseTestWorld.Prepare (часы за грейс §81) " +
            "и hexsoak --arena abuse --combat-frames.");

        Assert.That(stamps.Count, Is.GreaterThanOrEqualTo(3),
            $"Штамп начала замаха сменился {stamps.Count} раз(а) за всю арену. " +
            "Вид рисует удар ТОЛЬКО по смене этого числа: не меняется — не " +
            "рисуется ничего, при любых таймингах клипа (§103 r4).");

        Assert.That(swingingStamps.Count, Is.GreaterThanOrEqualTo(3),
            "Замахи были, но окно IsSwinging не совпало с ними ни разу — " +
            "значит вид физически не мог увидеть удар.");

        Assert.That(badIndex, Is.Empty,
            "Вариант удара вне листа оружия — вид молча сыграет случайный клип:\n  " +
            string.Join("\n  ", badIndex));

        // Мигание боевого флага = мирная поза посреди драки. MobSystem гасит
        // IsFighting у ВСЕХ каждый средний проход, и сцену перезажигает
        // FightScene.Latch — если латч отвалится, здесь будет десятки флипов.
        Assert.That(fightingFlips, Is.LessThanOrEqualTo(stamps.Count * 2 + 4),
            $"Боевой флаг чужака переключался {fightingFlips} раз при {stamps.Count} " +
            "замахах — он мигает внутри сцены. Вид читает его как боевую стойку, " +
            "и сцена пойдёт в мирной позе (§103 r3, FightScene.Latch).");

        Assert.That(lowestVictimHealth, Is.LessThan(1f),
            "Никто не пострадал — сцена не дошла до ударов, инварианты выше " +
            "проверяют пустоту.");

        // §104 r5: удары ЛОЖИЛИСЬ (здоровье падало) — значит и штамп попадания
        // обязан был смениться. Иначе кровь, флинч и звук молчат ровно так же,
        // как молчал замах до §103.
        Assert.That(hits.Count, Is.GreaterThanOrEqualTo(3),
            $"Здоровье жертвы падало, а штамп попадания сменился {hits.Count} " +
            "раз(а). Вид даёт брызгу, флинч и звук удара по СМЕНЕ этого штампа: " +
            "не меняется — удар прилетает в неподвижное тело беззвучно.");

        Assert.That(hitWeapons, Is.Not.Empty,
            "Ни одного «чем попали» — звук удара выбирать не по чему.");
        Assert.That(hitWeapons.All(w => w == "bite" || GearCatalog.Active.ContainsKey(w)),
            Is.True,
            "«Чем попали» не опознаётся ни как зубы, ни как снаряжение: " +
            string.Join(", ", hitWeapons.Select(w => "'" + w + "'")));
    }

    private static NpcSnapshot Find(WorldSnapshot snapshot, int id)
    {
        var npc = snapshot.Npcs.FirstOrDefault(n => n.Id.Value == id);
        Assert.That(npc, Is.Not.Null, $"NPC {id} не найден в снапшоте.");
        return npc;
    }
}

}

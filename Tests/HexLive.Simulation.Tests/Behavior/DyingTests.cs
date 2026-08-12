using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §105 «На грани смерти»: смерть перестала быть событием одного тика — тело
/// падает, лежит и тает, и всё это время его можно спасти.
///
/// <para>
/// Соак этих исходов не покажет: за пять колониальных дней на прототипном мире
/// не умирает никто, а на двадцатипятидневном смерть случается раз-другой и не
/// на каждом сиде. Вопрос «доходит ли до дела» — арена, и она здесь.
/// </para>
///
/// <para>
/// ⭐ Главное, что стережёт этот файл, — инвариант «умирающая ЖИВА»
/// (<c>Health &gt; 0</c>). Около сорока мест в симуляции читают
/// <c>Health &lt;= 0f</c> как «труп»; провались он, и лежащую перестанут
/// видеть ровно те системы, которые должны над ней склониться, а падение сразу
/// после входа в окно будет выглядеть как обычная мгновенная смерть.
/// </para>
/// </summary>
[NonParallelizable]
public sealed class DyingTests
{
    private bool _kenshiWasEnabled;
    private bool _aidFreezeWasEnabled;

    [SetUp]
    public void UseLegacySection105Model()
    {
        // This fixture is the executable contract for the superseded §105
        // timer model. §118 has its own focused fixture; keeping the two kill
        // switches separate makes both migration paths testable.
        _kenshiWasEnabled = Spec118.Enabled;
        Spec118.Enabled = false;

        // ⭐ И заморозка запаса помощью — тоже прочь, потому что здесь меряют
        // ЧАСЫ, а не помощь. §126/§49 r2 («хочешь спать — спи») сдвинул порог
        // сна, соседка перестала выбирать сон, добежала до раненой за те же
        // сорок тиков и начала лечить — запас честно замер, и гейт упал на
        // «часы не идут». Механика §105 сработала ровно как задумана, а тест
        // мерил её вместе с секундомером. Свой предмет он теперь изолирует.
        _aidFreezeWasEnabled = Spec105.AidFreezesReserve;
        Spec105.AidFreezesReserve = false;
    }

    [TearDown]
    public void RestoreKenshiModel()
    {
        Spec118.Enabled = _kenshiWasEnabled;
        Spec105.AidFreezesReserve = _aidFreezeWasEnabled;
    }

    // Довести до кровопотери: свежая глубокая рана + пустая кровь.
    //
    // Аптечку ОБЯЗАТЕЛЬНО обнулить: колонистка стартует с четырьмя бинтами, и
    // §40.3 авто-перевязка вытаскивает её раньше, чем кровь дойдёт до нуля —
    // тест мерил бы аптечку, а не окно умирания.
    private static void BleedToDeath(NPCState npc)
    {
        npc.Body.Parts[BodyPart.LegR] = 0.05f;
        npc.Wounds.Add(new WoundState
        {
            Id = 1, Zone = BodyPart.LegR, Severity = 0.5f, Heal01 = 0f, Seed = 1
        });
        npc.Health = npc.Body.Mean();
        npc.Needs.Blood = 0.001f;
        npc.Needs.Bandages = 0;
        npc.Needs.HerbalBandages = 0;
        npc.Needs.Pills = 0;
    }

    // Кольцо трассы подрезается на 2048 записях (≈11 тиков с подробной
    // трассой), поэтому типы событий СОБИРАЮТСЯ по ходу шагов. Искать их в
    // конце значит искать в том, что уже выбросили.
    private sealed class TraceWatch
    {
        private readonly WorldState _world;
        private long _watermark;

        public TraceWatch(WorldState world) => _world = world;

        public HashSet<string> Types { get; } = new();

        public void Drain()
        {
            foreach (var e in _world.Events.Items)
            {
                if (e.Seq <= _watermark)
                {
                    continue;
                }

                _watermark = e.Seq;
                Types.Add(e.Type);
            }
        }
    }

    private static TraceWatch Step(SimulationEngine engine, int ticks)
    {
        var watch = new TraceWatch(engine.World);
        for (var i = 0; i < ticks; i++)
        {
            engine.Step();
            watch.Drain();
        }

        return watch;
    }

    [Test]
    public void BloodLoss_LaysHerDownDyingInsteadOfKillingHer()
    {
        var engine = TestWorld.CreateEngine();
        var girl = engine.World.Entities.Npcs.Values.First();
        BleedToDeath(girl);

        var watch = Step(engine, 40);

        Assert.That(engine.World.Entities.Npcs.ContainsKey(girl.Id), Is.True,
            "Кровь на нуле больше не убивает в тот же тик.");
        Assert.That(girl.IsDying, Is.True);
        Assert.That(girl.Mind.DyingCause, Is.EqualTo(DyingCause.BloodLoss),
            "Причина выбирает и окно, и чем её спасать.");
        Assert.That(girl.Health, Is.GreaterThan(0f),
            "⭐ Умирающая ЖИВА: около сорока мест читают Health<=0 как «труп».");
        Assert.That(girl.IsUnconscious(engine.World.Tick), Is.True,
            "Бой и социум обязаны считать её беспомощной — §105 входит в IsUnconscious.");
        Assert.That(watch.Types, Contains.Item("Collapsed"));
        Assert.That(girl.Mind.DyingReserve, Is.GreaterThan(0f).And.LessThan(1f),
            "Запас тает по фактически прошедшим тикам.");
    }

    /// <summary>
    /// Никто не дотянулся — запас кончается, и она умирает.
    ///
    /// <para>
    /// «Никто не дотянулся» пришлось выстраивать честно, и это само по себе
    /// показание. Первая редакция теста только обнуляла соседкам бинты — и
    /// умирающая ВЫЖИВАЛА: её подкармливали, сытое тело затягивало рану до
    /// порога свёртывания (§44), кровотечение прекращалось, и симметричный
    /// выход §105.7 поднимал её сам. Механика сработала как задумана, неверна
    /// была посылка. Чтобы мерить именно исход «не успели», её организму
    /// нужно отказать: голод выше HealHungerGate закрывает и заживление, и
    /// восполнение крови.
    /// </para>
    /// </summary>
    [Test]
    public void NobodyReachesHer_SheDiesWhenTheReserveRunsOut()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        // Соседкам нечем и незачем помогать: без припасов §53 не поднимет
        // заявку, а собственный голод выше SelfHungerGate закрывает гейт §53.5.
        foreach (var other in world.Entities.Npcs.Values)
        {
            other.Needs.Bandages = 0;
            other.Needs.HerbalBandages = 0;
            other.Needs.Pills = 0;
            other.Needs.Hunger = 0.8f;
            other.CompassionTrait = 0f;
        }

        BleedToDeath(girl);
        // Голодное тело не заживляет ран и не восполняет кровь (HealHungerGate
        // 0.6) — единственный выход из окна ей закрыт.
        girl.Needs.Hunger = 0.8f;

        var diedAt = -1;
        for (var i = 0; i < 3000 && diedAt < 0; i++)
        {
            engine.Step();
            if (!world.Entities.Npcs.ContainsKey(girl.Id))
            {
                diedAt = world.Tick;
            }
        }

        Assert.That(diedAt, Is.GreaterThan(300),
            "Смерть НЕ мгновенная — окно обязано дать колонии шанс добежать.");
        Assert.That(diedAt, Is.LessThan(2500),
            "…но и не бесконечная: запас всё-таки кончается.");
        Assert.That(world.Entities.Corpses.ContainsKey(girl.Id), Is.True,
            "Смерть по-прежнему наступает единственным способом — свипом MobSystem.");
        var death = world.DeathRecords.LastOrDefault(d => d.EntityId.Equals(girl.Id));
        Assert.That(death?.Cause, Does.Contain("BledOut"),
            "Причина смерти доезжает до DeathRecord — событие эмитится ДО обнуления Health.");
    }

    [Test]
    public void Housemate_PullsHerBack_AndSheIsBarelyAliveAfter()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var all = world.Entities.Npcs.Values.ToList();
        var victim = all[0];
        var helper = all[1];

        // Помощница цела, сыта и с бинтами — гейт §53.5 «сначала выживи сама»
        // обязан её пропустить; иначе тест проверял бы этот гейт.
        helper.Needs.Bandages = 4;
        helper.Needs.HerbalBandages = 2;
        helper.Needs.Hunger = 0.1f;
        helper.Needs.Thirst = 0.1f;
        helper.Needs.Blood = 1f;
        helper.Health = 1f;
        helper.CompassionTrait = 1f;
        helper.Tile = victim.Tile;
        helper.Position = victim.Position;

        BleedToDeath(victim);

        var rescued = false;
        for (var i = 0; i < 900 && !rescued; i++)
        {
            engine.Step();
            rescued = !victim.IsDying && victim.Mind.ConvalescentUntilTick > 0;
        }

        Assert.That(rescued, Is.True,
            "Спасение — это §53 Aid: срочность 1.0 и вид помощи по причине.");
        Assert.That(world.Entities.Npcs.ContainsKey(victim.Id), Is.True);
        Assert.That(victim.Mind.ConvalescentUntilTick, Is.GreaterThan(world.Tick),
            "После подъёма несколько часов она еле ходит.");

        var effects = new List<ActiveEffect>();
        EffectEvaluator.Collect(victim, world.Tick, 0f, false, false, effects);
        Assert.That(effects.Any(e => e.Kind == EffectKind.Convalescent), Is.True,
            "§48.6: невидимых влияний нет — штраф обязан показаться чипом.");
        Assert.That(effects.Any(e => e.Kind == EffectKind.Dying), Is.False,
            "Чип умирания снят вместе с состоянием.");
    }

    /// <summary>
    /// Пробитая грудь держит её на земле, а не подбрасывает обратно на ноги.
    ///
    /// <para>
    /// Регресс, который был виден в бою: она валилась и вставала В ТОТ ЖЕ
    /// МОМЕНТ, снова получала удар, снова валилась. Причина — нулевой
    /// гистерезис: вход пиннил грудь РОВНО в <c>BodyFloor</c>, а выход
    /// спрашивал «грудь выше <c>BodyFloor</c>?», и первый же тик регенерации
    /// поднимал её на волосок выше порога. Упасть и встать стоило одного
    /// медленного тика.
    /// </para>
    /// </summary>
    [Test]
    public void TorsoCollapse_KeepsHerDown_NotOneTickAndUp()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        // Сытая и с бинтами — то есть в самых благоприятных для быстрого
        // подъёма условиях: регенерация открыта, аптечка при ней.
        girl.Needs.Hunger = 0.2f;

        // Через настоящий путь урона (§105 ResolveTrauma), а не присвоением
        // поля: проверяем ту развилку, которой пользуется бой.
        AmputateSystemHelpers.DebugBite(world, girl, BodyPart.Torso, 1f);
        Assert.That(girl.IsDying, Is.True, "Грудь в ноль обязана ронять в умирание.");
        Assert.That(girl.Mind.DyingCause, Is.EqualTo(DyingCause.VitalCrushed));

        var downFor = 0;
        for (var i = 0; i < 1500 && girl.IsDying; i++)
        {
            engine.Step();
            downFor++;
        }

        Assert.That(girl.IsDying, Is.False, "Она всё-таки должна подняться, если выжила.");
        Assert.That(downFor, Is.GreaterThan(200),
            "Упала с пробитой грудью — обязана отлежаться. Мгновенный подъём " +
            "превращает бой в мигание «упала-встала-упала».");
        Assert.That(girl.Body.Parts[BodyPart.Torso], Is.GreaterThan(Spec105.BodyFloor * 3f),
            "Встаёт, только когда грудь реально заросла ВЫШЕ порога падения, " +
            "а не на волосок над ним.");
    }

    /// <summary>
    /// Перевязка упирается в ПОТОЛОК УМЕНИЙ, а не доводит до сотни.
    ///
    /// <para>
    /// Регресс из игры: одна девочка садилась рядом и лечила соседку до 100% —
    /// бинт поднимал зоны безостановочно, и «спасли с того света» превращалось
    /// в «залечили начисто за пару минут».
    /// </para>
    ///
    /// <para>
    /// Обратная сторона важна не меньше: потолок новичка ОБЯЗАН быть выше
    /// порога подъёма (<c>Spec105.VitalExitHealth</c>), потому что Medicine у
    /// всех стартует с нуля. Потолок в 5%, о котором была первая мысль, сделал
    /// бы спасение физически недостижимым в начале игры.
    /// </para>
    /// </summary>
    [Test]
    public void Treatment_StopsAtTheHealersSkillCeiling()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var patient = world.Entities.Npcs.Values.First();
        var novice = world.Entities.Npcs.Values.Skip(1).First();

        Assert.That(novice.Skills.Get(SkillKind.Medicine), Is.EqualTo(0f),
            "Все стартуют с нулевым врачеванием — на этом и держится смысл теста.");

        var cap = AttributeMath.TreatCap(novice);
        Assert.That(cap, Is.GreaterThan(Spec105.VitalExitHealth),
            "Потолок новичка обязан быть выше порога подъёма, иначе спасать " +
            "в начале игры некому и §105 мертва.");
        Assert.That(cap, Is.LessThan(1f), "…и всё-таки НЕ сотня.");

        // Разбить зону и лечить много раз подряд — ровно то, что делала та
        // девочка. Расходники не кончаются: меряем потолок, а не аптечку.
        patient.Body.Parts[BodyPart.Torso] = 0.05f;
        patient.Health = patient.Body.Mean();
        for (var i = 0; i < 40; i++)
        {
            novice.Needs.Bandages = 4;
            novice.Needs.HerbalBandages = 2;
            ExecutionSystem.ApplyAidRelief(world, novice, patient, AidKind.Treat,
                new AidSupply.Spend(ContentIds.Bandage, 0f, herbal: true));
        }

        Assert.That(patient.Body.Parts[BodyPart.Torso], Is.LessThanOrEqualTo(cap + 0.001f),
            "Сорок перевязок подряд не поднимают зону выше потолка этих рук.");
        Assert.That(patient.Body.Parts[BodyPart.Torso], Is.GreaterThan(Spec105.VitalExitHealth),
            "…но до «встать» новичок довести обязан.");
    }

    [Test]
    public void KillSwitch_RestoresTheInstantDeath()
    {
        var was = Spec105.DyingEnabled;
        Spec105.DyingEnabled = false;
        try
        {
            var engine = TestWorld.CreateEngine();
            var girl = engine.World.Entities.Npcs.Values.First();
            BleedToDeath(girl);

            var watch = Step(engine, 40);

            Assert.That(engine.World.Entities.Npcs.ContainsKey(girl.Id), Is.False,
                "С выключенным §105 кровь на нуле снова убивает сразу.");
            Assert.That(watch.Types, Does.Not.Contain("Collapsed"));
            Assert.That(watch.Types, Contains.Item("BledOut"),
                "…и трасса та же, что была до §105, слово в слово.");
        }
        finally
        {
            Spec105.DyingEnabled = was;
        }
    }
}

}

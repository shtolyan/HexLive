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
/// §105.14 «Притворяется мёртвой»: очнувшись при враге рядом, она не встаёт —
/// лежит и прикидывается трупом, пока волк не потеряет к ней интерес.
///
/// <para>
/// ⭐ Главное утверждение этого файла — НЕ «она осталась лежать», а «ВРАГ ЕЁ
/// ОТПУСТИЛ». Наивная реализация (просто не давать ей встать) даёт поведение,
/// обратное задуманному: в этом проекте беспомощных догрызают — MobSystem
/// метит `IsUnconscious`-цель как лёгкую добычу, RaidMath.Opportunity весит
/// беспомощность В ПЛЮС, а §105 режет запас смерти от каждого удара по
/// лежащей. Притворство без потери интереса врага — самоубийство, поэтому
/// половина механики живёт на вражьей стороне (пара гейтов по форме §106).
/// </para>
///
/// <para>
/// Соак на этот вопрос не отвечает: «сколько раз за восемь дней» — не то же,
/// что «дошло ли до дела вообще» (CLAUDE.md). Арена отвечает за двадцать
/// секунд, соак ловит другое — «залегла навсегда».
/// </para>
/// </summary>
public sealed class PlayDeadTests
{
    // Волк вплотную к ней, уже ведущий погоню: и «не наводиться на новую», и
    // «бросить ведущуюся» проверяются на одном звере.
    private static HexLive.Simulation.Wildlife.MobState SpawnWolfOn(
        WorldState world, NPCState girl, bool chasing)
    {
        // До первого шага CurrentJunction ещё пуст — тогда берём любой узел её
        // гекса: зверю важно стоять НА ней, а не на конкретном джанкшене.
        var junctionId = girl.CurrentJunction ?? world.Tiles.Items[girl.Tile].Junctions[0];
        var junction = world.Junctions.Items[junctionId];
        var wolf = new HexLive.Simulation.Wildlife.MobState
        {
            Id = 4242,
            MobId = MobIds.Dog,
            Junction = junction.Id,
            Tile = girl.Tile,
            Position = junction.WorldPosition,
            TargetNpc = chasing ? girl.Id : null,
            Status = chasing
                ? HexLive.Simulation.Wildlife.MobStatus.Chasing
                : HexLive.Simulation.Wildlife.MobStatus.Roaming,
        };
        world.Mobs.Add(wolf);
        // Иначе спавнер подсыпет своих зверей и «врага рядом» устроит не тест.
        world.NextMobSpawnCheckTick = world.Tick + 1_000_000;
        return wolf;
    }

    // Уронить её обмороком (§40.13) и дать окну истечь: это самый короткий из
    // четырёх путей пробуждения, и ворота на подъёме у всех общие.
    private static void KnockOut(WorldState world, NPCState girl, int ticks = 4)
    {
        girl.Mind.FaintedUntilTick = world.Tick + ticks;
        ExecutionSystem.LieDownCentered(world, girl);
    }

    // Бодрая: иначе ветка усталости (StayDownIfSpent) уложит её спать раньше,
    // чем дело дойдёт до притворства, и тест померил бы §105 r5.
    private static void Rested(NPCState girl)
    {
        girl.Needs.Energy = 0.9f;
        girl.Needs.Stress = 0f;
    }

    [Test]
    public void NoEnemyNearby_SheJustGetsUp()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        world.Mobs.Clear();
        world.NextMobSpawnCheckTick = world.Tick + 1_000_000;
        Rested(girl);
        KnockOut(world, girl);

        for (var i = 0; i < 40; i++)
        {
            engine.Step();
        }

        Assert.That(girl.IsPlayingDead(world.Tick), Is.False,
            "Врага рядом нет — притворяться не от кого.");
        Assert.That(girl.Mind.PlayDeadSinceTick, Is.EqualTo(0),
            "Окно не взводилось вовсе.");
        Assert.That(world.Events.Items.Any(e => e.Type == "PlayDeadStarted"), Is.False);
    }

    /// <summary>
    /// ⭐ Ядро фичи: она лежит — И ВОЛК ЕЁ ОТПУСКАЕТ. Без второй половины
    /// притворство ухудшало бы её шансы, а не улучшало.
    /// </summary>
    [Test]
    public void WolfNearby_ShePlaysDead_AndTheWolfLosesInterest()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        Rested(girl);
        var wolf = SpawnWolfOn(world, girl, chasing: true);
        KnockOut(world, girl);

        for (var i = 0; i < 40; i++)
        {
            engine.Step();
            wolf.Tile = girl.Tile; // зверь караулит тело
        }

        Assert.That(girl.IsPlayingDead(world.Tick), Is.True,
            "Очнулась при волке — вставать нельзя, она лежит.");
        Assert.That(girl.Health, Is.GreaterThan(0f), "Притворство — не смерть.");
        Assert.That(girl.IsUnconscious(world.Tick), Is.False,
            "⭐ НЕ входит в IsUnconscious: его читатели — это и есть «догрызают " +
            "беспомощную» (MobSystem helpless, RaidMath.Opportunity).");
        Assert.That(girl.IsLyingDown(world.Tick), Is.True,
            "…но тело на земле, и помощь/соседки обязаны это видеть.");

        var dogEvents = string.Join(" | ", world.Events.Items
            .Where(e => e.Type.StartsWith("Dog"))
            .Select(e => $"{e.Type}: {e.Message}"));
        Assert.That(wolf.TargetNpc, Is.Null,
            $"⭐ ГЛАВНОЕ: волк потерял к ней интерес. Dog-события: [{dogEvents}]");
        Assert.That(wolf.Status, Is.EqualTo(HexLive.Simulation.Wildlife.MobStatus.Roaming));

        var effects = new System.Collections.Generic.List<ActiveEffect>();
        EffectEvaluator.Collect(girl, world.Tick, 0f, false, false, effects);
        Assert.That(effects.Any(e => e.Kind == EffectKind.PlayingDead), Is.True,
            "§48.6: состояние обязано быть видно игроку чипом.");
    }

    [Test]
    public void WolfLeaves_SheGetsUpAfterTheHold()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        Rested(girl);
        var wolf = SpawnWolfOn(world, girl, chasing: true);
        KnockOut(world, girl);

        for (var i = 0; i < 40; i++)
        {
            engine.Step();
            wolf.Tile = girl.Tile;
        }

        Assert.That(girl.IsPlayingDead(world.Tick), Is.True, "Предусловие: лежит.");
        world.Mobs.Remove(wolf); // волк ушёл

        for (var i = 0; i < Spec105.PlayDeadHoldTicks + 64; i++)
        {
            engine.Step();
        }

        Assert.That(girl.IsPlayingDead(world.Tick), Is.False,
            "Враг ушёл — окно дотикало, и она встала.");
        Assert.That(world.Events.Items.Any(e => e.Type == "PlayDeadEnded") ||
            girl.Mind.PlayDeadSinceTick == 0, Is.True,
            "Подъём прошёл через EndPlayDead (кольцо трассы могло подрезать событие).");
    }

    /// <summary>
    /// Предохранитель: волк, поселившийся у лагеря, не может уложить её
    /// навсегда — иначе это читалось бы как §102-зависание, а не как замысел.
    /// </summary>
    [Test]
    public void WolfNeverLeaves_SheStillGetsUpAtTheCap()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        Rested(girl);
        var wolf = SpawnWolfOn(world, girl, chasing: true);
        KnockOut(world, girl);

        var wasMax = Spec105.PlayDeadMaxTicks;
        Spec105.PlayDeadMaxTicks = 600; // короче Hold×3 — тест не должен идти часами
        try
        {
            var startedAt = -1;
            var stoodAt = -1;
            for (var i = 0; i < 4000 && stoodAt < 0; i++)
            {
                engine.Step();
                wolf.Tile = girl.Tile; // зверь не уходит НИКОГДА
                if (startedAt < 0 && girl.Mind.PlayDeadSinceTick != 0)
                {
                    startedAt = girl.Mind.PlayDeadSinceTick;
                }

                if (startedAt >= 0)
                {
                    Assert.That(girl.Mind.PlayDeadUntilTick,
                        Is.LessThanOrEqualTo(startedAt + Spec105.PlayDeadMaxTicks),
                        "Перевзвод обязан упираться в потолок, а не уезжать за него.");
                    if (girl.Mind.PlayDeadSinceTick == 0 && !girl.IsPlayingDead(world.Tick))
                    {
                        stoodAt = world.Tick;
                    }
                }
            }

            Assert.That(startedAt, Is.GreaterThan(0), "Предусловие: притворство началось.");
            Assert.That(stoodAt, Is.GreaterThan(0),
                "⭐ Встала, хотя враг рядом: без потолка это вечное лежание.");
            Assert.That(stoodAt - startedAt,
                Is.GreaterThanOrEqualTo(Spec105.PlayDeadMaxTicks),
                "…но не раньше потолка.");
        }
        finally
        {
            Spec105.PlayDeadMaxTicks = wasMax;
        }
    }

    /// <summary>
    /// ⭐ Обе ловушки, которые поймал соак (сид 42: 0 выживших из 4).
    ///
    /// <para>
    /// Первая: `TryStartPlayDead` был не идемпотентен, и обморок §40.13 поверх
    /// уже притворяющейся перештамповывал старт — потолок отсчитывался заново и
    /// не наступал НИКОГДА («PlayDeadStarted» каждые ~270 тиков без единого
    /// «Ended»). Вторая: у притворства нет сонного метаболизма комы, поэтому
    /// лежащая продолжала голодать и хотеть пить, не принимая решений; трупы
    /// на сиде 42 все были с Thirst=1,00 в трёх шагах от воды.
    /// </para>
    /// </summary>
    [Test]
    public void ReEntry_KeepsTheOriginalStart_AndOwnCrisisOutranksTheWolf()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        Rested(girl);
        var wolf = SpawnWolfOn(world, girl, chasing: true);
        KnockOut(world, girl);

        for (var i = 0; i < 40; i++)
        {
            engine.Step();
            wolf.Tile = girl.Tile;
        }

        Assert.That(girl.IsPlayingDead(world.Tick), Is.True, "Предусловие: лежит.");
        var firstStart = girl.Mind.PlayDeadSinceTick;

        // Обморок поверх притворства — и повторный вход через тот же край.
        girl.Mind.FaintedUntilTick = world.Tick + 4;
        for (var i = 0; i < 20; i++)
        {
            engine.Step();
            wolf.Tile = girl.Tile;
        }

        Assert.That(girl.Mind.PlayDeadSinceTick, Is.EqualTo(firstStart),
            "⭐ Старт НЕ перештамповывается: иначе потолок отсчитывается заново " +
            "и предохранитель не наступает никогда.");

        // Теперь жажда: она сильнее волка — лежать и умереть от неё нельзя.
        girl.Needs.Thirst = 1f;
        girl.Mind.IsDehydrated = true;
        for (var i = 0; i < 12; i++)
        {
            engine.Step();
            wolf.Tile = girl.Tile;
        }

        Assert.That(girl.IsPlayingDead(world.Tick), Is.False,
            "⭐ Свой кризис поднимает её ДО срока, хотя волк никуда не делся: " +
            "притворство — тактика, а не способ умереть лёжа.");
    }

    /// <summary>Кил-свитч: выключенная ручка = поведение до правки.</summary>
    [Test]
    public void KnobOff_BehavesExactlyAsBefore()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girl = world.Entities.Npcs.Values.First();
        Rested(girl);
        var wolf = SpawnWolfOn(world, girl, chasing: true);
        KnockOut(world, girl);

        var wasEnabled = Spec105.PlayDeadEnabled;
        Spec105.PlayDeadEnabled = false;
        try
        {
            for (var i = 0; i < 40; i++)
            {
                engine.Step();
                wolf.Tile = girl.Tile;
            }

            Assert.That(girl.Mind.PlayDeadUntilTick, Is.EqualTo(0),
                "Окно не взводится — значит и все проверки IsPlayingDead инертны.");
            Assert.That(girl.Mind.PlayDeadSinceTick, Is.EqualTo(0));
            Assert.That(girl.IsPlayingDead(world.Tick), Is.False);
            Assert.That(world.Events.Items.Any(e => e.Type == "PlayDeadStarted"), Is.False);
        }
        finally
        {
            Spec105.PlayDeadEnabled = wasEnabled;
        }
    }

    /// <summary>
    /// Гейты «не наводиться» на человеческой стороне: налётчик (§72) и абьюзер
    /// (§81) не выбирают притворяющуюся. Проверяется прямым вызовом
    /// селекторов — это дешевле и надёжнее, чем ждать налёта в соаке.
    /// </summary>
    [Test]
    public void HumanHunters_DoNotPickAPlayingDeadTarget()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var all = world.Entities.Npcs.Values.ToList();
        var mark = all[0];
        var hunter = all[1];

        // Притворство ставим напрямую: тест мерит СЕЛЕКТОРЫ, а не путь входа.
        mark.Mind.PlayDeadSinceTick = world.Tick;
        mark.Mind.PlayDeadUntilTick = world.Tick + Spec105.PlayDeadHoldTicks;

        Assert.That(RaidMath.BestVictim(world, hunter, out _),
            Is.Null.Or.Property("Id").Not.EqualTo(mark.Id),
            "§72: налётчик не выбирает притворяющуюся — и это исключение ДО " +
            "скоринга: Opportunity весит беспомощность в ПЛЮС.");
        Assert.That(AbuseMath.BestMark(world, hunter, out _),
            Is.Null.Or.Property("Id").Not.EqualTo(mark.Id),
            "§81: абьюзер тоже теряет к ней интерес.");
    }
}

}

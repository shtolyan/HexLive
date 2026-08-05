using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// ⭐ ЕДИНСТВЕННЫЙ таймлайн замаха (§29C.3 v2): windup → hit → recovery.
//
// §104 r2: до этого он существовал ДВАЖДЫ — здесь и в
// AnimalCombatSystem.RunCounterStrike, посимвольно, включая соль хеша 777.
// Копия и была тем классом багов, что стоил §103 четырёх кругов: собачья
// половина ставила SwingStartTick, человеческая эту строку потеряла, и удары
// человека против человека не рисовались НИКОГДА. Разошедшаяся копия
// компилируется и проходит тесты — поэтому её больше нет, а SwingTimelineGate
// следит, чтобы не завелась снова.
//
// Кто чем бьёт и как применяется урон — дело ВЫЗЫВАЮЩЕГО: человек идёт полным
// путём тела (ApplyHumanBlow — броня, рана, отрыв, износ, витали), собака
// принимает плоский Health, потому что тела у неё нет.
//
// WHY this and not §56's per-pass model, which was the obvious thing to reuse:
// PredationSystem deals PredationStrikePerPass(0.35) x MeleeStrikeBonus every
// MEDIUM tick, and a medium tick is one second. With a knife that is 0.516
// damage/second — a full-health torso is destroyed in two seconds, and
// VitalDestroyed kills outright. The §57 help cry has a 6-tile radius and the
// responder still has to WALK: she cannot arrive. It is just as lethal in
// reverse (three defenders = 0.66/s, the raider dies in 1.5 s). The timed model
// lands 0.221 every 2.74 s instead, which is the only version of this fight
// where a collective defence is physically possible.
internal static class MeleeSwing
{
    private const int TicksPerSecond = 4;

    // §99: нужен снаружи — сцена абьюза разводит свои удары по длине клипа.
    internal static int SecondsToTicks(float seconds) =>
        System.Math.Max(1, (int)System.Math.Round(seconds * TicksPerSecond));

    // Advance this actor's swing by one FAST tick. Returns true when a blow
    // lands this tick (damage/weaponId then describe it); starts a fresh windup
    // when recovered and the target is in reach.
    internal static bool TryAdvanceSwing(
        WorldState world, NPCState actor, bool inReach, out float damage, out string weaponId) =>
        TryAdvanceSwing(world, actor, inReach, out damage, out weaponId, out _);

    // §103: отдаёт ещё и длину КЛИПА, который только что отыграл. Постановочная
    // сцена разводит удары по ней, а не по базовой длительности оружия: у
    // кулака это разные числа (вариант удара против базы), и пауза считалась
    // не тем, чем меряется замах.
    internal static bool TryAdvanceSwing(
        WorldState world, NPCState actor, bool inReach,
        out float damage, out string weaponId, out float clipSeconds)
    {
        weaponId = EffectiveWeapon(actor);
        return TryAdvanceSwing(world, actor, inReach, weaponId, out damage, out clipSeconds);
    }

    /// <summary>
    /// Ядро. Оружие называет вызывающий: человек спрашивает
    /// <see cref="EffectiveWeapon"/> (знает про назначенное сценой), собачий бой
    /// берёт лучшее из рюкзака.
    /// </summary>
    internal static bool TryAdvanceSwing(
        WorldState world, NPCState actor, bool inReach, string weaponId,
        out float damage, out float clipSeconds)
    {
        damage = 0f;
        clipSeconds = 0f;

        var gear = GearCatalog.For(weaponId);

        if (actor.StrikeLandsAtTick > 0 && world.Tick >= actor.StrikeLandsAtTick)
        {
            actor.StrikeLandsAtTick = 0;
            gear.StrikeTimings(actor.SwingStrikeIndex,
                out var hitDelay, out var duration, out var cooldown);
            // §76: Agility shortens the recovery between swings — the same
            // weapon, swung back into position sooner. The windup (hitDelay)
            // and the animation length are left alone: those are the CLIP, and
            // speeding them up would desync the view's attack window.
            actor.StrikeReadyAtTick = world.Tick + SecondsToTicks(
                (duration - hitDelay + cooldown) * AttributeMath.AttackCooldownMult(actor));
            clipSeconds = duration;
            // Spec 19.3C: hurt arms strike weaker; the weapon owns its damage.
            // §76: StrikeFactor() now also carries her Strength and her Combat.
            damage = GearCatalog.Damage(weaponId) * actor.StrikeFactor();
            // §76: a landed blow is the only way Combat is practised. Awarded on
            // the HIT, not on the swing — swinging at air teaches nothing.
            SkillTrace.AwardHit(world, actor);
            return true;
        }

        if (actor.StrikeLandsAtTick == 0 && inReach && world.Tick >= actor.StrikeReadyAtTick)
        {
            actor.SwingStrikeIndex = gear.HasStrikeVariants
                ? System.Math.Min(gear.StrikeVariants.Length - 1,
                    (int)(MathUtil.Hash01(world.Seed, world.Tick, actor.Id.Value, 777) *
                        gear.StrikeVariants.Length))
                : -1;
            gear.StrikeTimings(actor.SwingStrikeIndex, out var hitDelay, out var duration, out _);
            actor.StrikeLandsAtTick = world.Tick + SecondsToTicks(hitDelay);
            actor.AttackAnimUntilTick = world.Tick + SecondsToTicks(duration);
            // ⭐ §103 r4: ШТАМП НАЧАЛА ЗАМАХА — то, по чему вид узнаёт, что бьют.
            //
            // Его здесь НЕ БЫЛО, и это была вся причина «удары есть, анимации
            // нет». NpcActorView.SetCombat опознаёт новый удар не по флагу
            // IsSwinging (окно живёт один-два тика и кадр может его проскочить),
            // а по СМЕНЕ этого штампа. Ставил его только собачий бой
            // (AnimalCombatSystem), а человек против человека — налёт и вся
            // сцена абьюза — не ставил никогда. Штамп оставался нулевым или
            // хранил чужой давний тик, смены не происходило, и вид не играл
            // ничего: ни замаха, ни удара, при любых таймингах клипа.
            //
            // Отсюда же и обманчивость: правки длительностей, числа ударов и
            // боевого флага честно меняли МОДЕЛЬ и не могли изменить картинку,
            // потому что картинка ждала другого сигнала.
            actor.SwingStartTick = world.Tick;
        }

        return false;
    }

    /// <summary>
    /// ⭐ ЧЕМ ОНА БЬЁТ НА САМОМ ДЕЛЕ — одно место на всех.
    ///
    /// <para>
    /// §97: обычно дерутся ЛУЧШИМ, что есть в руках. Но сцена может назначить
    /// оружие сама — наезд начинается рукопашкой, а тесак достают, когда уже
    /// ненавидят (§93). Пустая строка это кулаки, null — «как обычно».
    /// </para>
    /// <para>
    /// §103 r5: правило вынесено сюда, потому что вид считал его ПО-СВОЕМУ —
    /// брал лучшее оружие из рюкзака и не знал про назначенное сценой. Выходило
    /// «бьёт ножом, а урон как рукой»: он и правда бил кулаком, врала картинка.
    /// Экспортер снапшота теперь спрашивает здесь же.
    /// </para>
    /// </summary>
    internal static string EffectiveWeapon(NPCState actor) =>
        !actor.Body.CanUseToolsOrWeapons
            ? string.Empty
            : actor.Mind.ForcedMeleeWeaponId
              ?? SimBalance.BestMeleeWeapon(actor.Inventory.Items, actor.Body.IntactHands);

    /// <summary>
    /// Что сейчас должно быть вынуто из кобуры. Одна производная функция для
    /// всех боевых целей: назначили цель — вооружилась, сняли — убрала. Сцена
    /// с <see cref="NpcMind.ForcedMeleeWeaponId"/> имеет приоритет, потому что
    /// пустая строка там намеренно означает кулаки.
    /// </summary>
    internal static string ReadiedWeapon(NPCState actor)
    {
        if (!actor.Body.CanUseToolsOrWeapons)
        {
            return string.Empty;
        }

        if (actor.Mind.ForcedMeleeWeaponId is not null ||
            actor.Mind.CombatOpponentNpcId is not null ||
            GoalCatalog.ReadiesMeleeWeapon(actor.Mind.CurrentGoal))
        {
            return EffectiveWeapon(actor);
        }

        return string.Empty;
    }

    // Тайминги переехали в GearStats.StrikeTimings: это свойство снаряжения, и
    // спрашивать их должен ещё и ВИД (иначе он мерит замах базой, пока сим
    // мерит вариантом). Копия жила здесь и в AnimalCombatSystem.

    /// <summary>
    /// ⭐ ОТМЕТИТЬ ПОПАДАНИЕ ПО ЧЕЛОВЕКУ — один вызов на удар, кто бы ни бил.
    ///
    /// <para>
    /// Момент удара живёт один тик, и вид рисует только последний тик кадра —
    /// поэтому это ШТАМП, а не флаг (спек §83.2.3, тот же приём, что спас
    /// замах в §103). Зовётся и укусом зверя, и человеческим ударом: жертве
    /// всё равно, чем в неё прилетело, а виду нужен один сигнал.
    /// </para>
    /// </summary>
    /// <summary>Зубы зверя как «оружие» хит-штампа — та же константа, что
    /// читает вид (<see cref="GearCatalog.Bite"/>).</summary>
    internal const string BiteWeaponId = GearCatalog.Bite;

    internal static void StampHit(WorldState world, NPCState target, string weaponId,
        BodyPart part, Float2 from)
    {
        target.HitStampTick = world.Tick;
        target.HitWeaponId = weaponId ?? string.Empty;
        target.HitPart = part;
        target.HitFrom = from;
    }

    // A man aims high — far more torso and head than a dog's leg-first bite.
    internal static BodyPart PickHumanPart(WorldState world, int actorId)
    {
        var roll = MathUtil.Hash01(world.Seed, world.Tick, actorId, 703);
        if (roll < 0.45f) return BodyPart.Torso;
        if (roll < 0.65f) return BodyPart.Head;
        if (roll < 0.75f) return BodyPart.Pelvis;
        if (roll < 0.83f) return BodyPart.ArmR;
        if (roll < 0.90f) return BodyPart.ArmL;
        if (roll < 0.95f) return BodyPart.LegR;
        return BodyPart.LegL;
    }

    // Land a struck blow on a person, through the full established body
    // pipeline — armor, wound record, severance, garment wear, vitals — so a
    // human hit is bookkept exactly like a bite.
    internal static void ApplyHumanBlow(
        WorldState world, NPCState attacker, NPCState target,
        float damage, string weaponId, string traceName)
    {
        var part = AmputateSystemHelpers.RedirectFromStump(
            target, PickHumanPart(world, attacker.Id.Value));

        // ⭐ §104 r5: вот сейчас, вот этим, вот сюда. Единственный сигнал, по
        // которому вид синхронно даёт кровь, отбой тела и звук удара — см.
        // NPCState.HitStampTick. Ставится ДО пощады и до обнуления урона: удар
        // случился в любом случае, и видно его быть обязано.
        StampHit(world, target, weaponId, part, attacker.Position);
        var partArmor = EquipmentMath.ArmorForPart(world, target, part); // trace only
        var landed = EquipmentMath.Mitigate(world, target, part, damage);

        // §86: пощада. Человек не добивает того, кого не ненавидит: бьёт, пока
        // тот не сдался, и уходит. До конца доводят только заработанную
        // ненависть — симпатия падает с каждой сценой насилия (§81), так что
        // «добьют или нет» становится следствием ИСТОРИИ отношений, а не
        // отдельной ручки.
        //
        // Порог, а не запрет: удар всё равно наносится, рана пишется, кровь
        // идёт — просто здоровье не проваливается ниже порога. Проигравший
        // остаётся лежать битым, а не мёртвым.
        if (Spec86.MercyEnabled && Merciful(world, attacker, target))
        {
            var floorHp = Spec86.MercyHealthFloor;
            if (target.Health <= floorHp)
            {
                landed = 0f;
            }
            else
            {
                // Не дать этому удару перепрыгнуть порог.
                var room = (target.Health - floorHp) * target.Body.Parts.Count;
                landed = System.Math.Min(landed, room);
            }

            // Пощада работает и ПО ЧАСТЯМ, не только по среднему. room выше
            // конвертирует запас СРЕДНЕГО в урон одной части — это до ×7 её
            // максимума, поэтому серия ударов в одну голову уничтожала Head
            // (VitalDestroyed → мгновенная смерть) задолго до порога пощады.
            // Часть под ударом не опускается ниже MercyPartFloor — ни
            // мгновенная смерть, ни отрыв конечности (TrySeverOnBite требует
            // ровно 0) при пощаде невозможны.
            landed = System.Math.Min(landed,
                System.Math.Max(0f, target.Body.Parts[part] - Spec86.MercyPartFloor));
        }

        target.Body.Parts[part] = System.Math.Max(0f, target.Body.Parts[part] - landed);
        target.Health = target.Body.Mean();
        DamageReactionSystemHelpers.GrantAdrenaline(world, target, landed, traceName);
        WoundMath.Inflict(world, target, part, landed);
        AmputateSystemHelpers.TrySeverOnBite(world, target, part, landed);
        EquipmentMath.WearCoveringItems(world, target, part, SimBalance.ClothingBiteDurabilityWear);

        // §105: единая развилка «умерла или ещё умирает». Зовётся на КАЖДЫЙ
        // удар, а не только на добивающий: по лежащей на грани удар срезает
        // запас смерти напрямую — её догрызают.
        MortalityHelpers.ResolveTrauma(world, target, landed, $"NPC{attacker.Id.Value}");

        Trace.Emit(world, attacker.Id, traceName,
            $"Target=NPC{target.Id.Value} {part} -{landed:F3} (armor={partArmor:F2}) " +
            $"Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)} " +
            $"TargetHealth={target.Health:F2}");
    }

    // §86: щадит ли этот бьющий эту цель. Ненависть — заработанная: симпатия
    // ниже HatredAffinity значит несколько сцен насилия за спиной, а не
    // случайную ссору.
    private static bool Merciful(WorldState world, NPCState attacker, NPCState target)
    {
        if (!Spec86.MercyAppliesToOutsiders &&
            FactionRelations.AreHostile(attacker.Faction, target.Faction))
        {
            return false;
        }

        // §108: пришли ПОБИТЬ, а не казнить. Сговор набирает ненависть быстро
        // (жертва -0.35 за сцену, свидетельницы -0.18), так что к моменту
        // расправы почти каждая уже за порогом §86 — и замеры это подтвердили:
        // в 2 охотах из 4 чужак умирал на четвёртый-пятый день, а он на острове
        // один, и вместе с ним кончалась вся линия. Здесь пощада держится, пока
        // цель — их общая цель охоты: они бьют до «свалился», он приходит в
        // себя и ненавидит их сильнее (§91 — в следующий раз с ножом).
        // Выключить ручку — и расправа снова может стать смертельной.
        if (Spec108.GroupHuntMercyHolds &&
            attacker.Mind.CurrentGoal == GoalType.GroupHunt &&
            attacker.Mind.GroupHuntTargetNpcId is { } hunted &&
            hunted.Equals(target.Id))
        {
            return true;
        }

        return attacker.Social.GetOrCreate(target.Id).Affinity > Spec86.HatredAffinity;
    }

    // «Достаёт ли рука» переехало в InteractionReach.CanStrike — туда, где
    // объявлена вся таблица мер близости. Здесь оно было пятой мерой, о которой
    // таблица не знала, и §102 вырос ровно из этого зазора.
}

}

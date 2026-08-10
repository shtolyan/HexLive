using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §81: кого гнобить и чем это кончится. Сестра RaidMath — та же доктрина
// «арифметика, а не частные случаи»: одна формула силы, из которой сами собой
// вытекают и «она сдалась, потому что он с копьём», и «она отказала, потому что
// подошли подруги».
public static class AbuseMath
{
    // Что ему сейчас нужно от неё. Жажда идёт вперёд голода — она убивает
    // быстрее, тот же порядок держит §53.
    // Что он у неё возьмёт.
    //
    // §93: гопник отбирает НЕ ПОТОМУ ЧТО ГОЛОДЕН, а потому что может. Раньше
    // порог голода/жажды стоял на входе, и получалось так: гонит его
    // одиночество, живот при этом сытый — он подходил, тряс и уходил с
    // пустыми руками. За 132 сцены добыча случилась ДВА раза, и игрок
    // естественно не видел никакого отжима вовсе.
    //
    // Теперь порог решает только ПОРЯДОК: что схватить первым, если есть и
    // еда, и вода. Своя нужда важнее — но её отсутствие больше не мешает.
    public static AidKind Wants(NPCState npc)
    {
        if (npc.Needs.Thirst >= Spec81.AbuseSupplyFloor &&
            npc.Needs.Thirst >= npc.Needs.Hunger)
        {
            return AidKind.Hydrate;
        }

        if (npc.Needs.Hunger >= Spec81.AbuseSupplyFloor)
        {
            return AidKind.Feed;
        }

        // Сыт и напоен — но забрать всё равно надо. Что перевешивает у него
        // самого, то и берём.
        return npc.Needs.Thirst >= npc.Needs.Hunger ? AidKind.Hydrate : AidKind.Feed;
    }

    // §93: а если того, что он хотел, у неё нет — берём то, что есть. Пустые
    // руки после сцены читаются как «ничего не произошло».
    public static AidKind WhatToTake(WorldState world, NPCState abuser, NPCState mark)
    {
        var first = Wants(abuser);
        if (AidSupply.Has(world, mark, first))
        {
            return first;
        }

        var other = first == AidKind.Hydrate ? AidKind.Feed : AidKind.Hydrate;
        return AidSupply.Has(world, mark, other) ? other : AidKind.None;
    }

    // Насколько сильно его к этому тянет. Две независимые причины, берётся
    // бóльшая: пустой живот и пустые дни давят по отдельности.
    public static float Drive(NPCState npc)
    {
        var supply = 0f;
        var want = Wants(npc);
        if (want != AidKind.None)
        {
            var need = want == AidKind.Hydrate ? npc.Needs.Thirst : npc.Needs.Hunger;
            supply = need <= Spec81.AbuseNeedCeiling ? need : 0f;
        }

        // §82: одиночество давит СИЛЬНЕЕ голода. Раньше оно давало ровно
        // 1 - Social, то есть максимум 1.0, и тонуло среди бытовых дел: чужак
        // сидел с общением в нуле и спокойно строил лежанку. Множитель
        // поднимает нужду над бытом — человеку, который ни с кем не говорил,
        // не до лежанки.
        // §85: гнобит, пока общение не наберёт хотя бы половину, — потом идёт
        // по своим делам. Раньше порог стоял на 0.45 и он останавливался,
        // толком не начав.
        var social = npc.Needs.Social < Spec81.AbuseSocialFloor
            ? (1f - npc.Needs.Social) * Spec82.LonelinessDriveMult
            : 0f;

        return System.Math.Max(supply, social);
    }

    // §81.11: держат ли ещё льготные дни. Оба входа в цель — аукцион
    // DecisionSystem и прерывание RaidSystem.TryStartAbuse — обязаны звать
    // ЭТО, а не сравнивать тик сами: два рукописных сравнения уже разъехались
    // бы, как §72/§81. Смотрит ТОЛЬКО общение, не Drive: одержимость — про
    // пустые дни, а не про пустой живот.
    public static bool GraceHolds(WorldState world, NPCState npc) =>
        world.Tick < Spec81.AbuseGraceDays * EnvironmentSystem.DayLengthTicks &&
        npc.Needs.Social > Spec81.AbuseObsessionSocialCeiling;

    // §81.16: «сильно ранен» — хоть одна витальная зона (голова/грудь/таз,
    // BodyState.VitalParts — то же определение, что у смерти §105) потеряла
    // больше, чем AbuseWoundedVitalFloor. Такому не до сцен: оба входа в цель
    // (аукцион DecisionSystem и латч RaidSystem) обязаны звать ЭТО — по той же
    // причине, что и GraceHolds выше. Идущей сцены и самозащиты гейт не
    // касается: если бьют его, работает обычный бой и бегство §81.13.
    public static bool BadlyWounded(NPCState npc) =>
        npc.Body.VitalHealth() < Spec81.AbuseWoundedVitalFloor;

    // Боевая мощь в глазах смотрящего. Всё безразмерное, поэтому сумма —
    // сравнимое число, а не мешанина единиц.
    //
    // Множитель урона налёта (Spec72.RaidStrikeDamageMult) сюда НЕ входит
    // намеренно: это оценка того, что ВИДНО — оружие, руки, броня, хромота, — а
    // спрятанная в бою ручка сделала бы «прикидку» враньём о её собственных
    // шансах.
    public static float Force(WorldState world, NPCState npc)
    {
        var weapon = npc.Body.CanUseToolsOrWeapons
            ? SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.WeaponHands)
            : string.Empty;

        var offense = SimBalance.MeleeStrikeBonus(weapon) * npc.StrikeFactor();
        var condition = System.Math.Min(npc.Health, MobSystem.WorstPartHealth(npc));
        var defense = (1f + npc.EquippedArmor) * MathUtil.Clamp01(condition);

        return offense * Spec81.AbuseForceOffenseWeight +
               defense * Spec81.AbuseForceDefenseWeight;
    }

    // Во сколько раз он сильнее — с её стороны считая и подруг рядом. Радиус
    // берётся у дружеского прикрытия §57: «свои достаточно близко, чтобы
    // вмешаться» и «расклад изменился» — это буквально одно и то же расстояние.
    public static float Ratio(WorldState world, NPCState abuser, NPCState mark)
    {
        var markSide = Force(world, mark) *
            (1f + Spec81.AbuseAllyForceShare * RaidMath.AlliesAround(world, mark));
        return Force(world, abuser) / System.Math.Max(markSide, 0.0001f);
    }

    // §101: ответит ли она на наезд. Не бросок монетки и не «храбрая по
    // характеру», а трезвая оценка своих шансов плюс злость.
    //
    //   ШАНСЫ. edge = 1/ratio: единица — они на равных, 0.2 — он впятеро
    //   сильнее. Голая девушка против мужика с мачете видит это и не лезет.
    //
    //   ЗЛОСТЬ. Ненависть добавляет храбрости поверх расчёта: та, кого он
    //   тиранит неделю, огрызается и заведомо проигрывая. Это и делает
    //   картину живой — иначе слабая молчала бы ВСЕГДА, а сильная отвечала
    //   всегда, и никакой истории между ними не возникало бы.
    //
    // Бросок детерминированный (сид + тик + её id), поэтому реплей точен.
    public static bool AnswersBack(WorldState world, NPCState abuser, NPCState mark)
    {
        if (!mark.Body.CanUseToolsOrWeapons || mark.Body.IsProne ||
            mark.IsUnconscious(world.Tick))
        {
            return false;
        }

        var edge = MathUtil.Clamp01(1f / System.Math.Max(0.0001f, Ratio(world, abuser, mark)));
        var hatred = MathUtil.Clamp01(-mark.Social.GetOrCreate(abuser.Id).Affinity);
        var chance = MathUtil.Clamp01(
            Spec81.AbuseFightBackBase * edge + Spec81.AbuseFightBackHatred * hatred);

        return MathUtil.Hash01(world.Seed, world.Tick, mark.Id.Value, 5501) < chance;
    }

    // §81.11: почему у BestMark никого не осталось. Счётчики, а не «последняя
    // причина»: последняя нерепрезентативна, когда трёх девушек отсеяли три
    // РАЗНЫХ фильтра. Struct на стеке, строка собирается только при эмите.
    public struct MarkFilterTally
    {
        public int Hostile;    // живых враждебных кандидаток всего
        public int OutOfSight; // §81.12: есть, но он её не видит
        public int Helpless;   // без сознания / ничком
        public int Asleep, Fleeing, Sanctuary, Swimming, Claimed;

        public string ToMessage() =>
            $"Hostile={Hostile} OutOfSight={OutOfSight} Helpless={Helpless} " +
            $"Asleep={Asleep} Flee={Fleeing} Sanct={Sanctuary} " +
            $"Swim={Swimming} Claimed={Claimed}";
    }

    // Кого выбрать. Перебор идёт по РОСТЕРУ, а не по Perception.Agents: §72
    // развёл списки, и у чужака девушки лежат в Hostiles, а PerceivedAgent и
    // вовсе не носит содержимого рюкзака. Ничью разрывает меньший id — иначе
    // порядок обхода словаря протёк бы в реплей.
    public static NPCState BestMark(WorldState world, NPCState abuser, out bool hasLoot) =>
        BestMark(world, abuser, out hasLoot, out _);

    public static NPCState BestMark(WorldState world, NPCState abuser, out bool hasLoot,
        out MarkFilterTally tally)
    {
        hasLoot = false;
        tally = default;
        NPCState best = null;
        var bestScore = float.MinValue;

        foreach (var mark in world.Entities.Npcs.Values)
        {
            if (!IsEligibleMark(world, abuser, mark, ref tally))
            {
                continue;
            }

            // §85: ни подруги рядом, ни расклад сил больше НЕ ЗАПРЕЩАЮТ подойти.
            //
            // Замер показал, почему его не видно в игре: подойти он хочет 100%
            // времени, девушка в радиусе 54%, а жертву находил только 30% — два
            // этих гейта резали остальное. Причём тем сильнее, чем дружнее живёт
            // колония: девушки постоянно ходят друг к другу, и у каждой почти
            // всегда есть соседка.
            //
            // Не ей решать, можно к ней подходить или нет. Расклад сил остался
            // ВЕСОМ при выборе, кого предпочесть, и решает исход сцены — сдастся
            // она или огрызнётся, — но не пускает ли он её вообще.
            var ratio = Ratio(world, abuser, mark);
            var allies = RaidMath.AlliesAround(world, mark);

            // §93: «есть ли у неё ХОТЬ ЧТО-ТО», а не «есть ли ровно то, чего
            // ему хочется». Иначе носительница еды не считалась добычей, если
            // ему в тот момент хотелось пить, — и метка Loot в трейсе врала.
            var loot = WhatToTake(world, abuser, mark) != AidKind.None;

            // Носительница припаса лучше пустой, но пустая — тоже добыча:
            // одиночество закрывается самим фактом сцены.
            var distance = HexSpatialMath.HexDistance(abuser.Tile, mark.Tile);
            var score = (loot ? 1f : 0f)
                + 0.4f * MathUtil.Clamp01(ratio - 1f)
                // Близость — вес, а не порог: за дальней он всё равно пойдёт,
                // просто ближнюю предпочтёт. Шкала по радиусу-подсказке, но
                // расстояние сверх него уже ничего не отнимает.
                + 0.3f * MathUtil.Clamp01(
                    1f - (float)distance / System.Math.Max(1, Spec81.AbuseScanRadiusTiles))
                // Одиночка приятнее компании, но компания — не запрет.
                - 0.25f * allies;

            if (score > bestScore ||
                (score == bestScore && best is not null && mark.Id.Value < best.Id.Value))
            {
                bestScore = score;
                best = mark;
                hasLoot = loot;
            }
        }

        return best;
    }

    // §81.12: единый фильтр жертвы для первоначального выбора и маршрутного
    // перевыбора. Если две ветки начнут решать пригодность по-разному, абьюзер
    // сможет увидеть цель, на которую затем не имеет права переключиться (или
    // наоборот), поэтому правила и диагностические счётчики живут здесь.
    public static bool IsEligibleMark(
        WorldState world, NPCState abuser, NPCState mark, ref MarkFilterTally tally)
    {
        if (mark.Id.Equals(abuser.Id) ||
            mark.Health <= 0f ||
            !FactionRelations.AreHostile(abuser.Faction, mark.Faction))
        {
            return false;
        }

        tally.Hostile++;

        // §125.4: «глазами» — значит его глазами: радиус восприятия охотника.
        if (Spec81.AbuseHuntBySight &&
            HexSpatialMath.HexDistance(abuser.Tile, mark.Tile) >
                PerceptionMath.RadiusTiles(abuser))
        {
            tally.OutOfSight++;
            return false;
        }

        if (mark.IsUnconscious(world.Tick) || mark.Body.IsProne ||
            mark.IsPlayingDead(world.Tick))
        {
            tally.Helpless++;
            return false;
        }

        if (mark.Execution.CurrentInteraction == InteractionType.Sleep)
        {
            tally.Asleep++;
            return false;
        }

        if (mark.Mind.CurrentGoal == GoalType.Flee)
        {
            tally.Fleeing++;
            return false;
        }

        if (Spec81.AbuseRespectsSanctuary && MobSystem.IsNpcInSanctuary(world, mark))
        {
            tally.Sanctuary++;
            return false;
        }

        if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, mark))
        {
            tally.Swimming++;
            return false;
        }

        if (mark.Mind.PendingAbuseFrom is { } claimed && !claimed.Equals(abuser.Id))
        {
            tally.Claimed++;
            return false;
        }

        return true;
    }

    // §81.12 / bug #17: «ближе» означает не расстояние между гексами, а
    // реально проходимый маршрут по графу джанкшенов. Высота, вода и обход
    // препятствия могут сделать визуально близкую цель дальней по пути.
    // FindPath применяет те же правила проходимости, что обычное движение;
    // возвращаем геометрическую длину выбранного маршрута в world units.
    public static NPCState ClosestReachableMark(
        WorldState world, NPCState abuser, out bool hasLoot, out float routeLength)
    {
        hasLoot = false;
        routeLength = float.MaxValue;
        var start = abuser.CurrentJunction ??
            SpatialQueries.FindNearestJunction(world, abuser.Position);
        if (start is null)
        {
            return null;
        }

        var avoid = PathfindingSystem.OtherActorJunctions(world, abuser);
        NPCState best = null;
        foreach (var mark in world.Entities.Npcs.Values)
        {
            var tally = default(MarkFilterTally);
            if (!IsEligibleMark(world, abuser, mark, ref tally))
            {
                continue;
            }

            var goal = mark.CurrentJunction ??
                SpatialQueries.FindNearestJunction(world, mark.Position);
            if (goal is null)
            {
                continue;
            }

            var path = HexPathfinder.FindPath(
                world, start.Value, goal.Value, avoid, weightClimb: false,
                canJump: abuser.Body.CanJump);
            if (path.Count == 0)
            {
                continue;
            }

            var length = RouteLength(world, path);
            if (length < routeLength - 0.0001f ||
                (System.Math.Abs(length - routeLength) <= 0.0001f &&
                 best is not null && mark.Id.Value < best.Id.Value))
            {
                best = mark;
                routeLength = length;
                hasLoot = WhatToTake(world, abuser, mark) != AidKind.None;
            }
        }

        return best;
    }

    public static bool TryRouteLength(
        WorldState world, NPCState actor, NPCState target, out float routeLength)
    {
        routeLength = float.MaxValue;
        var start = actor.CurrentJunction ??
            SpatialQueries.FindNearestJunction(world, actor.Position);
        var goal = target.CurrentJunction ??
            SpatialQueries.FindNearestJunction(world, target.Position);
        if (start is null || goal is null)
        {
            return false;
        }

        var path = HexPathfinder.FindPath(
            world, start.Value, goal.Value,
            PathfindingSystem.OtherActorJunctions(world, actor),
            weightClimb: false, canJump: actor.Body.CanJump);
        if (path.Count == 0)
        {
            return false;
        }

        routeLength = RouteLength(world, path);
        return true;
    }

    private static float RouteLength(WorldState world, System.Collections.Generic.List<JunctionId> path)
    {
        var length = 0f;
        for (var i = 1; i < path.Count; i++)
        {
            if (world.Junctions.Items.TryGetValue(path[i - 1], out var from) &&
                world.Junctions.Items.TryGetValue(path[i], out var to))
            {
                length += HexSpatialMath.Distance(from.WorldPosition, to.WorldPosition);
            }
        }

        return length;
    }

    // Забрать припас. Отдельно от AidSupply.TrySpend, потому что там припас
    // ТРАТИТСЯ на месте, а тут переезжает в чужой рюкзак.
    //
    // ⭐ Ловушка: ItemInstance сравнивается по DefinitionId, поэтому
    // Items.Remove(id) снимает ПЕРВЫЙ подходящий, и переложить «такой же» предмет
    // значит потерять его заряды и износ. Ищем и переносим сам экземпляр.
    public static bool TryTake(
        WorldState world, NPCState abuser, NPCState mark, AidKind want, out string taken)
    {
        taken = null;
        if (want == AidKind.None || !abuser.Inventory.HasSpace)
        {
            return false;
        }

        var id = want == AidKind.Hydrate
            ? mark.Inventory.FindFirstDrink(world.Content)
            : mark.Inventory.FindFirstFood(world.Content);

        if (id is not null)
        {
            var index = mark.Inventory.Items.FindIndex(i => i.DefinitionId == id);
            if (index >= 0)
            {
                var instance = mark.Inventory.Items[index];
                mark.Inventory.Items.RemoveAt(index);
                abuser.Inventory.Items.Add(instance);
                taken = id;
                return true;
            }
        }

        // Воды может не быть предметом вовсе: содержимое фляги живёт на самой
        // девушке, а не на вещи, поэтому «отобрать флягу» отдало бы ему пустую
        // посуду. Забираем глоток.
        if (want == AidKind.Hydrate && mark.BottleCharges > 0)
        {
            mark.BottleCharges--;
            if (abuser.BottleWater == WaterKind.None || abuser.BottleCharges <= 0)
            {
                abuser.BottleWater = mark.BottleWater;
                abuser.BottleCharges = 1;
            }
            else
            {
                abuser.BottleCharges++;
            }

            if (mark.BottleCharges <= 0)
            {
                mark.BottleWater = WaterKind.None;
            }

            taken = "bottle.water";
            return true;
        }

        return false;
    }
}

}

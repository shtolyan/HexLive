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
            ? SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.IntactHands)
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

    // Кого выбрать. Перебор идёт по РОСТЕРУ, а не по Perception.Agents: §72
    // развёл списки, и у чужака девушки лежат в Hostiles, а PerceivedAgent и
    // вовсе не носит содержимого рюкзака. Ничью разрывает меньший id — иначе
    // порядок обхода словаря протёк бы в реплей.
    public static NPCState BestMark(WorldState world, NPCState abuser, out bool hasLoot)
    {
        hasLoot = false;
        NPCState best = null;
        var bestScore = float.MinValue;

        foreach (var mark in world.Entities.Npcs.Values)
        {
            if (mark.Id.Equals(abuser.Id) ||
                mark.Health <= 0f ||
                !FactionRelations.AreHostile(abuser.Faction, mark.Faction) ||
                mark.IsUnconscious(world.Tick) ||
                mark.Body.IsProne ||
                // Спящую не трогаем: кьюшка над спящей молча гасится (§60), и
                // вся сцена прошла бы без единого эмодзи, а «она взвесила силы
                // и сдалась» требует, чтобы она вообще была в сознании. Тихий
                // грабёж спящей — это другая механика, кража §40.5.
                mark.Execution.CurrentInteraction == InteractionType.Sleep ||
                mark.Mind.CurrentGoal == GoalType.Flee)
            {
                continue;
            }

            if (Spec81.AbuseRespectsSanctuary && MobSystem.IsNpcInSanctuary(world, mark))
            {
                continue;
            }

            // §85 r2: радиуса поиска БОЛЬШЕ НЕТ. Он был последним, что мешало:
            // жертву он находил лишь в половине замеров, и почти всё
            // оставшееся — «никого нет в семи гексах». А раз цели нет, цель
            // абьюза не участвует в аукционе, и он идёт пить воду — ровно то,
            // что видно в игре.
            //
            // Захотел — значит идёт, хоть через весь остров. Близость осталась
            // ВЕСОМ: ближняя приятнее дальней, но дальняя лучше, чем никакой.

            // Уже занята чужим рукопожатием — не влезаем в чужую сцену.
            if (mark.Mind.PendingAbuseFrom is { } claimed && !claimed.Equals(abuser.Id))
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

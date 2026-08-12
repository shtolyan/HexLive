using HexLive.Simulation.Common;
using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

// §126: ролл черт и ЕДИНСТВЕННОЕ место, где черта превращается в число, на
// которое умножает симуляция. Сестра AttributeMath, и по той же доктрине:
// модификатор, живущий в системе-потребителе, — это модификатор, который
// следующая система молча продублирует. Грепни этот файл, чтобы увидеть полный
// список того, что черта на самом деле даёт.
//
// Каждый хелпер обязан возвращать НЕЙТРАЛЬНОЕ значение при пустом наборе — так
// «нет черты» и «до §126» это одно и то же выражение.
internal static class TraitMath
{
    // ---- Ролл ---------------------------------------------------------------

    // §126.2. Полюсные пары катятся ОДНИМ хешем: полоса [0, A) — первая черта,
    // [A, A+B) — вторая, дальше — никакой. Исключение полюсов выходит из
    // устройства, а не из проверки: один бросок не может попасть в две полосы.
    //
    // Соль-блок (126, 12601..12605) — §53 держит 5301, §74 — 7401..7405,
    // §76 — 7601..7606. Каждой паре и каждой одиночной черте свой номер, чтобы
    // добавление черты не сдвигало ролл у всех остальных.
    public static void Roll(NPCState npc, int seed, int npcId)
    {
        if (!SpecTraits.Enabled)
        {
            // Пустой набор — игра до §126 байт-в-байт.
            return;
        }

        // ⭐ АБЬЮЗЕР И НЕРЯХА НЕ РОЛЛЯТСЯ НИКОГДА — ни здесь, ни при какой
        // настройке. Это черты ДИКАРЕЙ чужого клана, и достаются они только
        // авторски (бутстрап чужака и RaidWaveSystem). Отсутствие ролла — это и
        // есть правило «в нашем лагере таких не бывает»; выражено оно тем, что
        // броска попросту нет, а не проверкой фракции внутри ролла — иначе
        // черта снова стала бы производной от стороны конфликта, ради чего §126
        // и затевался. Соли 12601 и 12604 при этом ЗАНЯТЫ навсегда: их бывшие
        // владельцы ушли, но переиспользование сдвинуло бы ролл у всех.
        RollSingle(npc, seed, npcId, 12601, TraitKind.Neat, SpecTraits.NeatChance);

        RollPair(npc, seed, npcId, 12602,
            TraitKind.Lazy, SpecTraits.LazyChance,
            TraitKind.Diligent, SpecTraits.DiligentChance);

        RollPair(npc, seed, npcId, 12603,
            TraitKind.Coward, SpecTraits.CowardChance,
            TraitKind.Brave, SpecTraits.BraveChance);

        RollSingle(npc, seed, npcId, 12605, TraitKind.Sleepyhead, SpecTraits.SleepyChance);
    }

    private static void RollPair(NPCState npc, int seed, int npcId, int salt,
        TraitKind first, float firstChance, TraitKind second, float secondChance)
    {
        if (firstChance <= 0f && secondChance <= 0f)
        {
            return;
        }

        var roll = MathUtil.Hash01(seed, npcId, 126, salt);
        if (roll < firstChance)
        {
            npc.Traits.Add(first);
        }
        else if (roll < firstChance + secondChance)
        {
            npc.Traits.Add(second);
        }
    }

    private static void RollSingle(NPCState npc, int seed, int npcId, int salt,
        TraitKind kind, float chance)
    {
        if (chance <= 0f)
        {
            return;
        }

        if (MathUtil.Hash01(seed, npcId, 126, salt) < chance)
        {
            npc.Traits.Add(kind);
        }
    }

    // ---- Сон (§49 / §126.4) -------------------------------------------------

    // «Насколько выдохлась, чтобы лечь». ЕДИНСТВЕННОЕ место, где спрашивают
    // этот порог: и ставка сна, и «можно ли добывать огонь трением на ночь»
    // читают его отсюда, чтобы «пошла спать» и «собралась на ночь» не разъехались.
    //
    // Соня ложится раньше; спит она всё равно до полной энергии, поэтому
    // «дольше» получается само собой и второй ручки не требует.
    public static float EffectiveSleepThreshold(NPCState npc)
    {
        var threshold = SimBalance.SleepEnergyThreshold;
        if (npc.Traits.Has(TraitKind.Sleepyhead))
        {
            threshold += SpecTraits.SleepyThresholdBonus;
        }

        return MathUtil.Clamp01(threshold);
    }

    // ---- Быт и работа (§126.4) ----------------------------------------------

    // Насколько охотно свободные руки идут в дело. Крутит ЕДИНСТВЕННУЮ
    // прибавку `freeHands` у её истока — она кормит около восемнадцати
    // хозяйственных ставок, и характер обязан выражаться там один раз.
    public static float IndustryMult(NPCState npc)
    {
        if (npc.Traits.Has(TraitKind.Diligent))
        {
            return SpecTraits.DiligentIndustryMult;
        }

        return npc.Traits.Has(TraitKind.Lazy) ? SpecTraits.LazyIndustryMult : 1f;
    }

    // Прибавка к ставке досуга (Sit). Отдельно от IndustryMult: «меньше
    // работает» и «больше отдыхает» — не одно и то же, и лентяйка обязана не
    // просто реже браться за дело, а куда-то деваться вместо него.
    public static float LeisureBonus(NPCState npc) =>
        npc.Traits.Has(TraitKind.Lazy) ? SpecTraits.LazySitBonus : 0f;

    // Множитель порогов мытья и стирки. Порог — это «насколько грязной надо
    // стать, чтобы взяться»; чистюле хватает меньшего, поэтому множитель < 1.
    // Оба гола (Bathe и WashClothes) читают ОДИН хелпер: два множителя рано или
    // поздно разъехались бы, и получилась бы девушка, которая моется, но не
    // стирает.
    public static float GroomingThresholdMult(NPCState npc) =>
        npc.Traits.Has(TraitKind.Neat) ? SpecTraits.NeatThresholdMult : 1f;

    // ---- Опасность (§126.4 / §62) -------------------------------------------

    // Мягкая цена шага рядом с живым зверем: чем дороже, тем шире крюк. Трусиха
    // платит вдвое и обходит дальше. Радиус кольца общий для всех намеренно —
    // он считается одним BFS на тик, и персональный превратил бы его в BFS на
    // человека ради того же самого ответа.
    public static long DangerStepCost(NPCState npc) =>
        npc.Traits.Has(TraitKind.Coward)
            ? (long)(Spec62.DangerStepCost * SpecTraits.CowardDangerCostMult)
            : Spec62.DangerStepCost;

    // §62: планка «цела ли она настолько, чтобы драться первой». Храбрая
    // принимает бой раньше — планка ниже.
    public static float FitBoneHealth(NPCState npc) =>
        npc.Traits.Has(TraitKind.Brave)
            ? Spec62.FitBoneHealth * SpecTraits.BraveFitMult
            : Spec62.FitBoneHealth;

    // ---- Загрузка старого мира ---------------------------------------------

    // §126: чем ЭТОТ человек был до появления черт. Блоб v35 и старше их не
    // знает, а игра, которая его записала, гнобила (§81) и не мылась (§89) по
    // ФРАКЦИИ — в коде стояло ровно `Faction != Colony`. Значит загрузка
    // обязана вернуть то, чем он уже был, а не выдать нового мирного чужака,
    // который ходит полоскать рубаху.
    //
    // Отдельным именованным швом, а не четырьмя строками внутри ReadNpc:
    // правило «до §126 черта = фракция» — это факт про историю игры, и его
    // должно быть можно проверить гейтом, не подделывая двоичный блоб.
    public static void ApplyPreTraitDefaults(NPCState npc)
    {
        if (npc.Faction == Faction.Colony)
        {
            return;
        }

        npc.Traits.Add(TraitKind.Abuser);
        npc.Traits.Add(TraitKind.Slob);
    }

    // ---- Показ --------------------------------------------------------------

    // §126.5: базовые ключи локализации для листа персонажа. Резолвятся
    // СИМ-СТОРОНОЙ по той же причине, что и перки §76.6: набор черт —
    // состояние мира, и вид, который вывел бы его сам, разошёлся бы с
    // симуляцией на первой же правке ролла.
    public static void CollectTraits(NPCState npc,
        System.Collections.Generic.List<string> results)
    {
        foreach (var kind in TraitSet.All)
        {
            if (npc.Traits.Has(kind))
            {
                results.Add(TraitSet.LocKey(kind));
            }
        }
    }
}

}

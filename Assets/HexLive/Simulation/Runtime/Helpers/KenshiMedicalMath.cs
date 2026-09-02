using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§116 slow-tick wound progression and recovery.</summary>
internal static class KenshiMedicalMath
{
    private static readonly BodyPart[] Parts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    internal static void Tick(WorldState world, NPCState npc)
    {
        // Health==0 is the terminal latch until MobSystem moves the NPC to
        // Corpses. No recovery pass is allowed to reconstruct it from Body.Mean.
        if (npc.Health <= 0f)
        {
            return;
        }

        BodyDamageResolver.DecayAllHitBias(world, npc);
        var toughness = MathUtil.Clamp01(npc.Attributes.Toughness);
        RestFactors(world, npc, out var healMultiplier, out var degenerationMultiplier);

        var clotGain = Lerp(Spec118.ClotPerSlowTickLowToughness,
            Spec118.ClotPerSlowTickHighToughness, toughness);
        var bleedToughness = Lerp(1.3f, 0.7f, toughness);
        var totalBleed = 0f;
        foreach (var wound in npc.Wounds)
        {
            var openCut = wound.Severity * (1f - wound.Heal01);
            if (!wound.Stabilized && wound.Clot01 < 1f && openCut > 0f)
            {
                totalBleed += openCut * wound.BleedFactor * (1f - wound.Clot01) *
                    Spec118.SteadyBleedPerSlowTick * bleedToughness;
            }

            wound.Clot01 = wound.Stabilized
                ? 1f
                : MathUtil.Clamp01(wound.Clot01 + clotGain);
        }

        if (totalBleed > 0f)
        {
            BodyDamageResolver.DrainBlood(npc, totalBleed);
            npc.EffectImpacts.Record(
                NeedKind.Blood,
                EffectKind.Bleeding,
                EffectImpactDirection.Negative,
                EffectImpactCadence.Slow);
            Trace.Emit(world, npc.Id, "Bleeding",
                $"Loss={totalBleed:F4} Blood={npc.Needs.Blood:F3} " +
                $"Deficit={npc.Body.BloodDeficit:F3}");
        }

        foreach (var part in Parts)
        {
            TickDegeneration(world, npc, part, toughness, degenerationMultiplier);
            if (npc.Health <= 0f)
            {
                // Bug #54: fatal degeneration used to fall through into blunt/
                // cut recovery and the final medical-progression recompute,
                // resurrecting the body inside this same slow tick.
                return;
            }

            TickBluntRecovery(npc, part, healMultiplier);
            TickCutRecovery(world, npc, part, healMultiplier);
            TickCriticalRecovery(npc, part, healMultiplier);
        }

        // ⭐ Гейт сытости — про ВЫБОР, а у лежащей в окне умирания выбора нет.
        //
        // Сытое тело восстанавливает кровь, голодное нет: для ходячей это
        // честная сложность — иди поешь. Но BloodDeficit гасит ТОЛЬКО этот
        // вызов, а он же — единственное, что держит её в окне умирания
        // (KenshiRecovered требует BloodDeficit <= 0, а ResolveTrauma, пока
        // дефицит больше нуля, возвращает причину в BloodLoss). Лежащая без
        // сознания открыть гейт не может: поесть ей нечем, а §29C.2 — штатный
        // сторож от «нужда в максимуме, а смерти нет» — сам гейтован через
        // `&& !npc.IsDying` и на умирающую не смотрит.
        //
        // Замкнутый круг (баг #115, seed 63287937, npc3): дефицит замер на
        // 0.199, и девушка пролежала 21 000 тиков при Health=1.00, Blood=1.00,
        // Hunger=1.00, Thirst=1.00 — ни встать, ни умереть, и никакой шаг
        // симуляции в этом состоянии ничего не менял.
        //
        // Поэтому умирающей кровь восстанавливается независимо от голода. Это
        // не поблажка: она лишь ВЫХОДИТ из окна, а дальше голод и жажда
        // спрашивают с неё как со всех — либо успеет напиться, либо умрёт
        // обычным каналом §29C.2, который к тому моменту снова в силе.
        if ((npc.Needs.Hunger < SimBalance.HealHungerGate || npc.IsDying) &&
            (npc.Needs.Blood < 1f || npc.Body.BloodDeficit > 0f))
        {
            BodyDamageResolver.RestoreBlood(npc,
                SimBalance.BloodRefillPerTick * healMultiplier);
            npc.EffectImpacts.Record(
                NeedKind.Blood,
                EffectKind.BloodRecovery,
                EffectImpactDirection.Positive,
                EffectImpactCadence.Slow);
        }

        npc.Health = npc.IsDying
            ? System.Math.Max(npc.Body.Mean(), Spec105.BodyFloor)
            : npc.Body.Mean();
        MortalityHelpers.ResolveTrauma(world, npc, 0f, "medical progression");
    }

    private static void TickDegeneration(
        WorldState world, NPCState npc, BodyPart part, float toughness, float restMultiplier)
    {
        if (npc.Body.IsSevered(part))
        {
            return;
        }

        var openCut = 0f;
        foreach (var wound in npc.Wounds)
        {
            if (wound.Zone == part && !wound.Stabilized)
            {
                openCut += wound.Severity * (1f - wound.Heal01);
            }
        }

        if (openCut <= Spec118.DegenerationCutThreshold)
        {
            return;
        }

        var steps = System.Math.Max(0f,
            (openCut - Spec118.DegenerationCutThreshold) / Spec118.DegenerationStep);
        var amount = steps * Spec118.DegenerationPerStep *
            Lerp(1.7f, 0.03f, toughness) * restMultiplier;
        BodyDamageResolver.ApplyCutDegeneration(world, npc, part, amount);
    }

    private static void TickBluntRecovery(NPCState npc, BodyPart part, float restMultiplier)
    {
        var condition = npc.Body.Condition(part);
        if (condition.SplintSupport > 0f && npc.Body.Parts[part] >= condition.SplintSupport)
        {
            condition.SplintSupport = 0f;
        }
        if (condition.BluntDamage <= 0f)
        {
            return;
        }

        var amount = System.Math.Min(condition.BluntDamage,
            Spec118.BluntRecoveryPerSlowTick * restMultiplier);
        condition.BluntDamage -= amount;
        BodyDamageResolver.RestorePart(npc, part, amount);
    }

    // ⭐ Баг #119: у критической глубины должен быть обратный ход САМ ПО СЕБЕ.
    //
    // Вниз CriticalTrauma двигает единственный метод — RestorePart, — а звали
    // его только два тика выше: ушиб (нужен BluntDamage) и порез (нужна ЗАПИСЬ
    // раны в этой зоне). Но TickDegeneration углубляет зону СВЕРХ severity
    // самой раны, поэтому, когда рана закрывается и запись удаляется, остаток
    // критической глубины остаётся сиротой: ни ушиба, ни раны — и гасить его
    // больше нечем. Зона при этом навсегда пришпилена к нулю, потому что сытый
    // реген NeedsDecaySystem пропускает всё, у чего crit > 0.
    //
    // Так и вышло у Ирис (seed=476005489, tick=74917): целая правая нога
    // HP=0.000 при crit=0.536, ноль ран, ноль ушиба — и 4000 тиков вперёд без
    // единой сотой изменения. Ни еда, ни сон, ни повязка (перевязывать нечего),
    // ни шина (§118.5 даёт функцию, а не HP) её не поднимали.
    //
    // Темп берётся у рубцевания пореза — эта глубина им же и набрана, а
    // отдельная ручка потребовала бы ре-экспорта simdata ради того же числа.
    // Возврат ограничен ровно критической частью: положительную шкалу
    // по-прежнему поднимает штатный сытый реген со своим гейтом голода.
    private static void TickCriticalRecovery(
        NPCState npc, BodyPart part, float restMultiplier)
    {
        // §50: культя не восстанавливается никогда — она не заживает, её нет.
        if (npc.Body.IsSevered(part))
        {
            return;
        }

        var condition = npc.Body.Condition(part);
        if (condition.CriticalTrauma <= 0f)
        {
            return;
        }

        // §118.2: открытый неперевязанный порез держит зону — пока он течёт,
        // она углубляется, а не заживает. Лечение по-прежнему начинается с
        // повязки; сюда попадает только то, что уже нечем перевязать.
        foreach (var wound in npc.Wounds)
        {
            if (wound.Zone == part && !wound.Stabilized && wound.Heal01 < 1f)
            {
                return;
            }
        }

        var amount = System.Math.Min(condition.CriticalTrauma,
            Spec118.CutRecoveryPerSlowTick * restMultiplier);
        BodyDamageResolver.RestorePart(npc, part, amount);
    }

    private static void TickCutRecovery(
        WorldState world, NPCState npc, BodyPart part, float restMultiplier)
    {
        // A severed zone can never recover HP, but its remaining wounds still
        // have to close. Once an untreated stump has clotted naturally it is
        // no longer considered bleeding, so AI will not spend a dressing on
        // it; without this path Heal01 (and therefore the wound paint) stayed
        // at zero forever. Stabilised wounds close as before, while a bare
        // stump starts scarring only after clotting reaches one.
        //
        // ⭐ §118.2: ТО ЖЕ рубцевание — у свернувшейся раны ЦЕЛОЙ зоны, вчетверо
        // медленнее (NaturalScarringFactor). Ручка была объявлена и НИ РАЗУ не
        // прочитана: естественный ход имела только культя, а обычная зона ждала
        // повязки — навсегда, если повязки не случилось.
        //
        // Цена бага в сейве игрока (seed=-28275602, tick=61859): у Инес грудь
        // 0.054 HP и СЕМЬ несшитых записей, все Heal01=0.000 при Clot01=1. Одна
        // повязка стабилизирует ровно одну рану (§118.2), а один укус пишет
        // GashesPerHit=3 записи, — очередь не разгребается. Потолок сытого
        // регена = 1 − сумма severity = ровно 0.054, поэтому зона стояла
        // намертво: ни рана не закрывается, ни HP не растёт. Прогон сейва на
        // 4000 тиков вперёд не сдвинул грудь ни на сотую.
        //
        // Повязка остаётся заметно лучше (вчетверо быстрее) и по-прежнему
        // единственный способ ОСТАНОВИТЬ кровь: пока Clot01 < 1, рана не
        // рубцуется вовсе, а неперевязанный openCut выше порога ещё и углубляет
        // зону через TickDegeneration. Самолечение — это долго и с потерями.
        var severed = npc.Body.IsSevered(part);
        var budget = Spec118.CutRecoveryPerSlowTick * restMultiplier;
        for (var i = npc.Wounds.Count - 1; i >= 0 && budget > 0f; i--)
        {
            var wound = npc.Wounds[i];
            if (wound.Zone != part || wound.Heal01 >= 1f)
            {
                continue;
            }

            // Культя сохраняет свой прежний полный темп — на ней рубцевание уже
            // работало, и на нём оттюнена дуга §50.
            float rate;
            if (wound.Stabilized) rate = 1f;
            else if (severed && wound.Clot01 >= 1f) rate = 1f;
            else if (wound.Festering) continue; // §157.5: сама не рубцуется
            else if (wound.Clot01 >= 1f) rate = Spec118.NaturalScarringFactor;
            else continue;

            var open = wound.Severity * (1f - wound.Heal01);
            var amount = System.Math.Min(open, budget * rate);
            wound.Heal01 = MathUtil.Clamp01(wound.Heal01 + amount / wound.Severity);
            if (!severed)
            {
                BodyDamageResolver.RestorePart(npc, part, amount);
            }
            budget -= amount;

            if (wound.Heal01 >= 1f)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "WoundHealed",
                        $"{part} wound #{wound.Id} closed");
                }
                npc.Wounds.RemoveAt(i);
            }
        }

        if (WoundMath.OpenCutDamage(npc, part) <= 0f)
        {
            npc.BandagedZones.Remove(part);
            npc.GauzeZones.Remove(part);
        }
    }

    // §118.8: the fed positive-bar regen (NeedsDecaySystem) rides the same
    // rest ladder as blunt/cut/critical recovery, so "a bed heals twice as
    // fast" is one statement about the whole body, not three.
    internal static float RestHealMultiplier(WorldState world, NPCState npc)
    {
        RestFactors(world, npc, out var heal, out _);
        return heal;
    }

    private static void RestFactors(
        WorldState world, NPCState npc, out float heal, out float degeneration)
    {
        heal = 1f;
        degeneration = 1f;
        var lying = npc.Execution.CurrentInteraction == InteractionType.Sleep ||
            npc.IsUnconscious(world.Tick);
        if (!lying)
        {
            return;
        }

        heal = Spec118.GroundRestHealMultiplier;
        if (npc.Execution.TargetObject is not { } objectId ||
            !world.Entities.Objects.TryGetValue(objectId, out var bed))
        {
            return;
        }

        // §118.8: кровать в игре ОДНА — bed.basic; bed.leaf и building.hut_bed
        // существуют только как read-only алиасы старых сейвов, и загрузчик
        // (WorldSaveSerializer) переписывает их в bed.basic до первого тика.
        // Сравниваем через Canonicalize — защита на случай, если алиас всё же
        // просочится в живой мир, а не признак второй кровати.
        if (ContentIds.Canonicalize(bed.DefinitionId) == ContentIds.BedBasic)
        {
            heal = Spec118.BasicBedHealMultiplier;
            degeneration = Spec118.BasicBedDegenerationMultiplier;
        }
    }

    private static float Lerp(float from, float to, float value) =>
        from + (to - from) * MathUtil.Clamp01(value);
}

}

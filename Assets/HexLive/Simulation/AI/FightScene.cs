using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.AI
{

/// <summary>
/// ПОСТАНОВОЧНАЯ СЦЕНА БОЯ: чем бьёт, сколько раз и с какой паузой.
///
/// <para>
/// Заведена потому, что фразу «ударь кулаком три раза» до этого не мог
/// произнести никто целиком. Каждое слово принадлежало своему коду: «три» —
/// ручке баланса, «удара» — боевой системе, «кулаком» — выбору оружия,
/// «и разошлись» — такту сцены, а «видно» — экспортеру снапшота. Ни одно место
/// не могло ответить, случатся ли три ВИДИМЫХ удара; это выяснялось прогоном.
/// Отсюда и вечное «починил — и всё равно не так»: каждая правка чинила одно
/// слово из девяти.
/// </para>
/// <para>
/// Теперь намерение объявляется один раз (<see cref="Begin"/>), боевая система
/// только сообщает о попадании (<see cref="OnBlowLanded"/>), а сцена спрашивает
/// «готово?» (<see cref="IsComplete"/>). Правило «сколько ударов» живёт в одном
/// месте, и его видно.
/// </para>
/// <para>
/// ⭐ Пауза между ударами считается от ТОГО КЛИПА, который только что отыграл, а
/// не от базовой длительности оружия. Раньше это были две разные мерки: замах
/// брался из варианта удара (0.4 с), пауза — из базы оружия (1.5 с), и удары
/// либо перебивали друг друга, либо расходились втрое дальше задуманного. Та же
/// болезнь, что у пяти мерок дистанции в §102.
/// </para>
/// </summary>
public static class FightScene
{
    /// <summary>
    /// «Своё он уже сказал»: отсрочка следующего замаха настолько большая, что
    /// он не наступит до конца сцены. Не бесконечность, потому что это ТИК, и
    /// его складывают с текущим.
    /// </summary>
    private const int SceneOverSentinel = int.MaxValue / 4;

    /// <summary>
    /// ⭐ ОТПУСТИТЬ СЛОТ ЗАМАХА. Зовётся при ЛЮБОМ расцеплении пары, не только
    /// при штатном конце сцены.
    ///
    /// <para>
    /// §104 r7: сентинел «сцена доиграна» — это отсрочка на полмиллиарда тиков,
    /// и пережить сцену он не должен. Штатный <see cref="End"/> её снимал, а
    /// обрыв мимо него (RaidSystem расцепляет пару своим Unpair) — нет: боец
    /// оставался без замахов до переполнения, то есть навсегда. Сегодня все
    /// пути сцены идут через End, так что дыра закрыта косвенно — этот метод
    /// закрывает её структурно.
    /// </para>
    /// </summary>
    public static void ReleaseSwingSlot(NPCState npc)
    {
        if (npc == null)
        {
            return;
        }

        npc.StrikeLandsAtTick = 0;
        npc.PendingHumanStrikeTargetId = null;

        // Порог различает сентинел (~5·10⁸) и легальный кулдаун оружия
        // (десятки тиков): настоящую готовность мы не трогаем.
        if (npc.StrikeReadyAtTick >= SceneOverSentinel / 2)
        {
            npc.StrikeReadyAtTick = 0;
        }
    }

    /// <summary>
    /// Restore an in-flight human swing from a save without giving persistence
    /// a second implementation of the slot. The stored body part must stay
    /// paired with the exact timeline that selected it.
    /// </summary>
    public static void RestoreSwingSlot(
        NPCState npc,
        int landsAtTick,
        int readyAtTick,
        int animationUntilTick,
        int startTick,
        int strikeIndex,
        EntityId? targetId,
        Content.BodyPart part)
    {
        npc.StrikeLandsAtTick = landsAtTick;
        npc.StrikeReadyAtTick = readyAtTick;
        npc.AttackAnimUntilTick = animationUntilTick;
        npc.SwingStartTick = startTick;
        npc.SwingStrikeIndex = strikeIndex;
        npc.PendingHumanStrikeTargetId = targetId;
        npc.PendingHumanStrikePart = part;
    }

    /// <summary>
    /// Объявить сцену. <paramref name="weaponId"/>: пустая строка — кулаки,
    /// null — «как обычно, лучшим». <paramref name="spacingClips"/> — во
    /// сколько ДЛИН КЛИПА разводятся удары (1.0 — следующий сразу после того,
    /// как предыдущий доиграл).
    /// </summary>
    public static void Begin(WorldState world, NPCState actor, NPCState target,
        string weaponId, int blows, float spacingClips)
    {
        actor.Mind.ForcedMeleeWeaponId = weaponId;
        actor.Mind.SceneBlowsPlanned = blows;
        actor.Mind.SceneBlowSpacingClips = spacingClips;
        actor.Mind.AbuseBlows = 0;
        actor.Mind.SceneLastBlowRestTick = 0;

        Latch(world, actor, target);
    }

    /// <summary>
    /// ⭐ Держать бой ЗАЖЖЁННЫМ. MobSystem каждый средний проход гасит
    /// <c>IsFighting</c> у всех и выводит его заново из собачьих сцепок —
    /// поставленный однажды флаг живёт один-три тика. Для модели это почти
    /// незаметно, а вид читает его как боевую стойку, и сцена шла в мирной позе.
    /// Звать после MobSystem.
    /// </summary>
    public static void Latch(WorldState world, NPCState actor, NPCState target)
    {
        actor.IsFighting = true;
        actor.Mind.CombatOpponentNpcId = target.Id;

        // Ответчицу зажигаем только если она САМА в паре: сцена не решает за
        // неё, драться или терпеть (§100).
        if (target.Mind.CombatOpponentNpcId is { } back && back.Equals(actor.Id))
        {
            target.IsFighting = true;
        }
    }

    /// <summary>
    /// Удар лёг. Считает его и отодвигает готовность так, чтобы клип успел
    /// доиграть целиком — иначе следующий замах перебивает предыдущий на
    /// середине, и со стороны выходит «крови добавилось, а удара не видел».
    /// </summary>
    public static void OnBlowLanded(WorldState world, NPCState actor, float clipSeconds)
    {
        if (actor.Mind.SceneBlowsPlanned <= 0)
        {
            return; // не постановочная драка — пусть идёт по своим правилам
        }

        actor.Mind.AbuseBlows++;

        // Своё он уже сказал: дальше стоит и смотрит, а сцена доигрывает до
        // приговора. Столько ударов, сколько назначено, и ни одним больше.
        //
        // §104 r12: но КЛИП последнего удара обязан доиграть. Хит ложится в
        // середине клипа, а приговор наступал этим же тиком — End гасил пару и
        // ForcedMeleeWeaponId, и прострелка обрывалась со сменой оружия в
        // руке. Приговор (такт 4) теперь ждёт этот тик.
        if (IsComplete(actor))
        {
            actor.Mind.SceneLastBlowRestTick =
                world.Tick + Runtime.MeleeSwing.SecondsToTicks(clipSeconds);
        }

        var pause = IsComplete(actor)
            ? SceneOverSentinel
            : Runtime.MeleeSwing.SecondsToTicks(clipSeconds * actor.Mind.SceneBlowSpacingClips);

        var floor = world.Tick + pause;
        if (actor.StrikeReadyAtTick < floor)
        {
            actor.StrikeReadyAtTick = floor;
        }
    }

    /// <summary>
    /// ⭐ ЧЕМ ОН БУДЕТ БИТЬ — лестница ненависти §93/§97.
    ///
    /// <para>
    /// Наезд начинается рукопашкой, а тесак достают, когда уже ненавидят:
    /// симпатия падает с каждой сценой насилия, поэтому «кулаком или ножом»
    /// становится следствием ИСТОРИИ отношений, а не броска кубика. Между
    /// «злится» и «ненавидит» есть ступенька: нож вместо мачете.
    /// </para>
    /// <para>
    /// §104 r7: живёт здесь, потому что докблок этого класса обещает «чем
    /// бить» своим, а правило лежало в такте сцены (ExecutionSystem.Abuse) —
    /// то есть у того, кто сцену ИГРАЕТ, а не у того, кто её объявляет.
    /// </para>
    /// </summary>
    public static string PickWeapon(NPCState abuser, NPCState mark)
    {
        var affinity = abuser.Social.GetOrCreate(mark.Id).Affinity;
        if (!abuser.Body.CanUseToolsOrWeapons || affinity > Runtime.Spec81.AbuseWeaponAffinity)
        {
            return Content.GearCatalog.Fist;
        }

        var best = Runtime.SimBalance.BestMeleeWeapon(
            abuser.Inventory.Items, abuser.Body.WeaponHands);
        if (string.IsNullOrEmpty(best))
        {
            return Content.GearCatalog.Fist;
        }

        var depth = MathUtil.Clamp01(
            (Runtime.Spec81.AbuseWeaponAffinity - affinity) /
            System.Math.Max(0.0001f, 1f + Runtime.Spec81.AbuseWeaponAffinity));
        return depth >= Runtime.Spec81.AbuseHeavyWeaponDepth ? best : LighterThan(abuser, best);
    }

    // Ступенька ниже самого тяжёлого — нож вместо мачете. Если ничего легче
    // нет, остаются кулаки: лёгкая злость не берётся за тесак.
    private static string LighterThan(NPCState npc, string heaviest)
    {
        string lighter = null;
        foreach (var item in npc.Inventory.Items)
        {
            var id = item.DefinitionId;
            if (id == heaviest)
            {
                continue;
            }

            var gear = Content.GearCatalog.For(id);
            if (gear.Id != id || gear.MeleePriority <= 0)
            {
                continue;
            }

            if (lighter is null ||
                Content.GearCatalog.Damage(id) > Content.GearCatalog.Damage(lighter))
            {
                lighter = id;
            }
        }

        return lighter ?? Content.GearCatalog.Fist;
    }

    /// <summary>Все назначенные удары легли.</summary>
    public static bool IsComplete(NPCState actor) =>
        actor.Mind.SceneBlowsPlanned > 0 &&
        actor.Mind.AbuseBlows >= actor.Mind.SceneBlowsPlanned;

    /// <summary>Сцена окончена: снять намерение и расцепить пару.</summary>
    public static void End(WorldState world, NPCState actor, NPCState target)
    {
        actor.Mind.SceneBlowsPlanned = 0;
        actor.Mind.SceneBlowSpacingClips = 0f;
        actor.Mind.SceneLastBlowRestTick = 0;
        actor.Mind.ForcedMeleeWeaponId = null;
        // Готовность здесь гасится ПОЛНОСТЬЮ, а не по порогу: сцена кончилась,
        // и ждать её кулдаун незачем — дальше обычный бой по своим правилам.
        actor.StrikeReadyAtTick = 0;
        actor.StrikeLandsAtTick = 0;

        actor.IsFighting = false;
        actor.Mind.CombatOpponentNpcId = null;

        if (target != null)
        {
            target.IsFighting = false;
            target.Mind.CombatOpponentNpcId = null;
            ReleaseSwingSlot(target);
        }
    }
}

}

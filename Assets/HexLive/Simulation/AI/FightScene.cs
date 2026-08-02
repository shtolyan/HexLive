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
        var pause = IsComplete(actor)
            ? int.MaxValue / 4
            : Runtime.MeleeSwing.SecondsToTicks(clipSeconds * actor.Mind.SceneBlowSpacingClips);

        var floor = world.Tick + pause;
        if (actor.StrikeReadyAtTick < floor)
        {
            actor.StrikeReadyAtTick = floor;
        }
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
        actor.Mind.ForcedMeleeWeaponId = null;
        actor.StrikeReadyAtTick = 0;
        actor.StrikeLandsAtTick = 0;

        actor.IsFighting = false;
        actor.Mind.CombatOpponentNpcId = null;

        if (target != null)
        {
            target.IsFighting = false;
            target.Mind.CombatOpponentNpcId = null;
            target.StrikeLandsAtTick = 0;
        }
    }
}

}

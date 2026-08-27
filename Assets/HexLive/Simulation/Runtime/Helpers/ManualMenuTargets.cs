using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §149 r3 (#236): какие пункты ручного меню показывать против человека —
/// считается от лагеря ТОЙ, КТО ОТДАЁТ приказ, а не от буквальной
/// <c>Faction.Colony</c>.
/// <para>
/// §149 r2 научило «своему лагерю» ростер, туман и карту, но контекстное меню
/// осталось на старых гейтах. У игрока лагеря Colony2..Colony6 соседка по
/// лагерю попадала в ветку ЧУЖОЙ (сцена травли §81 вместо охоты §56, «взять на
/// руки» исчезало у стоящей своей), а девушка первого лагеря — в ветку своей,
/// хотя приём приказа считает её чужой. Меню предлагало ровно то, что приём
/// отбивает (`NotAlly`, `NotHostile`, `PersonNotAvailable`), и прятало то, что
/// он принимает.
/// </para>
/// <para>
/// Предикаты повторяют приём один в один: «свои» — тот же вопрос, что задают
/// <see cref="ManualCarryTargets.CanCarry"/> и приказ §56; сосед — пара РАЗНЫХ
/// девичьих лагерей, как в <c>CampDiplomacyMath.CanMerge</c>. Для лагеря
/// <c>Colony</c> оба ответа прежние, поэтому локальная игра и обычная выдача
/// первого лагеря не меняются.
/// </para>
/// </summary>
public static class ManualMenuTargets
{
    /// <summary>Своя: охота §56, «взять на руки» стоящей, обычная помощь.</summary>
    public static bool SameSide(Faction actor, Faction target) =>
        FactionRelations.AreAllies(actor, target);

    /// <summary>§146.12: соседний девичий лагерь — объединить или занять.</summary>
    public static bool NeighbourCamp(Faction actor, Faction target) =>
        actor != target &&
        FactionRelations.IsGirlCamp(actor) &&
        FactionRelations.IsGirlCamp(target);
}

}

using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §137: ОДИН ответ на «можно ли ей сейчас сидеть и отдыхать?».
///
/// <para>
/// Предикат один на оба конца — планировщик спрашивает его перед тем, как
/// посадить, исполнитель каждый тик, чтобы поднять. Две копии этого списка
/// разошлись бы на первой же правке, и разошлись бы молча: «садится и тут же
/// встаёт» выглядит как дрожание аукциона, а не как забытая строка.
/// </para>
/// <para>
/// ⭐ ГОЛОД И ЖАЖДА СЮДА НЕ ВХОДЯТ, и это осознанно. Чаще всего она праздна
/// именно потому, что нужду удовлетворить нечем: еды в мире нет, вода далеко,
/// цель после провала на кулдауне. Запретить отдых по голоду значило бы
/// выключить фичу ровно в том случае, ради которого её просили. Голодная,
/// которой нечего делать, садится — и встаёт, когда голод дозреет до кризиса
/// (латчи <c>IsStarving</c>/<c>IsDehydrated</c> ниже) и появится, за чем
/// вставать.
/// </para>
/// </summary>
internal static class IdleRestMath
{
    /// <summary>
    /// Что мешает сидеть ПРЯМО СЕЙЧАС. Всё это — либо «тело не в том
    /// положении» (лежит, ползёт, плывёт, несёт или несут), либо «происходит
    /// что-то, из-за чего надо быть на ногах».
    /// </summary>
    internal static bool Blocked(WorldState world, NPCState npc)
    {
        // Тело уже не стоит: лежачую (кома, сон, слёзы, притворство) сажать
        // некуда, ползущей стойки не существует вовсе (§50), пловчиха сидеть
        // не может по определению, а несущая тело обязана его донести.
        if (npc.IsLyingDown(world.Tick) || npc.Body.IsProne ||
            npc.IsBeingCarried || npc.IsCarryingPerson)
        {
            return true;
        }

        // §121/§137: explicit GroundSit also uses IdleRest when no ledge exists.
        // Manual control forbids unsolicited Idle, not the native Sit goal
        // installed by the player's rest order (or exhausted-work recovery).
        if (npc.Mind.ManualControl && npc.Mind.CurrentGoal != GoalType.Sit)
        {
            return true;
        }

        // Драка, свежий испуг, начатая против неё сцена, идущий к ней с
        // помощью или с разговором — всё это «встань».
        if (npc.IsFighting ||
            world.Tick < npc.Mind.AdrenalineUntilTick ||
            npc.Mind.PendingAbuseFrom is not null ||
            npc.Mind.PendingExpulsionFrom is not null ||
            npc.Mind.PendingTalkFrom is not null ||
            npc.Mind.PendingRomanceFrom is not null ||
            npc.Mind.PendingAidFrom is not null)
        {
            return true;
        }

        // Кризис своей нужды: не «голодна», а «голодает» — ровно тот латч,
        // по которому аукцион пробивает любые удержания.
        if (npc.Mind.IsStarving || npc.Mind.IsDehydrated)
        {
            return true;
        }

        // Вода: у берега граница гекса решает всё, поэтому спрашивается ТАЙЛ,
        // на котором она стоит, а не расстояние до воды.
        return world.Tiles.Items.TryGetValue(npc.Tile, out var tile) &&
            SpatialQueries.IsSwimTile(tile);
    }

    /// <summary>
    /// Свободна ли она сесть: ничто не мешает и колдаун вышел.
    /// <para>
    /// Узел (<c>CurrentJunction</c>) здесь НЕ требуется, и это не оплошность.
    /// Поле заполняет ходьба, а у только что появившейся в мире оно пусто —
    /// то есть ровно у той, которой заведомо нечего делать. Сидению узел и не
    /// нужен: оно ничего не бронирует и не занимает.
    /// </para>
    /// </summary>
    internal static bool CanStart(WorldState world, NPCState npc) =>
        world.Tick >= npc.Mind.RestCooldownUntilTick &&
        !npc.Movement.IsMoving &&
        !Blocked(world, npc);
}

}

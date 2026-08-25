using HexLive.Simulation.Agents;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;

namespace HexLive.UnityPresentation.Bootstrap
{

/// <summary>
/// §149 r2 (bug #232): «мой лагерь» ЭТОГО клиента. §149 научил интерфейс
/// владению (<see cref="ISimulationSource.CanControlNpc"/>), но вторым
/// слагаемым «свой» всюду осталась буквальная <c>Faction.Colony</c> — а на
/// сервере игроку выдают девушку ЛЮБОГО лагеря (Colony2..Colony6), и весь её
/// лагерь падал в «Чужаки», рисовался врагами на карте и прятался туманом.
/// Лагерь выводится из фракции выданной девушки по снапшоту; проводного поля
/// не нужно. Фолбэк — <c>Faction.Colony</c>: анонимный зритель и локальная
/// игра, где владение и так покрывает весь лагерь.
/// </summary>
public static class PlayerCampView
{
    private static int _tick = int.MinValue;
    private static ISimulationSource? _runner;
    private static Faction _camp = Faction.Colony;

    public static Faction Of(ISimulationSource? runner, WorldSnapshot? snapshot)
    {
        if (runner == null || snapshot == null)
        {
            return Faction.Colony;
        }

        if (snapshot.Tick == _tick && ReferenceEquals(runner, _runner))
        {
            return _camp;
        }

        _tick = snapshot.Tick;
        _runner = runner;
        _camp = Faction.Colony;
        foreach (var npc in snapshot.Npcs)
        {
            if (runner.CanControlNpc(npc.Id))
            {
                _camp = npc.Faction;
                break;
            }
        }

        return _camp;
    }

    /// <summary>Свой: под моим управлением ИЛИ из моего лагеря.</summary>
    public static bool IsMine(
        ISimulationSource? runner, WorldSnapshot? snapshot, NpcSnapshot npc) =>
        runner != null &&
        (runner.CanControlNpc(npc.Id) || npc.Faction == Of(runner, snapshot));

    /// <summary>
    /// Враждебность К ИГРОКУ, а не к лагерю №1: снапшотный
    /// <c>IsHostileToColony</c> посчитан сервером против буквальной
    /// <c>Faction.Colony</c> и для игрока другого лагеря структурно врёт.
    /// Mode в снапшоте нет, но он и не нужен: девушку НЕ из Colony выдают
    /// только в мульти-лагерных режимах, где мировая перегрузка AreHostile
    /// совпадает с чистой (solo-смягчение §146 действует лишь в режимах с
    /// одним девичьим лагерем — там выдача всегда Colony, и тогда точен
    /// серверный IsHostileToColony, включая solo-нейтралитет).
    /// </summary>
    public static bool HostileToPlayer(
        ISimulationSource? runner, WorldSnapshot? snapshot, NpcSnapshot npc)
    {
        if (IsMine(runner, snapshot, npc))
        {
            return false;
        }

        var camp = Of(runner, snapshot);
        return camp == Faction.Colony
            ? npc.IsHostileToColony
            : FactionRelations.AreHostile(npc.Faction, camp);
    }
}

}

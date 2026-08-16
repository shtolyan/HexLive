using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §118.4 r2 (баг #166): кого игрок вправе взять на руки вручную.
/// <para>
/// Правило игрока: «всегда можно брать СВОИХ персонажей на руки через ручное
/// управление, неважно, спят они или нет». Раньше поднять можно было только
/// лежащего (без сознания, спящего, ползущего или мёртвого) — то есть здоровую
/// колонистку, застрявшую не там, унести было нельзя вообще.
/// </para>
/// <para>
/// Чужой на ногах по-прежнему не даётся в руки: это не носилки, а захват, и он
/// решается боем. Мёртвого можно всегда — он никуда не идёт.
/// </para>
/// <para>
/// Предикат ОДИН на приём приказа и на его исполнение: до §128 r2 такие пары
/// уже разъезжались (см. PlayerLootTargets).
/// </para>
/// </summary>
internal static class ManualCarryTargets
{
    public static bool CanCarry(WorldState world, NPCState carrier, NPCState person, bool dead)
    {
        if (carrier is null || person is null || person.Id.Equals(carrier.Id) ||
            person.IsBeingCarried)
        {
            return false;
        }

        return dead ||
            FactionRelations.AreAllies(carrier, person) ||
            person.IsLyingDown(world.Tick);
    }
}

}

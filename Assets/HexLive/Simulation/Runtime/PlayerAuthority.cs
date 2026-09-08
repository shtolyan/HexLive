using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
    /// <summary>§123: the single simulation-side ownership gate.
    /// <para>
    /// ⭐ §149.4: «игрокова» — это `Faction.Colony` ИЛИ девушка, выданная
    /// сетевому игроку (<see cref="WorldState.PlayerControlledNpcs"/>). Пока
    /// здесь стояла одна фракция, сервер выдавал девушку соседнего лагеря
    /// (§149.2), пропускал её приказы через свою границу прав — и симуляция
    /// отбивала их «NotOwned». Снаружи это выглядело как «персонаж выдан, а
    /// управление недоступно», причём одинаково на тумблере, приказах и
    /// инвентаре.
    /// </para>
    /// <para>
    /// Набор наполняет ТОЛЬКО сервер и только из своего реестра назначений; в
    /// локальной игре он пуст, и ответ здесь прежний. Проверка лагеря остаётся
    /// и для набора: право приказывать не должно уметь распространиться на
    /// Outsiders или на потерпевшую (§146.10) через испорченный реестр.
    /// </para>
    /// </summary>
    public static class PlayerAuthority
    {
        public static bool CanControl(WorldState world, EntityId id, out NPCState npc)
        {
            if (world.Entities.Npcs.TryGetValue(id, out npc) && IsPlayerOwned(world, npc))
            {
                return true;
            }

            npc = null;
            return false;
        }

        /// <summary>Тот же ответ для уже найденного NPC — чтобы вызывающий не
        /// заводил вторую, расходящуюся формулировку «своей».</summary>
        public static bool IsPlayerOwned(WorldState world, NPCState npc) =>
            npc != null &&
            (world.CreationConfig != null ? world.PlayerControlledNpcs.Contains(npc.Id.Value) && FactionRelations.IsGirlCamp(npc.Faction) :
             npc.Faction == Faction.Colony ||
             (world.PlayerControlledNpcs.Contains(npc.Id.Value) &&
              FactionRelations.IsGirlCamp(npc.Faction)));

        public static bool CanMutateInventory(WorldState world, EntityId id, out NPCState npc) =>
            CanControl(world, id, out npc);
    }
}

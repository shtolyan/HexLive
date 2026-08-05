using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// §106: вода — убежище, и среда атаки — данные. Один ответ на два вопроса:
// «в какой среде стоит этот боец» и «достаёт ли атакующий с такими средами до
// цели там, где она стоит». Люди моб-листа не имеют, поэтому их Land-only
// объявлен здесь ОДИН раз (NpcMelee); звери несут свою среду в
// MobStats.AttackMediums (волк — Land, акула — Water), и включение акулы не
// потребует нового гейта — только чтения этого же.
internal static class CombatMedium
{
    // Плывёт = стоит на глубокой воде (Water && !Walkable, §40.18-B). Мерится
    // по ТАЙЛУ: береговой джанкшен смешанный, и стоящая на его сухом тайле не
    // пловчиха. Флага состояния нет намеренно — предикат не может протухнуть.
    internal static bool IsNpcSwimming(WorldState world, NPCState npc) =>
        SpatialQueries.IsSwimTile(world, npc.Tile);

    // В какой среде боец стоит СЕЙЧАС.
    internal static AttackMedium Of(WorldState world, NPCState npc) =>
        IsNpcSwimming(world, npc) ? AttackMedium.Water : AttackMedium.Land;

    // Достаёт ли атака с этими средами до цели там, где та стоит.
    internal static bool CanEngage(WorldState world, AttackMedium attackerMediums, NPCState target) =>
        (attackerMediums & Of(world, target)) != 0;

    // Человек против человека: бьют только с суши и только по суше. Ручка
    // выключает весь слой §106 — с ней false поведение байт-в-байт до-§106.
    internal static bool NpcMelee(WorldState world, NPCState actor, NPCState target) =>
        !Spec106.WaterSanctuaryEnabled ||
        (Of(world, actor) == AttackMedium.Land &&
         CanEngage(world, AttackMedium.Land, target));
}

}

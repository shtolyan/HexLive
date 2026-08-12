using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §133: голой при чужаке не ходят.
///
/// <para>
/// Игрок сформулировал это так: раздетой в жару можно — но только если
/// безопасно; а если рядом ЧУЖАК, надо прикрыться, и минимум это трусы и
/// лифчик, независимо от того, дают они защиту или нет. Броню при этом хотят
/// по-прежнему (это уже умеет <c>wantsArmor</c>), но она не заменяет бельё:
/// закрыты должны быть таз и грудь.
/// </para>
/// </summary>
public static class ModestyMath
{
    /// <summary>Насколько сильно хочется прикрыться при чужаке.</summary>
    public const float CoverNeed = 0.9f;

    /// <summary>
    /// Знает ли она о чужаке рядом: видит врага, помнит опасное место или
    /// помнит самого чужака.
    /// </summary>
    public static bool OutsiderKnown(WorldState world, NPCState npc)
    {
        if (npc.Perception.Hostiles.Count > 0 || npc.Memory.Dangers.Count > 0)
        {
            return true;
        }

        foreach (var remembered in npc.Perception.Remembered)
        {
            if (world.Entities.Npcs.TryGetValue(remembered.Id, out var seen) &&
                seen.Health > 0f && seen.Faction != npc.Faction)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Открыт ли таз или грудь — то, что при чужаке прикрывают в первую очередь.</summary>
    public static bool MissingCover(WorldState world, NPCState npc) =>
        !IsCovered(world, npc, BodyPart.Pelvis) || !IsCovered(world, npc, BodyPart.Torso);

    /// <summary>Сколько «стыдных» частей закрыла бы эта вещь прямо сейчас (0..2).</summary>
    public static int CoverGainFromWearing(WorldState world, NPCState npc, string definitionId)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def) ||
            def.Layer is null)
        {
            return 0;
        }

        var gain = 0;
        if (def.Covers.Contains(BodyPart.Pelvis) && !IsCovered(world, npc, BodyPart.Pelvis))
        {
            gain++;
        }

        if (def.Covers.Contains(BodyPart.Torso) && !IsCovered(world, npc, BodyPart.Torso))
        {
            gain++;
        }

        return gain;
    }

    private static bool IsCovered(WorldState world, NPCState npc, BodyPart part)
    {
        foreach (var worn in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(worn.DefinitionId, out var def) &&
                def.Layer is not null && def.Covers.Contains(part))
            {
                return true;
            }
        }

        return false;
    }
}

}

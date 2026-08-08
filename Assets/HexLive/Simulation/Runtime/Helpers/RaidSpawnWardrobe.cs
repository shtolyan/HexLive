using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>Deterministic day-wave clothing that every female arrival receives.</summary>
internal static class RaidSpawnWardrobe
{
    private const float ExtraTopChance = 0.20f;

    internal static void EquipFemale(WorldState world, NPCState npc)
    {
        if (world == null || npc == null)
        {
            return;
        }

        var briefs = Pool(world, garment =>
            garment.Layer == WearLayer.Underwear &&
            garment.Covers.Contains(BodyPart.Pelvis) &&
            !garment.Covers.Contains(BodyPart.Torso));
        var bras = Pool(world, garment =>
            garment.Layer == WearLayer.Underwear &&
            garment.Covers.Contains(BodyPart.Torso) &&
            !garment.Covers.Contains(BodyPart.Pelvis));

        Equip(npc, briefs, MathUtil.Hash01(world.Seed, npc.Id.Value, 76, 7601));
        Equip(npc, bras, MathUtil.Hash01(world.Seed, npc.Id.Value, 76, 7602));

        if (MathUtil.Hash01(world.Seed, npc.Id.Value, 76, 7603) < ExtraTopChance)
        {
            var tops = Pool(world, garment =>
                garment.Layer == WearLayer.Wear &&
                garment.Covers.Contains(BodyPart.Torso) &&
                !garment.Covers.Contains(BodyPart.Pelvis) &&
                garment.Warmth <= 0.06f);
            Equip(npc, tops, MathUtil.Hash01(world.Seed, npc.Id.Value, 76, 7604));
        }
    }

    private static string[] Pool(WorldState world, Func<GarmentParams, bool> keep)
    {
        var result = new List<string>();
        foreach (var garment in GarmentLibrary.Active)
        {
            if (garment == null || garment.Sex == GarmentSex.Male || !keep(garment) ||
                !world.Content.ObjectDefinitions.TryGetValue(garment.Id, out var definition) ||
                !definition.Tags.Contains("Clothing"))
            {
                continue;
            }

            result.Add(garment.Id);
        }

        result.Sort(StringComparer.Ordinal);
        return result.ToArray();
    }

    private static void Equip(NPCState npc, string[] pool, float roll)
    {
        if (pool.Length == 0)
        {
            return;
        }

        var index = Math.Min(pool.Length - 1, (int)(roll * pool.Length));
        var id = pool[index];
        if (!npc.WornItems.Contains(id))
        {
            npc.WornItems.Add(id);
        }
    }
}

}

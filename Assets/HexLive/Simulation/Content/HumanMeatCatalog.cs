using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Content
{
// §56.7: identity survives inventory, saves and roasting without a new item-state field.
public static class HumanMeatCatalog
{
    public const string ArmRaw = "food.human_arm_raw";
    public const string LegRaw = "food.human_leg_raw";
    public const string TorsoRaw = "food.human_torso_raw";
    public const string ArmCooked = "food.human_arm_cooked";
    public const string LegCooked = "food.human_leg_cooked";
    public const string TorsoCooked = "food.human_torso_cooked";
    public const string ButcherPerson = "butcher.person";
    public static readonly string[] RawParts = { ArmRaw, LegRaw, TorsoRaw };
    public static bool IsHumanPart(string id) => id is ArmRaw or LegRaw or TorsoRaw or ArmCooked or LegCooked or TorsoCooked;
    public static bool IsRaw(string id) => id == ContentIds.MeatRaw || id is ArmRaw or LegRaw or TorsoRaw;
    public static bool IsCooked(string id) => id == ContentIds.MeatCooked || id is ArmCooked or LegCooked or TorsoCooked;
    public static int CountRaw(NPCState npc)
    {
        var count = 0;
        foreach (var item in npc.Inventory.Items) if (IsRaw(item.DefinitionId)) count++;
        return count;
    }
    public static ItemInstance FirstRaw(NPCState npc) => npc.Inventory.Items.Find(i => IsRaw(i.DefinitionId));
    public static string Cooked(string id) => id switch
    {
        ArmRaw => ArmCooked, LegRaw => LegCooked, TorsoRaw => TorsoCooked,
        ContentIds.MeatRaw => ContentIds.MeatCooked, _ => id
    };
    public static string FromLimb(string variant) => variant is "ArmL" or "ArmR" ? ArmRaw : LegRaw;
    public static void AppendDefinitions(Dictionary<string, ObjectDefinition> defs)
    {
        foreach (var raw in RawParts)
        {
            foreach (var id in new[] { raw, Cooked(raw) })
            {
                var def = new ObjectDefinition { Id = id, DisplayName = id };
                def.Tags.Add(ObjectTags.Food);
                if (IsRaw(id)) def.Tags.Add(ObjectTags.RawMeat);
                def.Interactions.Add(new InteractionDefinition { Id = "pickup." + id,
                    Type = InteractionType.PickUp, DurationTicks = 4 });
                if (IsCooked(id)) def.Interactions.Add(new InteractionDefinition {
                    Id = "eat." + id, Type = InteractionType.Eat, DurationTicks = 8,
                    Effects = { HungerDelta = -SimBalance.CookedMeatHunger, ComfortDelta = -0.1f } });
                defs[id] = def;
            }
        }
    }
}
}

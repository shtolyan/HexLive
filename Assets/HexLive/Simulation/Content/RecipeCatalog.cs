using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Content
{

// Spec §54 (R2): the crafting ingredient bill, as data. Recipes used to live as
// two parallel hardcoded switches in ExecutionSystem (validation + effect); the
// duplicated ingredient ids/counts were exactly where the §54 firewood→stick
// rewire risks drift. This table is the single source of truth for INPUTS +
// craft gating (lit fire, one-rack). Output/placement logic stays in the effect
// switch (outputs are varied — tools, worn garments, furniture, side-effects —
// and mostly unchanged by §54).
public sealed class RecipeIngredient
{
    public string Id { get; }

    public int Count { get; }

    public RecipeIngredient(string id, int count)
    {
        Id = id;
        Count = count;
    }
}

public sealed class Recipe
{
    public GoalType Goal { get; }

    public IReadOnlyList<RecipeIngredient> Inputs { get; }

    // Spec 29F.3: cooking needs the fire actually lit (ResourceAmount > 0).
    public bool NeedsLitFire { get; }

    // Spec 35.5: only one drying rack in the colony.
    public bool RequiresNoRack { get; }

    public Recipe(GoalType goal, RecipeIngredient[] inputs, bool needsLitFire = false, bool requiresNoRack = false)
    {
        Goal = goal;
        Inputs = inputs;
        NeedsLitFire = needsLitFire;
        RequiresNoRack = requiresNoRack;
    }
}

public static class RecipeCatalog
{
    // §54 phase 0: ids are still the pre-split materials (resource.firewood).
    // Phase 1 retargets the wood ingredients here (firewood → stick/log) in one
    // place instead of across two switches.
    public static readonly IReadOnlyDictionary<GoalType, Recipe> ByGoal = Build();

    private static Dictionary<GoalType, Recipe> Build()
    {
        var d = new Dictionary<GoalType, Recipe>();

        void Add(GoalType goal, RecipeIngredient[] inputs, bool fire = false, bool noRack = false) =>
            d[goal] = new Recipe(goal, inputs, fire, noRack);

        RecipeIngredient I(string id, int count) => new(id, count);

        // Spec §54: hand tools / arrows / spear / rack are framed from STICKS
        // (the split-log currency), not raw logs.
        Add(GoalType.CraftSpear, new[] { I("resource.stick", 1) });
        Add(GoalType.CookMeat, new[] { I("food.meat_raw", 1) }, fire: true);
        Add(GoalType.CraftLeather, new[] { I("resource.hide", 1) });
        Add(GoalType.CraftAxe, new[] { I("resource.stick", 1), I("resource.stone", 1) });
        Add(GoalType.CraftPickaxe, new[] { I("resource.stick", 1), I("resource.stone", 2) });
        Add(GoalType.CraftRack, new[] { I("resource.stick", 2) }, noRack: true);
        // Spec §54.2: the campfire bed is the leaf MAT — EXACTLY the pieces it's
        // assembled from (16 leaf blades + 6 stick rails, baked from the leaf-mat
        // prefab; see BedFactory.BillFor("bed.leaf")). The premium bedroll is a
        // separate progressive build-site piece (BedFactory.BillFor("bed.basic")).
        Add(GoalType.CraftBed, new[] { I("resource.palm_leaf", 16), I("resource.stick", 6) });
        // Spec §54: a tent is lashed from leaves + a bolt of cloth.
        Add(GoalType.CraftTent, new[] { I("resource.palm_leaf", 4), I("resource.cloth", 1) });
        // Spec §54: the bow needs a rope bowstring (lashing).
        Add(GoalType.CraftBow, new[] { I("resource.stick", 2), I("resource.hide", 1), I("resource.rope", 1) });
        Add(GoalType.CraftArrows, new[] { I("resource.stick", 1) });
        Add(GoalType.CraftBandage, new[] { I("resource.herb_leaf", 2) });
        // Spec §54: cordage & cloth from fiber; the knife from a stick + stone.
        Add(GoalType.CraftRope, new[] { I("resource.fiber", SimBalance.RopeFiberCost) });
        Add(GoalType.CraftCloth, new[] { I("resource.fiber", SimBalance.ClothFiberCost) });
        Add(GoalType.CraftKnife, new[] { I("resource.stick", SimBalance.KnifeStickCost), I("resource.stone", SimBalance.KnifeStoneCost) });

        return d;
    }
}

}

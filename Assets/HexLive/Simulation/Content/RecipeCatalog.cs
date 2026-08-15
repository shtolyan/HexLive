using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Content
{

// Spec §54 (R2): the crafting ingredient bill, as data. Recipes used to live as
// two parallel hardcoded switches in ExecutionSystem (validation + effect); the
// duplicated ingredient ids/counts were exactly where the §54 firewood→stick
// rewire risks drift. This table is the single source of truth for INPUTS,
// physical item OUTPUT, and craft gating (station, lit fire, one-rack).
// Placed furniture and specialized output application remain in their effect
// adapters; ordinary item discovery/lifecycle is derived from this data.
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

    // §119.2: non-empty means this recipe produces one physical inventory
    // item. Keeping output beside inputs removes the old parallel
    // GoalByOutput registry and makes the recipe the only source of truth.
    public string OutputDefinitionId { get; }

    // Spec 29F.3: cooking needs the fire actually lit (ResourceAmount > 0).
    public bool NeedsLitFire { get; }

    // Spec 35.5: only one drying rack in the colony.
    public bool RequiresNoRack { get; }

    // §gear-craft: WHERE the craft happens — a station tag ("Campfire"…).
    // EMPTY means «где угодно»: the character crafts right where he stands,
    // no walk to a workbench. Code defaults keep the campfire; an item's
    // asset clears or changes it.
    public string Station { get; }

    // §119: persistent work units required by the world-bound output object.
    public int BaseWorkTicks { get; }

    public Recipe(GoalType goal, RecipeIngredient[] inputs, bool needsLitFire = false,
        bool requiresNoRack = false, string station = "Campfire", int baseWorkTicks = 24,
        string outputDefinitionId = "")
    {
        Goal = goal;
        Inputs = inputs;
        OutputDefinitionId = outputDefinitionId ?? "";
        NeedsLitFire = needsLitFire;
        RequiresNoRack = requiresNoRack;
        Station = needsLitFire && string.IsNullOrEmpty(station) ? "Campfire" : (station ?? "");
        BaseWorkTicks = System.Math.Max(1, baseWorkTicks);
    }
}

public static class RecipeCatalog
{
    // §54 phase 0: ids are still the pre-split materials (resource.firewood).
    // Phase 1 retargets the wood ingredients here (firewood → stick/log) in one
    // place instead of across two switches.
    //
    // §gear-craft: the recipe now LIVES ON THE OUTPUT ITEM's asset — the
    // knife's GearConfig / the rope's WorldObjectConfig declares its "Крафт"
    // section (ingredients + станция), the tunings push it here via Override,
    // and the code table below is only the engine-free default/fallback.
    private static Dictionary<GoalType, Recipe> _byGoal;

    public static IReadOnlyDictionary<GoalType, Recipe> ByGoal => _byGoal ??= Build();

    /// <summary>Asset-driven recipe for an output item: replaces the default's
    /// inputs/fire flag (RequiresNoRack is preserved). Unknown output ids are
    /// ignored — a craft needs its goal-layer verb to exist.</summary>
    public static void Override(string outputId, RecipeIngredient[] inputs, bool needsLitFire,
        string station = "Campfire", int baseWorkTicks = 0)
    {
        if (string.IsNullOrEmpty(outputId) || inputs == null)
        {
            return;
        }

        _byGoal ??= Build();
        Recipe existing = null;
        foreach (var candidate in _byGoal.Values)
        {
            if (candidate.OutputDefinitionId == outputId)
            {
                existing = candidate;
                break;
            }
        }
        if (existing is null)
        {
            return; // a craft still needs its authored goal-layer verb
        }

        var goal = existing.Goal;
        var noRack = existing.RequiresNoRack;
        var work = baseWorkTicks > 0 ? baseWorkTicks : existing.BaseWorkTicks;
        _byGoal[goal] = new Recipe(
            goal, inputs, needsLitFire, noRack, station, work, outputId);
    }

    /// <summary>The craft's station tag; "" = craft in place, anywhere.</summary>
    public static string StationOf(GoalType goal) =>
        ByGoal.TryGetValue(goal, out var recipe) ? recipe.Station : "Campfire";

    public static int WorkTicksOf(GoalType goal) =>
        ByGoal.TryGetValue(goal, out var recipe) ? recipe.BaseWorkTicks : Spec119.DefaultItemCraftWork;

    public static string OutputOf(GoalType goal) =>
        ByGoal.TryGetValue(goal, out var recipe)
            ? recipe.OutputDefinitionId
            : string.Empty;

    /// <summary>True for crafts whose output is an inventory ITEM (the ones an
    /// asset can re-home/re-price); placed-object crafts (bed/tent/rack) are
    /// station-bound by nature.</summary>
    public static bool IsItemOutputGoal(GoalType goal) =>
        ByGoal.TryGetValue(goal, out var recipe) &&
        !string.IsNullOrEmpty(recipe.OutputDefinitionId);

    /// <summary>Crafts represented by a persistent 0..100% output object.
    /// Cooking is deliberately excluded: its raw-meat object already lives on
    /// the spit and FireSystem owns that separate roasting process.</summary>
    public static bool UsesPersistentProject(GoalType goal) =>
        goal != GoalType.CookMeat && IsItemOutputGoal(goal);

    public static void ResetToDefaults() => _byGoal = Build();

    /// <summary>Every item-output recipe with its output id — read by the
    /// SimData exporter (placed-object crafts stay code-owned).</summary>
    public static IEnumerable<KeyValuePair<string, Recipe>> ItemRecipes()
    {
        foreach (var recipe in ByGoal.Values)
        {
            if (!string.IsNullOrEmpty(recipe.OutputDefinitionId))
            {
                yield return new KeyValuePair<string, Recipe>(
                    recipe.OutputDefinitionId, recipe);
            }
        }
    }

    /// <summary>How much of one ingredient this craft costs (0 if none) — the
    /// decision layer reads THIS, not balance constants, so asset-tuned
    /// recipes keep want/gather/craft consistent end-to-end.</summary>
    public static int InputCount(GoalType goal, string itemId)
    {
        if (!ByGoal.TryGetValue(goal, out var recipe))
        {
            return 0;
        }

        foreach (var input in recipe.Inputs)
        {
            if (input.Id == itemId)
            {
                return input.Count;
            }
        }

        return 0;
    }

    private static Dictionary<GoalType, Recipe> Build()
    {
        var d = new Dictionary<GoalType, Recipe>();

        void Add(GoalType goal, RecipeIngredient[] inputs, bool fire = false, bool noRack = false,
            string station = "Campfire", int work = 24, string output = "") =>
            d[goal] = new Recipe(
                goal, inputs, fire, noRack, station, work, output);

        RecipeIngredient I(string id, int count) => new(id, count);

        // Spec §54: hand tools / arrows / spear / rack are framed from STICKS
        // (the split-log currency), not raw logs.
        Add(GoalType.CraftSpear, new[] { I("resource.stick", 1) }, output: ContentIds.Spear);
        Add(GoalType.CookMeat, new[] { I("food.meat_raw", 1) }, fire: true,
            output: ContentIds.MeatCooked);
        // Leather craft actually produces the wearable pants. The old
        // pseudo-output resource.leather had no physical world definition.
        Add(GoalType.CraftLeather, new[] { I("resource.hide", 1) },
            output: ContentIds.LeatherPants);
        Add(GoalType.CraftAxe, new[] { I("resource.stick", 1), I("resource.stone", 1) },
            output: ContentIds.AxeStone);
        Add(GoalType.CraftPickaxe, new[] { I("resource.stick", 1), I("resource.stone", 2) },
            output: ContentIds.PickaxeStone);
        Add(GoalType.CraftRack, new[] { I("resource.stick", 2) }, noRack: true);
        // Spec §54.2: the campfire bed is the leaf MAT — EXACTLY the pieces it's
        // assembled from (16 leaf blades + 6 stick rails, baked from the leaf-mat
        // The canonical bed is a progressive build-site piece, not an atomic recipe.
        Add(GoalType.CraftBed, new[] { I("resource.palm_leaf", 16), I("resource.stick", 6) });
        // Spec §54: a tent is lashed from leaves + a bolt of cloth.
        Add(GoalType.CraftTent, new[] { I("resource.palm_leaf", 4), I("resource.cloth", 1) });
        // §gear: bow/arrow recipes removed — archery is retired pending the
        // hunting rework (craft avails are hard-false in the decision layer).
        Add(GoalType.CraftBandage, new[] { I("resource.herb_leaf", 2) },
            output: ContentIds.Bandage);
        // Spec §54: cordage & cloth from fiber; the knife from a stick + stone.
        Add(GoalType.CraftRope, new[] { I("resource.fiber", SimBalance.RopeFiberCost) },
            output: ContentIds.Rope);
        Add(GoalType.CraftCloth, new[] { I("resource.fiber", SimBalance.ClothFiberCost) },
            output: ContentIds.Cloth);
        Add(GoalType.CraftKnife, new[]
        {
            I("resource.stick", SimBalance.KnifeStickCost),
            I("resource.stone", SimBalance.KnifeStoneCost)
        }, output: ContentIds.Knife);
        Add(GoalType.CraftSplint, new[] { I(ContentIds.Stick, 2), I(ContentIds.Rope, 1) },
            station: "", output: ContentIds.Splint);
        Add(GoalType.CraftWoodenArm, new[]
        {
            I(ContentIds.Board, 2), I(ContentIds.Rope, 2), I(ContentIds.Hide, 1)
        }, station: "Workbench", work: Spec119.WoodenProstheticCraftWork,
            output: ContentIds.WoodenArm);
        Add(GoalType.CraftWoodenLeg, new[]
        {
            I(ContentIds.Board, 3), I(ContentIds.Rope, 2), I(ContentIds.Hide, 1)
        }, station: "Workbench", work: Spec119.WoodenProstheticCraftWork,
            output: ContentIds.WoodenLeg);

        return d;
    }
}

}

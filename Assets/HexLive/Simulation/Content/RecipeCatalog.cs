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
    private static Dictionary<string, Recipe> _byOutput;

    // §138.6: item identity is data; ByGoal remains the legacy/AI adapter.
    public static IReadOnlyDictionary<string, Recipe> ByOutput
    {
        get
        {
            if (_byOutput == null)
            {
                _byOutput = new Dictionary<string, Recipe>(System.StringComparer.Ordinal);
                foreach (var recipe in ByGoal.Values)
                    if (!string.IsNullOrEmpty(recipe.OutputDefinitionId))
                        _byOutput.Add(recipe.OutputDefinitionId, recipe);
            }
            return _byOutput;
        }
    }

    public static bool TryGet(GoalType goal, string outputId, out Recipe recipe)
    {
        if (goal == GoalType.CraftItem)
            return ByOutput.TryGetValue(outputId ?? "", out recipe);
        return ByGoal.TryGetValue(goal, out recipe);
    }

    public static string OutputOf(GoalType goal, string outputId) =>
        TryGet(goal, outputId, out var recipe) ? recipe.OutputDefinitionId : "";

    public static IReadOnlyDictionary<GoalType, Recipe> ByGoal => _byGoal ??= Build();

    /// <summary>§138.6: register or replace an item recipe, retaining any
    /// legacy goal and its specialized completion adapter.</summary>
    public static void Override(string outputId, RecipeIngredient[] inputs, bool needsLitFire,
        string station = "Campfire", int baseWorkTicks = 0)
    {
        if (string.IsNullOrWhiteSpace(outputId) || inputs == null)
            throw new System.ArgumentException("A recipe requires an output id and inputs.");
        var ids = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var input in inputs)
            if (input == null || string.IsNullOrWhiteSpace(input.Id) || input.Count <= 0 || !ids.Add(input.Id))
                throw new System.ArgumentException("Recipe ingredients require unique ids and positive counts.");

        ByOutput.TryGetValue(outputId, out var existing);
        var goal = existing?.Goal ?? GoalType.CraftItem;
        var work = baseWorkTicks > 0 ? baseWorkTicks : existing?.BaseWorkTicks ?? Spec119.DefaultItemCraftWork;
        var recipe = new Recipe(goal, (RecipeIngredient[])inputs.Clone(), needsLitFire,
            existing?.RequiresNoRack ?? false, station, work, outputId);
        _byOutput[outputId] = recipe;
        if (goal != GoalType.CraftItem) _byGoal[goal] = recipe;
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
        goal == GoalType.CraftItem ||
        (ByGoal.TryGetValue(goal, out var recipe) &&
         !string.IsNullOrEmpty(recipe.OutputDefinitionId));

    /// <summary>Crafts represented by a persistent 0..100% output object.
    /// Cooking is deliberately excluded: its raw-meat object already lives on
    /// the spit and FireSystem owns that separate roasting process.</summary>
    public static bool UsesPersistentProject(GoalType goal) =>
        goal != GoalType.CookMeat && IsItemOutputGoal(goal);

    public static void ResetToDefaults()
    {
        _byGoal = Build();
        _byOutput = null;
    }

    /// <summary>§138.6: includes data-only items, not placed-object crafts.</summary>
    public static IEnumerable<KeyValuePair<string, Recipe>> ItemRecipes() => ByOutput;

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
        Add(GoalType.CraftSpear, new[]
        {
            I(ContentIds.SharpStone, 1), I(ContentIds.Rope, 1)
        }, station: "", output: ContentIds.Spear);
        Add(GoalType.CookMeat, new[] { I("food.meat_raw", 1) }, fire: true,
            output: ContentIds.MeatCooked);
        // Leather craft actually produces the wearable pants. The old
        // pseudo-output resource.leather had no physical world definition.
        Add(GoalType.CraftLeather, new[] { I(ContentIds.Hide, 1), I(ContentIds.Cloth, 1) },
            output: ContentIds.LeatherPants);
        Add(GoalType.CraftAxe, new[]
        {
            I(ContentIds.Stick, 1), I(ContentIds.Stone, 1), I(ContentIds.Rope, 1)
        }, station: "", output: ContentIds.AxeStone);
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
        Add(GoalType.CraftBandage, new[]
        {
            I(ContentIds.HerbLeaf, 2), I(ContentIds.Cloth, 1)
        }, station: "", output: ContentIds.Bandage);
        // §138.6: plaster can receive a data recipe; its actual bill remains
        // a separate content decision. No recipe is added by this foundation.
        // §172: cordage & cloth from fiber; a crude flake bridges stone and knife.
        Add(GoalType.CraftRope, new[] { I("resource.fiber", SimBalance.RopeFiberCost) },
            output: ContentIds.Rope);
        Add(GoalType.CraftCloth, new[] { I("resource.fiber", SimBalance.ClothFiberCost) },
            output: ContentIds.Cloth);
        Add(GoalType.CraftSharpStone, new[] { I(ContentIds.Stone, 2) },
            station: "", output: ContentIds.SharpStone);
        Add(GoalType.CraftKnife, new[]
        {
            I("resource.stick", SimBalance.KnifeStickCost),
            I(ContentIds.SharpStone, 1),
            I(ContentIds.Rope, 1)
        }, station: "", output: ContentIds.Knife);
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

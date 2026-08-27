namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// Sim-crafted garments (§54 hide clothing) have a simulation identity but no
/// art bundle of their own: the wardrobe pipeline only ships DAZ imports, and
/// nothing authors a dedicated "hide pants" mesh. Without a mapping the id
/// falls through every wear check into <c>object/&lt;id&gt;</c>, which the
/// registry rightly rejects — the item is then invisible on the ground, on the
/// body, and in the inventory list ("Нет active record для
/// object/clothing.leather_pants").
///
/// This is the single place that names the borrowed art. Every resolver
/// (worn visuals, ground drops, icons, prewarm) must translate through
/// <see cref="ArtId"/> before touching the content registry.
/// </summary>
public static class WearArtAliases
{
    /// <summary>The wear-bundle id that carries this sim id's art. Identity
    /// for every ordinary garment.</summary>
    public static string ArtId(string simDefinitionId) => simDefinitionId switch
    {
        // §54: "Hide Pants", crafted from resource.hide at the campfire.
        HexLive.Simulation.Content.ContentIds.LeatherPants => "clothing.pants_biker",
        _ => simDefinitionId,
    };

    /// <summary>True when this sim id has no bundle of its own and borrows
    /// another garment's art.</summary>
    public static bool IsAliased(string simDefinitionId) =>
        !ReferenceEquals(ArtId(simDefinitionId), simDefinitionId);
}

}

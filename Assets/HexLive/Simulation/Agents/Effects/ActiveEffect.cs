namespace HexLive.Simulation.Agents.Effects
{
    // Spec §48: one effect currently acting on a survivor. Kind indexes the
    // catalog; Intensity (0..1) is how hard it bites right now — the tooltip
    // reads it as mild/severe and the chip ring brightens with it. Value type,
    // cheap to collect into a per-NPC list each snapshot.
    public readonly struct ActiveEffect
    {
        public readonly EffectKind Kind;

        public readonly float Intensity;

        // Optional localized explanation for the concrete cause. The catalog
        // still owns the stable title/emoji; this only refines the tooltip for
        // states such as coma, fainting, crying and dying.
        public readonly string DetailKey;

        public ActiveEffect(EffectKind kind, float intensity, string detailKey = null)
        {
            Kind = kind;
            Intensity = intensity < 0f ? 0f : intensity > 1f ? 1f : intensity;
            DetailKey = detailKey ?? string.Empty;
        }
    }
}

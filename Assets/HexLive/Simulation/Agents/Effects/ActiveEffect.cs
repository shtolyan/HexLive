using System.Collections.Generic;

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

    // §48.7: every parameter visible in the character-card needs grid. This is
    // intentionally presentation-neutral: the sim owns which branch touches
    // which parameter, while the view owns layout and colour.
    public enum NeedKind
    {
        Hunger,
        Thirst,
        Energy,
        Comfort,
        Social,
        Temperature,
        Stamina,
        Blood,
        Hygiene,
        Stress,
        Compassion,
        Breath
    }

    public enum EffectImpactDirection
    {
        Negative,
        Positive
    }

    // Fast owners replace their rows every simulation tick; Slow rows survive
    // between the 16-tick need/temperature passes.
    public enum EffectImpactCadence
    {
        Fast,
        Slow
    }

    public readonly struct EffectImpact
    {
        public readonly NeedKind Need;
        public readonly EffectKind Kind;
        public readonly EffectImpactDirection Direction;
        public readonly EffectImpactCadence Cadence;

        public EffectImpact(
            NeedKind need,
            EffectKind kind,
            EffectImpactDirection direction,
            EffectImpactCadence cadence)
        {
            Need = need;
            Kind = kind;
            Direction = direction;
            Cadence = cadence;
        }
    }

    // Transient and deliberately not persisted. Mutation sites write what they
    // actually applied; snapshot/UI consumers only read it. Rows carry no
    // changing magnitude so the remote Character group stays delta-friendly.
    public sealed class EffectImpactLedger
    {
        private readonly List<EffectImpact> _items = new();

        public IReadOnlyList<EffectImpact> Items => _items;

        public void Clear(EffectImpactCadence cadence)
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Cadence == cadence)
                {
                    _items.RemoveAt(i);
                }
            }
        }

        public void Record(
            NeedKind need,
            EffectKind kind,
            EffectImpactDirection direction,
            EffectImpactCadence cadence)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.Need != need || item.Kind != kind || item.Cadence != cadence)
                {
                    continue;
                }

                if (item.Direction != direction)
                {
                    _items[i] = new EffectImpact(need, kind, direction, cadence);
                }

                return;
            }

            _items.Add(new EffectImpact(need, kind, direction, cadence));
        }
    }
}

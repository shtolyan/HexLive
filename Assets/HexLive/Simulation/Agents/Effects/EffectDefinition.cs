using System.Collections.Generic;

namespace HexLive.Simulation.Agents.Effects
{
    // Spec §48: the static, declarative description of one effect — everything
    // that is TRUE of the effect regardless of who has it. The live "is it on,
    // and how hard" is the separate ActiveEffect. Kept Unity-free (no Color):
    // the presentation layer maps polarity → ring colour and Emoji → glyph.
    public sealed class EffectDefinition
    {
        public EffectKind Kind { get; }

        public EffectPolarity Polarity { get; }

        public EffectCategory Category { get; }

        // Placeholder glyph shown inside the chip circle. Emoji for v1 (spec
        // §48.3) — trivially swapped for a generated image per kind later.
        public string Emoji { get; }

        // Localization keys: "<TitleKey>" is the short name on the chip's
        // tooltip, "<DescKey>" the one-line "what it does". Both resolve in Loc.
        public string TitleKey { get; }

        public string DescKey { get; }

        public EffectDefinition(
            EffectKind kind,
            EffectPolarity polarity,
            EffectCategory category,
            string emoji)
        {
            Kind = kind;
            Polarity = polarity;
            Category = category;
            Emoji = emoji;
            var slug = kind.ToString().ToLowerInvariant();
            TitleKey = $"effect.{slug}.title";
            DescKey = $"effect.{slug}.desc";
        }
    }

    // Spec §48: the single source of truth for what each effect is. The
    // evaluator only ever emits kinds that appear here; the UI reads polarity,
    // category and the placeholder emoji from here.
    public static class EffectCatalog
    {
        private static readonly Dictionary<EffectKind, EffectDefinition> Map = Build();

        public static IReadOnlyDictionary<EffectKind, EffectDefinition> All => Map;

        public static EffectDefinition Get(EffectKind kind) => Map[kind];

        public static bool TryGet(EffectKind kind, out EffectDefinition def) =>
            Map.TryGetValue(kind, out def);

        private static Dictionary<EffectKind, EffectDefinition> Build()
        {
            var map = new Dictionary<EffectKind, EffectDefinition>();

            void Add(EffectKind kind, EffectPolarity polarity, EffectCategory category, string emoji)
                => map[kind] = new EffectDefinition(kind, polarity, category, emoji);

            // Injury / blood
            Add(EffectKind.Bleeding, EffectPolarity.Debuff, EffectCategory.Injury, "🩸");
            Add(EffectKind.Injured, EffectPolarity.Debuff, EffectCategory.Injury, "🤕");
            Add(EffectKind.Hobbled, EffectPolarity.Debuff, EffectCategory.Injury, "🦵");
            Add(EffectKind.Bandaged, EffectPolarity.Buff, EffectCategory.Injury, "🩹");
            Add(EffectKind.Maimed, EffectPolarity.Debuff, EffectCategory.Injury, "🦿");

            // Environment
            Add(EffectKind.StrongSun, EffectPolarity.Debuff, EffectCategory.Environment, "🌞");
            Add(EffectKind.Sunstroke, EffectPolarity.Debuff, EffectCategory.Environment, "☀️");
            Add(EffectKind.Sunburnt, EffectPolarity.Debuff, EffectCategory.Environment, "🥵");
            Add(EffectKind.Hot, EffectPolarity.Debuff, EffectCategory.Environment, "🌡️");
            Add(EffectKind.Heatstroke, EffectPolarity.Debuff, EffectCategory.Environment, "🔥");
            Add(EffectKind.Cold, EffectPolarity.Debuff, EffectCategory.Environment, "❄️");
            Add(EffectKind.Freezing, EffectPolarity.Debuff, EffectCategory.Environment, "🥶");
            Add(EffectKind.Soaked, EffectPolarity.Debuff, EffectCategory.Environment, "💦");
            Add(EffectKind.Cozy, EffectPolarity.Buff, EffectCategory.Environment, "🏕️");

            // Survival
            Add(EffectKind.Starving, EffectPolarity.Debuff, EffectCategory.Survival, "🍽️");
            Add(EffectKind.Dehydrated, EffectPolarity.Debuff, EffectCategory.Survival, "💧");
            Add(EffectKind.Sick, EffectPolarity.Debuff, EffectCategory.Survival, "🤢");
            Add(EffectKind.Exhausted, EffectPolarity.Debuff, EffectCategory.Survival, "😮‍💨");
            Add(EffectKind.Fainted, EffectPolarity.Debuff, EffectCategory.Survival, "😵");
            Add(EffectKind.Coma, EffectPolarity.Debuff, EffectCategory.Survival, "😵‍💫"); // §60
            Add(EffectKind.WellFed, EffectPolarity.Buff, EffectCategory.Survival, "😋");
            Add(EffectKind.Rested, EffectPolarity.Buff, EffectCategory.Survival, "💪");
            Add(EffectKind.Snug, EffectPolarity.Buff, EffectCategory.Survival, "🛌");

            // Mind
            Add(EffectKind.Stressed, EffectPolarity.Debuff, EffectCategory.Mind, "😰");
            Add(EffectKind.Grieving, EffectPolarity.Debuff, EffectCategory.Mind, "😢");
            Add(EffectKind.Lonely, EffectPolarity.Debuff, EffectCategory.Mind, "😞");
            Add(EffectKind.Miserable, EffectPolarity.Debuff, EffectCategory.Mind, "😣");
            Add(EffectKind.Content, EffectPolarity.Buff, EffectCategory.Mind, "😊");

            // Hygiene
            Add(EffectKind.Filthy, EffectPolarity.Debuff, EffectCategory.Hygiene, "🧟");

            return map;
        }
    }
}

using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// Engine-free override table for WORLD OBJECT definitions — the object
    /// mirror of <see cref="MobCatalog"/>/<see cref="GearCatalog"/>. The Unity
    /// layer (ObjectTuning) feeds it from per-object WorldObjectConfig assets
    /// BEFORE the world is built; <c>WorldStateFactory</c> applies it on top of
    /// <see cref="PrototypeContentCatalog.CreateDefaults"/>.
    ///
    /// Semantics are MERGE, not replace: for an id that already exists in the
    /// defaults, declared interactions replace same-id interactions (or append
    /// new ones) and tags are unioned — so an asset can add a "распилить"
    /// action to the log without re-describing (and risking) the rest of the
    /// definition. A brand-new id becomes a whole new world object.
    /// </summary>
    public static class WorldObjectLibrary
    {
        private static readonly Dictionary<string, ObjectDefinition> Overrides = new();

        public static void Clear() => Overrides.Clear();

        // The registered override definitions — read by the SimData exporter.
        public static IReadOnlyCollection<ObjectDefinition> Registered => Overrides.Values;

        public static void Override(ObjectDefinition definition)
        {
            if (definition != null && !string.IsNullOrEmpty(definition.Id))
            {
                Overrides[definition.Id] = definition;
            }
        }

        /// <summary>Merge every registered override into the built defaults.</summary>
        public static void ApplyTo(Dictionary<string, ObjectDefinition> defs)
        {
            if (defs == null)
            {
                return;
            }

            foreach (var pair in Overrides)
            {
                if (!defs.TryGetValue(pair.Key, out var baseDef))
                {
                    defs[pair.Key] = pair.Value; // brand-new object
                    continue;
                }

                foreach (var tag in pair.Value.Tags)
                {
                    if (!baseDef.Tags.Contains(tag))
                    {
                        baseDef.Tags.Add(tag);
                    }
                }

                if (pair.Value.Produce != null)
                {
                    baseDef.Produce = pair.Value.Produce;
                }

                if (pair.Value.Storage.Count > 0)
                {
                    baseDef.Storage.Clear();
                    baseDef.Storage.AddRange(pair.Value.Storage);
                }

                foreach (var incoming in pair.Value.Interactions)
                {
                    var replaced = false;
                    for (var i = 0; i < baseDef.Interactions.Count; i++)
                    {
                        if (baseDef.Interactions[i].Id == incoming.Id)
                        {
                            baseDef.Interactions[i] = incoming;
                            replaced = true;
                            break;
                        }
                    }

                    if (!replaced)
                    {
                        baseDef.Interactions.Add(incoming);
                    }
                }
            }
        }
    }
}

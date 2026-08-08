using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// The headless data bridge. In the editor every catalog (mobs, gear,
    /// world objects, recipes) is tuned through ScriptableObjects; a headless
    /// probe or soak harness cannot load those. Instead the editor EXPORTS the
    /// applied catalogs to one JSON file (HexLive ▸ Export Sim Data — writes
    /// <c>SimData/simdata.json</c> at the repo root), and a headless host calls
    /// <see cref="LoadAndApply"/> as its first line — the sim then runs on the
    /// exact numbers the game runs on, not on the code defaults.
    ///
    /// Engine-free on purpose: hand-rolled minimal JSON reader (objects,
    /// arrays, strings, numbers, bools), no UnityEngine, no external packages.
    /// </summary>
    public static class SimDataFile
    {
        // Repo-root-relative conventional location of the export.
        public const string DefaultRelativePath = "SimData/simdata.json";

        // v2: adds the "balance" (SimBalance/Spec*/HexHopTuning statics),
        // "garments" and per-interaction "effects" sections — before v2 the
        // headless harness ran on code-default balance despite §59.3.
        // v3: Kenshi damage profiles on gear and mobs.
        // v4 (§119): every item recipe exports persistent base work and station.
        public const int SchemaVersion = 4;

        /// <summary>§59.3: the MANDATORY form for probes/soaks — throws when
        /// the export is missing, unparseable or a STALE schema version (a v1
        /// file has no balance section, so the probe would silently run on
        /// code-default balance). Running on code defaults is forbidden.
        /// Export via HexLive ▸ Export Sim Data.</summary>
        public static void Require(string path)
        {
            string text;
            try
            {
                text = System.IO.File.ReadAllText(path);
            }
            catch
            {
                throw new System.InvalidOperationException(
                    $"SimData export not found/unreadable at '{path}'. " +
                    "Run Unity menu 'HexLive ▸ Export Sim Data (JSON)' first — " +
                    "headless runs must NOT use code defaults (spec §59.3).");
            }

            if (MiniJson.Parse(text) is not Dictionary<string, object> root)
            {
                throw new System.InvalidOperationException(
                    $"SimData export at '{path}' is not valid JSON. " +
                    "Re-export via Unity menu 'HexLive ▸ Export Sim Data (JSON)'.");
            }

            var version = root.TryGetValue("version", out var v) && v is double d
                ? (int)System.Math.Round(d)
                : 1;
            if (version < SchemaVersion)
            {
                throw new System.InvalidOperationException(
                    $"SimData export at '{path}' is schema v{version}, need v{SchemaVersion} " +
                    "(no balance/garments sections — the probe would run on code-default balance). " +
                    "Re-export via Unity menu 'HexLive ▸ Export Sim Data (JSON)'.");
            }

            Apply(root);
        }

        /// <summary>Reads the export and applies every section onto the live
        /// catalogs. Returns false (and changes nothing) when the file is
        /// missing or unparseable. Prefer <see cref="Require"/> in harnesses.</summary>
        public static bool LoadAndApply(string path)
        {
            string text;
            try
            {
                text = System.IO.File.ReadAllText(path);
            }
            catch
            {
                return false;
            }

            return ApplyJson(text);
        }

        public static bool ApplyJson(string json)
        {
            if (MiniJson.Parse(json) is not Dictionary<string, object> root)
            {
                return false;
            }

            Apply(root);
            return true;
        }

        private static void Apply(Dictionary<string, object> root)
        {
            ApplyBalance(root);
            ApplyMobs(root);
            ApplyGear(root);
            ApplyGarments(root);
            ApplyWorldObjects(root);
            ApplyRecipes(root);
        }

        private static void ApplyBalance(Dictionary<string, object> root)
        {
            if (root.TryGetValue("balance", out var section) &&
                section is Dictionary<string, object> balance)
            {
                foreach (var pair in balance)
                {
                    // Unknown keys are skipped (a newer export against older
                    // code); both sides enumerate via BalanceReflection, so a
                    // freshly added knob round-trips with zero schema churn.
                    var field = BalanceReflection.Find(pair.Key);
                    if (field == null)
                    {
                        continue;
                    }

                    var t = field.FieldType;
                    if (t == typeof(float) && pair.Value is double f)
                    {
                        field.SetValue(null, (float)f);
                    }
                    else if (t == typeof(int) && pair.Value is double i)
                    {
                        field.SetValue(null, (int)System.Math.Round(i));
                    }
                    else if (t == typeof(long) && pair.Value is double l)
                    {
                        field.SetValue(null, (long)System.Math.Round(l));
                    }
                    else if (t == typeof(bool) && pair.Value is bool b)
                    {
                        field.SetValue(null, b);
                    }
                }
            }
        }

        private static void ApplyGarments(Dictionary<string, object> root)
        {
            if (root.TryGetValue("garments", out var section) && section is List<object> garments)
            {
                var list = new List<GarmentParams>();
                foreach (var entry in garments)
                {
                    if (entry is not Dictionary<string, object> g)
                    {
                        continue;
                    }

                    var covers = new List<BodyPart>();
                    foreach (var name in Strings(g, "covers"))
                    {
                        if (System.Enum.TryParse<BodyPart>(name, true, out var part))
                        {
                            covers.Add(part);
                        }
                    }

                    // §84: пол берётся из КОДОВЫХ умолчаний по id — в экспорте
                    // такого поля нет, а без переноса импорт стирал бы пол у
                    // всех вещей разом, и запрет молча переставал работать
                    // именно в headless-пробах, где его и проверяют.
                    var gid = Str(g, "id");
                    var gsex = GarmentSex.Any;
                    foreach (var d in GarmentLibrary.Defaults)
                    {
                        if (d.Id == gid)
                        {
                            gsex = d.Sex;
                            break;
                        }
                    }

                    list.Add(new GarmentParams(
                        gid,
                        Str(g, "displayName"),
                        System.Enum.TryParse<WearLayer>(Str(g, "layer"), true, out var layer)
                            ? layer
                            : WearLayer.Wear,
                        F(g, "warmth", 0f),
                        F(g, "armor", 0f),
                        F(g, "thermalDelta", 0f),
                        I(g, "dressDurationTicks", 20),
                        I(g, "capacity", 0),
                        gsex,
                        covers.ToArray())
                    {
                        // Absent means "its own art" — the constructor already
                        // set that, so an older export still reads correctly.
                        PrototypeId = string.IsNullOrEmpty(Str(g, "prototypeId"))
                            ? gid
                            : Str(g, "prototypeId"),
                    });
                }

                if (list.Count > 0)
                {
                    GarmentLibrary.Override(list);
                }
            }
        }

        private static void ApplyMobs(Dictionary<string, object> root)
        {
            if (root.TryGetValue("mobs", out var section) && section is List<object> mobs)
            {
                foreach (var entry in mobs)
                {
                    if (entry is not Dictionary<string, object> m)
                    {
                        continue;
                    }

                    MobCatalog.Override(new MobStats
                    {
                        Id = Str(m, "id"),
                        MaxHealth = F(m, "maxHealth", 1f),
                        // "biteDamage" is the legacy pre-rename key.
                        AttackDamage = F(m, "attackDamage", F(m, "biteDamage", 0.09f)),
                        CutFraction = F(m, "cutFraction",
                            MobCatalog.For(Str(m, "id")).CutFraction),
                        BloodLossMultiplier = F(m, "bloodLossMultiplier",
                            MobCatalog.For(Str(m, "id")).BloodLossMultiplier),
                        AttackWindupSeconds = F(m, "attackWindupSeconds", 0.1f),
                        AttackCooldownSeconds = F(m, "attackCooldownSeconds", 0.8f),
                        AggroRadiusTiles = I(m, "aggroRadiusTiles", 2),
                        RoamChance = F(m, "roamChance", 0.2f),
                        ChaseStepsPerTick = I(m, "chaseStepsPerTick", 1),
                        GlideSegmentSeconds = F(m, "glideSegmentSeconds", 1f),
                        GlideSnapDistance = F(m, "glideSnapDistance", 6f),
                        // Pre-hold-distance exports carry no key; 0.9 matches
                        // the MobStats field default, not the shark's 0.
                        MeleeHoldDistance = F(m, "meleeHoldDistance", 0.9f),
                        // §106: a pre-AttackMediums export carries no key — fall
                        // back to the CATALOG default for this id (not the field
                        // default Land, which would silently turn the shark
                        // terrestrial on every stale export).
                        AttackMediums = Medium(m, "attackMediums", Str(m, "id")),
                        RaidChancePerDay = F(m, "raidChancePerDay", 0f),
                        RaidPackSize = I(m, "raidPackSize", 0),
                    });
                }
            }
        }

        private static void ApplyGear(Dictionary<string, object> root)
        {
            if (root.TryGetValue("gear", out var section) && section is List<object> gear)
            {
                foreach (var entry in gear)
                {
                    if (entry is not Dictionary<string, object> g)
                    {
                        continue;
                    }

                    var stats = new GearStats
                    {
                        Id = Str(g, "id"),
                        Damage = F(g, "damage", 0.15f),
                        CutFraction = F(g, "cutFraction",
                            GearCatalog.For(Str(g, "id")).CutFraction),
                        BloodLossMultiplier = F(g, "bloodLossMultiplier",
                            GearCatalog.For(Str(g, "id")).BloodLossMultiplier),
                        HitDelaySeconds = F(g, "hitDelaySeconds", 1.5f),
                        AttackDurationSeconds = F(g, "attackDurationSeconds", 2f),
                        CooldownSeconds = F(g, "cooldownSeconds", 1f),
                        AttackSpeed = F(g, "attackSpeed", 1f),
                        MeleePriority = I(g, "meleePriority", 0),
                        TwoHanded = B(g, "twoHanded"),
                        HarvestSpeedMult = F(g, "harvestSpeedMult", 1f),
                    };
                    foreach (var name in Strings(g, "capabilities"))
                    {
                        if (System.Enum.TryParse<GearCapability>(name, true, out var flag))
                        {
                            stats.Capabilities |= flag;
                        }
                    }

                    // Per-strike variant timings (fists: punches/kicks).
                    if (g.TryGetValue("strikes", out var strikesRaw) &&
                        strikesRaw is List<object> strikes && strikes.Count > 0)
                    {
                        var variants = new List<StrikeVariant>();
                        foreach (var strikeEntry in strikes)
                        {
                            if (strikeEntry is Dictionary<string, object> s)
                            {
                                variants.Add(new StrikeVariant
                                {
                                    HitDelaySeconds = F(s, "hitDelaySeconds", 0.2f),
                                    AttackDurationSeconds = F(s, "attackDurationSeconds", 0.4f),
                                    CooldownSeconds = F(s, "cooldownSeconds", 0.2f),
                                });
                            }
                        }

                        stats.StrikeVariants = variants.Count > 0 ? variants.ToArray() : null;
                    }

                    GearCatalog.Override(stats);
                }
            }
        }

        private static void ApplyWorldObjects(Dictionary<string, object> root)
        {
            if (root.TryGetValue("worldObjects", out var section) && section is List<object> objects)
            {
                foreach (var entry in objects)
                {
                    if (entry is not Dictionary<string, object> o)
                    {
                        continue;
                    }

                    var def = new ObjectDefinition
                    {
                        Id = Str(o, "id"),
                        DisplayName = Str(o, "displayName"),
                    };
                    foreach (var tag in Strings(o, "tags"))
                    {
                        def.Tags.Add(tag);
                    }

                    if (o.TryGetValue("produce", out var pv) && pv is Dictionary<string, object> produce)
                    {
                        var producedId = Str(produce, "item");
                        if (!string.IsNullOrEmpty(producedId))
                        {
                            def.Produce = new ProduceDefinition
                            {
                                ProducedDefinitionId = producedId,
                                IntervalTicks = I(produce, "intervalTicks", 300),
                                MaxConcurrent = I(produce, "maxConcurrent", 2),
                                MaxDistanceTiles = I(produce, "radiusTiles", 1),
                            };
                        }
                    }

                    if (o.TryGetValue("storage", out var sv) && sv is List<object> storage)
                    {
                        foreach (var se in storage)
                        {
                            if (se is Dictionary<string, object> row &&
                                System.Enum.TryParse<StoredKind>(Str(row, "kind"), true, out var kind))
                            {
                                def.Storage.Add(new StoredResource { Kind = kind, Amount = F(row, "amount", 1f) });
                            }
                        }
                    }

                    if (o.TryGetValue("interactions", out var iv) && iv is List<object> interactions)
                    {
                        foreach (var ie in interactions)
                        {
                            if (ie is not Dictionary<string, object> i)
                            {
                                continue;
                            }

                            var interaction = new InteractionDefinition
                            {
                                Id = Str(i, "id"),
                                Type = System.Enum.TryParse<InteractionType>(Str(i, "type"), true, out var t)
                                    ? t
                                    : InteractionType.Process,
                                DurationTicks = I(i, "durationTicks", 40),
                            };
                            // The serializer owns the string↔enum mapping —
                            // JSON carries enum NAMES, unknown names are skipped.
                            foreach (var cap in Strings(i, "requiredCapabilities"))
                            {
                                if (System.Enum.TryParse<GearCapability>(cap, true, out var flag) &&
                                    flag != GearCapability.None &&
                                    !interaction.RequiredCapabilities.Contains(flag))
                                {
                                    interaction.RequiredCapabilities.Add(flag);
                                }
                            }
                            // v2: eat/drink/dress payoffs round-trip too — a
                            // v1 export silently dropped them, leaving the
                            // catalog's code defaults in headless runs.
                            if (i.TryGetValue("effects", out var ev) &&
                                ev is Dictionary<string, object> effects)
                            {
                                interaction.Effects.HungerDelta = F(effects, "hunger", 0f);
                                interaction.Effects.EnergyDelta = F(effects, "energy", 0f);
                                interaction.Effects.ComfortDelta = F(effects, "comfort", 0f);
                                interaction.Effects.ThermalDelta = F(effects, "thermal", 0f);
                                interaction.Effects.ThirstDelta = F(effects, "thirst", 0f);
                                interaction.Effects.WarmthDelta = F(effects, "warmth", 0f);
                                interaction.Effects.ArmorDelta = F(effects, "armor", 0f);
                            }

                            if (i.TryGetValue("yields", out var yv) && yv is List<object> yields)
                            {
                                foreach (var ye in yields)
                                {
                                    if (ye is Dictionary<string, object> y)
                                    {
                                        interaction.Yields.Add(new HarvestDrop
                                        {
                                            DefinitionId = Str(y, "id"),
                                            Count = I(y, "count", 1),
                                            Scatter = B(y, "scatter"),
                                        });
                                    }
                                }
                            }

                            def.Interactions.Add(interaction);
                        }
                    }

                    WorldObjectLibrary.Override(def);
                }
            }
        }

        private static void ApplyRecipes(Dictionary<string, object> root)
        {
            if (root.TryGetValue("recipes", out var section) && section is List<object> recipes)
            {
                foreach (var entry in recipes)
                {
                    if (entry is not Dictionary<string, object> r)
                    {
                        continue;
                    }

                    var inputs = new List<RecipeIngredient>();
                    if (r.TryGetValue("inputs", out var iv) && iv is List<object> list)
                    {
                        foreach (var ie in list)
                        {
                            if (ie is Dictionary<string, object> i)
                            {
                                inputs.Add(new RecipeIngredient(Str(i, "id"), I(i, "count", 1)));
                            }
                        }
                    }

                    RecipeCatalog.Override(
                        Str(r, "output"), inputs.ToArray(),
                        B(r, "needsLitFire"), Str(r, "station"), I(r, "baseWorkTicks", 0));
                }
            }
        }

        /// <summary>§59.3: serialize the LIVE catalogs to the export schema.
        /// The single serializer — the editor menu applies the SO layers and
        /// writes this string; a headless host can dump its state the same way.</summary>
        public static string ExportJson()
        {
            // Deterministic order (sorted by id) — the export is committed, so
            // stable diffs matter more than dictionary insertion order.
            static List<T> Sorted<T>(IEnumerable<T> source, System.Func<T, string> key)
            {
                var list = new List<T>(source);
                list.Sort((a, b) => string.CompareOrdinal(key(a), key(b)));
                return list;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"version\": {SchemaVersion},\n");

            // v2: every tunable static (SimBalance, Spec bags, HexHopTuning) as
            // a flat sorted "Class.Field": value map. Enumerated via
            // BalanceReflection on BOTH sides, so a new knob round-trips with
            // zero schema churn. G9 floats — the values must survive the trip
            // bit-close, this section is balance, not prose.
            sb.Append("  \"balance\": {\n");
            var firstBalance = true;
            foreach (var pair in BalanceReflection.EnumerateFields())
            {
                if (!firstBalance) sb.Append(",\n");
                firstBalance = false;
                var value = pair.Value.GetValue(null);
                var text = value switch
                {
                    float f => f.ToString("G9", System.Globalization.CultureInfo.InvariantCulture),
                    bool b => b ? "true" : "false",
                    _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                };
                sb.Append($"    {Q(pair.Key)}: {text}");
            }

            sb.Append("\n  },\n  \"mobs\": [\n");
            var first = true;
            foreach (var pair in Sorted(MobCatalog.Active, p => p.Key))
            {
                var m = pair.Value;
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("    {")
                  .Append($"\"id\": {Q(m.Id)}, \"maxHealth\": {N(m.MaxHealth)}, \"attackDamage\": {N(m.AttackDamage)}, ")
                  .Append($"\"cutFraction\": {N(m.CutFraction)}, \"bloodLossMultiplier\": {N(m.BloodLossMultiplier)}, ")
                  .Append($"\"attackWindupSeconds\": {N(m.AttackWindupSeconds)}, \"attackCooldownSeconds\": {N(m.AttackCooldownSeconds)}, ")
                  .Append($"\"aggroRadiusTiles\": {m.AggroRadiusTiles}, \"roamChance\": {N(m.RoamChance)}, ")
                  .Append($"\"chaseStepsPerTick\": {m.ChaseStepsPerTick}, \"glideSegmentSeconds\": {N(m.GlideSegmentSeconds)}, ")
                  .Append($"\"glideSnapDistance\": {N(m.GlideSnapDistance)}, \"meleeHoldDistance\": {N(m.MeleeHoldDistance)}, ")
                  .Append($"\"attackMediums\": {Q(m.AttackMediums.ToString())}, ")
                  .Append($"\"raidChancePerDay\": {N(m.RaidChancePerDay)}, \"raidPackSize\": {m.RaidPackSize}}}");
            }

            sb.Append("\n  ],\n  \"gear\": [\n");
            first = true;
            foreach (var pair in Sorted(GearCatalog.Active, p => p.Key))
            {
                var g = pair.Value;
                if (g.Id != pair.Key)
                {
                    continue; // fist-fallback cache rows
                }

                if (!first) sb.Append(",\n");
                first = false;
                var caps = new System.Text.StringBuilder();
                foreach (GearCapability flag in System.Enum.GetValues(typeof(GearCapability)))
                {
                    if (flag != GearCapability.None && g.Has(flag))
                    {
                        if (caps.Length > 0) caps.Append(", ");
                        caps.Append(Q(flag.ToString()));
                    }
                }

                var strikes = new System.Text.StringBuilder();
                if (g.HasStrikeVariants)
                {
                    foreach (var v in g.StrikeVariants)
                    {
                        if (strikes.Length > 0) strikes.Append(", ");
                        strikes.Append($"{{\"hitDelaySeconds\": {N(v.HitDelaySeconds)}, ")
                               .Append($"\"attackDurationSeconds\": {N(v.AttackDurationSeconds)}, ")
                               .Append($"\"cooldownSeconds\": {N(v.CooldownSeconds)}}}");
                    }
                }

                sb.Append("    {")
                  .Append($"\"id\": {Q(g.Id)}, \"damage\": {N(g.Damage)}, \"cutFraction\": {N(g.CutFraction)}, ")
                  .Append($"\"bloodLossMultiplier\": {N(g.BloodLossMultiplier)}, \"hitDelaySeconds\": {N(g.HitDelaySeconds)}, ")
                  .Append($"\"attackDurationSeconds\": {N(g.AttackDurationSeconds)}, \"cooldownSeconds\": {N(g.CooldownSeconds)}, ")
                  .Append($"\"attackSpeed\": {N(g.AttackSpeed)}, \"meleePriority\": {g.MeleePriority}, ")
                  .Append($"\"twoHanded\": {(g.TwoHanded ? "true" : "false")}, \"harvestSpeedMult\": {N(g.HarvestSpeedMult)}, ")
                  .Append($"\"capabilities\": [{caps}], \"strikes\": [{strikes}]}}");
            }

            sb.Append("\n  ],\n  \"garments\": [\n");
            first = true;
            foreach (var g in Sorted(GarmentLibrary.Active, x => x.Id))
            {
                if (!first) sb.Append(",\n");
                first = false;
                var covers = new System.Text.StringBuilder();
                foreach (var part in g.Covers)
                {
                    if (covers.Length > 0) covers.Append(", ");
                    covers.Append(Q(part.ToString()));
                }

                sb.Append("    {")
                  .Append($"\"id\": {Q(g.Id)}, \"displayName\": {Q(g.DisplayName)}, \"layer\": {Q(g.Layer.ToString())}, ")
                  .Append($"\"warmth\": {N(g.Warmth)}, \"armor\": {N(g.Armor)}, \"thermalDelta\": {N(g.ThermalDelta)}, ")
                  .Append($"\"dressDurationTicks\": {g.DressDurationTicks}, \"capacity\": {g.Capacity}, ");

                // Only when it differs (§31B.4E): almost every garment is its
                // own prototype, and writing the id twice on ~100 rows would be
                // noise in a file people read.
                if (!string.IsNullOrEmpty(g.PrototypeId) && g.PrototypeId != g.Id)
                {
                    sb.Append($"\"prototypeId\": {Q(g.PrototypeId)}, ");
                }

                sb.Append($"\"covers\": [{covers}]}}");
            }

            sb.Append("\n  ],\n  \"worldObjects\": [\n");
            first = true;
            // Export the effective catalog, not just ScriptableObject
            // overrides. Otherwise a code-default object added since the last
            // JSON export (splints/prostheses in §118) is absent forever when a
            // headless process loads the old JSON and immediately re-exports.
            var effectiveObjects = new Dictionary<string, ObjectDefinition>(
                PrototypeContentCatalog.CreateDefaults());
            WorldObjectLibrary.ApplyTo(effectiveObjects);
            foreach (var def in Sorted(effectiveObjects.Values, d => d.Id))
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("    {").Append($"\"id\": {Q(def.Id)}, \"displayName\": {Q(def.DisplayName)}, \"tags\": [");
                for (var i = 0; i < def.Tags.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Q(def.Tags[i]));
                }

                if (def.Produce != null)
                {
                    sb.Append($"], \"produce\": {{\"item\": {Q(def.Produce.ProducedDefinitionId)}, ")
                      .Append($"\"intervalTicks\": {def.Produce.IntervalTicks}, \"maxConcurrent\": {def.Produce.MaxConcurrent}, ")
                      .Append($"\"radiusTiles\": {def.Produce.MaxDistanceTiles}}}, \"storage\": [");
                }
                else
                {
                    sb.Append("], \"storage\": [");
                }
                for (var i = 0; i < def.Storage.Count; i++)
                {
                    var st = def.Storage[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append($"{{\"kind\": {Q(st.Kind.ToString())}, \"amount\": {N(st.Amount)}}}");
                }

                sb.Append("], \"interactions\": [");
                for (var i = 0; i < def.Interactions.Count; i++)
                {
                    var it = def.Interactions[i];
                    if (i > 0) sb.Append(", ");
                    var caps = new System.Text.StringBuilder();
                    foreach (var cap in it.RequiredCapabilities)
                    {
                        if (caps.Length > 0) caps.Append(", ");
                        caps.Append(Q(cap.ToString()));
                    }

                    // v2: non-zero effect deltas (eat/drink/dress payoffs).
                    var fx = new System.Text.StringBuilder();
                    void Fx(string name, float value)
                    {
                        if (value == 0f) return;
                        if (fx.Length > 0) fx.Append(", ");
                        fx.Append($"{Q(name)}: {N(value)}");
                    }

                    Fx("hunger", it.Effects.HungerDelta);
                    Fx("energy", it.Effects.EnergyDelta);
                    Fx("comfort", it.Effects.ComfortDelta);
                    Fx("thermal", it.Effects.ThermalDelta);
                    Fx("thirst", it.Effects.ThirstDelta);
                    Fx("warmth", it.Effects.WarmthDelta);
                    Fx("armor", it.Effects.ArmorDelta);

                    sb.Append("{")
                      .Append($"\"id\": {Q(it.Id)}, \"type\": {Q(it.Type.ToString())}, ")
                      .Append($"\"requiredCapabilities\": [{caps}], \"durationTicks\": {it.DurationTicks}, ")
                      .Append(fx.Length > 0 ? $"\"effects\": {{{fx}}}, " : "")
                      .Append("\"yields\": [");
                    for (var y = 0; y < it.Yields.Count; y++)
                    {
                        var yd = it.Yields[y];
                        if (y > 0) sb.Append(", ");
                        sb.Append($"{{\"id\": {Q(yd.DefinitionId)}, \"count\": {yd.Count}, \"scatter\": {(yd.Scatter ? "true" : "false")}}}");
                    }

                    sb.Append("]}");
                }

                sb.Append("]}");
            }

            sb.Append("\n  ],\n  \"recipes\": [\n");
            first = true;
            foreach (var pair in Sorted(RecipeCatalog.ItemRecipes(), p => p.Key))
            {
                var r = pair.Value;
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("    {")
                  .Append($"\"output\": {Q(pair.Key)}, \"needsLitFire\": {(r.NeedsLitFire ? "true" : "false")}, ")
                  .Append($"\"station\": {Q(r.Station)}, \"baseWorkTicks\": {r.BaseWorkTicks}, \"inputs\": [");
                for (var i = 0; i < r.Inputs.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append($"{{\"id\": {Q(r.Inputs[i].Id)}, \"count\": {r.Inputs[i].Count}}}");
                }

                sb.Append("]}");
            }

            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        private static string Q(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string N(float v) =>
            v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

        private static string Str(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

        private static float F(Dictionary<string, object> d, string key, float fallback) =>
            d.TryGetValue(key, out var v) && v is double n ? (float)n : fallback;

        private static int I(Dictionary<string, object> d, string key, int fallback) =>
            d.TryGetValue(key, out var v) && v is double n ? (int)System.Math.Round(n) : fallback;

        private static bool B(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is bool b && b;

        // §106: attack medium rides as its enum name ("Land"/"Water"/
        // "Amphibious"); a stale export without the key keeps the mob's own
        // catalog default so the shark never silently turns terrestrial.
        private static AttackMedium Medium(
            Dictionary<string, object> d, string key, string mobId)
        {
            if (d.TryGetValue(key, out var v) && v is string s &&
                System.Enum.TryParse<AttackMedium>(s, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            return MobCatalog.Defaults.TryGetValue(mobId, out var stats)
                ? stats.AttackMediums
                : AttackMedium.Land;
        }

        private static IEnumerable<string> Strings(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var v) && v is List<object> list)
            {
                foreach (var item in list)
                {
                    if (item is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        yield return s.Trim();
                    }
                }
            }
        }
    }

    /// <summary>A ~100-line JSON reader for the export schema: objects →
    /// Dictionary&lt;string,object&gt;, arrays → List&lt;object&gt;, numbers →
    /// double, plus string/bool/null. No writer here — the editor exporter
    /// builds its JSON with a StringBuilder.</summary>
    internal static class MiniJson
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var pos = 0;
            try
            {
                var value = ParseValue(text, ref pos);
                return value;
            }
            catch
            {
                return null;
            }
        }

        private static object ParseValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            switch (s[p])
            {
                case '{': return ParseObject(s, ref p);
                case '[': return ParseArray(s, ref p);
                case '"': return ParseString(s, ref p);
                case 't': p += 4; return true;
                case 'f': p += 5; return false;
                case 'n': p += 4; return null;
                default: return ParseNumber(s, ref p);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int p)
        {
            var result = new Dictionary<string, object>();
            p++; // {
            SkipWs(s, ref p);
            if (s[p] == '}')
            {
                p++;
                return result;
            }

            while (true)
            {
                SkipWs(s, ref p);
                var key = ParseString(s, ref p);
                SkipWs(s, ref p);
                p++; // :
                result[key] = ParseValue(s, ref p);
                SkipWs(s, ref p);
                if (s[p] == ',')
                {
                    p++;
                    continue;
                }

                p++; // }
                return result;
            }
        }

        private static List<object> ParseArray(string s, ref int p)
        {
            var result = new List<object>();
            p++; // [
            SkipWs(s, ref p);
            if (s[p] == ']')
            {
                p++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(s, ref p));
                SkipWs(s, ref p);
                if (s[p] == ',')
                {
                    p++;
                    continue;
                }

                p++; // ]
                return result;
            }
        }

        private static string ParseString(string s, ref int p)
        {
            var sb = new System.Text.StringBuilder();
            p++; // opening quote
            while (s[p] != '"')
            {
                if (s[p] == '\\')
                {
                    p++;
                    sb.Append(s[p] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        'u' => ParseUnicode(s, ref p),
                        var c => c
                    });
                }
                else
                {
                    sb.Append(s[p]);
                }

                p++;
            }

            p++; // closing quote
            return sb.ToString();
        }

        private static char ParseUnicode(string s, ref int p)
        {
            var code = System.Convert.ToInt32(s.Substring(p + 1, 4), 16);
            p += 4;
            return (char)code;
        }

        private static double ParseNumber(string s, ref int p)
        {
            var start = p;
            while (p < s.Length && (char.IsDigit(s[p]) || s[p] is '-' or '+' or '.' or 'e' or 'E'))
            {
                p++;
            }

            return double.Parse(s.Substring(start, p - start),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p]))
            {
                p++;
            }
        }
    }
}

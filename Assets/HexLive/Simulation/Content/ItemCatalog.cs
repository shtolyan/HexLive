using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    // Spec §51: how the inventory UI classifies one item for its icon, tint and
    // "what is this" copy. Kept Unity-free (no Color/Sprite) — the presentation
    // layer maps the category to a colour and the Emoji to a placeholder glyph,
    // exactly like EffectCatalog does for status effects (§48). Real icons can
    // replace the emoji per id later without touching this file.
    public enum ItemCategory
    {
        Weapon,
        Tool,
        Clothing,
        Armor,
        Food,
        Water,
        Medicine,
        Resource,
        Misc
    }

    // The static "what is this item" descriptor the inventory panel reads. Name
    // and description resolve through Loc: "item.<slug>.name" / ".desc" when a
    // hand-authored string exists, otherwise the panel falls back to the
    // definition's DisplayName and a generic per-category blurb
    // ("itemcat.<category>.desc"). Nothing here is mutable per-NPC — the live
    // wetness/durability of a specific instance stays on ItemInstance.
    public sealed class ItemInfo
    {
        public string DefinitionId { get; }

        public ItemCategory Category { get; }

        // Placeholder glyph shown in the slot (emoji for v1, spec §51.2).
        public string Emoji { get; }

        // Per-item localization keys (may be absent → panel uses fallbacks).
        public string NameKey { get; }

        public string DescKey { get; }

        // Per-category localization keys (always present in Loc).
        public string CategoryNameKey { get; }

        public string CategoryDescKey { get; }

        public ItemInfo(string definitionId, ItemCategory category, string emoji)
        {
            DefinitionId = definitionId;
            Category = category;
            Emoji = emoji;

            var slug = Slug(definitionId);
            NameKey = $"item.{slug}.name";
            DescKey = $"item.{slug}.desc";

            var cat = category.ToString().ToLowerInvariant();
            CategoryNameKey = $"itemcat.{cat}.name";
            CategoryDescKey = $"itemcat.{cat}.desc";
        }

        // Loc-key friendly form of an id: lowercase, every non-alphanumeric
        // character collapsed to '_' ("Shirt G3F_31977" → "shirt_g3f_31977").
        public static string Slug(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return string.Empty;
            }

            var chars = id.ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }

    // Spec §51: single source of truth for how an inventory item is presented.
    // Classification is derived from the ObjectDefinition's tags/layer so newly
    // added content (e.g. imported garments) is covered automatically; a small
    // per-id emoji table sharpens the glyph where the category default is too
    // generic.
    public static class ItemCatalog
    {
        // A nicer glyph than the category default for well-known items.
        private static readonly Dictionary<string, string> EmojiById = new()
        {
            ["food.coconut"] = "🥥",
            ["food.coconut_pierced"] = "🥥",
            ["food.coconut_open"] = "🥥",
            ["food.meat_cooked"] = "🍖",
            ["food.meat_raw"] = "🥩",
            ["tool.bottle"] = "🧴",
            ["tool.pot"] = "🍲",
            ["tool.lighter"] = "🔥",
            ["tool.axe_stone"] = "🪓",
            ["tool.machete"] = "🗡️",
            ["tool.pickaxe_stone"] = "⛏️",
            ["tool.saw"] = "🪚",
            ["tool.bow"] = "🏹",
            ["tool.spear"] = "🔱",
            ["resource.arrow"] = "🎯",
            ["resource.stone"] = "🪨",
            ["resource.log"] = "🪵",
            ["resource.stick"] = "🥢",
            ["resource.fiber"] = "🌾",
            ["resource.rope"] = "🪢",
            ["resource.cloth"] = "🧵",
            ["tool.knife"] = "🔪",
            ["resource.hide"] = "🟫",
            ["resource.palm_leaf"] = "🍃",
            ["resource.herb_leaf"] = "🌿",
            ["item.bandage"] = "🩹",
        };

        // Spec §52: how badly the NPC wants to keep this in a full pack. Water
        // and food outrank everything — you drop a stone before your dinner. The
        // haul-to-fire / spill-overflow logic drops the LOWEST importance first,
        // and the keep-a-stash logic fetches the HIGHEST it lacks.
        // Spec §54: water is paramount; food and DEFENCE (a weapon) rank next and
        // roughly equal — a survivor guards her blade almost as fiercely as her
        // dinner and never sets it down to free a pocket.
        public static int Importance(ItemCategory category) => category switch
        {
            ItemCategory.Water => 100,
            ItemCategory.Food => 95,
            ItemCategory.Weapon => 93,
            ItemCategory.Medicine => 80,
            ItemCategory.Tool => 60,
            ItemCategory.Armor => 45,
            ItemCategory.Clothing => 40,
            ItemCategory.Resource => 20,
            _ => 10
        };

        // Importance of a specific item, category resolved from its definition
        // (falls back to the id-prefix heuristic when the def is absent).
        public static int Importance(ObjectDefinition def) =>
            Importance(def != null && IsWaterSourceId(def.Id)
                ? ItemCategory.Water
                : def != null ? Classify(def) : ItemCategory.Misc);

        public static int ImportanceById(string definitionId) =>
            Importance(IsWaterSourceId(definitionId)
                ? ItemCategory.Water
                : ClassifyById(definitionId));

        public static bool IsWaterContainerId(string definitionId) =>
            definitionId == "tool.bottle" ||
            definitionId == "food.coconut_pierced";

        public static bool IsWaterSourceId(string definitionId) =>
            IsWaterContainerId(definitionId) ||
            definitionId == "food.coconut";

        // Spec §52: a pierced coconut is only "water" while charges remain —
        // drained, it's a shell to crack open someday, not a drink. Ranking a
        // dry shell as Water(100) wedged packs shut: it outranked the knife
        // (93) and fresh food (95), so nothing could ever displace it (seed
        // 1104049673: death by thirst over three empty shells). The bottle is
        // excluded — its charges live on the NPC, not the item instance.
        public static bool IsDrainedWaterShell(string definitionId, float resourceAmount) =>
            definitionId == "food.coconut_pierced" && resourceAmount <= 0f;

        // Resolve from a full definition (preferred — tags/layer drive category).
        public static ItemInfo Resolve(ObjectDefinition def)
        {
            if (def == null)
            {
                return new ItemInfo(string.Empty, ItemCategory.Misc, CategoryEmoji(ItemCategory.Misc));
            }

            var category = Classify(def);
            var emoji = EmojiById.TryGetValue(def.Id, out var e) ? e : CategoryEmoji(category);
            return new ItemInfo(def.Id, category, emoji);
        }

        // Fallback when only the id is known (no ObjectDefinition on hand).
        public static ItemInfo Resolve(string definitionId)
        {
            var category = ClassifyById(definitionId);
            var emoji = EmojiById.TryGetValue(definitionId, out var e) ? e : CategoryEmoji(category);
            return new ItemInfo(definitionId, category, emoji);
        }

        public static ItemCategory Classify(ObjectDefinition def)
        {
            if (def == null)
            {
                return ItemCategory.Misc;
            }

            var tags = def.Tags;

            // Water containers are strategic inventory items regardless of their
            // static authoring tags: bottle is technically a tool, pierced coconut
            // is technically a coconut, but both are carried water.
            if (IsWaterContainerId(def.Id) || tags.Contains("CoconutWater"))
            {
                return ItemCategory.Water;
            }

            // Wearables first: a Layer (or the Clothing tag) makes it apparel;
            // the Armor tag splits protective gear from plain clothing.
            if (def.Layer.HasValue || tags.Contains("Clothing"))
            {
                return tags.Contains("Armor") ? ItemCategory.Armor : ItemCategory.Clothing;
            }

            // Spec §54: anything that can DEFEND is a Weapon — the spear/bow, but
            // also the axe, pickaxe and knife (all real strike-back weapons now).
            // Weapons rank near food in importance so a survivor never drops her
            // means of fighting off a dog.
            if (tags.Contains("Weapon") || tags.Contains("Axe") ||
                tags.Contains("Pickaxe") || tags.Contains("Knife") ||
                tags.Contains("Machete"))
            {
                return ItemCategory.Weapon;
            }

            if (tags.Contains("Medicine") || tags.Contains("Herb"))
            {
                return ItemCategory.Medicine;
            }

            if (tags.Contains("Food") || tags.Contains("RawMeat"))
            {
                return ItemCategory.Food;
            }

            if (tags.Contains("RawWater") || tags.Contains("Water"))
            {
                return ItemCategory.Water;
            }

            // Non-combat tools (pot, lighter, bottle, hammer, saw) — the axe /
            // pickaxe / knife are classified as Weapon above.
            if (tags.Contains("Tool") || tags.Contains("Saw"))
            {
                return ItemCategory.Tool;
            }

            if (tags.Contains("Wood") || tags.Contains("Stone") || tags.Contains("Hide") ||
                tags.Contains("PalmLeaf") || tags.Contains("Resource"))
            {
                return ItemCategory.Resource;
            }

            return ItemCategory.Misc;
        }

        // Weak id-prefix inference for the id-only fallback path.
        private static ItemCategory ClassifyById(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return ItemCategory.Misc;
            }

            if (IsWaterContainerId(id)) return ItemCategory.Water;
            if (id.StartsWith("armor.")) return ItemCategory.Armor;
            if (id.StartsWith("clothing.") || id.StartsWith("underwear.")) return ItemCategory.Clothing;
            if (id.StartsWith("food.")) return ItemCategory.Food;
            if (id.StartsWith("water.")) return ItemCategory.Water;
            if (id == "item.bandage") return ItemCategory.Medicine;
            if (id.StartsWith("tool.")) return ItemCategory.Tool;
            if (id.StartsWith("resource.")) return ItemCategory.Resource;

            // Imported garments carry no dotted prefix — treat as clothing.
            return ItemCategory.Clothing;
        }

        public static string CategoryEmoji(ItemCategory category)
        {
            return category switch
            {
                ItemCategory.Weapon => "⚔️",
                ItemCategory.Tool => "🛠️",
                ItemCategory.Clothing => "👕",
                ItemCategory.Armor => "🛡️",
                ItemCategory.Food => "🍎",
                ItemCategory.Water => "💧",
                ItemCategory.Medicine => "💊",
                ItemCategory.Resource => "📦",
                _ => "❔"
            };
        }
    }

    // Melee weapon combat stats — the per-weapon data seam (user pass, July
    // 2026): damage, windup and cooldown belong to the weapon, exactly like
    // animal timings belong to the animal (SimBalance.Dog*). Damage resolves
    // through the existing SimBalance strike tunables so HexTuningConfig keeps
    // working; each piece of GEAR carries its own full sheet (GearStats).
    //
    // GEAR = weapons AND tools unified: the axe is both (chops wood, fights),
    // the knife is both (cuts/butchers, fights), the pickaxe mines but can
    // clobber a wolf too. One entry per item id, holding EVERYTHING the sim
    // needs: combat numbers, the swing timing, tool CAPABILITIES (what verbs
    // it enables) and weapon selection priority. Systems ask "does the
    // inventory hold something that CanCut?" — never "is there a tool.knife" —
    // so a brand-new tool works everywhere its capabilities say, code-free.
    //
    // The attack timing model (per gear): the attack ANIMATION starts the
    // moment the swing starts; the damage, the victim's flinch AND the blood
    // spray land HitDelaySeconds in — the strike moment inside the animation;
    // the rest of the clip is follow-through until AttackDurationSeconds, then
    // CooldownSeconds of standing recovery. Exchange cycle = duration + cooldown.
    //
    // Mirrors MobCatalog exactly: engine-free defaults below; the Unity layer
    // overrides entries at startup from per-item GearConfig ScriptableObjects
    // (Resources/HexLive/Gear/, via GearTuning). Adding a weapon or tool = one
    // asset (a defaults entry here is optional — an asset alone fully defines
    // a new item); no flat fields on any shared config.
    public static class GearCatalog
    {
        public const string Fist = "";                  // bare hands (empty id)
        // §104 r5: «чем попали» для хит-штампа, когда попали ЗУБЫ. Не id
        // снаряжения: пустая строка уже занята кулаками, а вид выбирает по
        // этому полю звук удара. Живёт здесь, чтобы модель и вид читали одну
        // константу, а не две одинаковые строки в разных сборках.
        public const string Bite = "bite";
        public const string Knife = "tool.knife";
        public const string Axe = "tool.axe_stone";
        public const string Machete = "tool.machete";
        public const string Spear = "tool.spear";
        public const string Pickaxe = "tool.pickaxe_stone";
        public const string Hammer = "tool.hammer";
        public const string Saw = "tool.saw";
        public const string Lighter = "tool.lighter";
        public const string Pot = "tool.pot";
        public const string Bottle = "tool.bottle";
        public const string Bandage = "item.bandage";

        private static System.Collections.Generic.Dictionary<string, GearStats> _active;

        public static System.Collections.Generic.IReadOnlyDictionary<string, GearStats> Defaults => BuildDefaults();

        // The live table (defaults + overrides) — read by the SimData exporter.
        public static System.Collections.Generic.IReadOnlyDictionary<string, GearStats> Active => _active ??= BuildDefaults();

        /// <summary>Stats for a gear id ("" = fists). Unknown ids fall back to
        /// (and cache) the fist sheet so callers can't NRE.</summary>
        public static GearStats For(string gearId)
        {
            _active ??= BuildDefaults();
            var key = gearId ?? Fist;
            if (_active.TryGetValue(key, out var stats))
            {
                return stats;
            }

            var fallback = _active[Fist];
            _active[key] = fallback;
            return fallback;
        }

        public static void Override(GearStats stats)
        {
            if (stats == null || stats.Id == null)
            {
                return;
            }

            _active ??= BuildDefaults();
            _active[stats.Id] = stats;
        }

        public static void ResetToDefaults()
        {
            _active = BuildDefaults();
        }

        // Convenience accessors (the shape the combat code reads).
        public static float Damage(string gearId) => For(gearId).Damage;
        public static float HitDelaySeconds(string gearId) => For(gearId).HitDelaySeconds;
        public static float AttackDurationSeconds(string gearId) => For(gearId).AttackDurationSeconds;
        public static float CooldownSeconds(string gearId) => For(gearId).CooldownSeconds;
        public static float AttackSpeed(string gearId) => For(gearId).AttackSpeed;

        /// <summary>Spec §79 — HOW FAST this job goes: the pace is set by the
        /// piece of gear that actually DOES it, i.e. the best HarvestSpeedMult
        /// among the carried items that carry one of the capabilities the work
        /// is gated on (machete 2 → half the time, stone axe 1 → the authored
        /// time, knife 0.75 → longer).
        ///
        /// Matching on the capability is the whole point: before §79 the best
        /// multiplier in the WHOLE pack won, so a saw in the backpack would
        /// have sped up mining a boulder it cannot even touch — the call site
        /// papered over that with an `if (!isBoulder)`. Gear that cannot do the
        /// job now simply never enters the max.</summary>
        public static float BestSpeedMultFor(
            System.Collections.Generic.IEnumerable<Agents.ItemInstance> items,
            GearCapability anyOf)
        {
            if (anyOf == GearCapability.None)
            {
                return 1f;
            }

            var best = 0f;
            foreach (var item in items)
            {
                var stats = For(item.DefinitionId);
                if (stats.Id != item.DefinitionId ||   // fist-fallback cache ≠ real gear
                    (stats.Capabilities & anyOf) == 0)
                {
                    continue;
                }

                if (stats.HarvestSpeedMult > best)
                {
                    best = stats.HarvestSpeedMult;
                }
            }

            // Nothing in the pack does this job (bare-handed work, or a gate
            // that let her through some other way) — authored pace.
            return best > 0f ? best : 1f;
        }

        /// <summary>The capabilities a job accepts: the content's declared
        /// any-of set when it has one, else the legacy per-type inference that
        /// mirrors the gates in ExecutionSystem. ONE place decides "which tool
        /// does this work", so the gate and the speed can never disagree.</summary>
        public static GearCapability RequiredCapabilities(
            InteractionDefinition interaction, ObjectDefinition definition)
        {
            if (interaction == null)
            {
                return GearCapability.None;
            }

            if (interaction.RequiredCapabilities.Count > 0)
            {
                var declared = GearCapability.None;
                foreach (var capability in interaction.RequiredCapabilities)
                {
                    declared |= capability;
                }

                return declared;
            }

            var tags = definition?.Tags;
            switch (interaction.Type)
            {
                case InteractionType.Harvest:
                    if (tags != null && tags.Contains("Boulder"))
                    {
                        return GearCapability.Mine;
                    }

                    // Spec §54: yucca is cut with a blade, trees are felled.
                    return tags != null && tags.Contains("Yucca")
                        ? GearCapability.Cut
                        : GearCapability.ChopWood;
                case InteractionType.Process:
                    return tags != null && tags.Contains("Coconut")
                        ? GearCapability.Cut
                        : GearCapability.ChopWood;
                case InteractionType.Butcher:
                    return GearCapability.Butcher;
                default:
                    return GearCapability.None;
            }
        }

        /// <summary>Authored ticks re-paced by the tool. Rounds instead of
        /// truncating: the pre-§79 call site divided by an INT, so anything
        /// between 1 and 2 (a slow knife, a 1.5 power tool) silently collapsed
        /// back to "no bonus at all" and only whole multipliers ever did
        /// anything. Never returns 0 — a zero-tick job finishes on the tick it
        /// starts and skips its own animation.</summary>
        public static int ScaleTicks(int ticks, float speedMult)
        {
            if (speedMult <= 0f || System.MathF.Abs(speedMult - 1f) < 0.0001f)
            {
                return ticks;
            }

            return System.Math.Max(1, (int)System.MathF.Round(ticks / speedMult));
        }

        /// <summary>Does the inventory hold any gear with this capability?</summary>
        public static bool HasCapability(
            System.Collections.Generic.IEnumerable<Agents.ItemInstance> items, GearCapability capability)
        {
            foreach (var item in items)
            {
                if (For(item.DefinitionId).Has(capability))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Named-capability variant ("Sew", "Fish"… or a built-in
        /// name) — the seam future verbs gate on without touching the enum.</summary>
        public static bool HasCapability(
            System.Collections.Generic.IEnumerable<Agents.ItemInstance> items, string capabilityName)
        {
            foreach (var item in items)
            {
                if (For(item.DefinitionId).Has(capabilityName))
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly GearCapability[] AllCapabilities =
        {
            GearCapability.Cut, GearCapability.Butcher, GearCapability.ChopWood,
            GearCapability.Mine, GearCapability.Hammer, GearCapability.Ignite,
            GearCapability.Boil, GearCapability.Saw, GearCapability.Sew,
            GearCapability.CarryWater, GearCapability.Dressing,
        };

        /// <summary>GOAP tool-pickup filter: does grabbing this gear ADD
        /// anything over what the inventory already covers — a verb she can't
        /// do yet, or a strictly better melee weapon her hands can wield?
        /// Items outside the gear table (lighter, pot, bottle…) return true —
        /// they keep the legacy "any missing Tool is worth taking" rule.</summary>
        public static bool AddsValueOver(
            System.Collections.Generic.IEnumerable<Agents.ItemInstance> items,
            string gearId, int intactHands)
        {
            var stats = For(gearId);
            if (stats.Id != gearId)
            {
                return true; // not gear-managed → legacy behavior
            }

            foreach (var capability in AllCapabilities)
            {
                if (stats.Has(capability) && !HasCapability(items, capability))
                {
                    return true;
                }
            }

            return stats.MeleePriority >
                       For(BestMeleeWeapon(items, intactHands)).MeleePriority &&
                   intactHands >= (stats.TwoHanded ? 2 : 1);
        }

        /// <summary>The best usable melee weapon in the inventory by
        /// MeleePriority (0 = not a weapon). Two-handed gear needs both hands.
        /// Empty string = fists. Fully data-driven — a new SO with a priority
        /// automatically joins the selection.</summary>
        public static string BestMeleeWeapon(
            System.Collections.Generic.IEnumerable<Agents.ItemInstance> items, int intactHands)
        {
            var bestId = Fist;
            var bestPriority = 0;
            foreach (var item in items)
            {
                Consider(item.DefinitionId, intactHands, ref bestId, ref bestPriority);
            }

            return bestId;
        }

        /// <summary>Same selection over raw item ids (presentation snapshots
        /// carry strings) — ONE picker for sim and view, no duplicated
        /// priority lists.</summary>
        public static string BestMeleeWeapon(
            System.Collections.Generic.IEnumerable<string> itemIds, int intactHands)
        {
            var bestId = Fist;
            var bestPriority = 0;
            foreach (var id in itemIds)
            {
                Consider(id, intactHands, ref bestId, ref bestPriority);
            }

            return bestId;
        }

        private static void Consider(string id, int intactHands, ref string bestId, ref int bestPriority)
        {
            var stats = For(id);
            if (stats.MeleePriority <= bestPriority ||
                intactHands < (stats.TwoHanded ? 2 : 1) ||
                stats.Id != id)  // fist-fallback cache ≠ a weapon
            {
                return;
            }

            bestId = stats.Id;
            bestPriority = stats.MeleePriority;
        }

        // Damage carries the old NpcStrikePerPass(0.15) × strike-bonus values
        // verbatim (fist ×1, knife ×1.25, axe ×1.875, spear ×2.5). Every cycle
        // stays 3.0 s (duration + cooldown) — same DPS as before the split.
        // HitDelay sits at ~75% of the swing: that's where the procedural
        // knife/axe arc actually crosses the target, so the wound, the flinch
        // and the blood all fire ON the visible strike, not during the windup.
        private static System.Collections.Generic.Dictionary<string, GearStats> BuildDefaults()
        {
            return new System.Collections.Generic.Dictionary<string, GearStats>
            {
                [Fist] = new GearStats
                {
                    Id = Fist,
                    Damage = 0.15f,
                    HitDelaySeconds = 1.1f,       // ~75% of the 1.5 s jab
                    AttackDurationSeconds = 1.5f,
                    CooldownSeconds = 1.5f,
                    AttackSpeed = 1f,
                    MeleePriority = 0,
                    // Рукопашка: 4 удара (левый/правый кулак, левая/правая
                    // нога) — clip order in GearConfig.strikes (fist.asset).
                    // Placeholder 0.2 s замах/доигрыш/перезарядка per strike;
                    // tuned via the asset sliders (GearTuning override).
                    StrikeVariants = new[]
                    {
                        new StrikeVariant(), // Punch A
                        new StrikeVariant(), // Punch B
                        new StrikeVariant(), // Kick A
                        new StrikeVariant(), // Kick B
                    },
                },
                [Knife] = new GearStats
                {
                    Id = Knife,
                    Damage = 0.1875f,             // 0.15 × 1.25
                    HitDelaySeconds = 1.5f,       // замах 1.5 s → hit → 0.5 s follow-through
                    AttackDurationSeconds = 2.0f,
                    CooldownSeconds = 1.0f,
                    AttackSpeed = 1f,
                    MeleePriority = 10,
                    Capabilities = GearCapability.Cut | GearCapability.Butcher,
                    // §79: a knife is the WORST thing to chop with. It was tied
                    // with the axe at 1.0 before, which read as a bug — the same
                    // log split in the same time whichever blade she carried.
                    HarvestSpeedMult = 0.75f,
                },
                [Axe] = new GearStats
                {
                    Id = Axe,
                    Damage = 0.28125f,            // 0.15 × 1.875 (1.5× knife)
                    HitDelaySeconds = 1.65f,      // heavier windup
                    AttackDurationSeconds = 2.2f,
                    CooldownSeconds = 0.8f,
                    AttackSpeed = 0.8f,
                    MeleePriority = 20,
                    Capabilities = GearCapability.Cut | GearCapability.ChopWood,
                    HarvestSpeedMult = 1f,        // §79: the reference pace
                },
                // §79: железное мачете — не каменный век, а трофей. Ни скрафтить,
                // ни найти: его приносит на остров чужак (§72), и в колонию оно
                // попадает единственным способом — с его тела.
                [Machete] = new GearStats
                {
                    Id = Machete,
                    // Ровно вдвое сильнее топора при ЕГО ЖЕ таймингах: тот же
                    // замах, та же длина взмаха, та же перезарядка — разница
                    // целиком в железе, а не в темпе.
                    Damage = 0.5625f,             // 2 × axe
                    HitDelaySeconds = 1.65f,
                    AttackDurationSeconds = 2.2f,
                    CooldownSeconds = 0.8f,
                    AttackSpeed = 0.8f,
                    // Выше копья (30) — лучшее оружие на острове, и одноручное,
                    // так что BestMeleeWeapon берёт его даже с одной рукой.
                    MeleePriority = 35,
                    // Мачете делает всё, что делают нож и топор: режет, свежует,
                    // рубит дерево.
                    Capabilities = GearCapability.Cut | GearCapability.Butcher |
                                   GearCapability.ChopWood,
                    HarvestSpeedMult = 2f,        // §79: вдвое быстрее топора
                },
                [Spear] = new GearStats
                {
                    Id = Spear,
                    Damage = 0.375f,              // 0.15 × 2.5 (2× knife), two-handed
                    HitDelaySeconds = 1.5f,
                    AttackDurationSeconds = 2.0f,
                    CooldownSeconds = 1.0f,
                    AttackSpeed = 0.6f,
                    MeleePriority = 30,
                    TwoHanded = true,
                },
                [Pickaxe] = new GearStats
                {
                    Id = Pickaxe,
                    Damage = 0.24f,
                    HitDelaySeconds = 1.65f,
                    AttackDurationSeconds = 2.2f,
                    CooldownSeconds = 0.8f,
                    AttackSpeed = 0.8f,
                    MeleePriority = 8,            // a desperate swing, below the knife
                    Capabilities = GearCapability.Mine,
                },
                [Hammer] = new GearStats
                {
                    Id = Hammer,
                    Damage = 0.2f,
                    HitDelaySeconds = 1.3f,
                    AttackDurationSeconds = 1.8f,
                    CooldownSeconds = 1.2f,
                    AttackSpeed = 0.9f,
                    MeleePriority = 6,
                    Capabilities = GearCapability.Hammer,
                },
                [Saw] = new GearStats
                {
                    Id = Saw,
                    Damage = 0.21f,               // toothed edge — between hammer and pickaxe
                    HitDelaySeconds = 1.3f,
                    AttackDurationSeconds = 1.8f,
                    CooldownSeconds = 1.2f,
                    AttackSpeed = 0.9f,
                    MeleePriority = 7,            // оружие-инструмент: a desperate but real swing
                    Capabilities = GearCapability.ChopWood | GearCapability.Saw,
                    HarvestSpeedMult = 2f,        // spec 35.2: the saw fells twice as fast
                },
                // §gear-personal: the former "personal effects" live on the SAME
                // rails as every item now — droppable, losable, GOAP-fetchable.
                // No combat sheet (priority 0); their verbs are capabilities.
                [Lighter] = new GearStats
                {
                    Id = Lighter,
                    Damage = 0.15f,
                    MeleePriority = 0,
                    Capabilities = GearCapability.Ignite,
                },
                [Pot] = new GearStats
                {
                    Id = Pot,
                    Damage = 0.15f,
                    MeleePriority = 0,
                    Capabilities = GearCapability.Boil,
                },
                // CarryWater/Dressing make the ex-personal items GOAP-fetchable:
                // a girl WITHOUT a bottle walks over for a dropped one, a girl
                // with one ignores duplicates (AddsValueOver).
                [Bottle] = new GearStats
                {
                    Id = Bottle, Damage = 0.15f, MeleePriority = 0,
                    Capabilities = GearCapability.CarryWater,
                },
                // The medkit bandage as a catalogued item (stub sheet — the §44
                // dressing mechanics still run on Needs counters; migrating the
                // counters onto instances is the next step).
                [Bandage] = new GearStats
                {
                    Id = Bandage, Damage = 0.15f, MeleePriority = 0,
                    Capabilities = GearCapability.Dressing,
                },
            };
        }
    }

    /// <summary>What a piece of gear can DO — the verbs the sim gates on.
    /// Systems query capabilities, never item ids, so new gear plugs in
    /// data-only.</summary>
    [System.Flags]
    public enum GearCapability
    {
        None = 0,
        Cut = 1 << 0,       // blade work: yucca fiber, coconut piercing, cordage
        Butcher = 1 << 1,   // carcass/corpse butchering
        ChopWood = 1 << 2,  // felling trees, splitting logs
        Mine = 1 << 3,      // boulder/rock mining
        Hammer = 1 << 4,    // raising build-sites
        Ignite = 1 << 5,    // start a fire without friction (the lighter)
        Boil = 1 << 6,      // boil/cook in a vessel (the pot)
        Saw = 1 << 7,       // fine sawing (boards from a log)
        Sew = 1 << 8,       // stitching (the needle, future)
        CarryWater = 1 << 9, // holds drinking water (the bottle)
        Dressing = 1 << 10, // wound dressing (the medkit bandage)
    }

    /// <summary>One gear item's full sheet — weapon numbers, swing timing and
    /// tool capabilities. Plain mutable fields so asset overrides just assign.</summary>
    public sealed class GearStats
    {
        public string Id = string.Empty;

        // ── Weapon side ──
        // Per-hit damage before the striker's StrikeFactor and target armor.
        public float Damage = 0.15f;

        // Замах: when the damage (and blood/flinch) lands inside the attack
        // animation. Once the swing started, the hit always lands.
        public float HitDelaySeconds = 1.5f;

        // Full attack animation length (hit + follow-through).
        public float AttackDurationSeconds = 2.0f;

        // Standing recovery AFTER the animation finishes.
        public float CooldownSeconds = 1.0f;

        // Legacy cadence hint for the medium-pass assist paths (defenders,
        // predation) — 1 = every pass, lower = skip passes.
        public float AttackSpeed = 1f;

        // Harvest speedup: tree-felling duration is divided by the best mult
        // among carried gear (saw 2 = twice as fast). 1 = no bonus.
        public float HarvestSpeedMult = 1f;

        // Weapon selection: highest priority in the inventory is drawn for a
        // fight; 0 = never used as a weapon. Needs both hands if TwoHanded.
        public int MeleePriority;

        public bool TwoHanded;

        // ── Strike variants (рукопашка) ──
        // Optional per-strike timing rows: when non-empty, every swing picks
        // ONE variant (deterministic hash) and its timings replace the flat
        // HitDelay/Duration/Cooldown above for that exchange. The chosen index
        // is mirrored to presentation (NpcState.SwingStrikeIndex) so the view
        // plays the MATCHING clip — variant order == GearConfig.strikes order.
        // Null/empty = single-timing gear (knife/axe path, unchanged).
        public StrikeVariant[] StrikeVariants;

        public bool HasStrikeVariants => StrikeVariants != null && StrikeVariants.Length > 0;

        /// <summary>
        /// ⭐ ТАЙМИНГИ ТЕКУЩЕГО ЗАМАХА — один расчёт на всех.
        ///
        /// <para>
        /// Выбранный вариант удара, когда он есть у снаряжения (кулаки), иначе
        /// плоский лист. Живёт здесь, а не в боевой системе, по двум причинам:
        /// это свойство СНАРЯЖЕНИЯ, и спрашивать его должен ещё и вид — иначе
        /// он мерит замах базовой длительностью, пока сим мерит вариантом.
        /// Ровно эта пара мерок стоила §103 (и, в пространстве, §102).
        /// </para>
        /// <para>
        /// Раньше метод существовал ДВАЖДЫ, посимвольно: в MeleeSwing и в
        /// AnimalCombatSystem. Копия и есть тот класс багов, из-за которого
        /// удары человека были невидимы.
        /// </para>
        /// </summary>
        public void StrikeTimings(int strikeIndex,
            out float hitDelaySeconds, out float durationSeconds, out float cooldownSeconds)
        {
            if (HasStrikeVariants && strikeIndex >= 0 && strikeIndex < StrikeVariants.Length)
            {
                var variant = StrikeVariants[strikeIndex];
                hitDelaySeconds = variant.HitDelaySeconds;
                durationSeconds = variant.AttackDurationSeconds;
                cooldownSeconds = variant.CooldownSeconds;
                return;
            }

            hitDelaySeconds = HitDelaySeconds;
            durationSeconds = AttackDurationSeconds;
            cooldownSeconds = CooldownSeconds;
        }

        // ── Tool side ──
        public GearCapability Capabilities = GearCapability.None;

        public bool Has(GearCapability capability) => (Capabilities & capability) != 0;

        // Name variant for the JSON bridge — resolves to the enum. A NEW verb
        // is one enum member (§59: capabilities are typed, never free strings).
        public bool Has(string capabilityName) =>
            System.Enum.TryParse<GearCapability>(capabilityName, true, out var flag) &&
            flag != GearCapability.None && Has(flag);
    }

    /// <summary>One strike's timing inside a variant-based attack (fists:
    /// left/right punch, left/right kick). Same phase model as the flat gear
    /// timing: замах → hit at HitDelaySeconds → follow-through until
    /// AttackDurationSeconds → CooldownSeconds of recovery.</summary>
    public sealed class StrikeVariant
    {
        // ⭐ Значения по умолчанию — ДЛИНА НАСТОЯЩЕГО КЛИПА, а не заглушка.
        //
        // Здесь стояло 0.2 / 0.4 / 0.2 с пометкой «placeholder, tuned via the
        // asset sliders». Оттюнены они не были, а StrikeTimings предпочитает
        // вариант базовым цифрам оружия — то есть базовые 1.1/1.5/1.5 у кулака
        // были мертвы, и работали заглушки.
        //
        // Цена: при 4 тиках в секунду 0.4 с превращаются в ДВА тика, и ровно
        // столько вид получает на проигрыш полуторасекундного клипа удара.
        // Снаружи это выглядит как «начался замах и всё оборвалось» — сцена
        // абьюза на пять секунд читалась как одна.
        //
        // Теперь по умолчанию столько же, сколько у базового кулака: замах
        // 1.1 с, клип 1.5 с, восстановление 1.5 с. Ассет (fist.asset) вправе
        // задать своё — но молчание ассета больше не означает «четверть
        // секунды».
        public float HitDelaySeconds = 1.1f;
        public float AttackDurationSeconds = 1.5f;
        public float CooldownSeconds = 1.5f;
    }
}

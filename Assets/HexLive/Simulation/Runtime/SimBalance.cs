namespace HexLive.Simulation.Runtime
{
    /// <summary>
    /// The single home for the core gameplay-balance numbers that used to be
    /// <c>private const</c> literals scattered through the tick systems — the
    /// dials that decide how fast a character hungers, thirsts, tires, chills,
    /// bleeds and heals, and how much food / water / rest / clothing give back.
    ///
    /// Static (like <see cref="Spec49"/> / <see cref="Spec50"/>) so the headless
    /// soak harness can bisect and, above all, so the serialized
    /// <c>HexTuningConfig</c> asset can push live editor-slider values in at
    /// startup. Every default here EQUALS the value the code shipped with, so
    /// wiring this changed no behaviour — it only made the numbers tunable.
    ///
    /// Read live from the systems (via <c>=&gt; SimBalance.X</c> shims that kept
    /// the old const names), so a value edited at runtime takes effect the next
    /// tick.
    /// </summary>
    public static class SimBalance
    {
        // ─────────────────────────────────────────────────────────────
        // Need drift — gained/drained per SLOW tick (~150 slow ticks/day).
        // Hunger/Thirst also scale by metabolism; Thirst by the sweat factor.
        // ─────────────────────────────────────────────────────────────
        // Spec §52: the inventory/build overhaul makes life busier, so survival
        // pressure eases — halved hunger (eat ~half as often) and gentler thirst
        // so NPCs stop obsessing over food/water and get on with building.
        public static float HungerRate = 0.0055f;       // hunger gained per slow tick (§52: was 0.011)
        public static float ThirstRate = 0.010f;        // thirst gained per slow tick (§52: was 0.013)
        public static float EnergyRate = 0.007f;        // energy drained per slow tick awake (~1 bar/day)
        public static float ComfortRate = 0.01f;        // comfort drained per slow tick awake
        public static float SocialRate = 0.008f;        // social drained per slow tick
        public static float SweatThirstFactor = 0.25f;  // extra thirst per unit of overheating

        // ─────────────────────────────────────────────────────────────
        // Seek / action thresholds — when a need becomes attractive enough
        // to plan a goal for it.
        // ─────────────────────────────────────────────────────────────
        public static float GetFoodHungerThreshold = 0.35f;  // harvest food from world above this
        public static float SleepEnergyThreshold = 0.45f;    // sleep available below this energy (or after dark)
        public static float SitComfortThreshold = 0.6f;      // sit available below this comfort
        public static float SitNeedGate = 0.6f;              // ...but never on an empty stomach / dry throat above this
        public static float SleepInterruptHunger = 0.6f;     // wake a sleeper once hunger crosses this
        public static float SleepInterruptThirst = 0.6f;     // wake a sleeper once thirst crosses this
        public static float DressThermalThreshold = 0.45f;   // dress once cold discomfort crosses this (§52: was 0.35 — less fussy)
        public static float DressColdTemp = 14f;             // ...and only when effective temp is below this
        public static float DressWarmthCeiling = 0.5f;       // ...and not already bundled past this warmth

        // ─────────────────────────────────────────────────────────────
        // Starvation / dehydration — the "stuck agent" death channel.
        // ─────────────────────────────────────────────────────────────
        public static float StarvingEnterThreshold = 0.85f;  // "Starving/Dehydrated" appraisal turns on
        public static float StarvingClearThreshold = 0.60f;  // ...and off (hysteresis)
        public static float StarvingBoost = 1f;              // emergency goal-boost while starving
        public static float StarveDeathThreshold = 0.95f;    // above this, HP starts draining
        public static float StarveDamageBoth = 0.05f;        // HP/part/slow tick when starved AND parched
        public static float StarveDamageOne = 0.03f;         // HP/part/slow tick when only one is maxed

        // ─────────────────────────────────────────────────────────────
        // Natural healing / regen.
        // ─────────────────────────────────────────────────────────────
        public static float HealHungerGate = 0.6f;      // fed-heal (and blood refill) only while hunger below this
        public static float HealthRegenPerTick = 0.0030f; // HP regained per part per slow tick (~0.45/day)

        // ─────────────────────────────────────────────────────────────
        // Blood / first aid.
        // ─────────────────────────────────────────────────────────────
        public static float BleedRateFactor = 0.09f;        // blood lost/tick = (0.4 − worstPart) × this
        public static float BloodRefillPerTick = 0.005f;    // blood regained/tick while fed (×3 asleep, ×2 fireside)
        public static float BandageBloodThreshold = 0.35f;  // auto-bandage fires below this blood

        // ─────────────────────────────────────────────────────────────
        // Sickness (raw water). A bout pays into a bounded torso-damage budget.
        // ─────────────────────────────────────────────────────────────
        public static float RawWaterSickChance = 0.15f;     // chance a raw-water drink makes you sick
        public static int SicknessDurationTicks = 600;      // 🤢 icon / malaise window (~6 game hours)
        public static float SicknessDamagePerBout = 0.08f;  // torso damage added to the budget per bout
        public static float SicknessDamageBudgetCap = 0.16f; // budget can't exceed this (no ground-into-dust)
        public static float SickTorsoPerSlowTick = 0.002f;  // pace the budget pay-down
        public static float SickTorsoFloor = 0.15f;         // sickness can't grind the torso below this
        public static float SickComfortPerSlowTick = 0.006f; // feeling lousy while sick

        // ─────────────────────────────────────────────────────────────
        // Drink restore (per full bottle, dripped over the drink duration).
        // ─────────────────────────────────────────────────────────────
        public static int DrinkBottleDurationTicks = 16;   // gulp-by-gulp duration
        public static float DrinkThirstRaw = 0.7f;         // thirst removed by a raw bottle
        public static float DrinkThirstBoiled = 0.85f;     // thirst removed by a boiled bottle
        public static float DrinkComfortBoiled = 0.05f;    // boiled water is a small comfort too
        // Spec §52: a filled bottle holds several gulps — refill only when dry.
        public static int BottleCapacity = 3;              // drinks per fill
        public static int CoconutWaterCapacity = 4;        // pierced coconut gulps

        // ─────────────────────────────────────────────────────────────
        // Spec §52: slot inventory. The pack has no base cap — the body
        // carries a couple of hand slots and every worn garment adds pockets
        // (GarmentParams.Capacity). Naked ⇒ HandSlots only.
        // ─────────────────────────────────────────────────────────────
        public static int HandSlots = 2;                   // items the bare hands can hold

        // ─────────────────────────────────────────────────────────────
        // Rest / sleep restore.
        // ─────────────────────────────────────────────────────────────
        public static float GroundSleepEnergy = 0.12f;      // energy per night sleeping on the ground
        public static float GroundSitEnergy = 0.05f;        // energy per ground-sit
        public static float GroundSitComfort = 0.15f;       // comfort per ground-sit
        public static float GroundSitComfortLedge = 0.25f;  // ...more on a ledge (nice view)
        public static float BedEnergy = 0.18f;              // energy per night in a real bed
        public static float LeafBedEnergy = 0.15f;          // energy per night on a leaf mat

        // §54.11: sleep recovers ENERGY faster — a base lift so nights are shorter
        // by DEFAULT (they slept ~45% of the time), plus a fireside bonus and a bed
        // bonus. This is the payoff for building a bed and camping by the fire: you
        // recover faster ⇒ sleep less ⇒ more time on your feet to build/gather.
        // Added once per slow tick while asleep, on top of the base restore.
        public static float SleepEnergyBaseBonus = 0.010f;    // always while asleep
        public static float SleepEnergyFireBonus = 0.005f;    // + by a lit fire
        public static float SleepEnergyLeafBedBonus = 0.006f; // + on a leaf mat
        public static float SleepEnergyBasicBedBonus = 0.010f;// + on a premium bedroll
        public static float ChairComfort = 0.4f;            // comfort per sit in a chair
        public static float ChairEnergy = 0.1f;             // energy per sit in a chair

        // Spec §60: coma. Exhaustion wakes at the shared threshold; blood-loss
        // coma uses its own hysteresis so a survivor who is still low on blood
        // does not stand up, act for a few ticks, then collapse again.
        public static float ComaWakeThreshold = 0.15f;
        public static float ComaBloodEnterThreshold = 0.25f;
        public static float ComaBloodWakeThreshold = 0.35f;

        // ─────────────────────────────────────────────────────────────
        // Food restore (hunger removed when eaten).
        // ─────────────────────────────────────────────────────────────
        public static float CoconutHunger = 0.6f;      // eating a coconut
        public static float CookedMeatHunger = 0.9f;   // eating cooked meat
        // §55: pierced coconut water is drunk one small gulp at a time; the full
        // coconut quenches roughly one thirst bar across CoconutWaterCapacity uses.
        public static float CoconutThirst = 0.25f;     // thirst removed per coconut gulp

        // ─────────────────────────────────────────────────────────────
        // Temperature model. Effective temp = global + warmth×10 (+indoor/
        // −water/+fire). Comfort band is [ColdBandTemp, HotBandTemp].
        // ─────────────────────────────────────────────────────────────
        public static float ColdBandTemp = 14f;         // below this, cold pressure accrues
        public static float HotBandTemp = 22f;          // above this, heat pressure accrues
        public static float ColdPressureSlope = 0.02f;  // discomfort/tick per °C below the band (§52: was 0.03 — less cold-anxious)
        public static float HeatPressureSlope = 0.025f; // discomfort/tick per °C above the band
        public static float ThermalPressureCap = 0.12f; // max discomfort accrued per tick
        public static float ThermalComfyRecovery = 0.03f; // discomfort SHED per tick inside the band
        public static float ThermalDamageGate = 0.85f;  // |signed comfort| above this deals HP
        public static float ThermalHpHit = 0.012f;      // HP/part/tick from hypothermia/heatstroke
        public static float IndoorWarmthBonus = 4f;     // being indoors adds this to effective temp
        public static float WaterCoolBonus = 3f;        // being in water subtracts this
        public static float FireWarmthRange1 = 8f;      // campfire warmth at 1 tile
        public static float FireWarmthRange2 = 4f;      // campfire warmth at 2 tiles

        // ─────────────────────────────────────────────────────────────
        // Spec 35.4: cool-off goal stability. CoolOff used to be a move-only
        // "walk near shade" plan that completed the instant she arrived, reset
        // CurrentGoal→None, and — because ThermalDiscomfort was still above the
        // 0.35 entry edge — re-won at zero margin the very next tick. That tight
        // None→CoolOff loop was ~40% of ALL goal churn (and she never actually
        // cooled, since the plan parked her on an approach tile, not the shade).
        // Fix: a latched entry (enter 0.35 / clear 0.20) + an in-place dwell that
        // holds on a genuinely cooling tile until the clear threshold is reached.
        public static float CoolOffEnterThreshold = 0.35f; // overheat latch turns on
        public static float CoolOffClearThreshold = 0.20f;  // ...and off (hysteresis)
        public static float CoolOffSunClear = 0.45f;        // sun exposure must also fall below this to stop
        public static int CoolOffSettleTicks = 120;         // refractory after a completed cool-off (no instant re-win)

        // ─────────────────────────────────────────────────────────────
        // Sun / tan / sunburn (on uncovered parts, in open sun).
        // ─────────────────────────────────────────────────────────────
        public static float TanRate = 0.0009f;        // tan gained per (UV−0.5) per uncovered part (halved — tanning takes ~2x longer)
        public static float SunburnRate = 0.004f;     // acute redness gained (faster than tan settles)
        public static float SunExposureRate = 0.3f;   // exposure meter gained (fills toward a burn event)
        public static float SunburnBurnDamage = 0.08f; // HP torn off a part by a burn event

        // ─────────────────────────────────────────────────────────────
        // Hygiene (soft, UI-tracked).
        // ─────────────────────────────────────────────────────────────
        public static float HygieneWashGain = 0.05f;   // hygiene regained per tick at the waterside
        public static float HygieneDriftLoss = 0.0004f; // hygiene lost per tick living (~10 days clean→filthy)
        public static float ClothingDirtGain = 0.00035f;
        public static float DirtyClothingComfortLoss = 0.002f;
        public static float BatheNeedThreshold = 0.4f;
        public static int BatheDurationTicks = 100; // one in-game hour
        public static int WashClothesDurationTicks = 40;
        public static float WashClothesNeedThreshold = 0.2f;

        // ─────────────────────────────────────────────────────────────
        // Stamina / stress (soft — colour the UI, nudge rest, feed collapse).
        // ─────────────────────────────────────────────────────────────
        public static float StaminaRestGain = 0.06f;   // stamina/tick while resting
        public static float StaminaWorkDrain = 0.05f;  // stamina/tick while working
        public static float StaminaIdleGain = 0.015f;  // stamina/tick while idle
        public static float StressUpRate = 0.05f;      // stress/tick in danger/combat/pain/starvation
        public static float StressDownRate = 0.03f;    // stress shed/tick in calm

        // ─────────────────────────────────────────────────────────────
        // Climate swing.
        // ─────────────────────────────────────────────────────────────
        public static float BaseTemperature = 15.5f;      // mean global temperature
        public static float TemperatureAmplitude = 9.5f;  // ± swing (25° at 15:00, 6° at 03:00)
        public static float RainTempDrop = 3f;            // rain cools the air this much

        // ─────────────────────────────────────────────────────────────
        // Wounds.
        // ─────────────────────────────────────────────────────────────
        public static float HealPerSlowTick = 1f / 300f; // wound-close rate (~2 game days, ×2 asleep, ×0.5 moving)
        public static int MaxWounds = 36;                // painted-wound record cap
        public static int GashesPerHit = 3;              // a landed bite files this many gash records
        public static float MinSplittableDamage = 0.09f; // hits below this don't split into gashes

        // ─────────────────────────────────────────────────────────────
        // Combat — dogs & sharks.
        // ─────────────────────────────────────────────────────────────
        public static float NpcStrikePerPass = 0.15f;   // an NPC's bare strike-back baseline per landed hit
        public static int AdrenalineTicks = 80;         // fresh damage keeps her too alert to sleep
        public static float AdrenalineEnergyFloor = 0.05f;
        public static float AdrenalineMoveSpeedFactor = 1.5f;
        // Per-mob combat/behaviour (bite damage, HP, windup/cooldown, aggro,
        // roam, chase, glide, pack-raid) moved OUT of here into MobCatalog —
        // one config per mob type, tuned by its own MobConfig ScriptableObject.
        // See Content/MobCatalog.cs. NpcStrikePerPass stays: it's the human,
        // not a mob. Per-gear numbers live in Content.GearCatalog.

        // Clothing condition. Durability is also the inventory HP bar, so
        // combat wear should stay close to what the player sees.
        public static float ClothingBiteDurabilityWear = 0.065f; // per covering garment on a dog bite
        public static float ClothingPassiveWearPerDay = 0.025f;  // natural worn-cloth wear per game day

        // Melee weapon numbers (damage, замах/hit-delay, animation length,
        // cooldown, cadence) moved OUT of here into Content.GearCatalog —
        // one GearStats per item, tuned by its own GearConfig asset
        // (Resources/HexLive/Weapons/). The helpers below delegate so the
        // legacy assist/predation call sites keep their exact formulas.

        // Weapon pick is priority-driven from the gear sheets now — a new
        // weapon asset with a MeleePriority joins the selection code-free.
        public static string BestMeleeWeapon(
            System.Collections.Generic.IEnumerable<Agents.ItemInstance> items, int intactHands)
        {
            return Content.GearCatalog.BestMeleeWeapon(items, intactHands);
        }

        public static float MeleeStrikeBonus(string weaponId)
        {
            return NpcStrikePerPass > 0f
                ? Content.GearCatalog.Damage(weaponId) / NpcStrikePerPass
                : 1f;
        }

        public static float MeleeAttackSpeed(string weaponId)
        {
            return Content.GearCatalog.AttackSpeed(weaponId);
        }

        public static bool MeleeStrikeReady(int tick, int actorId, string weaponId)
        {
            var speed = MeleeAttackSpeed(weaponId);
            if (speed >= 0.999f)
            {
                return true;
            }

            const int cadenceWindow = 5;
            var strikesPerWindow = System.Math.Max(1,
                System.Math.Min(cadenceWindow, (int)System.Math.Round(speed * cadenceWindow)));
            var phase = System.Math.Abs(tick + actorId * 37) % cadenceWindow;
            return phase < strikesPerWindow;
        }

        // ─────────────────────────────────────────────────────────────
        // Spec §54 — Stranded-Deep resource loop (placeholder values; balance
        // is tuned separately and later).
        // ─────────────────────────────────────────────────────────────
        // Wood chain: a log is chopped into this many sticks (the fuel/craft
        // currency), over this many ticks (needs an axe).
        public static int LogSplitYield = 4;
        public static int LogSplitDurationTicks = 120; // ×3 slower (longer axe-chop to make sticks)
        public static int CoconutProcessDurationTicks => System.Math.Max(1, LogSplitDurationTicks / 3);

        // §54.14: the campfire is a STAGED build like the beds — a stick pile
        // (a working fire from stage 1 on), then upgrades raised in place:
        // a dense stone ring, then the roasting spit (2 planted forked posts →
        // crossbar → rope lashings). No hammer at any stage.
        // §54.12 rule: MUST equal the per-material sums of
        // BuildSiteMath.CampfireStages (which mirror the campfire_final
        // prefab's staged piece groups "1".."5").
        public static int CampfireBillSticks = 12; // 9 pile + 2 posts + 1 crossbar
        public static int CampfireBillStones = 18; // the dense ring
        public static int CampfireBillRope = 2;    // one lashing per post joint

        // §54.14 (r2): the stages are FUNCTIONAL, not only visual.
        // Stage 1 (stick pile) = a working fire: warmth, comfort, crafting.
        // Stage 2 (stone ring) insulates the pit — fuel burns at this fraction
        // of the normal rate (0.5 = a load of wood lasts twice as long).
        public static float CampfireRingBurnMultiplier = 0.5f;
        // Stage 3 (the roasting spit) unlocks cooking: raw meat is HUNG on the
        // spit and roasts over a lit fire for this long (100 ticks = 1 game
        // hour), then turns into cooked meat that stays hanging until taken.
        public static int MeatRoastDurationTicks = 200;
        // How many chunks hang on the crossbar at once.
        public static int CampfireSpitCapacity = 3;

        // §54.14 (r2): loose sticks scattered around the marked hearth spot at
        // world start. The site itself is EMPTY (the colony piles the fire with
        // its own hands), but the material lies within reach — without it the
        // cold start deadlocks: sticks otherwise come from splitting logs
        // (axe) and the axe is crafted at the fire that doesn't exist yet;
        // deadfall sheds too slowly and the knife chain eats every stick
        // (probe: 0-4/9 piled in 2 days, 3 seeds). 9 pile + knife + first fuel.
        public static int CampfireStarterSticks = 14;

        // §54.14 (r2)/§45 r5: friction-lighting stays available this long
        // after the girl was last below the freezing threshold (-0.35 TC) —
        // the walk from wherever the cold caught her to the pit must not
        // revoke the hand-drill (4 game hours; nights are long, dawns slow).
        public static int FrictionLightGraceTicks = 400;

        // §54.2: beds are raised at a progressive build-site (haul each piece, it
        // grows piece by piece, a hammer finishes it) — NOT an atomic craft. The
        // bill = EXACTLY the assembled prefab's piece counts, so every delivery
        // reveals one piece and the finished bed is whole. Re-count the prefab
        // children (bed_leaf_final / bed_basic_final) to retune.
        //
        // bed.leaf (early survival mat): a leaf bundle, stick ribs and a couple
        // of rope lashings. The assembled prefab has more visual pieces, but the
        // sim bill is grouped into buildable bundles; counting every blade/lashing
        // turned the first bed into a multi-day project that missed the survival
        // window entirely.
        // §54.12: MUST equal the per-material sums of BuildSiteMath.BedLeafStages
        // (which mirror the bed_leaf_final prefab's staged piece groups "1".."4").
        public static int BedLeafBillLeaves = 46;
        public static int BedLeafBillSticks = 8;
        public static int BedLeafBillRope = 8;

        // bed.basic (premium bedroll, bed_basic_final): 4 log side-rails (two per
        // side) + stick cross-slats + rope lashings + a full leaf mattress.
        public static int BedBasicBillLogs = 4;
        public static int BedBasicBillSticks = 5;
        public static int BedBasicBillRope = 10;
        public static int BedBasicBillLeaves = 50;
        // §54.12: the SECOND bed tier. Once every girl has a leaf mat, the
        // colony starts building premium bedrolls (bed.basic) from scratch —
        // each at its OWN fireside site, one at a time, until every girl has
        // one. NOT an upgrade: the leaf mats stay untouched.
        public static bool BedBasicEnabled = true;

        // §35.5B: the drying rack is a staged fireside build-site like the beds
        // (two planted uprights → two rails → four lashings), not an atomic
        // craft. MUST equal the per-material sums of
        // BuildSiteMath.DryingRackStages (which mirror the drying_rack_final
        // prefab's staged piece groups "1".."3").
        public static int RackBillSticks = 4;
        public static int RackBillRope = 4;
        // §35.5B: how many garments hang on the rack at once (one per hanger
        // slot on the assembled prefab's rails).
        public static int RackCapacity = 8;

        // §54.2: how many fronds each palm's crown is built from AND how many
        // loose leaves drop when that crown is chopped — the SAME number per size
        // (the visual crown = the yield). Big palm has a fuller crown / bigger
        // yield than the small one. Read by PalmCrownFactory (look) and the crown
        // defs' Process yields (drop). Tune to move both look and yield together.
        // ×3 vs the old 14: beds now use ~3× more leaves, so one felled palm drops
        // roughly enough for a bed (was 14 → ~3 palms per bed). Approximate balance,
        // not a per-frond count. (SmallPalm retired — only big palms spawn now.)
        public static int BigPalmCrownLeaves = 42;
        public static int SmallPalmCrownLeaves = 8;

        // §54.13 build-window tuning. A 10-day 6-seed soak showed the bed site
        // starving: the peacetime window was open only ~1-36% of npc-ticks.
        // Two gates ate it: (1) thirst/hunger >= 0.55 — the girls EQUILIBRATE
        // around 0.5-0.6 (drinking only starts at 0.35 and must win the
        // auction), so the old gate tracked the colony's resting state, not an
        // emergency; (2) ANY danger memory — a single wolf sighting is
        // remembered 2400 ticks (a full day) and froze construction colony-wide.
        // Build now pauses at the same 0.65 the errand-canceling lifeThreatened
        // check uses, and only for FRESH danger (seen within the last
        // BuildDangerFreshTicks), not day-old ghosts.
        public static float BuildNeedGate = 0.65f;
        public static int BuildDangerFreshTicks = 600;

        // Fiber → rope / cloth (crafted at the fire); knife = sticks + stone.
        // Enough cordage that a couple of cut yucca can supply the first bed's
        // lashings without exhausting the island's entire rope economy.
        public static int FiberPerPlant = 4;    // fiber yielded per fibrous plant
        public static int RopeFiberCost = 1;
        public static int ClothFiberCost = 4;
        public static int KnifeStickCost = 1;
        public static int KnifeStoneCost = 1;

        // Butchering & carcasses. A slain animal leaves a carcass that rots
        // after this many ticks; knifing it takes ButcherDurationTicks and
        // yields this much meat + one hide.
        public static int CarcassDecayTicks = 2400;
        public static int ButcherDurationTicks = 30;
        public static int CarcassMeatYield = 2;

        // Meat spoilage (ground items): raw rots fast, cooked lasts; cooking is
        // effectively preservation. Ticks from when the item lands on the ground.
        public static int MeatRawSpoilTicks = 1800;
        public static int MeatCookedSpoilTicks = 4800;

        // Cannibalism: butchering a housemate's corpse is allowed but costs
        // comfort, and NPCs won't do it unless genuinely starving.
        public static bool CannibalismEnabled = true;
        public static float CannibalismComfortPenalty = 0.25f;
        public static float CannibalizeHungerGate = 0.85f;

        // §56 Predation cannibalism: the absolute last resort — a low-compassion
        // survivor, starving with NO other food (not even an existing corpse to
        // butcher), may KILL the weakest housemate and eat them. Ranked below
        // every softer food source; heavy consequences. PredationEnabled=false
        // restores pre-§56 behaviour byte-for-byte.
        public static bool PredationEnabled = true;
        // Strictly above CannibalizeHungerGate (0.85) so an existing corpse is
        // always butchered before anyone is killed.
        public static float PredationHungerGate = 0.95f;
        // Only survivors with CompassionTrait at/below this ceiling will consider
        // it (trait spread is 0.35..1.0, so a minority).
        public static float PredationCompassionCeiling = 0.45f;
        // Base goal score — kept below GetFood/Hunt/Butcher so it never outranks
        // a real food source even before the "no other food" gate.
        public static float PredationBaseScore = 0.05f;
        // Damage per adjacent strike, aimed at a vital. Deliberately DECISIVE
        // (~6x a dog's 0.06 bite): this is a desperate knife-killing, and in a
        // famine the victim is itself starving fast — a weak strike loses the
        // race to starvation and never lands the kill (soak-observed). Still
        // scaled down by a starved attacker's StrikeFactor() and by armor.
        public static float PredationStrikePerPass = 0.35f;
        // Heavy comfort hit on the killer (vs 0.25 for butchering a found body).
        public static float PredationComfortPenalty = 0.6f;
        // Sharp relationship collapse: each witness's Affinity toward the killer.
        public static float PredationWitnessAffinityLoss = 0.8f;
        // Self-defence: a preyed-on victim fights back (real strikes at the
        // attacker, its weapon/StrikeFactor — reusing NpcStrikePerPass), and
        // BOLTS for an indoor refuge once its Health drops below this. So an
        // armed, healthy victim can wound, kill, or outrun a starved predator.
        public static float PredationFleeHealth = 0.6f;
    }
}

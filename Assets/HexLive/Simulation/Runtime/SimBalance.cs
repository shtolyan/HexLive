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
        // Need drift — gained/drained per SLOW tick (16 ticks = 4 real seconds).
        // Tuned in units of 150 slow ticks = 2400 ticks = 10 REAL minutes. That
        // used to be one game day; the clock is now stretched 10x, so a visual
        // day holds ten of these — the real-time pace is unchanged.
        // Hunger/Thirst also scale by metabolism; Thirst by the sweat factor.
        // ─────────────────────────────────────────────────────────────
        // Spec §52: the inventory/build overhaul makes life busier, so survival
        // pressure eases — halved hunger (eat ~half as often) and gentler thirst
        // so NPCs stop obsessing over food/water and get on with building.
        // §53.7 easing: aid now spends real supplies, so the metabolic clock is
        // halved again — the tuned rates (0.0037 / 0.008) go to HALF, and the
        // code defaults are aligned onto the same numbers so asset, SimData, SO
        // and fallback all agree.
        public static float HungerRate = 0.00185f;      // hunger gained per slow tick (§53.7: was 0.0037 tuned / 0.0055 default)
        public static float ThirstRate = 0.004f;        // thirst gained per slow tick (§53.7: was 0.008 tuned / 0.010 default)
        public static float EnergyRate = 0.005f;        // energy drained per slow tick awake (~1 bar per 200 slow ticks / 13 real min; was 0.007 — softened to cut exhaustion comas)
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
        public static float DressWarmthGainMin = 0.05f;      // §52.7: ...and only if a reachable garment ACTUALLY raises warmth by ≥ this (clamp-aware) — never re-wear the same/worse shirt (~+0.5°C; +0.1 warmth = +1°C)

        // ─────────────────────────────────────────────────────────────
        // Starvation / dehydration — the "stuck agent" death channel.
        // ─────────────────────────────────────────────────────────────
        public static float StarvingEnterThreshold = 0.85f;  // "Starving/Dehydrated" appraisal turns on
        public static float StarvingClearThreshold = 0.60f;  // ...and off (hysteresis)
        public static float StarvingBoost = 1f;              // emergency goal-boost while starving
        public static float StarveDeathThreshold = 0.95f;    // above this, HP starts draining
        public static float StarveDamageBoth = 0.01f;        // HP/part/slow tick when starved AND parched (÷5 — hunger/thirst was draining HP too fast)
        public static float StarveDamageOne = 0.006f;        // HP/part/slow tick when only one is maxed (÷5 — hunger/thirst was draining HP too fast)

        // ─────────────────────────────────────────────────────────────
        // Natural healing / regen.
        // ─────────────────────────────────────────────────────────────
        public static float HealHungerGate = 0.6f;      // fed-heal (and blood refill) only while hunger below this
        public static float HealthRegenPerTick = 0.0030f; // HP regained per part per slow tick (~0.45 per 150 slow ticks / 10 real min)

        // ─────────────────────────────────────────────────────────────
        // Blood / first aid.
        // ─────────────────────────────────────────────────────────────
        public static float BleedRateFactor = 0.06f;        // blood lost/tick = (0.4 − worstPart) × this
        public static float BloodRefillPerTick = 0.005f;    // blood regained/tick while fed (×3 asleep, ×2 fireside)
        public static float BandageBloodThreshold = 0.35f;  // auto-bandage fires below this blood

        // ─────────────────────────────────────────────────────────────
        // Sickness (raw water). A bout pays into a bounded torso-damage budget.
        // ─────────────────────────────────────────────────────────────
        public static float RawWaterSickChance = 0.15f;     // chance a raw-water drink makes you sick
        public static int SicknessDurationTicks = 600;      // 🤢 icon / malaise window (600 ticks / 2.5 real min)
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
        // carries a couple of hand slots + a small always-there base load, and
        // every worn garment adds pockets (GarmentParams.Capacity).
        // Naked ⇒ HandSlots + BaseCarrySlots.
        // ─────────────────────────────────────────────────────────────
        public static int HandSlots = 2;                   // items the bare hands can hold
        // Always-there carry beyond the hands (belt/tuck/cradle). Naked girl =
        // HandSlots + this. Raised the naked cap 2→4 so she can haul a leaf/rope
        // bundle for the bed instead of deadlocking on a full bottle+tool pair.
        public static int BaseCarrySlots = 2;

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
        public static float SleepEnergyBaseBonus = 0.026f;    // always while asleep (was 0.020 — faster recovery to cut exhaustion comas)
        public static float SleepEnergyFireBonus = 0.005f;    // + by a lit fire
        public static float SleepEnergyLeafBedBonus = 0.006f; // + on a leaf mat
        public static float SleepEnergyBasicBedBonus = 0.010f;// + on a premium bedroll
        public static float ChairComfort = 0.4f;            // comfort per sit in a chair
        public static float ChairEnergy = 0.1f;             // energy per sit in a chair

        // Spec §60 r2 (coma rework): energy 0 is a DEAD-TIRED SLEEP, not a
        // death-lookalike — she crashes where she stands and sleeps it off
        // like a normal ground sleeper (a landed wound jolts her awake).
        // Waking at the old 0.15 line just re-drained to 0 within hours and
        // the day became a chain of micro-collapses (100-200 per 25-day
        // soak); sleeping through to a properly rested line turns the pit
        // into ONE long nap.
        public static float ExhaustedSleepWakeEnergy = 0.45f;
        // Blood-loss unconsciousness keeps its own hysteresis so a survivor
        // who is still low on blood does not stand up, act for a few ticks,
        // then drop again. (ComaWakeThreshold retired with the rework.)
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
        // A body actively warmed by a strong heat source (fire ring / indoors)
        // thaws ~8× faster: standing by the fire should CLEAR the night's cold
        // in a few ticks, not slowly bleed it off. Only sheds — never adds.
        public static float FireThawRecovery = 0.25f;   // discomfort SHED/tick when comfy AND fire/indoor is warming
        public static float ThermalDamageGate = 0.85f;  // |signed comfort| above this deals HP
        public static float ThermalHpHit = 0.012f;      // HP/part/tick from hypothermia/heatstroke
        // §82 r2: ниже этого порога жара и холод ВИТАЛЬНУЮ часть не доламывают.
        // Перегрев бьёт все семь частей каждый медленный тик, поэтому для
        // человека в глухой броне без порога это гарантированная смерть.
        public static float ThermalVitalFloor = 0.35f;
        public static float IndoorWarmthBonus = 4f;     // being indoors adds this to effective temp
        public static float WaterCoolBonus = 3f;        // being in water subtracts this
        // Fire is a STRONG heat source — huddling by it must reach the comfy
        // band [16,22] even on the coldest rainy night (floor ~3°, naked).
        // fireRelief is clamped to (HotBandTemp − baseTemp) below, so this can
        // never overheat: it tops out AT 22° and a dressed body caps sooner.
        public static float FireWarmthRange1 = 18f;     // campfire warmth at 1 tile (→ ~22° from 3° floor)
        public static float FireWarmthRange2 = 11f;     // campfire warmth at 2 tiles

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
        public static float TanRate = 0.0009f;        // tan gained per (UV−0.5) per uncovered part. DEFAULT ONLY — tune live via CharacterBalance.asset (tanRate); BalanceTuning mirrors it over this at boot.
        public static float TanStrength = 1f;          // overall tan DARKNESS (presentation-only): NpcActorView scales the tan tint toward bare skin by this. 1 = full look, lower = paler/less dark. Tune live via CharacterBalance.asset (tanStrength).
        public static float SunburnRate = 0.004f;     // acute redness gained (faster than tan settles)
        public static float SunExposureRate = 0.3f;   // exposure meter gained (fills toward a burn event)
        public static float SunburnBurnDamage = 0.08f; // HP torn off a part by a burn event
        // §82: ниже этого порога солнце ВИТАЛЬНУЮ часть не доламывает.
        // Солнечный удар доводит до беспамятства, но не отрывает голову: без
        // порога забронированный целиком человек умирает от загара за треть
        // дня — у него открыта ровно одна часть, и все удары приходят в неё.
        public static float SunburnVitalFloor = 0.45f;

        // ─────────────────────────────────────────────────────────────
        // Hygiene (soft, UI-tracked).
        // ─────────────────────────────────────────────────────────────
        public static float HygieneWashGain = 0.05f;   // hygiene regained per tick at the waterside
        public static float HygieneDriftLoss = 0.0004f; // hygiene lost per slow tick living (~2500 slow ticks / 2.8 real hours clean→filthy)
        public static float ClothingDirtGain = 0.00035f;
        public static float DirtyClothingComfortLoss = 0.002f;
        public static float BatheNeedThreshold = 0.4f;
        public static int BatheDurationTicks = 100; // 100 ticks / 25 real seconds
        // §40.6 r2 (laundry-in-hand): 80 ticks — the piece is doffed off the
        // body / picked up off the shore into the hand and scrubbed there.
        public static int WashClothesDurationTicks = 80;
        public static float WashClothesNeedThreshold = 0.2f;

        // Laundry audit (Jul 2026): worn dirt never reached the wash chain —
        // DirtyGarmentWashNeed only scans garments ALREADY lying on a bathing
        // tile, so clothes were washed just 9-16 times per 40 days and worn
        // pieces sat at dirt 0.7-1.0 forever. Dirty WORN clothing also pulls
        // Bathe (body hygiene chain): batheNeed takes
        // max(1-Hygiene, worstWornDirt × this weight). Since §40.6 r2 the
        // wash chain reads worn dirt DIRECTLY (weight 0.75 in the auction),
        // so it usually outbids this pull and washes the piece in hand.
        public static float BatheWornDirtWeight = 0.9f;

        // Drying audit (Jul 2026): DryClothes gated on wetness > 0.5 — after a
        // wash (wet 1.0) passive on-body drying closes that window in ~0.2
        // days, and the old 0.15+0.4×wet score lost the auction to Sit, so
        // rack-hanging fired 0 times in 40-day soaks. Wider window via this
        // threshold (score raised in DecisionSystem alongside).
        public static float DryClothesWetThreshold = 0.35f;

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
        public static float HealPerSlowTick = 1f / 300f; // wound-close rate (300 slow ticks / 20 real min, ×2 asleep, ×0.5 moving)
        public static int MaxWounds = 36;                // painted-wound record cap
        public static int GashesPerHit = 3;              // a landed bite files this many gash records
        public static float MinSplittableDamage = 0.09f; // hits below this don't split into gashes

        // ─────────────────────────────────────────────────────────────
        // Combat — dogs & sharks.
        // ─────────────────────────────────────────────────────────────
        public static float NpcStrikePerPass = 0.15f;   // an NPC's bare strike-back baseline per landed hit

        // §71: the colony's global walking pace, multiplied into the one place
        // distance-per-tick is computed (MovementSystem). The per-NPC
        // npc.MoveSpeed field has always been a hardcoded 1 that nothing ever
        // assigns, so this knob — not that field — is how the pace is tuned.
        // NOTE: the hex hop (HexHopTuning.HopSeconds) is on a wall clock and
        // does NOT scale with this, so ledge crossings keep their duration.
        public static float BaseMoveSpeedFactor = 1.2f; // §71: 1.5 was a shade brisk, eased 20%

        // §71: how fast she TURNS, as a multiple of npc.TurnSpeed (90°/s, a
        // field nothing ever assigns). At 2.4 that is 216°/s = 54° per tick,
        // so the 60° minimum bend of the hex lattice resolves in ONE tick.
        public static float BaseTurnSpeedFactor = 2.4f;

        // §71 turning: a turn used to be a hard STOP — any residual facing
        // error over 30° skipped translation entirely, and since the lattice's
        // smallest possible bend is 60° while the trip point sat at 52.5°,
        // EVERY corner cost a 3-tick dead stop. Now only a near-reversal stops
        // her; everything gentler is taken as a curve, with a speed penalty
        // that fades to nothing below the deadzone.
        public static float TurnFreezeAngle = 100f;   // above this she plants and pivots
        public static float TurnFreeAngle = 40f;      // below this, no penalty at all
        public static float TurnMinSpeedFactor = 0.6f; // slowest a graded turn gets
        public static float PostTurnPauseSeconds = 0.15f; // only after a planted pivot

        // §71 RUNNING. Gait is a decision, not a side effect of speed: she
        // walks by default and runs only for a REASON, so a running figure
        // always means something happened. Each reason carries its own pace;
        // they do not compound (MovementSystem takes the largest).
        public static float FleeRunSpeedFactor = 2.25f;  // running for her life
        public static float NeedRunSpeedFactor = 1.6f;   // hurrying to food/water when desperate

        // §71 BREATH — the sprint reserve (NPCNeeds.Breath). Drained per tick
        // while running, refilled while walking, faster while standing still.
        // Hysteresis on purpose: once spent she must recover to the re-arm line
        // before she may run again, so she cannot flicker between gaits.
        public static float BreathDrainPerTick = 0.0045f;   // ~55 s of running from full
        public static float BreathWalkRecoverPerTick = 0.0022f; // ~110 s walking it back
        public static float BreathIdleRecoverPerTick = 0.006f;  // standing catches it fast
        public static float BreathReArm = 0.35f;            // must climb back to this to run again

        public static int AdrenalineTicks = 80;         // fresh damage keeps her too alert to sleep
        public static float AdrenalineEnergyFloor = 0.05f;
        // §71: the adrenaline sprint, raised 1.5x (was 1.5, so 2.25).
        public static float AdrenalineMoveSpeedFactor = 2.25f;
        // Per-mob combat/behaviour (bite damage, HP, windup/cooldown, aggro,
        // roam, chase, glide, pack-raid) moved OUT of here into MobCatalog —
        // one config per mob type, tuned by its own MobConfig ScriptableObject.
        // See Content/MobCatalog.cs. NpcStrikePerPass stays: it's the human,
        // not a mob. Per-gear numbers live in Content.GearCatalog.

        // Clothing condition. Durability is also the inventory HP bar, so
        // combat wear should stay close to what the player sees.
        // Per covering garment on a dog bite. Cut 5x (was 0.013) — clothes
        // were shredding faster than the surf could replace them.
        public static float ClothingBiteDurabilityWear = 0.0026f;
        // Natural worn-cloth wear per 150 slow ticks (10 real minutes). The
        // name says "day" because that used to be a day; MoistureSystem still
        // divides by 150, which is what keeps the real-time wear rate fixed.
        // Cut 5x (was 0.005) alongside the bite wear.
        public static float ClothingPassiveWearPerDay = 0.001f;

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
        // Spec 40.15 — the escape raft gate (balance audit, Jul 2026).
        // ─────────────────────────────────────────────────────────────
        // BuildRaft used to demand hunger/thirst < 0.55 and an EMPTY danger
        // memory. The coconut economy equilibrates needs at ~0.55-0.7 and §62
        // spotting restamps danger daily, so surviving colonies sat at raft
        // 0/10 for 40 days (gate fully open 0.0-2.8% of NPC-slow-ticks,
        // danger alone blocking 61-95%). The gate now tolerates moderate
        // needs and only fears danger remembered NEAR the girl herself —
        // a wolf seen across the island must not cancel the coast run.
        public static float RaftNeedGate = 0.7f;
        public static int RaftDangerRadiusTiles = 4;

        // TEMPORARY kill-switch (Jul 2026): the §40.15 escape-raft mechanic is
        // being reworked, so it is turned OFF for now — NPCs must not build it
        // (or hoard wood for it) at all while it's observed. Flipping this back
        // to true restores the old endgame verbatim: nothing was removed, the
        // three raft motivations (BuildRaft goal, the GatherWood raft demand,
        // and whole-log hoarding) are simply gated on this flag. Kept as a
        // `static readonly` code switch, not a tunable knob, so the coverage
        // gate skips it and it stays out of the balance assets / simdata.json.
        public static readonly bool RaftEnabled = false;

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
        // How many chunks hang on the crossbar at once (= the 6 fixed skewer
        // slots the CampfireSpitMeat view lays out along the bar).
        public static int CampfireSpitCapacity = 6;

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
        // revoke the hand-drill (400 ticks / 100 real seconds).
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

        // §54.15: the water collector is a staged build-site like the rack —
        // 4 planted uprights → the stone stand → the top rim → the corner
        // lashings → the leaf funnel. MUST equal the per-material sums of
        // BuildSiteMath.WaterCollectorStages (which mirror the
        // water_collector_final prefab's staged piece groups "1".."5").
        public static int WaterCollectorBillSticks = 8;  // 4 uprights + 4 rim
        public static int WaterCollectorBillStones = 5;  // the vessel stand
        public static int WaterCollectorBillRope = 8;    // two lashings per corner
        public static int WaterCollectorBillLeaves = 11; // the funnel

        // §54.15: how long steady rain takes to fill the parked bottle to the
        // brim (600 ticks / 2.5 real minutes). The vessel's
        // ResourceAmount holds fill progress 0..1; a full bottle converts to
        // BottleCapacity gulps of clean rain water on TakeVessel.
        public static int WaterCollectorFillTicks = 600;

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

        // §64.9 — DON'T FELL THE LAST PALM. A palm is not lumber, it is the
        // colony's WATER: coconuts are the island's drinking supply and a felled
        // palm never regrows. Building may take the surplus of the grove, never
        // its seed stock. Measured (30-day soak, seed 12345, before this gate):
        // the grove sat steady at 7 palms / 7 coconuts for a fortnight, then the
        // bed's log stage took all seven between day 16 and 18 — coconuts hit 0
        // on day 20 and the whole colony died of thirst on day 21, at Thermal
        // ±0.0 with GetWater burning 7.9% of every waking tick.
        // Counted over what she can actually see, like every other build gate.
        // The genuine no-wood-at-all fire emergency is exempt (freezing kills
        // sooner than thirst, and that branch already demands a dead fire AND no
        // reachable wood of any kind).
        public static int PalmGroveReserve = 4;

        // §80: радиус правила «сначала подбери с земли, потом добывай ещё».
        // Меряется и от самого NPC, и от стройки, ради которой он добывает.
        // Общеостровной меры тут быть не может: восприятие помнит предметы по
        // всей карте, так что «где-то лежит камень» истинно почти всегда и
        // заморозило бы добычу навсегда.
        public static int PickUpFirstRadiusTiles = 4;

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
        // §54.16: raw was 1800 — SHORTER than the 2400-tick danger memory that
        // every kill site carries, so meat dropped by a slain beast was
        // guaranteed to rot before anyone was allowed to shop there. 2600 keeps
        // the chunk alive past the mark even when the fear isn't cleared.
        public static int MeatRawSpoilTicks = 2600;
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

        // Spec 29C.4A cornered-fight valve. A flee is only worth it if it puts
        // ground between the girl and the mob; if a dog stays in MELEE with her
        // for this many ticks after she started running (TicksPerSecond = 4, so
        // ~4 s), the escape has failed — she turns and fights to the death
        // rather than be bitten for free until a limb tears off and she goes
        // prone (seed 351193917: Молди fled a dog that pinned her at Dist=0 for
        // 130+ ticks, never once striking back, LegR severed at t1095 → prone →
        // dead). Generous enough that a genuine door-dash a step from sanctuary
        // still completes; far short of the ~130-tick maul-to-amputation window.
        public static int FleeStallTicks = 16;
        // Once she commits to the stand, hold the commitment this long past the
        // last melee tick so the flee assessment can't bounce her straight back
        // into a run. Re-armed each engaged tick, so it only lapses once the mob
        // is dead or has broken contact — then normal life (incl. a fresh flee)
        // resumes.
        public static int FightCommitGraceTicks = 40;

        // Spec 29C.4A standoff-release valve. A HEALTHY girl squared up against
        // a tile-adjacent mob that has failed to reach melee for this many
        // CONTINUOUS ticks (no bite, no strike, ~10 s at 1x) stops honouring
        // the stance latch — the mob demonstrably cannot close the junction
        // gap, so freezing her "fighting" only starves the player of an NPC
        // (seed 521091321 day 30: Марта/Молли stood "дерётся" against a wolf
        // that never attacked). Real melee contact resets the window, so a
        // genuine run-down (dog biting between cooldowns) never trips it.
        public static int StandoffReleaseTicks = 40;

        // How long a released girl ignores the square-up latch — enough to
        // walk away, drink, re-plan. If the mob still hangs around unreached
        // after the grace, the stance re-arms and the valve re-judges.
        public static int StandoffReleaseGraceTicks = 240;
    }
}

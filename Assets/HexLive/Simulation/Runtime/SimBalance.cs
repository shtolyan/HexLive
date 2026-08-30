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
        // ⭐ §139.1: бюджет бодрствования — ДЕВЯТЬ игровых часов с полной шкалы.
        // Сутки = 24 000 тиков = 1500 медленных, значит 9 ч = 562 медленных
        // тика, откуда 1/562 = 0.0018. Было 0.0045 (живое значение ассета):
        // полной шкалы хватало на 3.5 часа, при том что сон в кровати набирает
        // её за 4 — «поспала четыре часа, через три валится». Отсюда и
        // вырубания, и смерти: §49.10 замер показал минимум энергии 0.00 у всех
        // троих и 31 обморок за пять суток. Код/конфиг/ассет/simdata тут
        // разъехались на три разных числа (0.005 / 0.007 / 0.0045) — сведены.
        public static float EnergyRate = 0.0018f;       // energy drained per slow tick awake (9 in-game hours per full bar)
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
        public static float SleepInterruptHunger = 0.7f;     // wake a sleeper once hunger crosses this (= food bar below 30%)
        public static float SleepInterruptThirst = 0.7f;     // wake a sleeper once thirst crosses this (= water bar below 30%)
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
        public static float StarveDamageBoth = 0.0006f;      // HP/part/slow tick when starved AND parched (~1.3 days from last sip to zero)
        public static float StarveDamageOne = 0.00036f;      // HP/part/slow tick when only one is maxed (design: 2 full days with no water at all = 100→0; ramp to 0.95 takes ~238 slow ticks, damage window ~2762 more, day = 1500 slow ticks)

        // ─────────────────────────────────────────────────────────────
        // Natural healing / regen.
        // ─────────────────────────────────────────────────────────────
        public static float HealHungerGate = 0.6f;      // fed-heal (and blood refill) only while hunger below this
        // §118.8: healing is measured in DAYS now — base 1/3000 of the bar per
        // slow tick (2 game days awake, 1 day on bed.basic via the ×2 rest
        // multiplier applied in NeedsDecaySystem). Was 0.0030 (~5 game hours
        // for a full part), which read as "couple of real hours and she's new".
        public static float HealthRegenPerTick = 1f / 3000f; // HP per part per slow tick, × rest multiplier

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
        // Drink restore. Bug #305 (вердикт игрока): глоток — это МИЛЛИЛИТРЫ,
        // не проценты. Литровая бутылка = 10 глотков по ~100 мл; жажда с
        // глотка снимается пропорционально (суммарно на бутылку — как прежние
        // 3 «больших» глотка: 10×0.21 ≈ 3×0.7). Жаждущая пьёт несколько
        // глотков подряд тем же next-sip циклом, что и кокос.
        // ─────────────────────────────────────────────────────────────
        public static int DrinkBottleDurationTicks = 16;   // gulp-by-gulp duration
        public static float DrinkThirstRaw = 0.21f;        // thirst removed by one 100 ml raw sip
        public static float DrinkThirstBoiled = 0.26f;     // thirst removed by one 100 ml boiled sip
        public static float DrinkComfortBoiled = 0.015f;   // boiled water is a small comfort too
        // Spec §52 + bug #305: a filled 1 L bottle holds ten 100 ml sips —
        // refill only when dry.
        public static int BottleCapacity = 10;             // sips per fill
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
        // §54.11 r2 / bug #150: timed interactions own no Energy knobs at all.
        // Recovery is owned by the slow-tick bonuses below, where its game-hour
        // duration is explicit and identical for voluntary sleep and coma.
        public static float GroundSitComfort = 0.15f;       // comfort per ground-sit
        public static float GroundSitComfortLedge = 0.25f;  // ...more on a ledge (nice view)

        // §54.11 r2: one slow tick is 16 game ticks; a game hour is 1000.
        // +0.002/tick therefore fills 0→1 in 500 slow ticks = 8000 game
        // ticks = 8 h on the ground. A real bed adds another +0.002, so it
        // fills in 250 slow ticks = 4000 game ticks = 4 h. Sleeping no longer
        // pays EnergyRate at the same time, and the old interaction/fire
        // channels are zero, so these are NET durations rather than an
        // approximation produced by several stacked systems.
        public static float SleepEnergyBaseBonus = 0.002f;
        public static float SleepEnergyFireBonus = 0f;
        public static float SleepEnergyBasicBedBonus = 0.002f;
        public static float ChairComfort = 0.4f;            // comfort per sit in a chair

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
        // §60.7: без сознания (кома/обморок/умирание) В ГЛУБОКОЙ ВОДЕ — тонет:
        // столько тиков непрерывно, и тело умирает, если не очнулась и её не
        // вытащили на сушу. Считает NeedsDecaySystem.TickDrowning.
        public static int DrownDeathTicks = 1000;

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
        // Per-slow-tick rates, so they scale with DayLengthTicks: when the day
        // went 2400 → 24000 ticks, the sun window got 10× more ticks and all
        // sun-side rates below were cut ×10 to keep the same per-DAY pacing
        // (~10 sunny days to a full tan, ~2-3 burn events/day half-dressed).
        // TanFadeRate is the inverse contract: at the current 1500 slow ticks
        // per game day, 1 / 6000 removes a full tan in four no-UV days.
        // ─────────────────────────────────────────────────────────────
        public static float TanRate = 0.00009f;       // tan gained per (UV−0.5) per uncovered part. DEFAULT ONLY — tune live via CharacterBalance.asset (tanRate); BalanceTuning mirrors it over this at boot.
        public static float TanFadeRate = 0.00016668f; // tan lost per no-tanning slow tick; float-safe clamp reaches zero within 6000 ticks / four game days
        public static float TanStrength = 1f;          // overall tan DARKNESS (presentation-only): NpcActorView scales the tan tint toward bare skin by this. 1 = full look, lower = paler/less dark. Tune live via CharacterBalance.asset (tanStrength).
        public static float SunburnRate = 0.0012f;    // acute redness reaches a visible peak within one open-sun day
        public static float SunExposureRate = 0.04f;  // exposure meter gained (fills toward a burn event)
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
        // Баг #9: кровь пачкает. Гигиена, теряемая на единицу ОБЩЕГО HP,
        // списанного уроном: 3 ⇒ суммарная треть максимума здоровья обнуляет
        // гигиену — надо полностью помыться. Через неё же прячутся кровяные
        // капли на коже (баг #10): помылась — капли исчезли, раны остались.
        public static float HygieneDamageLoss = 3f;
        // §40.8-H r10: кровяная подложка = грязь. Прирост BloodSoil зоны на
        // единицу режущего урона: 2 ⇒ мачете (~0.48 cut) заливает торс почти
        // до потолка, топор (~0.2) — до половины, укус волка (~0.08) — ~0.15
        // за укус, царапина 0.01 остаётся ниже onset-порога спеклов (0.05).
        public static float BloodSoilPerCut = 2f;
        // Смывание крови водой, за тик на водяном тайле (= HygieneWashGain,
        // чтобы кровь и общая грязь сходили синхронно). На суше кровь не
        // дрейфует — засохла и держится до мытья, как грязь на одежде.
        public static float BloodSoilWashPerTick = 0.05f;
        public static float ClothingDirtGain = 0.00035f;
        public static float DirtyClothingComfortLoss = 0.002f;
        // Clean-clothes comfort bands: tiny wash residue is not dirt. Pristine
        // clothes feel pleasant, 10-20% is neutral, 20-60% ramps gently into
        // the legacy penalty, and 60-100% keeps the legacy rate unchanged.
        public const float CleanClothingComfortBonusThreshold = 0.10f;
        public const float CleanClothingDirtThreshold = 0.20f;
        public const float DirtyClothingFullPenaltyThreshold = 0.60f;
        public const float CleanClothingComfortMaxGain = 0.0002f;
        public static float BatheNeedThreshold = 0.4f;
        public static int BatheDurationTicks = 100; // 100 ticks / 25 real seconds
        // §40.6 r2 (laundry-in-hand): 80 ticks — the piece is doffed off the
        // body / picked up off the shore into the hand and scrubbed there.
        public static int WashClothesDurationTicks = 80;
        // §40.6: мелкие следы ниже 10% не запускают стирку и не
        // рисуются на одежде. Один порог не даёт логике и виду
        // расходиться на пограничных значениях.
        public static float WashClothesNeedThreshold = 0.10f;

        // Drying audit (Jul 2026): DryClothes gated on wetness > 0.5 — after a
        // wash (wet 1.0) passive on-body drying closes that window in ~0.2
        // days, and the old 0.15+0.4×wet score lost the auction to Sit, so
        // rack-hanging fired 0 times in 40-day soaks. Wider window via this
        // threshold (score raised in DecisionSystem alongside).
        public static float DryClothesWetThreshold = 0.35f;

        // ─────────────────────────────────────────────────────────────
        // Stamina / stress (soft — colour the UI, nudge rest, feed collapse).
        // ─────────────────────────────────────────────────────────────
        // Bug #316 (вердикт игрока): дрова не носят по одной палке — партия
        // на один подход к костру. Пока в карманах меньше и палки ещё видны,
        // сбор продолжается; замёрзшая или потухший костёр топят сразу.
        public static int FuelHaulBatchSticks = 3;

        public static float StaminaRestGain = 0.06f;   // stamina/tick while resting
        public static float StaminaWorkDrain = 0.05f;  // stamina/tick while working
        public static float StaminaIdleGain = 0.015f;  // stamina/tick while idle
        public const float StaminaExhaustedThreshold = 0.15f;
        public const float StaminaExhaustedSitBoost = 3f;
        public static float StressUpRate = 0.05f;      // stress/tick in danger/combat/pain/starvation
        public static float StressDownRate = 0.03f;    // stress shed/tick in calm

        // §110: сколько тиков она лежит и рыдает после стресс-краха (ветка
        // §40.13). Дольше 80-тикового обморока намеренно: это СЦЕНА — подруга
        // должна успеть дойти и утешить (§53 Console укорачивает плач).
        public static int CryingBreakdownTicks = 240;

        // §110: сколько стресса снимают сами слёзы за МЕДЛЕННЫЙ тик (поверх
        // обычной формулы). 0.04 × 15 медленных тиков плача ≈ −0.6: встаёт
        // заметно спокойнее, но не в ноль — иначе разрядка обесценивает всё,
        // что довело её до срыва.
        public static float CryingStressRelief = 0.04f;

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
        // Combat — predators.
        // ─────────────────────────────────────────────────────────────
        public static float NpcStrikePerPass = 0.15f;   // an NPC's bare strike-back baseline per landed hit

        /// <summary>
        /// ⭐ §104 r8: ВСЕ удары идут по таймлайну замаха, а не по легаси-фазе.
        ///
        /// <para>
        /// Половина melee-урона в игре до сих пор наносится «по проходу»:
        /// защитницы против собак (MobSystem) и весь §56 (PredationSystem)
        /// бьют раз в средний тик под фазовым гейтом <see cref="MeleeStrikeReady"/>
        /// и НЕ ставят ни одного видового сигнала. Кровь есть, замаха нет —
        /// ровно тот класс бага, что чинил §103, только он там и остался.
        /// </para>
        /// <para>
        /// Включение меняет и каденцию, и летальность: §56 бьёт 0.35/сек, а
        /// таймлайн — 0.221 раз в 2.74 с. Это ЖЕЛАЕМЫЙ сдвиг (коллективная
        /// защита становится физически возможной, см. шапку MeleeSwing), но
        /// собачий баланс на грани. A/B-соаки на 60000 тиков дали 3/4→2/4,
        /// 4/4→2/4, 4/4→2/4 — арифметика: легаси бил урон оружия РАЗ В СЕКУНДУ,
        /// таймлайн наносит его раз в 1.24…1.9 с. Включено ПО ПРОСЬБЕ автора,
        /// чтобы тюнить баланс на видимых ударах; калибровка по DPS — следующая
        /// задача, и до неё колония слабее прежнего.
        /// </para>
        /// </summary>
        public static bool TimedMeleeEverywhere = true;

        /// <summary>
        /// §26.6A r5 — «сквозь чужое тело рука не проходит».
        /// <para>
        /// Скриншот пользователя: кокос вскрывают ЧЕРЕЗ ствол пальмы. Это была
        /// не промашка вида, а арифметика: смежность субсетки ровно 0.375 wu
        /// (замерено на всех 192 920 рёбрах), а <c>BesideReach(0)</c> = 0.80 wu,
        /// то есть ДВА шага — и ровно один занятый узел помещается между рукой
        /// и добычей. Ствол пальмы — ровно один занятый узел. r4 при этом
        /// разрешал ободу проходить сквозь любой футпринт объекта.
        /// </para>
        /// <para>
        /// ⚠️ ЦЕНА ЗАМЕРЕНА, и она не нулевая: 30 сидов × 24000 тиков дали
        /// 86/120 → 72/120 живых и два полных вайпа там, где их не было
        /// (хуже на 10 сидах, лучше на 2). Смерти при этом СМЕСТИЛИСЬ В БОЙ
        /// (VitalPartDestroyed 4→10 на 12 сидах), голодных смертей не прибавилось —
        /// то есть колония не заперта без еды, а просто теряет часть рабочего
        /// времени на обход препятствий, а против собак она и так на грани
        /// (см. TimedMeleeEverywhere выше и заметку про хрупкость к собакам).
        /// </para>
        /// <para>
        /// Поэтому — рубильник, а не свершившийся факт. ВКЛЮЧЁН по просьбе
        /// автора (визуальная ложь дороже), выключается одной строкой, если
        /// баланс в живой игре окажется важнее. Компенсировать правильнее
        /// собачьим уроном, а не возвратом руки сквозь ствол.
        /// </para>
        /// </summary>
        public static bool ReachThroughBodiesBlocked = true;

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

        // §71 RUNNING. Gait is a decision, not a side effect of speed. An
        // autonomous routine route spends the same finite breath reserve as
        // an emergency, only at a calmer pace. Reasons do not compound:
        // MovementSystem takes the largest factor.
        public static float RoutineRunSpeedFactor = 1.6f;
        public static float FleeRunSpeedFactor = 2.25f;  // running for her life
        public static float NeedRunSpeedFactor = 1.6f;   // hurrying to food/water when desperate

        // §71.4: за сколько до СТАТИЧНОЙ цели бегущая переходит на шаг.
        // Торможения в системе нет вовсе — скорость на последнем шаге ровно
        // та же, что на первом, и приход выглядел как удар в стену. 3.0 ≈ 1.15
        // гекса (гекс поперёк = 2.6 ед.), на беговом темпе это около секунды
        // шага. Погоня за АГЕНТОМ и побег исключены в MovementSystem.
        public static float ArrivalWalkDistance = 3.0f;

        // §71 BREATH — the sprint reserve (NPCNeeds.Breath). Drained per tick
        // while running, refilled while walking, faster while standing still.
        // Hysteresis on purpose: once spent she must recover to the re-arm line
        // before she may run again, so she cannot flicker between gaits.
        // §71.1 (пересчёт): рывок должен КОНЧАТЬСЯ на глазах. Старые числа
        // давали 55 с бега с полного бака и 40 с шага до перевзвода — то есть
        // за одну сцену чередование не успевало случиться ни разу, и колония,
        // которой §89/§107/охота раздали поводы бежать, читалась просто
        // «бегущей». Теперь полный бак ≈ 23 с, повторный рывок ≈ 12.5 с, а
        // отдышаться шагом ≈ 27.5 с: бег стал рывком, а не режимом.
        public static float BreathDrainPerTick = 0.011f;    // ~23 s of running from full
        public static float BreathWalkRecoverPerTick = 0.005f;  // ~27 s walking back to the re-arm line
        public static float BreathIdleRecoverPerTick = 0.010f;  // standing catches it twice as fast
        public static float BreathReArm = 0.55f;            // must climb back to this to run again

        public static int AdrenalineTicks = 80;         // fresh damage keeps her too alert to sleep
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
        // the roasting spit (2 planted forked posts → crossbar → rope
        // lashings), then the dense stone ring (§54.17 r3: the spit moved
        // ahead of the ring so cooking unlocks in days, not never — the
        // 18-stone ring is the long tail). No hammer at any stage.
        // §54.12 rule: MUST equal the per-material sums of
        // BuildSiteMath.CampfireStages (which mirror the campfire_final
        // prefab's staged piece groups "1".."5").
        public static int CampfireBillSticks = 12; // 9 pile + 2 posts + 1 crossbar
        public static int CampfireBillStones = 18; // the dense ring
        public static int CampfireBillRope = 2;    // one lashing per post joint

        // §120.2: the indoor hearth is the same craft built small, and its bill
        // matches its authored art piece for piece — 2 spit posts, the cross bar
        // and the skewer, a 7-stone ring, one lashing. A household fire must be
        // cheaper to raise again than the outdoor ring, not the same price.
        // These are const, not tuning dials: each number counts real pieces in
        // furniture.hearth.fbx, so moving one without re-authoring the model
        // would just desync the bill from the art it pays for.
        public const int HutHearthBillSticks = 8;  // 2 posts + spit bar + 5 pile
        public const int HutHearthBillStones = 12; // the tight little ring
        public const int HutHearthBillRope = 1;    // one lashing for the spit

        // §54.14 (r2): the stages are FUNCTIONAL, not only visual.
        // Stage 1 (stick pile) = a working fire: warmth, comfort, crafting.
        // The stone ring (last stage since §54.17 r3) insulates the pit — fuel
        // burns at this fraction of the normal rate (0.5 = a load of wood
        // lasts twice as long).
        public static float CampfireRingBurnMultiplier = 0.5f;
        // §120: roofed hearth ×0.5 again. A finished indoor stone ring burns
        // at 0.25x bare-fire rate — exactly half the best outdoor fire.
        public static float IndoorFireBurnMultiplier = 0.5f;
        // The roasting spit unlocks cooking: raw meat is HUNG on the
        // spit and roasts over a lit fire for this long (100 ticks = 1 game
        // hour), then turns into cooked meat that stays hanging until taken.
        public static int MeatRoastDurationTicks = 200;
        // §54.17: CookMeat's auction curve. Base + weight are chosen so that
        // whenever cooking is actually possible (raw meat in the pack, a lit
        // fire with a free hook) it outbids GetFood (= Hunger) by a fixed
        // margin at EVERY hunger level — the old 0.3 + 0.4·H curve lost to
        // GetFood on the whole domain where both were available (crossover
        // 0.33 vs the 0.35 GetFood threshold), so she fetched forever and
        // never hung the chunk.
        public static float CookMeatBase = 0.4f;
        public static float CookMeatHungerWeight = 1.0f;
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
        // The only bed (bed.basic): 4 log side-rails (two per
        // side) + stick cross-slats + rope lashings + a full leaf mattress.
        public static int BedBasicBillLogs = 4;
        public static int BedBasicBillSticks = 5;
        public static int BedBasicBillRope = 10;
        public static int BedBasicBillLeaves = 50;

        // §120/§133: the wardrobe became buildable when a player plan started
        // staking its OWN furniture — before that it only ever appeared, free,
        // inside the canonical hut. Boards for the carcass and the doors, sticks
        // for the hanging rail and the feet, one lashing.
        // These are const, not tuning dials, for the same reason the hut hearth's
        // bill is: each number counts real pieces of the authored cabinet, so
        // moving one without re-authoring the model only desyncs bill from art.
        public const int WardrobeBillBoards = 8;
        public const int WardrobeBillSticks = 4;
        public const int WardrobeBillRope = 1;

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
        // §54.2 r2 (#168): считается ПО МИРУ, а не по восприятию, и распространён
        // на топливную ветку. Прежняя мера («сколько пальм ВИЖУ») делала резерв
        // локальным: в свежей роще видно больше резерва, срубила, отошла — и
        // снова видно больше. Топливная ветка и вовсе была от резерва свободна.
        // Замер по сейву игрока (seed −28147312, tick 91585): пальм в мире 0,
        // пней 17 — ровно та смерть от жажды, которую этот резерв и заводили
        // предотвращать.
        public static int PalmGroveReserve = 4;

        // §146.8: валка под БИЛЛ стройки (брёвна/доски) открывается цензом
        // здорового леса: пока пальм в мире БОЛЬШЕ этого порога, дом можно
        // кормить деревом; у порога стройка останавливается, а «пальма — вода
        // колонии» (§64.9) остаётся законом. На острове Feud (17 пальм) ветка
        // не открывается никогда — §64.9 сохранён там точно. 0 = выключено.
        public static int FellForBuildPalmFloor = 30;

        // §54.2 r2 (#168): «сначала собери, что лежит». Пока на острове валяется
        // столько крон+листьев, новое дерево не пилят. Мера общеостровная — в
        // отличие от §80 ниже — потому что урожай пальмы это и есть след
        // прошлой рубки: он лежит там, где рубили, и «не вижу» тут значит
        // «отошла», а не «нету». На сейве игрока лежало 595 листьев.
        public static int LooseHarvestBacklog = 8;

        // §80: радиус правила «сначала подбери с земли, потом добывай ещё».
        // Меряется и от самого NPC, и от стройки, ради которой он добывает.
        // Общеостровной меры тут быть не может: восприятие помнит предметы по
        // всей карте, так что «где-то лежит камень» истинно почти всегда и
        // заморозило бы добычу навсегда.
        public static int PickUpFirstRadiusTiles = 4;

        // §54.19: сколько тиков подряд стройку должно быть НЕЧЕМ закрыть,
        // прежде чем очередь перестанет отдавать ей единственный слот.
        //
        // Слот один на колониста, и он достаётся первому подходящему звену
        // цепочки `?? `. Кровать, которой не хватает четырёх брёвен, занимала
        // furnitureSite насовсем — и всё, что стоит ниже (гардероб, верстак,
        // да и любая следующая стройка), не строилось НИКОГДА, хотя материал
        // на него лежал рядом. Мёртвая стройка не снимается и не отменяется:
        // она уходит в конец очереди и вернётся в тот же тик, когда материал
        // для неё снова появится в мире.
        //
        // ⭐ const, а не ручка: пересобрать SimData/simdata.json без Unity
        // нельзя (меню HexLive ▸ Export Sim Data), а BalanceParityGate требует
        // от КАЖДОЙ `public static` ручки строки в экспорте. Заводить дырявый
        // экспорт ради числа, которое подбирается раз — хуже, чем константа;
        // ровно так же живут HutHearthBill* выше. Станет нужно крутить —
        // превратить в `public static int` вместе с переэкспортом.
        //
        // 1200 тиков = 5 минут игры на 4 Гц: длиннее любой ходки за материалом
        // (кто-то донёс бы и снял метку), короче, чем стоит терпеть простой.
        public const int BuildSiteUnstockableSkipTicks = 1200;

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
        // §54.16 history: raw was 1800 — SHORTER than the 2400-tick danger
        // memory every kill site carries, so meat was guaranteed to rot before
        // anyone was allowed to shop there; 2600 outlived the mark. §54.17
        // raised both by design decision: raw 12000 (5 event cycles) so a kill
        // survives long enough to be hauled and cooked, cooked 20000 so a
        // roast is a real larder — cook today, the colony eats for days.
        public static int MeatRawSpoilTicks = 12000;
        public static int MeatCookedSpoilTicks = 20000;

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

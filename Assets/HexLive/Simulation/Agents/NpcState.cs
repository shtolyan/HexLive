using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime.Journal;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Agents
{

// Spec 19.3C: per-part health (molly BoneHealthSystem, simplified).
public sealed class BodyState
{
    public const float UsableHandFunctionThreshold = 0.20f;

    public System.Collections.Generic.Dictionary<BodyPart, float> Parts { get; } = new()
    {
        [BodyPart.Head] = 1f,
        [BodyPart.Torso] = 1f,
        [BodyPart.Pelvis] = 1f,
        [BodyPart.ArmL] = 1f,
        [BodyPart.ArmR] = 1f,
        [BodyPart.LegL] = 1f,
        [BodyPart.LegR] = 1f
    };

    // Spec §50: limbs that have been severed and are gone for good. A severed
    // zone is pinned at 0 HP and never regenerates (unlike a merely-mauled zone,
    // which heals back). Only arms/legs can be severed — never Head/Torso/Pelvis.
    public System.Collections.Generic.HashSet<BodyPart> Severed { get; } = new();

    // §116: extra condition that cannot live in the legacy 0..1 Parts map.
    // The map is eagerly populated so save/snapshot code has a stable seven-row
    // shape and old callers can continue reading Parts unchanged.
    public System.Collections.Generic.Dictionary<BodyPart, BodyPartCondition> Conditions { get; } = new()
    {
        [BodyPart.Head] = new(),
        [BodyPart.Torso] = new(),
        [BodyPart.Pelvis] = new(),
        [BodyPart.ArmL] = new(),
        [BodyPart.ArmR] = new(),
        [BodyPart.LegL] = new(),
        [BodyPart.LegR] = new()
    };

    public float BloodDeficit { get; set; }

    public BodyPartCondition Condition(BodyPart part) => Conditions[part];

    public bool IsSevered(BodyPart part) => Severed.Contains(part);

    public bool AnySevered => Severed.Count > 0;

    // Spec §50: jumping needs two supporting legs. A functional fitted
    // prosthesis restores that support even when its deliberately reduced
    // mobility score is below the natural-leg injury threshold.
    public bool CanJump => LegSupportsJump(BodyPart.LegL) &&
                           LegSupportsJump(BodyPart.LegR);

    public bool HasNoLegs => LimbFunction(BodyPart.LegL) <= 0f &&
                             LimbFunction(BodyPart.LegR) <= 0f;

    // §50-prone: «лежит». A lost leg (EITHER one) puts her on the ground — she
    // crawls, she cannot stand. More prone states may join later (fainted,
    // pinned…); gate on THIS, not on leg counts. Heavy tools, weapons and a
    // standing fight need BOTH a standing body and at least one usable hand.
    // Light hand-work (opening a coconut while prone) checks HasUsableHand
    // separately at its own planning/execution gates.
    public bool IsProne => LimbFunction(BodyPart.LegL) <= 0f ||
                           LimbFunction(BodyPart.LegR) <= 0f;

    // ⭐ ПОЛЗЁТ — это шире, чем «нет ноги», и определение должно быть ОДНО.
    //
    // Кроме потерянной ноги (IsProne) на землю кладут и две просто изувеченные:
    // ниже этого порога обе ноги уже не держат. Правило жило только в
    // экспортёре снапшота, то есть знал о нём ВИД и не знала симуляция, — и
    // модель считала ползущую обычной ходячей. Отсюда картинка, которую
    // невозможно объяснить игроку: девушка ползёт по земле и при этом несёт на
    // руках другую (сейв seed=476005489, tick=5525: ноги 0.35 и 0.17, а
    // CarriedNpcId занят). Теперь порог живёт здесь, а экспортёр читает его.
    public const float CrawlLegFunctionThreshold = 0.40f;

    public bool IsCrawling => IsProne ||
        (LimbFunction(BodyPart.LegL) < CrawlLegFunctionThreshold &&
         LimbFunction(BodyPart.LegR) < CrawlLegFunctionThreshold);

    public bool HasUsableHand => IntactHands > 0;

    public bool CanUseToolsOrWeapons => !IsProne && HasUsableHand;

    // Spec §52: how many hands can still hold things — one inventory slot each,
    // and the pair a two-handed weapon needs. Lose an arm, lose a hand slot.
    public int IntactHands =>
        (LimbFunction(BodyPart.ArmL) >= UsableHandFunctionThreshold ? 1 : 0) +
        (LimbFunction(BodyPart.ArmR) >= UsableHandFunctionThreshold ? 1 : 0);

    public bool CanUseTwoHanded => LimbFunction(BodyPart.ArmL) >= 0.75f &&
                                   LimbFunction(BodyPart.ArmR) >= 0.75f;

    // Existing weapon selection accepts only a hand count. One means
    // one-handed weapons still work while the two-handed candidate is filtered.
    public int WeaponHands => CanUseTwoHanded ? IntactHands : System.Math.Min(1, IntactHands);

    // Sever a limb: mark it gone and pin its HP to 0. Idempotent.
    public void Sever(BodyPart part)
    {
        Severed.Add(part);
        Parts[part] = 0f;
        Conditions[part].SplintSupport = 0f;
    }

    public float LimbFunction(BodyPart part)
    {
        var condition = Conditions[part];
        if (IsSevered(part))
        {
            return condition.Prosthetic?.EffectiveFunction ?? 0f;
        }

        return System.Math.Max(Parts[part], condition.SplintSupport);
    }

    private bool LegSupportsJump(BodyPart part)
    {
        if (IsSevered(part))
        {
            return Conditions[part].Prosthetic?.EffectiveFunction > 0f;
        }

        return LimbFunction(part) >= 0.75f;
    }

    public float Mean()
    {
        var sum = 0f;
        foreach (var value in Parts.Values)
        {
            sum += value;
        }

        return sum / Parts.Count;
    }

    // §105 r4: ⭐ ЧТО ТАКОЕ ВИТАЛЬНАЯ ЗОНА — одно определение на весь проект.
    //
    // Голова, грудь и ТАЗ. Раньше пара «голова + грудь» была вписана руками в
    // пяти местах (проверка смерти, пол умирающей, два температурных порога,
    // расчёт полоски), и добавление таза означало вспомнить про каждое —
    // ровно тот класс ошибок, ради которого §105 собрала восемь сайтов урона в
    // один ResolveTrauma. Теперь спрашивают ЗДЕСЬ.
    //
    // Конечности сюда не входят намеренно: они доходят до нуля и даже
    // отрываются (§50), не убивая, — калечат, но не кончают.
    public static readonly BodyPart[] VitalParts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis
    };

    public static bool IsVital(BodyPart part) =>
        part is BodyPart.Head or BodyPart.Torso or BodyPart.Pelvis;

    // §105 r2: ВИТАЛЬНОЕ здоровье — худшая из зон, потеря которых убивает.
    //
    // Отличается от Health (среднее по семи зонам) намеренно, и показывать
    // игроку надо именно это. Среднее врёт в обе стороны: разбитая в хлам
    // грудь при целых руках и ногах даёт «здоровье 0.75», то есть «всё
    // неплохо», хотя следующий удар убивает; а обглоданные конечности при
    // целом торсе роняют среднее, хотя жизни ничего не грозит. Полоска над
    // портретом читает ЭТО — «сколько осталось до того, как станет поздно».
    //
    // Балансу от этого ни жарко ни холодно: Health остаётся ровно тем же
    // числом, что и был, и все пороги симуляции по-прежнему смотрят на него.
    public float VitalHealth()
    {
        var worst = 1f;
        foreach (var part in VitalParts)
        {
            if (Parts[part] < worst)
            {
                worst = Parts[part];
            }
        }

        return worst;
    }

    // §105 r3: ЧИСЛО НА ПОРТРЕТЕ — витальное здоровье, дополнительно
    // придавленное состоянием конечностей.
    //
    // r2 показывал ровно худшую витальную зону. Про «сколько осталось до того,
    // как станет поздно» это правда, но про тело — нет: раздробленная рука или
    // нога не двигали число ВООБЩЕ, и портрет уверял «100%» у колонистки,
    // которая еле стоит. Возврат к среднему по семи зонам исключён — именно оно
    // враньём в обе стороны и породило r2.
    //
    // Поэтому берётся МИНИМУМ из двух мнений:
    //   * витальное (r2) — целые руки и ноги не могут его завысить, пробитая
    //     грудь по-прежнему читается как критическая;
    //   * взвешенное тело — конечности тянут число вниз, каждая на свою долю.
    //
    // Веса — presentation-константы, а НЕ ручки баланса (потому const: ручка
    // обязана ехать в simdata.json). Ни одна система на это число не смотрит:
    // пороги симуляции по-прежнему читают Health и VitalHealth.
    private const float DisplayWeightHead = 0.24f;
    private const float DisplayWeightTorso = 0.24f;
    private const float DisplayWeightPelvis = 0.12f;
    private const float DisplayWeightLimb = 0.10f; // ×4 конечности = 0.40

    public float DisplayHealth()
    {
        var weighted =
            Parts[BodyPart.Head] * DisplayWeightHead +
            Parts[BodyPart.Torso] * DisplayWeightTorso +
            Parts[BodyPart.Pelvis] * DisplayWeightPelvis +
            (Parts[BodyPart.ArmL] + Parts[BodyPart.ArmR] +
             Parts[BodyPart.LegL] + Parts[BodyPart.LegR]) * DisplayWeightLimb;
        var vital = VitalHealth();
        return weighted < vital ? weighted : vital;
    }

    public bool VitalDestroyed(out BodyPart part)
    {
        // Порядок — как в VitalParts: голова первой, потому что трасса и §105
        // разбирают именно её отдельно (голова в ноль убивает мгновенно).
        foreach (var candidate in VitalParts)
        {
            if (Parts[candidate] <= 0f)
            {
                part = candidate;
                return true;
            }
        }

        part = BodyPart.Head;
        return false;
    }

    // Spec 19.3C: mauled legs mean hobbling, hurt arms mean weak strikes.
    // Spec §50: a merely-mauled leg keeps the 0.4 floor (a hurt leg still
    // shuffles at 40%); a LOST leg (one or both) means she crawls — a fixed
    // slow pace (CrawlSpeedFactor, ~1/3 of walking) that pairs with the crawl
    // animation, regardless of how the other leg is doing.
    // §50: насколько медленнее ПОВОРАЧИВАЕТСЯ тело. Ползущая разворачивается
    // тем же множителем, что и ползёт (CrawlSpeedFactor): на локтях поворот —
    // это перебор руками, а не вращение вокруг оси. Отдельной ручки нарочно
    // нет — один шаг, один поворот, одна цифра; понадобится разводить — заводим
    // ручку и зеркалим её в конфиг-ассет и simdata.json (§59.3).
    public float MobilityTurnFactor() =>
        IsProne ? HexLive.Simulation.Runtime.Spec50.CrawlSpeedFactor : 1f;

    public float MobilityFactor()
    {
        if (IsProne)
        {
            return HexLive.Simulation.Runtime.Spec50.CrawlSpeedFactor;
        }

        if (IsSevered(BodyPart.LegL) || IsSevered(BodyPart.LegR))
        {
            return System.Math.Min(LimbFunction(BodyPart.LegL), LimbFunction(BodyPart.LegR));
        }

        return 0.4f + 0.6f * (LimbFunction(BodyPart.LegL) + LimbFunction(BodyPart.LegR)) * 0.5f;
    }

    // §76: renamed from StrikeFactor(). This is ONLY the limb half of the melee
    // factor — how much of a blow her arms can still put behind it. The blow
    // itself is NPCState.StrikeFactor(), which multiplies this by her innate
    // Strength and her learned Combat. Damage sites must call THAT.
    //
    // The rename is deliberate: it broke every call site at compile time so
    // none could be left reading the un-attributed number, and it keeps them
    // broken for anyone who reaches past NPCState in future.
    public float LimbStrikeFactor()
    {
        var left = LimbFunction(BodyPart.ArmL);
        var right = LimbFunction(BodyPart.ArmR);
        if (left <= 0f && right <= 0f)
        {
            return 0f;
        }

        return (left + right) * 0.5f;
    }

    // 1.0 with both arms; the §50 severed multiplier for one gone, its square
    // for both gone (a lost arm can't strike back — the collapse is below the
    // 0.4 mauled floor).
    private float SeveredArmMult()
    {
        var gone = (IsSevered(BodyPart.ArmL) ? 1 : 0) + (IsSevered(BodyPart.ArmR) ? 1 : 0);
        if (gone == 0)
        {
            return 1f;
        }

        var mult = HexLive.Simulation.Runtime.Spec50.SeveredLimbMobilityMult;
        return gone == 2 ? mult * mult : mult;
    }
}

public sealed class BodyPartCondition
{
    // Portion of missing legacy HP caused by blunt trauma. It heals without a
    // dressing; cut damage is represented by WoundState records.
    public float BluntDamage { get; set; }

    // Normalized negative depth after Parts reached zero: 0 == 0 HP, 1 ==
    // -100% in Kenshi terms.
    public float CriticalTrauma { get; set; }

    public float SplintSupport { get; set; }

    public float HitBias { get; set; } = 1f;

    public int HitBiasChangedTick { get; set; }

    // §40.8-H r10: накопительная кровяная «грязь» под спеклы (0..1). Растёт
    // только от режущего урона, создающего рану (WoundMath.InflictCut);
    // смывается только водой/купанием. Заживление ран и реген HP её НЕ
    // трогают — голод/жажда/жара/болезнь тело не красят.
    public float BloodSoil { get; set; }

    public ProstheticState Prosthetic { get; set; }
}

public sealed class ProstheticState
{
    public string DefinitionId { get; set; } = string.Empty;

    public BodyPart Part { get; set; }

    // Remaining device HP, not a 0..1 fraction. Max comes from its definition.
    public float Condition { get; set; }

    public float MaxCondition { get; set; }

    public float Function { get; set; }

    public bool Mechanical { get; set; }

    public float EffectiveFunction => MaxCondition <= 0f
        ? 0f
        : Function * System.Math.Max(0f, System.Math.Min(1f, Condition / MaxCondition));
}

// Spec 40.8B: one landed bite/hit = one wound record. The zone-health model
// (BodyState) keeps driving HP/posture/balance exactly as before; wounds are
// the parallel VISUAL truth — where the skin is broken, how it looks and how
// far it has closed. Placement/look derive deterministically from Seed.
public sealed class WoundState
{
    public int Id { get; set; }

    public BodyPart Zone { get; set; }

    // HP the hit cost at infliction (bookkeeping/UI; balance stays in BodyState).
    public float Severity { get; set; }

    // 0 = fresh and vivid, 1 = fully closed (record removed) — the decal
    // fades with this.
    public float Heal01 { get; set; }

    // §116: clotting/stabilisation are separate from closing the injury.
    public float Clot01 { get; set; }

    public bool Stabilized { get; set; }

    // ⭐ §118.2: заклеена ПЛАСТЫРЕМ, а не забинтована.
    //
    // Пластырь — дешёвая альтернатива бинту: он стабилизирует РОВНО ОДНУ рану,
    // тогда как бинт закрывает всю зону. Поэтому состояние живёт на записи
    // раны, а не в BandagedZones: на одном торсе может быть налеплено сколько
    // угодно пластырей, по одному на порез, и поверх них ещё бинт.
    //
    // Лечит он так же (Stabilized + Clot01=1) — отличается ценой и тем, что
    // рисуется точечно на самой ране, а не обмоткой вокруг конечности.
    public bool Plastered { get; set; }

    public float BleedFactor { get; set; } = 1f;

    public int Seed { get; set; }
}

public sealed class NPCState
{
    public EntityId Id { get; set; }

    // Spec 19.3 / iteration 23: presentation identity — the girls have
    // names and bodies; the simulation itself never branches on them.
    // §74: DisplayName is now a NAME ID from ColonistAppearance.NameIds — the
    // player-facing text comes from the I2 term `npc.<lowercase id>.name`.
    public string DisplayName { get; set; } = string.Empty;

    public string ActorMesh { get; set; } = string.Empty;

    // §84: на какое тело сшита его одежда. Выводится из меша, а не хранится
    // отдельно: тело и пол — это одно и то же, и второе поле рано или поздно
    // разошлось бы с первым. Зеркало презентационного ActorSex.Of.
    public Content.GarmentSex Sex =>
        ActorMesh is "Kshishtof" or "Tonny"
            ? Content.GarmentSex.Male
            : Content.GarmentSex.Female;

    // §74: the three traits that used to be implied by ActorMesh and are now
    // rolled independently. EMPTY MEANS "as before": the body's own materials,
    // the hairstyle authored on the actor prefab, and the voice folder named
    // after the mesh. That default is what lets a pre-§74 save, a test-scene
    // bootstrap and the outsider all keep working untouched.
    public string SkinSet { get; set; } = string.Empty;

    // §85: iris colour, split out of SkinSet so a face and a pair of eyes are
    // two choices. Empty means "the eye materials the body prefab shipped with",
    // which is what keeps the outsider, the test scenes and pre-§85 saves as
    // they were.
    public string EyeColor { get; set; } = string.Empty;

    public string Hairstyle { get; set; } = string.Empty;

    public string VoiceBank { get; set; } = string.Empty;

    // §72: which side this body is on. The ONE thing the simulation DOES branch
    // on above — everything else (needs, GOAP, crafting, wardrobe) is identical
    // for a colonist and an outsider. Defaults to Colony so every pre-§72 path
    // and every old save behaves exactly as before.
    public Faction Faction { get; set; } = Faction.Colony;

    // §28.15C v3: КАКИМ клипом она упала. Выбирается симуляцией по хешу сида,
    // а не видом по Random: иначе одно и то же тело падало бы по-разному у
    // сервера и у каждого зрителя, и по-новому после каждой перезагрузки —
    // поза лежащего тела это состояние мира, а не украшение кадра.
    //
    // Момента смерти здесь намеренно НЕТ: его уже хранит world.DeathRecords, а
    // виду он не нужен — «упала прямо сейчас» против «лежит с прошлой сессии»
    // он различает по тому, был ли у него живой вид этого тела кадром раньше.
    // Второе поле с тем же смыслом рано или поздно разошлось бы с первым.
    public int DeathAnimVariant { get; set; }

    public FragmentId Fragment { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId? CurrentJunction { get; set; }

    public Float2 Position { get; set; } = Float2.Zero;

    public float RotationDegrees { get; set; }

    public float MoveSpeed { get; set; } = 1f;

    public float TurnSpeed { get; set; } = 90f;

    public float PostTurnPause { get; set; } = 0.4f;

    // Spec 31A.5A/35.5: worn item instances; warmth/armor are recomputed
    // from this list, never mutated directly. Wet items give no warmth.
    public System.Collections.Generic.List<ItemInstance> WornItems { get; } = new();

    public float EquippedWarmth { get; set; }

    // Spec 29C.2/29C.4: health and damage absorption.
    // Health is the mean of body parts (spec 19.3C), kept as a field for
    // snapshots and thresholds; recomputed after every wound/regen.
    public float Health { get; set; } = 1f;

    public BodyState Body { get; } = new();

    // Spec 40.8B: wounds are first-class records — one per landed bite/hit
    // (starvation/heat/sickness drain HP with NO wound). Each wound knows its
    // zone, the damage it cost, a deterministic Seed (exact decal spot & look,
    // stable across frames and save-replays) and Heal01: 0 fresh → 1 healed
    // (the decal fades with it; the record is removed when fully closed).
    public System.Collections.Generic.List<WoundState> Wounds { get; } = new();

    // Spec 44: zones currently dressed with a HERBAL leaf bandage — presentation
    // draws the leaf-wrap decal; cleared as the zone heals past 0.7.
    public System.Collections.Generic.HashSet<Content.BodyPart> BandagedZones { get; } = new();

    // Spec 44: zones dressed with a pre-made MEDKIT bandage (spec 40.3) — a
    // plain gauze wrap, not gathered plantain; presentation draws the gauze
    // decal instead of the leaf wrap. Cleared as the zone heals past 0.7.
    public System.Collections.Generic.HashSet<Content.BodyPart> GauzeZones { get; } = new();

    public int NextWoundId { get; set; } = 1;

    // §116: authoritative two-way rescue link. Carrier owns CarriedNpcId;
    // patient owns CarriedByNpcId. Load and runtime validation clear half-links.
    public EntityId? CarriedNpcId { get; set; }

    public EntityId? CarriedByNpcId { get; set; }

    public ObjectId? RescueDestinationObjectId { get; set; }

    public bool IsCarryingPerson => CarriedNpcId is not null;

    public bool IsBeingCarried => CarriedByNpcId is not null;

    public float EquippedArmor { get; set; }

    // Spec 29C.3: set while a dog is engaging this NPC; combat is reactive.
    public bool IsFighting { get; set; }

    // Timed melee exchange (AnimalCombatSystem): >0 while a swing is winding
    // up — it lands at exactly this tick. Not persisted (restarts on load).
    public int StrikeLandsAtTick { get; set; }

    // Next tick a new swing may start (weapon cooldown gate).
    public int StrikeReadyAtTick { get; set; }

    // The current swing's attack ANIMATION runs until this tick (weapon
    // AttackDurationSeconds from the swing start) — presentation plays the
    // attack clip across exactly this window; the damage itself lands earlier
    // (StrikeLandsAtTick = start + HitDelaySeconds). Not persisted.
    public int AttackAnimUntilTick { get; set; }

    // The tick that window OPENED. Presentation fires the attack clip on a
    // swing-start it has not played yet, rather than on the rising edge of
    // "is the window open right now".
    //
    // Why the edge is not enough: the window is 1-2 ticks wide (fists round
    // down to 1), and the view renders the LAST tick of a frame, not every
    // tick. At 50x/MAX the runner steps dozens of ticks per frame, so most
    // swings open and close entirely between two rendered frames — the blow
    // lands on a body that never moved. A tick stamp survives the skip; a
    // boolean edge cannot. Not persisted (transient, like the window itself).
    public int SwingStartTick { get; set; }

    // Which strike variant the current swing uses when the drawn gear has
    // per-strike timings (fists: 2 punches + 2 kicks). Picked at swing start,
    // mirrored to the snapshot so the view plays the MATCHING clip. -1 =
    // single-timing gear (knife/axe). Not persisted.
    public int SwingStrikeIndex { get; set; } = -1;

    // §30: a human picks the body part at wind-up start. Persisted so saving
    // between wind-up and impact cannot reroll the target.
    public EntityId? PendingHumanStrikeTargetId { get; set; }

    public BodyPart PendingHumanStrikePart { get; set; } = BodyPart.Torso;

    // ⭐ §104 r5: ТИК, В КОТОРЫЙ ПО НЕЙ ПОПАЛИ. Тот же приём, что и
    // SwingStartTick, и по той же причине: момент удара живёт ОДИН тик, а вид
    // рисует только последний тик кадра — булев флаг «сейчас попали» был бы
    // невидим на любой скорости выше 1x.
    //
    // Зачем вообще: до этого вид узнавал о попадании косвенно и с опозданием —
    // по падению здоровья (порог 0.02 по СРЕДНЕМУ, а кулак даёт landed/7 ≈
    // 0.015, то есть флинча просто не было) и по появлению новой раны с
    // гейтом в 0.4 с. Звука удара по человеку не было вовсе. Штамп даёт виду
    // ровно то, что нужно: вот сейчас, вот этим.
    //
    // Ставится ДАЖЕ когда урон обнулила пощада (§86): удар случился, кулак
    // прилетел — видно и слышно это должно быть. Не персистится.
    public int HitStampTick { get; set; }

    // Чем по ней попали в тот тик: "" — кулаки, id снаряжения, MobBiteWeaponId
    // — зубы. Вид выбирает по этому звук удара и характер брызги.
    public string HitWeaponId { get; set; } = string.Empty;

    // ⭐ §104 r9: КУДА попали и ОТКУДА. Вид отбивает эту кость назад — не
    // клипом реакции, а процедурно, поверх любой анимации, поэтому ему нужны
    // ровно две вещи: зона тела и позиция бьющего (направление считается от
    // кости). Зона — та же, что у ран, чтобы кость искалась одной таблицей.
    public BodyPart HitPart { get; set; } = BodyPart.Torso;

    // Мировая позиция того, кто ударил, на момент удара.
    public Float2 HitFrom { get; set; }

    // Spec 35.4: accumulated sun exposure; burns at 1.0.
    public float SunExposure { get; set; }

    // Spec 35.5: how wet the BODY itself is, 0 dry .. 1 soaked. Rain and
    // water tiles wet the skin directly — independent of clothing — so a
    // naked or near-naked survivor still reads as soaked (💦 effect + wet
    // discomfort), not just a wet garment. MoistureSystem drives it exactly
    // like item wetness (snap wet on exposure, dry gradually); the Soaked
    // effect and the §49.7 comfort penalty take max(body, worn).
    public float BodyWetness { get; set; }

    // Spec §53: this girl's personality weight for compassion (0..1). Seeded
    // once at spawn and fixed for life. It scales BOTH how fast her Compassion
    // need drains from others' suffering AND the strength of her Aid bid — a
    // high-trait girl drops a bed build to tend a wounded housemate, a low-trait
    // one helps only when she has nothing else to do. Default is a middling
    // value; WorldStateFactory spreads it per-NPC.
    public float CompassionTrait { get; set; } = 0.6f;

    // Spec §76: WHO she is — six innate characteristics rolled once from the
    // world seed and fixed for life (AttributeMath.Roll). Defaults are the
    // human average, so an un-rolled body is the pre-§76 game exactly.
    public AttributeSet Attributes { get; } = new();

    // Spec §76: WHAT she has learned — eight trades that grow with practice
    // (SkillMath.Award). Everyone starts at zero.
    public SkillSet Skills { get; } = new();

    // Spec §126: WHAT KIND of person she is — named traits rolled once from the
    // world seed (TraitMath.Roll) or authored on the bootstrap, and fixed for
    // life. Empty is the pre-§126 game exactly: every gate asks Has(), and an
    // empty mask always answers no.
    //
    // Deliberately NOT derived from Faction. Faction answers "who is my enemy";
    // a trait answers "what will I do when the choice is mine" — the outsider's
    // §81 bullying and §89 filth used to be spelled as `Faction != Colony`, so a
    // colonist could not be a slob and a stranger could not be tidy.
    public TraitSet Traits { get; } = new();

    // Spec §76: THE melee output factor — limb condition (§19.3C/§50) × innate
    // Strength × learned Combat. Every damage site in every combat system must
    // read this and never Body.LimbStrikeFactor() directly, or the next system
    // written will quietly fight without characteristics.
    public float StrikeFactor() =>
        Body.LimbStrikeFactor() *
        HexLive.Simulation.Runtime.AttributeMath.MeleeDamageMult(this);

    public NPCNeeds Needs { get; } = new();

    public NPCMind Mind { get; } = new();

    // Spec §105: она УМИРАЕТ — лежит с обнулённым статом, и запас смерти
    // тикает. Живая (Health держится над нулём полом Spec105.BodyFloor,
    // именно чтобы её не приняли за труп три десятка проверок `Health <= 0`),
    // но беспомощная и спасаемая.
    public bool IsDying => Mind.DyingCause != AI.DyingCause.None;

    // Spec §60: out cold — either the short stamina faint (spec 40.13) or a
    // stat-gated coma. One question every consumer asks the same way: can this
    // body act at all right now? Combat/decision/presentation gate on THIS.
    // §105: умирание входит СЮДА, а не заводит свой параллельный вопрос — тем
    // самым все три десятка читателей (бой не бьёт беспомощную, перцепция не
    // зовёт её болтать, помощь считает её лежачей) получают верную семантику
    // без единой правки на своей стороне.
    public bool IsUnconscious(int tick) =>
        Mind.ComaCause != AI.ComaCause.None || IsDying || tick < Mind.FaintedUntilTick;

    // Spec §110: lying down and crying — the stress arm of the §40.13 collapse.
    // Deliberately NOT part of IsUnconscious: she is awake (pain interrupts,
    // cues/speech still show), she just can't act until she cries it out.
    public bool IsCrying(int tick) => tick < Mind.CryingUntilTick;

    // §105.14: притворяется мёртвой — лежит неподвижно, пока рядом враг.
    // Она В СОЗНАНИИ, и это НАМЕРЕННО не входит в IsUnconscious: его читатели
    // (MobSystem helpless, RaidMath.Opportunity, §56/§81) — это и есть
    // «догрызают беспомощную», а притворство обязано делать ровно обратное:
    // враг теряет к ней интерес (гейты по форме §106, см. IsNpcInRefugeFrom).
    public bool IsPlayingDead(int tick) => tick < Mind.PlayDeadUntilTick;

    // Spec §53/§60: is this body lying flat on the ground right now — knocked
    // out (coma/faint), asleep, crying (§110), or legless-prone? A lying ward
    // keeps her authored pose (helpers/chatters must not spin her to "face"
    // them), and the aid animation kneels beside her only when she is DOWN.
    public bool IsLyingDown(int tick) =>
        IsUnconscious(tick) ||
        IsCrying(tick) ||
        IsPlayingDead(tick) ||
        Execution.CurrentInteraction == InteractionType.Sleep ||
        Body.IsProne;

    public NPCPlanState Plan { get; } = new();

    public NPCExecutionState Execution { get; } = new();

    public MovementState Movement { get; } = new();

    public PerceptionSnapshot Perception { get; } = new();

    public MemoryState Memory { get; } = new();

    public SocialState Social { get; } = new();

    // Spec §136: личный дневник — одна запись в игровой час о самом важном.
    // В отличие от бортового самописца (§30.14, инструмент наблюдения), это
    // состояние мира: оно переживает сейв и едет по проводу.
    public NpcJournal Journal { get; } = new();

    public InventoryState Inventory { get; } = new();

    // Spec 29G: junctions covered by a lying body — housemates path around.
    public System.Collections.Generic.List<HexLive.Simulation.Common.JunctionId> ClaimedJunctions { get; } = new();

    // Spec 29H: what the carried bottle currently holds (one bottle per NPC).
    public WaterKind BottleWater { get; set; } = WaterKind.None;

    // Spec §52: a filled bottle holds several gulps. Filling charges it to
    // SimBalance.BottleCapacity; each drink spends one; at 0 the bottle empties
    // (BottleWater → None) and only then is a refill trip worthwhile. This is
    // what lets a colony stop obsessing over water — one fill, several drinks.
    public int BottleCharges { get; set; }
}

// Spec 29H: the contents of an NPC's water bottle.
public enum WaterKind
{
    None,
    Raw,    // filled at a pond/river bank — 30 % sickness on drink
    Boiled, // filled at a lit campfire with a pot — safe, quenches more
    Rain    // §54.15: collected by the water collector's leaf funnel — clean, no sickness roll
}

}

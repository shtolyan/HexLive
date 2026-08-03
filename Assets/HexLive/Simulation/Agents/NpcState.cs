using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Agents
{

// Spec 19.3C: per-part health (molly BoneHealthSystem, simplified).
public sealed class BodyState
{
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

    public bool IsSevered(BodyPart part) => Severed.Contains(part);

    public bool AnySevered => Severed.Count > 0;

    // Spec §50: jumping needs both legs. A survivor missing either leg can't
    // hop an elevation step (or dive water) — that terrain becomes off-limits.
    public bool CanJump => !IsSevered(BodyPart.LegL) && !IsSevered(BodyPart.LegR);

    public bool HasNoLegs => IsSevered(BodyPart.LegL) && IsSevered(BodyPart.LegR);

    // §50-prone: «лежит». A lost leg (EITHER one) puts her on the ground — she
    // crawls, she cannot stand. More prone states may join later (fainted,
    // pinned…); gate on THIS, not on leg counts. Lying means: no tools, no
    // weapons, no standing fight (the old gate checked BOTH legs, so a
    // one-legged crawler stood up and boxed wolves — the bug).
    public bool IsProne => IsSevered(BodyPart.LegL) || IsSevered(BodyPart.LegR);

    public bool CanUseToolsOrWeapons => !IsProne;

    // Spec §52: how many hands can still hold things — one inventory slot each,
    // and the pair a two-handed weapon needs. Lose an arm, lose a hand slot.
    public int IntactHands =>
        (IsSevered(BodyPart.ArmL) ? 0 : 1) + (IsSevered(BodyPart.ArmR) ? 0 : 1);

    // Sever a limb: mark it gone and pin its HP to 0. Idempotent.
    public void Sever(BodyPart part)
    {
        Severed.Add(part);
        Parts[part] = 0f;
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

    public bool VitalDestroyed(out BodyPart part)
    {
        if (Parts[BodyPart.Head] <= 0f)
        {
            part = BodyPart.Head;
            return true;
        }

        if (Parts[BodyPart.Torso] <= 0f)
        {
            part = BodyPart.Torso;
            return true;
        }

        part = BodyPart.Head;
        return false;
    }

    // Spec 19.3C: mauled legs mean hobbling, hurt arms mean weak strikes.
    // Spec §50: a merely-mauled leg keeps the 0.4 floor (a hurt leg still
    // shuffles at 40%); a LOST leg (one or both) means she crawls — a fixed
    // slow pace (CrawlSpeedFactor, ~1/3 of walking) that pairs with the crawl
    // animation, regardless of how the other leg is doing.
    public float MobilityFactor()
    {
        if (IsSevered(BodyPart.LegL) || IsSevered(BodyPart.LegR))
        {
            return HexLive.Simulation.Runtime.Spec50.CrawlSpeedFactor;
        }

        return 0.4f + 0.6f * (Parts[BodyPart.LegL] + Parts[BodyPart.LegR]) * 0.5f;
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
        var baseFactor = 0.4f + 0.6f * (Parts[BodyPart.ArmL] + Parts[BodyPart.ArmR]) * 0.5f;
        return baseFactor * SeveredArmMult();
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

    // Spec §53/§60: is this body lying flat on the ground right now — knocked
    // out (coma/faint), asleep, crying (§110), or legless-prone? A lying ward
    // keeps her authored pose (helpers/chatters must not spin her to "face"
    // them), and the aid animation kneels beside her only when she is DOWN.
    public bool IsLyingDown(int tick) =>
        IsUnconscious(tick) ||
        IsCrying(tick) ||
        Execution.CurrentInteraction == InteractionType.Sleep ||
        Body.IsProne;

    public NPCPlanState Plan { get; } = new();

    public NPCExecutionState Execution { get; } = new();

    public MovementState Movement { get; } = new();

    public PerceptionSnapshot Perception { get; } = new();

    public MemoryState Memory { get; } = new();

    public SocialState Social { get; } = new();

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

using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Debug
{

public sealed class WorldSnapshot
{
    public int Tick { get; set; }

    // Spec 29C.1: the seed every chance roll mixes. Consumers that used to read
    // WorldState.Seed directly (the history log picks its file by it) get it here
    // instead — a client that does not own a WorldState has no other way to know.
    public int Seed { get; set; }

    // Seconds of world time one tick is worth. Needed to turn a tick STAMP into
    // "how long ago was that" — the view uses it to shorten a hop arc it observed
    // late, and a networked client needs it to pace its own interpolation clock.
    public float TickDeltaTime { get; set; }

    public float Temperature { get; set; }

    public string Clock { get; set; } = string.Empty;

    // The same time of day as Clock, unformatted (0..1). Clock is for humans; the
    // sky/ambience controllers need the raw number and were reaching into
    // WorldState.Environment to get it.
    public float TimeOfDayNormalized { get; set; }

    public string DayPhase { get; set; } = string.Empty;

    public float UvIndex { get; set; }

    public bool IsRaining { get; set; }

    // Spec 40.15: logs hauled to the escape raft, and the target — for a HUD
    // "escape progress" readout. At Progress >= Target the colony can leave.
    public int RaftProgress { get; set; }
    public int RaftTarget { get; set; }

    public bool Completed { get; set; }

    // Spec 43: sun path for the renderer — align the directional light to
    // the sim's shadow math so visual shadows match sim shade.
    public Float2 SunDirection { get; set; } = Float2.Zero;
    public float SunElevationDegrees { get; set; }

    public List<TileSnapshot> Tiles { get; } = new();

    public List<JunctionSnapshot> Junctions { get; } = new();

    public List<ObjectSnapshot> Objects { get; } = new();

    public List<NpcSnapshot> Npcs { get; } = new();

    /// <summary>
    /// §28.15C v3: тела погибших — та же запись, что и у живых, потому что тело
    /// это и есть она: её лицо, её одежда, её раны.
    ///
    /// <para>
    /// ⭐ ОТДЕЛЬНЫЙ список, а не флаг в <see cref="Npcs"/>. «Npcs» читают ростер,
    /// камера, итоговая сводка и половина панелей, и все они спрашивают одно:
    /// «кто ещё жив». Подмешать туда трупы значило бы, что каждое из этих мест
    /// обязано вспомнить про флаг, а забывшее — молча посчитает покойницу
    /// выжившей. Здесь «живые» так и остаются живыми.
    /// </para>
    /// </summary>
    public List<NpcSnapshot> Corpses { get; } = new();

    public List<DeathRecordSnapshot> DeathRecords { get; } = new();

    public List<MobSnapshot> Mobs { get; } = new();

    public List<CrabSnapshot> Crabs { get; } = new();

    public List<SharkSnapshot> Sharks { get; } = new();

    public List<TraceEventSnapshot> TraceEvents { get; } = new();
}

public sealed class DeathRecordSnapshot
{
    public int EntityId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int Tick { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public string Cause { get; set; } = string.Empty;
}

public sealed class CrabSnapshot
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;
}

public sealed class MobSnapshot
{
    public int Id { get; set; }

    // MobCatalog/MobConfig id ("dog", …) — presentation picks the view by it.
    public string MobId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;

    public float Health { get; set; }

    public string Status { get; set; } = string.Empty;

    // 29C.3 v2: the NPC this dog is chasing/fighting (-1 none) — presentation
    // turns the fighters to face each other.
    public int TargetNpcId { get; set; } = -1;

    // Timed melee: true while the bite is winding up (the 0.2 s snap) —
    // presentation pulses the attack animation on this flag.
    public bool IsAttacking { get; set; }

    // The tick that windup started. The flag above is only ~2 ticks wide, so a
    // frame that skips those ticks (fast-forward, or a decimated stream) never
    // sees it — the stamp does. 0 = never attacked.
    public int AttackStartTick { get; set; }
}

// Spec 40.18: a shark patrolling the water (presentation renders a fin/model).
public sealed class SharkSnapshot
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;
}

public sealed class TileSnapshot
{
    public TileCoord Coord { get; set; } = TileCoord.Zero;

    public bool Walkable { get; set; }

    public bool Blocked { get; set; }

    public bool Indoor { get; set; }

    public bool Water { get; set; }

    public int Elevation { get; set; }
}

public sealed class JunctionSnapshot
{
    public JunctionId Id { get; set; }

    public Float2 WorldPosition { get; set; } = Float2.Zero;

    public List<TileCoord> Tiles { get; } = new();

    public bool Blocked { get; set; }

    public bool Occupied { get; set; }

    public bool Reserved { get; set; }

    // Spec 40.17: this walkable junction straddles a one-level elevation step —
    // presentation draws a climb-seam marker ("точечки на шве") and plays the
    // hand-over-hand climb here.
    public bool IsClimbSeam { get; set; }

    // Spec 40.18: an opened swim junction — presentation draws water an NPC can
    // cross (and sites the swim animation / shark patrol).
    public bool IsSwimmable { get; set; }

    public List<JunctionId> Neighbors { get; } = new();
}

public sealed class ObjectSnapshot
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    // Spec §66: the yaw a built piece stands at (sim angle, degrees CCW from
    // +X). The view maps it through SimulationUnityMapper.ToUnityYawDegrees.
    public float RotationDegrees { get; set; }

    // Spec 29E.3: fuel ticks. For a campfire, > 0 means lit/burning.
    public float ResourceAmount { get; set; }

    public float Wetness { get; set; }

    public float Durability { get; set; } = 1f;

    public float Dirtiness { get; set; }

    public float Bloodiness { get; set; }

    // Spec 40.13: whose body/marker this is (corpse.npc / grave.npc carry the
    // dead NPC's id in CurrentUser) — lets the view adopt the actor's ragdoll.
    public int? OwnerNpcId { get; set; }

    // Spec §50: object variant tag — for body.limb_severed, the BodyPart name
    // so the view bakes the matching bone chain from the owner's mesh.
    public string Variant { get; set; } = string.Empty;

    // Spec §54/29C.3: when the object appeared — lets the view distinguish a
    // freshly slain carcass (play the death clip) from a restored one.
    public int SpawnTick { get; set; }

    // Spec §54: build-site payload, so the view can assemble a piece from its
    // delivered components (a bed growing from hauled stones/logs). Empty
    // BuildProduct ⇒ not a site. Delivered* are how many of each material have
    // been dropped into the site so far (out of Bill*).
    public string BuildProduct { get; set; } = string.Empty;

    public int BillLogs { get; set; }

    public int BillStones { get; set; }

    public int BillLeaves { get; set; }

    public int DeliveredLogs { get; set; }

    public int DeliveredStones { get; set; }

    public int DeliveredLeaves { get; set; }

    // Spec §54.2: beds also bill sticks (rails/slats) and rope (bedroll binding).
    public int BillSticks { get; set; }

    public int BillRope { get; set; }

    public int BillBoards { get; set; }

    public int DeliveredSticks { get; set; }

    public int DeliveredRope { get; set; }

    public int DeliveredBoards { get; set; }

    // §119: an unfinished item exists in the world from the first work cycle.
    public int CraftWorkRequired { get; set; }

    public int CraftWorkDone { get; set; }

    public int CraftBatchCount { get; set; } = 1;

    public int? CraftStationObjectId { get; set; }

    public bool CraftActive { get; set; }

    public List<string> CraftIngredients { get; } = new();

    // §54.14 (r2): meat hanging on the campfire's roasting spit — raw chunks
    // still roasting and cooked ones waiting to be taken (the view hangs them
    // on the crossbar).
    public int RoastingRaw { get; set; }

    public int RoastingCooked { get; set; }

    public List<JunctionId> Junctions { get; } = new();
}

public sealed class NpcSnapshot
{
    public EntityId Id { get; set; }

    // §74: a name ID (resolved through I2 as `npc.<id>.name`), not a label.
    public string DisplayName { get; set; } = string.Empty;

    public string ActorMesh { get; set; } = string.Empty;

    // §74: the composition the view assembles the body from — material donor,
    // hair prefab, voice folder. Empty = the mesh's own, i.e. pre-§74 look.
    public string SkinSet { get; set; } = string.Empty;

    // §85: iris colour, its own axis (Resources/HexLive/Eyes/<id>/). Empty =
    // the eye materials the body prefab shipped with.
    public string EyeColor { get; set; } = string.Empty;

    public string Hairstyle { get; set; } = string.Empty;

    public string VoiceBank { get; set; } = string.Empty;

    // §72: which side this survivor is on, and the one question the UI actually
    // asks — precomputed sim-side so no view file needs the Runtime namespace.
    // Defaults keep every un-updated UI path behaving exactly as pre-§72.
    public Agents.Faction Faction { get; set; } = Agents.Faction.Colony;

    public bool IsHostileToColony { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public Float2 Position { get; set; } = Float2.Zero;

    public float RotationDegrees { get; set; }

    public float Health { get; set; }

    public bool IsFighting { get; set; }

    // ⭐ §104 r10: С КЕМ она дерётся (-1 — ни с кем). Вид наводит боевой IK на
    // ЭТОГО противника. Считать «ближайшего дерущегося» самому он больше не
    // может: пока правило жило в виде, оно уводило руку к волку за полкарты.
    public int CombatOpponentNpcId { get; set; } = -1;

    // Timed melee: true while this NPC's attack ANIMATION window is running
    // (swing started, clip not finished) — presentation plays the attack clip
    // across exactly this window.
    public bool IsSwinging { get; set; }

    // The tick that swing window OPENED. IsSwinging spans 1-2 ticks, which a
    // frame can skip whole (fast-forward renders only the last tick it stepped);
    // the view fires the attack clip on a start tick it has not played yet.
    // 0 = never swung.
    public int SwingStartTick { get; set; }

    // §103 r5: чем она бьёт СЕЙЧАС — с учётом оружия, назначенного сценой.
    // Вид считал это сам («лучшее из рюкзака») и не знал про назначенное:
    // сцена абьюза бьёт кулаками, а на картинке был нож. Пустая строка —
    // кулаки, ровно как в симуляции.
    public string MeleeWeaponId { get; set; } = string.Empty;

    // Which strike variant the current swing uses when the drawn gear has
    // per-strike timings (fists: punches/kicks). The view plays the matching
    // clip from GearConfig.strikes. -1 = single-timing gear (random clip).
    public int StrikeIndex { get; set; } = -1;

    // ⭐ §104 r5: тик, в который по НЕЙ попали (0 — ни разу). Вид ловит смену
    // штампа и в тот же кадр даёт брызгу крови, флинч и звук удара. До этого
    // он узнавал о попадании по падению здоровья (порог 0.02 по среднему —
    // кулак не дотягивал) и по новой ране с гейтом 0.4 с, а звука удара по
    // человеку не было вовсе.
    public int HitStampTick { get; set; }

    // Чем попали: "" — кулаки, id снаряжения, "bite" — зубы.
    public string HitWeaponId { get; set; } = string.Empty;

    // Куда попали — зона тела строкой, той же, что у ран ("Torso", "Head"…):
    // вид ищет кость одной таблицей и отбивает её назад.
    public string HitPart { get; set; } = string.Empty;

    // Откуда ударили (мировая позиция бьющего). Направление отбоя вид считает
    // от кости к этой точке и толкает в противоположную сторону.
    public Float2 HitFrom { get; set; }

    public List<string> BodyParts { get; } = new();

    // "Zone=0.35" — worn-armor absorption per body part (EquipmentMath),
    // for the health window's per-limb protection readout.
    public List<string> PartArmor { get; } = new();

    // Spec §50: zones that have been severed (BodyPart names). The view hides
    // the matching bone chain and stamps a blood stump at the cut.
    public List<string> SeveredParts { get; } = new();

    public string WorstBodyPart { get; set; } = string.Empty;

    // Spec 40.8/40.6: zones with NO garment coverage — skin decals (wounds,
    // dirt, sweat) may only appear on these; clothing hides the rest.
    public List<string> UncoveredParts { get; } = new();

    // Spec 44: zones dressed with a herbal bandage — leaf-wrap decal.
    public List<string> BandagedZones { get; } = new();

    public float Hunger { get; set; }

    public float Thirst { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

    // Spec §53: compassion need (1 = at peace, 0 = wrung out).
    public float Compassion { get; set; }

    public float ThermalDiscomfort { get; set; }

    // Spec 29C.10: signed thermal comfort for the UI — 0 ideal, - cold, + hot.
    public float ThermalComfort { get; set; }

    // Spec 40.1: stamina 0..1 — the energy to act (UI + panting cue).
    public float Stamina { get; set; }

    // Spec 40.6: hygiene 1=clean..0=filthy (UI + grime tint).
    public float Hygiene { get; set; }

    // Spec 40.2: blood 0..1 — bleeds from bad wounds; 0 = death (UI).
    public float Blood { get; set; }

    // Spec 40.7: tan 0=pale..1=dark — sun on bare skin (skin-paint cue).
    public float TanLevel { get; set; }

    // Spec 40.3: bandages left in the med pouch (UI).
    public int Bandages { get; set; }
    public int Pills { get; set; }
    public float Sunburn { get; set; }

    // Spec 35.4: the UV actually hitting this NPC right now — 0 indoors, in
    // water or at night; shade cuts the global index to 20%. For the panel.
    public float EffectiveUv { get; set; }
    public bool IsShaded { get; set; }

    // Spec 40.9: authoritative injury-locomotion hint for the presentation
    // pose layer, derived from body damage + faint. One of: Faint, Crawl,
    // Limp, ArmHang, HeadClutch, Upright.
    public string PostureHint { get; set; } = "Upright";

    // Spec 40.1: winded — stamina spent to the floor. Drives the panting
    // pose/breath in presentation. Derived (Stamina < 0.15), export-only.
    public bool Winded { get; set; }

    // §71: is she RUNNING? The sim decides the gait from the urgency reasons;
    // the view must NOT re-derive it from measured speed (that is what had the
    // whole colony permanently at a trot). Drives the GaitBlend tree.
    public bool IsRunning { get; set; }

    // §71: the sprint reserve (0..1), spent running and refilled walking.
    public float Breath { get; set; }

    // §21.21B hex-step hop: "Up"/"Down" while the sim walks the jump path
    // (Movement.HopTimer > 0), else "". The view starts the jump clip and its
    // vertical arc on the rising edge of this signal.
    public string HopKind { get; set; } = string.Empty;

    // §21.21B: the tile the hop lands on — the view derives the exact target
    // ground height (water dives land below the surface, not one step down).
    public TileCoord HopTargetTile { get; set; } = TileCoord.Zero;

    // §21.21B v15: the tile the hop took off from. The arc's height delta is
    // target - from, so a hop first SEEN mid-window still arcs the right way;
    // deriving it from Tile broke there, because the sim commits Tile to the
    // landing tile while HopKind is still set (delta 0 = body hangs a step off
    // the ground, then teleports when the window closes).
    public TileCoord HopFromTile { get; set; } = TileCoord.Zero;

    // §21.21B: the tick the hop started. The view arms the arc on a start it
    // has not played yet (HopKind's rising edge is lost whenever the frame
    // skips the tick that raised it) and, when it observes one late, shortens
    // the arc to what is left of the window instead of overshooting with a
    // full-length one. 0 = never hopped.
    public int HopStartTick { get; set; }

    // Spec 40.13: knocked out — the presentation lays the body limp.
    public bool IsFainted { get; set; }

    // Spec §60: comatose — the presentation plays the death clip and holds
    // the body motionless as if dead until IsUnconscious clears, then the
    // get-up plays (the wake grace covers it).
    public bool IsUnconscious { get; set; }

    // Spec §105: она УМИРАЕТ — лежит, и запас смерти тикает. Вид роняет тело
    // той же цепочкой падения, что и обморок, а полоска умирания едет в
    // Effects чипом Dying (сила чипа = сколько уже вытекло), поэтому здесь
    // хватает одного флага.
    public bool IsDying { get; set; }

    // Spec §110: сломалась от стресса — ЛЕЖИТ И ПЛАЧЕТ, но в сознании. Вид
    // укладывает её сонной цепочкой (LieDown→Sleep, а не падением), держит
    // вторую позу сна и лицо «cry», и периодически даёт слёзный смайл со
    // всхлипом. Отдельный флаг именно потому, что это НЕ беспамятство.
    public bool IsCrying { get; set; }

    // §81.10: понурая походка после сцены — вид подменяет ей клип шага, а сим
    // одновременно режет скорость вдвое. Флаг, а не таймер вида: так он
    // переживает сейв, доезжает до удалённого зрителя и не врёт на перемотке.
    public bool IsSadWalk { get; set; }

    // §105.14: притворяется мёртвой — очнулась, но не встаёт, пока рядом враг.
    // Вид держит её упавшей (цепочка падения, без сна): для игрока это тело,
    // которое лежит подозрительно неподвижно, а панель говорит, что она жива.
    public bool IsPlayingDead { get; set; }

    // Spec §53 r2: while this NPC is aiding a housemate (Feed/Hydrate/Treat/…),
    // is her WARD lying down (coma/faint/asleep/prone)? The kneeling "tending"
    // craft pose only plays over a lying ward; over a standing ward the helper
    // just stands and shows the item in hand. False when not aiding.
    public bool AidTargetLyingDown { get; set; }

    // Iter 28: sitting at a one-step ledge junction — the presentation
    // lifts the body so the butt rests on the upper step. Export-only.
    public bool IsLedgeSit { get; set; }

    // Iter 28 fix: how many elevation steps her butt is BELOW the seat
    // surface (the higher tile of the seam) — 0 when she already stands on
    // the higher tile (sit on her own edge, e.g. a land/water rim), 1 when
    // she perches up from the lower tile. The view lifts by this × per-step.
    public int LedgeSeatStepsUp { get; set; }

    // Spec 41.5: wake-up grace — standing still, coming to her senses after
    // sleep; presentation holds the idle so the get-up clip can finish.
    public bool IsWaking { get; set; }

    // Spec 40.13: stress 0=calm..1=breaking point (UI).
    public float Stress { get; set; }

    public string CurrentGoal { get; set; } = string.Empty;

    // Spec §64: the colonist's current dream (aspiration) — the DreamType name,
    // localized by presentation into the character-panel dream pill. "None" when
    // she has nothing left to dream of.
    public string CurrentDream { get; set; } = string.Empty;

    public string PlanStatus { get; set; } = string.Empty;

    public string MovementStatus { get; set; } = string.Empty;

    public string ExecutionStatus { get; set; } = string.Empty;

    public string CurrentInteraction { get; set; } = string.Empty;

    // The concrete inventory object that should be visible in the acting hand
    // for the current interaction. Empty means empty hands.
    public string HeldItemId { get; set; } = string.Empty;

    // Spec 28.15E: the subject of the current Talk (TalkTopic name), or "" when
    // not talking. The presentation shows the matching emoji in an overhead
    // bubble while the speaker is chatting.
    public string TalkTopic { get; set; } = string.Empty;

    // §108: о КОМ разговор, когда тема — человек (сегодня только Stranger).
    // Вид берёт по этому id запечённый круглый портрет и ставит его в бабл
    // вместо эмодзи. Null для всех прочих тем.
    public int? TalkTopicPeerId { get; set; }

    // Spec 28.15E: last talk outcome, for the Sims-style relationship pop over
    // the head. TalkResultTick is when the outcome resolved (the view fires the
    // "+/-" once per new tick); TalkResultDelta is the signed affinity change
    // (+ good chat, - quarrel), whose magnitude drives single vs double glyph.
    public int TalkResultTick { get; set; } = -1;
    public float TalkResultDelta { get; set; }

    // One-shot social cue for overhead bubbles: talk request, refusal, aid,
    // quarrel, resentment, or shock. Empty when no recent cue.
    public int SocialCueTick { get; set; } = -1;
    public string SocialCueKind { get; set; } = string.Empty;
    public int? SocialCuePeerId { get; set; }
    public string SocialCueItemId { get; set; } = string.Empty;

    // §Wardrobe-anim: the tick window of the current timed interaction, so the
    // view can split dress/undress into their gather + garment-in-hand beats.
    // Both 0 when no timed interaction is running.
    //
    // This used to be a precomputed 0..1 progress float — but progress is a
    // function of the CURRENT tick, so it changed every single tick and marked
    // an otherwise-motionless crafting colonist as "changed" on every frame.
    // Two ints that change once, plus ProgressAt() below, cost nothing to keep
    // fresh and let the view derive the same number exactly.
    public int ExecutionStartTick { get; set; }

    public int ExecutionEndTick { get; set; }

    /// <summary>
    /// Fraction [0..1] of the current timed interaction at <paramref name="worldTick"/>,
    /// or 0 when none is running. Lives here rather than in the view so both
    /// ends compute it identically.
    /// </summary>
    public float ProgressAt(int worldTick)
    {
        var total = ExecutionEndTick - ExecutionStartTick;
        if (total <= 0)
        {
            return 0f;
        }

        var elapsed = (worldTick - ExecutionStartTick) / (float)total;
        return elapsed < 0f ? 0f : elapsed > 1f ? 1f : elapsed;
    }

    // §77.5: how long the WHOLE current interaction lasts, in sim seconds
    // (ticks × TickDeltaTime). The view divides the clip length by this to play
    // the work animation exactly once per interaction, so a job stretched by
    // the §76 attributes or shortened by a §78 tool still reads as one gesture.
    // 0 when no timed interaction is running.
    public float InteractionSeconds { get; set; }

    // §Wardrobe-anim: the garment the NPC is holding in hand mid dress/undress
    // (spawned as a hand prop by the view), or empty. During the "don" beat of
    // a dress it's the target garment; during the "gather" beat of an undress
    // it's the piece just taken off. Empty at all other times.
    public string HeldGarmentId { get; set; } = string.Empty;

    // §40.6 r2 (laundry-in-hand): live condition of the held garment so the
    // hand prop shows the dirt/blood actually washing OUT during the scrub.
    public float HeldGarmentDirt { get; set; }
    public float HeldGarmentBlood { get; set; }
    public float HeldGarmentWet { get; set; }
    public float HeldGarmentDurability { get; set; } = 1f;

    // §Wardrobe-anim: the world object this NPC is interacting with (if any),
    // so the renderer can hide a garment lying on the ground once its owner has
    // picked it up into hand for the "don" beat (no double-visible garment).
    public int? TargetObjectId { get; set; }

    public TileCoord? TargetTile { get; set; }

    public bool IsStarving { get; set; }

    public List<string> InventoryItems { get; } = new();

    public int InventoryUsedSlots { get; set; }

    // "definitionId\tcount" for stackable carried resources folded into one row.
    public List<string> InventoryStacks { get; } = new();

    // Spec 40.11: "definitionId\tdurability" per carried item — lets the
    // inventory detail view show clothing HP even after a damaged garment is
    // taken off.
    public List<string> InventoryDurability { get; } = new();

    // Per-instance clothing condition for garments carried in the backpack.
    public List<string> InventoryWetness { get; } = new();

    public List<string> InventoryDirtiness { get; } = new();

    public List<string> InventoryBloodiness { get; } = new();

    // "definitionId\tamountLiters\tcapacityLiters" for carried water containers.
    // The UI treats bottle and pierced coconut as one water-container category.
    public List<string> InventoryWater { get; } = new();

    public List<string> WornItems { get; } = new();

    // Spec §52.8: tool ids currently parked in a worn holster's typed weapon
    // slots. Presentation pins each to the matching tool.* anchor of the worn
    // holster prefab — the leg-slung look — skipping any id shown in the hand.
    public List<string> HolsteredItems { get; } = new();

    // Spec 40.11: "definitionId\tdurability" per worn garment — for the
    // character panel's wear progress bars.
    public List<string> WornDurability { get; } = new();

    // Spec 35.5: "definitionId\twetness" per worn garment — rain soaks cloth,
    // fire/racks dry it; presentation shows a wet sheen that fades as it dries.
    public List<string> WornWetness { get; } = new();

    // "definitionId\tdirtiness" per worn garment.
    public List<string> WornDirtiness { get; } = new();

    public List<string> WornBloodiness { get; } = new();

    // Spec 40.8B: "Zone|seed|heal01" per open wound — each maps to ONE decal
    // whose exact spot/look derive from seed and whose alpha fades with heal.
    public List<string> Wounds { get; } = new();

    // Spec §48: active effects, "Kind\tintensity[\tdetailKey]" each — derived
    // read-only by EffectEvaluator. The optional localized detail explains the
    // concrete reason for coma/fainting/crying/dying in the hover tooltip.
    public List<string> Effects { get; } = new();

    // Spec §76: the character sheet. "Strength\t0.62" per innate attribute,
    // "Combat\t0.31" per learned trade — same tab-separated idiom as Effects.
    public List<string> Attributes { get; } = new();

    public List<string> Skills { get; } = new();

    // Spec §76.6: finished localization keys ("perk.strength.high"), resolved
    // SIM-side. The band thresholds are Spec76 knobs, so a view that re-derived
    // them would disagree with the simulation the moment either was tuned.
    public List<string> Perks { get; } = new();

    // Spec 40.8B: HP fraction held hostage by open wounds (Fallout-style red
    // bar segment — regen can't cross it; it shrinks as wounds close).
    public float WoundLockedHp { get; set; }

    // §105 r2: ХУДШАЯ витальная зона (голова/грудь) — то, что панель рисует
    // кольцом вокруг портрета. НЕ то же, что Health: среднее по семи зонам
    // врёт в обе стороны (разбитая грудь при целых конечностях читается как
    // «0.75, всё неплохо», хотя следующий удар убивает). Считается симом —
    // «что такое витальная зона» знает BodyState.VitalHealth, и вид не должен
    // заводить второе мнение.
    public float VitalHealth { get; set; } = 1f;

    public int InventoryCapacity { get; set; }

    // §28.15C v3: каким клипом она упала. Число обязано прийти из симуляции, а
    // не родиться в кадре: иначе тело лежало бы в разной позе у сервера, у
    // каждого зрителя и после каждой перезагрузки.
    public int DeathAnimVariant { get; set; }

    public int? GoalLockEndTick { get; set; }

    public List<string> CooldownGoals { get; } = new();

    public List<string> Relationships { get; } = new();

    public List<RelationshipSnapshot> RelationshipDetails { get; } = new();

    public int KnownObjectCount { get; set; }

    public List<string> KnownObjects { get; } = new();

    public List<JunctionId> Path { get; } = new();

    public List<GoalScoreSnapshot> GoalScores { get; } = new();
}

public sealed class RelationshipSnapshot
{
    public int OtherId { get; set; }

    public string OtherName { get; set; } = string.Empty;

    public float Trust { get; set; }

    public float Familiarity { get; set; }

    public float Affinity { get; set; }
}

public sealed class GoalScoreSnapshot
{
    public string Goal { get; set; } = string.Empty;

    public float FinalScore { get; set; }
}

public sealed class TraceEventSnapshot
{
    // Monotonic id from SimulationEventBuffer — lets a consumer dedup by watermark
    // instead of by object identity, which no longer works once events are exported.
    public long Seq { get; set; }

    public int Tick { get; set; }

    public int? EntityId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

}

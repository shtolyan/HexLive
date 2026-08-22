using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// §72: the blows of a human fight, and ONLY the blows. Who is fighting whom,
// who rallies, who runs and who dies is RaidSystem's business — this mirrors
// exactly the MobSystem/AnimalCombatSystem split, for the same reason: the
// timing has to run on the Fast layer or a swing cannot be timed at all.
//
// Every fighter carries Mind.CombatOpponentNpcId while the fight is live; that
// is both the pairing and the claim on the single swing slot each body has.
public sealed class HumanCombatSystem : ISimulationSystem
{
    public string Name => nameof(HumanCombatSystem);

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        if (!Spec72.Enabled)
        {
            return;
        }

        foreach (var actor in world.Entities.Npcs.Values)
        {
            if (actor.Mind.CombatOpponentNpcId is not { } opponentId)
            {
                continue;
            }

            if (actor.Health <= 0f || actor.IsUnconscious(world.Tick) || actor.Body.IsProne ||
                !world.Entities.Npcs.TryGetValue(opponentId, out var opponent) ||
                opponent.Health <= 0f || opponent.IsUnconscious(world.Tick) || opponent.Body.IsProne)
            {
                HumanCombatPairing.ClearFor(world, actor);
                CombatHelpSystem.ClearAssist(actor);
                continue;
            }

            // Water ends an existing human pair instead of leaving a swing
            // slot and assist lock alive on opposite sides of the shoreline.
            if (!CombatMedium.NpcMelee(world, actor, opponent) &&
                (CombatMedium.IsNpcSwimming(world, actor) ||
                 CombatMedium.IsNpcSwimming(world, opponent)))
            {
                HumanCombatPairing.ClearFor(world, actor);
                CombatHelpSystem.ClearAssist(actor);
                continue;
            }

            // Turn to face — the same courtesy the dog fight pays.
            //
            // §109.13: ⭐ НО ТОЛЬКО ЕСЛИ СТОИШЬ. Идущего разворачивает
            // MovementSystem — по следующему узлу пути; этот же доворот целил
            // в противника, и на каждом быстром тике тело дёргалось между
            // двумя хотелками: измерено ±22.5° туда-сюда на месте, а на бегу
            // — «колбасит». Ровно такая же оговорка уже стоит у отжима
            // стойки ниже (`actor.Movement.IsMoving`), просто до поворота её
            // не донесли. Пар вне сцен стало много (§109 — ответный бой,
            // защитницы), и старая дыра вылезла на каждом подходе.
            if (!actor.Movement.IsMoving)
            {
                FaceOpponent(world, actor, opponent);
            }

            // And square up at arm's length — the same spacing the dog fight
            // keeps (AnimalCombatSystem.ClampToHoldDistance).
            HoldStandOff(world, actor, opponent);

            var hunting = FactionRelations.AreHostile(world, actor, opponent) &&
                actor.Mind.CurrentGoal == GoalType.GroupHunt &&
                actor.Mind.GroupHuntTargetNpcId is { } huntTarget &&
                huntTarget.Equals(opponent.Id);
            var raiding = FactionRelations.AreHostile(world, actor, opponent) &&
                ((actor.Mind.RaidTargetNpcId is { } raidTarget && raidTarget.Equals(opponent.Id)) ||
                 (actor.Mind.AbuseTargetNpcId is { } abuseTarget && abuseTarget.Equals(opponent.Id)));
            var expelling = FactionRelations.AreHostile(world, actor, opponent) &&
                actor.Mind.CurrentGoal == GoalType.Expel &&
                actor.Mind.ExpulsionTargetNpcId is { } expelTarget &&
                expelTarget.Equals(opponent.Id);
            var damageMultiplier = raiding && actor.Mind.CurrentGoal == GoalType.Raid
                ? Spec72.RaidStrikeDamageMult
                : 1f;

            var inReach = InteractionReach.CanStrike(world, actor, opponent);
            if (!MeleeSwing.TryAdvanceHumanSwing(world, actor, opponent, inReach,
                    damageMultiplier, out var damage, out var weaponId, out var clipSeconds))
            {
                continue;
            }

            if (!inReach || damage <= 0f)
            {
                // The selected part belongs to this resolved wind-up. Never
                // let a miss leak that target into the next swing.
                actor.PendingHumanStrikeTargetId = null;
                continue; // the swing resolved into thin air — she stepped away
            }

            // §97: «нападает» — это и налёт, и сцена абьюза. Без второй половины
            // ЕГО удары помечались как ответные (RaidFoughtBack), и по логу было
            // не разобрать, кто кого бьёт.
            // §108: и третья половина — групповая охота. Тут «нападает» уже
            // ОНА, и без этой ветки её удары попадали бы в лог как ответные,
            // то есть расправа читалась бы как самооборона.
            // §103: постановочная сцена САМА считает свои удары и сама решает,
            // когда открыть следующий замах — см. AI/FightScene. Здесь стоял
            // блок, знавший про абьюз поимённо: он различал бьющего и
            // отбивающуюся, лез в Spec81 за числом ударов и разводил их по
            // БАЗОВОЙ длительности оружия, тогда как сам замах брался из
            // варианта удара. Две мерки на одно расстояние — ровно та болезнь,
            // что дала мёртвую зону §102, только во времени.
            FightScene.OnBlowLanded(world, actor, clipSeconds);

            if (hunting)
            {
                actor.Mind.GroupHuntBlowsLanded++;
                // Счёт расправы висит на НЁМ: охотница может уйти, побои
                // остаются (§108.5).
                opponent.Mind.GroupHuntBlowsTaken++;
            }

            MeleeSwing.ApplyHumanBlow(world, actor, opponent, damage, weaponId,
                hunting ? "GroupHuntStruck" : expelling ? "CampExpelStruck" :
                raiding ? "RaidStruck" : "RaidFoughtBack");

            // Emit the outcome HERE, at the blow that caused it. MobSystem
            // sweeps every 0-health NPC on the next medium pass — before
            // RaidSystem gets a look — so a trace left to the medium layer
            // would simply never fire, and the soak would count zero raider
            // deaths while the man died six times.
            if (opponent.Health <= 0f)
            {
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, raiding || expelling ? "RaidKilledVictim" : "RaiderKilled",
                        $"NPC{opponent.Id.Value} ({opponent.DisplayName}) killed by " +
                        $"NPC{actor.Id.Value} ({actor.DisplayName}) at " +
                        $"Tile={opponent.Tile.Q},{opponent.Tile.R}");
                }
                HumanCombatPairing.ClearFor(world, opponent);
                CombatHelpSystem.ClearAssist(opponent);
            }
        }
    }

    // §72.5 combat stand-off — the human mirror of the mob fight's
    // ClampToHoldDistance. A paired STANDING fighter backs her rendered
    // Position off until the pair is MeleeHoldDistance apart; each side runs
    // this for itself, so a mutual pairing splits the gap symmetrically and a
    // one-sided one (a defender on the raider) converges alone. A MOVING
    // fighter is owned by MovementSystem and left alone — a chase can always
    // close, and there is no tug-of-war over Position. Junction/Tile (reach,
    // pathing, occupancy) are untouched, and movement self-heals: the next
    // walk simply starts from the shifted spot.
    private static void HoldStandOff(WorldState world, NPCState actor, NPCState opponent)
    {
        var hold = Spec72.MeleeHoldDistance;
        if (hold <= 0.001f || actor.Movement.IsMoving)
        {
            return;
        }

        var distance = HexSpatialMath.Distance(actor.Position, opponent.Position);
        if (distance >= hold)
        {
            return;
        }

        // Straight away from the opponent. Two coincident bodies (the
        // same-junction corner case) split along a stable per-pair axis —
        // hashed from the ids only, never the tick, so it cannot jitter.
        Float2 dir;
        if (distance > 0.0001f)
        {
            dir = HexSpatialMath.Normalize(actor.Position - opponent.Position);
        }
        else
        {
            var angle = MathUtil.Hash01(world.Seed, actor.Id.Value, opponent.Id.Value, 811) *
                2f * System.MathF.PI;
            dir = new Float2(System.MathF.Cos(angle), System.MathF.Sin(angle));
        }

        // Each tick closes at most half the remaining gap: two standing
        // fighters meet the ring exactly instead of overshooting past it, and
        // a lone adjuster still converges geometrically within a second.
        var step = System.MathF.Min(
            Spec72.MeleeHoldGlideSpeed * world.TickDeltaTime,
            (hold - distance) * 0.5f);
        var next = actor.Position + dir * step;

        // §109.11: стойка ПЯТИТСЯ, но не УЕЗЖАЕТ. Junction не двигается вместе
        // с Position, и отжим без предела дрейфа против непрерывно наступающего
        // противника превращался в караван через полкарты: она скользит в
        // боевой позе, «убегая» без единого шага, он бежит следом. Дальше
        // радиуса от СВОЕГО узла стойка не отступает — упёрлась, значит стоит
        // (и это честно: за спиной может быть обрыв, которого Position-глайд
        // не видит).
        // Гейт ЗАКРЫТЫЙ по умолчанию: нет якоря — не двигаемся. Позитивная
        // форма («якорь есть И далеко ⇒ стоп») была дырой: CurrentJunction
        // обнуляют извне, когда под ногами возводят стену (§45 r5), чинит это
        // PerceptionSystem на СРЕДНЕМ такте, а отжим идёт на быстром — и в
        // окне между ними предел не действовал вовсе.
        if (actor.CurrentJunction is not { } ownJunction ||
            !world.Junctions.Items.TryGetValue(ownJunction, out var anchor) ||
            HexSpatialMath.Distance(next, anchor.WorldPosition) >
                Spec72.MeleeHoldMaxDriftWorldUnits)
        {
            return;
        }

        // §109.14: ⭐ ОТЖИМ НЕ ВЫХОДИТ ЗА СВОЙ ГЕКС. Двигается ТОЛЬКО Position,
        // а npc.Tile остаётся прежним — и высоту пола вид берёт именно от
        // тайла (ActorGroundY). Отжатый на соседний гекс рисуется на высоте
        // СТАРОГО: если сосед выше, тело уходит в землю — «боевая стойка по
        // игреку в землю его вбивает». Держим внутри своего гекса: 0.8×радиуса
        // заведомо меньше вписанной окружности (0.866×R), так что тайл под
        // ногами не меняется, а значит и высота честная.
        // Порог — НЕ фиксированный радиус. По описанной (1.0R) тело всё равно
        // вылезало за грань в направлениях между вершинами, а по вписанной
        // (0.866R) отжим запрещался бы законно стоящей НА вершине — узлы
        // решётки сидят и там. Правило поэтому такое: за вписанную окружность
        // не выталкиваем, а тому, кто уже стоит дальше, не даём уехать ЕЩЁ
        // дальше от центра своего гекса.
        var centre = HexSpatialMath.TileToWorld(actor.Tile);
        var wasOut = HexSpatialMath.Distance(actor.Position, centre);
        var willBeOut = HexSpatialMath.Distance(next, centre);
        if (willBeOut > System.Math.Max(wasOut, HexSpatialMath.HexRadius * 0.866f))
        {
            return;
        }

        actor.Position = next;
    }

    // §81.15: internal — жертва, почуявшая приближение, разворачивается тем же
    // манером, что и боец, только без пары (пара тут означает замах).
    internal static void FaceOpponent(WorldState world, NPCState actor, NPCState opponent)
    {
        var direction = new Float2(
            opponent.Position.X - actor.Position.X,
            opponent.Position.Y - actor.Position.Y);
        if (System.Math.Abs(direction.X) < 0.0001f && System.Math.Abs(direction.Y) < 0.0001f)
        {
            return;
        }

        actor.RotationDegrees = MathUtil.RotateTowards(
            actor.RotationDegrees,
            HexSpatialMath.AngleDegrees(direction),
            actor.TurnSpeed * 4f * AttributeMath.TurnSpeedMult(actor) * world.TickDeltaTime);
    }
}

}

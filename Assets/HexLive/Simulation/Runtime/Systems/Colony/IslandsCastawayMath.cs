using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §157.5–§157.6: потерпевшая режима «Острова». Раз в семь дней прибой выносит
/// на берег ЭТОГО острова голую женщину без сознания: энергия 0 (спит, где
/// упала), обе ноги ниже порога ползания, незабинтованная рана на груди,
/// которая без бинта убьёт её — но не раньше чем через полдня. Она сразу
/// член лагеря острова (не <see cref="Faction.Castaway"/>: та союзна только
/// <see cref="Faction.Colony"/>, и девушки прочих лагерей к ней бы не
/// подошли), а её появление — SOS: память лагеря узнаёт, где она лежит и что
/// ей нужен бинт, без взгляда и без радиуса.
/// </summary>
internal static class IslandsCastawayMath
{
    // Авторское тело — литералы под ареной (как §146.10), не ручки баланса:
    // срок «умрёт без бинта, но ≥ полдня» держится ИМЕННО этими числами.
    // Toughness пиннится: множитель деградации lerp(1.7, 0.03, T) при
    // случайной T дал бы срок от часа до недели.
    internal const float Toughness = 0.90f;
    internal const float TorsoHealth = 0.60f;
    internal const float TorsoWoundSeverity = 0.45f;
    internal const float TorsoWoundClot = 0.20f;
    internal const float TorsoWoundBleedFactor = 0.25f;
    internal const int ShoreMinDistanceTiles = 4;
    internal const int ShoreMaxDistanceTiles = 30;

    private static readonly List<EntityId> _scratch = new();

    internal static bool TrySpawn(
        WorldState world, Faction faction, TileCoord home, int arrival, EntityId id)
    {
        if (!PopulationArrivalMath.TryPickShoreLanding(
                world, home, ShoreMinDistanceTiles, ShoreMaxDistanceTiles,
                sequence: arrival + (int)faction * 1000, salt: 15803, out var landing) &&
            !PopulationArrivalMath.TryPickLanding(world, home, arrival, salt: 13201, out landing))
        {
            return false;
        }

        var look = PopulationArrivalMath.RollFemaleLook(world, id.Value);
        var npc = new NPCState
        {
            Id = id,
            DisplayName = look.NameId,
            ActorMesh = look.Mesh,
            SkinSet = look.SkinSet,
            EyeColor = look.EyeColor,
            Hairstyle = look.Hairstyle,
            VoiceBank = look.VoiceBank,
            Faction = faction,
            Fragment = landing.Fragment,
            Tile = landing.Tile,
            Position = landing.Position,
            CurrentJunction = landing.Junction,
        };

        npc.Needs.Hunger = 0.45f;
        npc.Needs.Thirst = 0.35f;
        npc.Needs.Energy = 0f;
        npc.Needs.Comfort = 0.20f;
        npc.Needs.Social = 0.50f;
        npc.Needs.ThermalDiscomfort = 0.60f;
        npc.Needs.Blood = 0.62f;

        npc.Body.Parts[BodyPart.Head] = 0.85f;
        npc.Body.Parts[BodyPart.Torso] = TorsoHealth;
        npc.Body.Parts[BodyPart.Pelvis] = 0.85f;
        npc.Body.Parts[BodyPart.ArmL] = 0.70f;
        npc.Body.Parts[BodyPart.ArmR] = 0.55f;
        npc.Body.Parts[BodyPart.LegL] = 0.32f;
        npc.Body.Parts[BodyPart.LegR] = 0.36f;
        npc.Health = npc.Body.Mean();

        // Грудь: канал смерти — деградация незабинтованной витальной зоны.
        npc.Wounds.Add(new WoundState
        {
            Id = npc.NextWoundId++,
            Zone = BodyPart.Torso,
            Severity = TorsoWoundSeverity,
            Heal01 = 0f,
            Clot01 = TorsoWoundClot,
            Stabilized = false,
            Festering = true,
            BleedFactor = TorsoWoundBleedFactor,
            Seed = world.Seed ^ id.Value ^ 15811
        });
        // Нога: второй бинт, не смертельная.
        npc.Wounds.Add(new WoundState
        {
            Id = npc.NextWoundId++,
            Zone = BodyPart.LegL,
            Severity = 0.15f,
            Heal01 = 0f,
            Clot01 = 0.60f,
            Stabilized = false,
            BleedFactor = 0.30f,
            Seed = world.Seed ^ id.Value ^ 15813
        });

        npc.CompassionTrait = Spec53.TraitMin +
            MathUtil.Hash01(world.Seed, id.Value, 53, 5301) *
            (Spec53.TraitMax - Spec53.TraitMin);
        AttributeMath.Roll(npc, world.Seed, id.Value);
        npc.Attributes.Set(AttributeKind.Toughness, Toughness);
        TraitMath.Roll(npc, world.Seed, id.Value);
        // Голая и с пустыми руками — только тело доплыло.
        EquipmentMath.Recalculate(world, npc);

        PopulationArrivalMath.AddToWorld(world, npc, landing.Junction);
        Trace.Emit(world, npc.Id, "CastawayWashedAshore",
            $"Day={EnvironmentSystem.CalendarDay(world.Tick)} Camp={faction} " +
            $"Name={npc.DisplayName} Tile={npc.Tile.Q},{npc.Tile.R} " +
            $"Junction={landing.Junction.Value}");

        // ПОСЛЕ AddToWorld: AnchorLyingBody читает occupancy, а TryAbort на
        // пустом плане — no-op. Энергия 0 → сон где упала (§60 r2).
        NeedsDecaySystem.EnterComa(world, npc, ComaCause.Exhaustion);
        Broadcast(world, npc);
        return true;
    }

    /// <summary>
    /// §157.6: SOS. Не через TryMoanForHelp (его глушит IsUnconscious) —
    /// прямая запись в память каждой живой девушки ЕЁ лагеря: где лежит, что
    /// беспомощна и что ей нужен бинт. Это память, не взгляд: LastSeenTick на
    /// тик позади, иначе перцепция примет запись за «вижу сейчас».
    /// </summary>
    internal static void Broadcast(WorldState world, NPCState victim)
    {
        victim.Mind.LastHelpCryTick = world.Tick;
        var aidKind = AidAssessment.Assess(victim, world.Tick, out var severity);
        if (aidKind == AidKind.None)
        {
            aidKind = AidKind.Treat;
        }

        severity = System.Math.Max(severity, Spec53.HeavyAidSuffering);
        SocialCueSignals.Stamp(world, victim, "HelpMoan", null);

        foreach (var hearer in world.Entities.Npcs.Values)
        {
            if (hearer.Id.Equals(victim.Id) || hearer.Faction != victim.Faction ||
                hearer.Health <= 0f || hearer.IsUnconscious(world.Tick) ||
                hearer.Execution.CurrentInteraction == InteractionType.Sleep ||
                CampDiplomacyMath.CareWillingness(world, hearer, victim) <= 0f)
            {
                continue;
            }

            if (!hearer.Memory.KnownAgents.TryGetValue(victim.Id, out var met))
            {
                met = new Memory.AgentMemory { Id = victim.Id };
                hearer.Memory.KnownAgents[victim.Id] = met;
            }

            met.Faction = victim.Faction;
            met.Tile = victim.Tile;
            met.Junction = victim.CurrentJunction;
            met.LastSeenTick = world.Tick - 1;
            met.Suffering = severity;
            met.AidKind = aidKind;
            met.Helpless = true;
            SocialCueSignals.Stamp(world, hearer, "MoanHeard", victim.Id);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, hearer.Id, "CastawaySosHeard",
                    $"Victim=NPC{victim.Id.Value} Suffering={severity:F2} Kind={aidKind}");
            }
        }
    }

    /// <summary>
    /// §157.6: повтор SOS раз в кулдаун крика, пока лежит без сознания вне
    /// лагеря, её не несут и никто не назначен ей в помощь.
    /// </summary>
    internal static void RunSos(WorldState world)
    {
        _scratch.Clear();
        foreach (var pair in world.Entities.Npcs)
        {
            var npc = pair.Value;
            if (npc.Health > 0f && npc.Mind.ComaCause != ComaCause.None &&
                !npc.IsBeingCarried && npc.Mind.PendingAidFrom is null &&
                FactionRelations.IsGirlCamp(npc.Faction) &&
                !ColonyQueries.InCamp(world, npc.Tile, npc.Faction) &&
                world.Tick - npc.Mind.LastHelpCryTick >= Spec57.HelpCryCooldownTicks)
            {
                _scratch.Add(pair.Key);
            }
        }

        _scratch.Sort((a, b) => a.Value.CompareTo(b.Value));
        foreach (var id in _scratch)
        {
            Broadcast(world, world.Entities.Npcs[id]);
        }
    }
}

}

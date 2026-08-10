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

/// <summary>Trace verbosity knob. The per-tick chatter events (TickStart,
/// ExecProgress, Path*/Movement* step spam) allocate an interpolated string
/// each — ~1600 strings per combat frame in the 2026-07-19 deep capture —
/// yet nothing consumes them in a normal game (GameHistoryLog filters by its
/// own whitelist). Presentation turns Verbose OFF in player builds; the
/// editor and headless harness probes keep the full stream (default true).</summary>
public static class SimTrace
{
    public static bool Verbose = true;
}

internal static class Trace
{
    public static void Emit(WorldState world, EntityId entityId, string type, string message)
    {
        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = entityId.Value,
            Type = type,
            Message = message
        });

        // Единственная точка, которая видит КАЖДОЕ событие ровно один раз —
        // поэтому самописец висит здесь, а не у потребителей. В сборке игрока
        // поле null, и это стоит одной проверки (spec §30.14).
        world.FlightRecorder?.Record(entityId.Value, world.Tick, type, message);

        // §30.17: по той же причине здесь же ставится штамп причины смерти.
        // Он ОБЯЗАН быть состоянием, а не поиском по кольцу: кольцо — это
        // диагностика, его глубина зависит от многословности трассы, а Cause
        // уходит в сейв и по проводу (см. NpcMind.DeathCauseText).
        if (DeathCauses.Contains(type) &&
            world.Entities.Npcs.TryGetValue(entityId, out var dying))
        {
            dying.Mind.DeathCauseText = type + ": " + message;
            dying.Mind.DeathCauseTick = world.Tick;
        }
    }

    /// <summary>§30.17: события, которые ОБЪЯСНЯЮТ смерть. Список один на игру
    /// и живёт здесь, рядом со штампом; MobSystem читает готовую строку.
    /// Все они — «хроника», а не болтовня: гейт трассировки их не касается,
    /// иначе причина смерти зависела бы от положения отладочного флага.</summary>
    private static readonly System.Collections.Generic.HashSet<string> DeathCauses =
        new(System.StringComparer.Ordinal)
        {
            "BledOut",
            "DogFight",
            "Drowned",
            "Heatstroke",
            "Hypothermia",
            "LimbSevered",
            "PreyFoughtBack",
            "Preyed",
            // §72: без этих двух каждая смерть в рейде записывалась бы как
            // выведенное истощение — и соак врал бы про ту самую механику,
            // которую им тюнят.
            "RaidFoughtBack",
            "RaidStruck",
            "SharkBite",
            "StarvedToDeath",
            "Sunburn",
            "VitalPartDestroyed",
        };

    public static void EmitSystem(WorldState world, string type, string message)
    {
        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = null,
            Type = type,
            Message = message
        });
    }

    public static string FormatTile(TileCoord? tile) => tile is null ? "-" : $"{tile.Value.Q},{tile.Value.R}";

    public static string FormatNeeds(NPCNeeds n) =>
        $"H={n.Hunger:F2} W={n.Thirst:F2} E={n.Energy:F2} C={n.Comfort:F2} S={n.Social:F2} T={n.ThermalDiscomfort:F2}";

    public static string FormatPos(Float2 p) => $"({p.X:F2},{p.Y:F2})";

    public static string FormatJunction(JunctionId? j) => j is null ? "-" : j.Value.Value.ToString();
}

}

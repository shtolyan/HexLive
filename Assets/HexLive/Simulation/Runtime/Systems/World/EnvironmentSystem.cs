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

// Spec 19.7A: derives the clock from the tick and drives the temperature
// sinusoid. Must run before needs/temperature systems within the Slow layer.
public sealed class EnvironmentSystem : ISimulationSystem
{
    public string Name => nameof(EnvironmentSystem);

    public TickLayer Layer => TickLayer.Slow;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.PerChunk;

    public static int DayLengthTicks => WorldBalance.DayLengthTicks;
    // The gameplay cadence for the seeded per-cycle rolls (rain, storms, surf
    // gifts, raids) — deliberately NOT the visual day, see WorldBalance.
    public static int EventCycleTicks => WorldBalance.EventCycleTicks;

    // The normalized day starts at 06:00, so 23:00 is 17/24 into it and the
    // interval stays contiguous through the 00:00 wrap until dawn.
    internal static bool IsAfter23(WorldState world) =>
        world.Environment.TimeOfDayNormalized >= 17f / 24f;
    // Spec 42: a real tropical swing — 25° at the 15:00 peak (dressed girls
    // cross the >24 undress gate and strip for the day), 6° at 03:00 (layers
    // and the campfire earn their keep at night).
    private static float BaseTemperature => SimBalance.BaseTemperature;
    private static float TemperatureAmplitude => SimBalance.TemperatureAmplitude;

    public void Run(WorldState world)
    {
        var progress = (world.Tick % DayLengthTicks) / (float)DayLengthTicks;
        var previousPhase = world.Environment.Phase;

        world.Environment.TimeOfDayNormalized = progress;
        world.Environment.Phase = progress switch
        {
            < 0.25f => DayPhase.Morning,
            < 0.5f => DayPhase.Day,
            < 0.75f => DayPhase.Evening,
            _ => DayPhase.Night
        };

        // Warmest at 15:00 (progress 0.375), coldest at 03:00 (progress 0.875).
        world.Environment.GlobalTemperature = BaseTemperature +
            TemperatureAmplitude * System.MathF.Sin((progress - 0.125f) * 2f * System.MathF.PI);

        // Spec 35.4/35.5: UV over the daylight half, peaking 0.9 at midday;
        // rain halves it and cools the air 3 degrees.
        world.Environment.UvIndex = progress < 0.5f
            ? 0.9f * System.MathF.Sin(System.MathF.PI * progress / 0.5f)
            : 0f;
        if (world.Environment.IsRaining)
        {
            world.Environment.UvIndex *= 0.5f;
            world.Environment.GlobalTemperature -= SimBalance.RainTempDrop;
        }

        if (world.Environment.Phase != previousPhase)
        {
            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "PhaseChanged",
                    $"{previousPhase}->{world.Environment.Phase} " +
                    $"Clock={FormatClock(progress)} Temp={world.Environment.GlobalTemperature:F1}");
            }
        }

        RebuildShadows(world, progress);
    }

    // Spec 43: cast shadows. The sun rises east (p=0, 06:00), peaks south at
    // noon (p=0.25) and sets west (p=0.5); elevation follows the same sine
    // (8° at the horizons, 65° at noon). Every tile marches a short ray
    // TOWARD the sun: a blocker (tall hex, +ShadeSteps for canopy, +2 for
    // indoor walls) shades it when its silhouette clears the sun line.
    // Dawn/dusk throw multi-tile shadows off a cliff or a palm; at noon a
    // 1-step ledge shades nothing. Ray length covers a 7-step palm down to
    // ~15° sun so the sim shadow keeps up with the rendered one.
    private static int ShadowRaySteps => WorldBalance.ShadowRaySteps;
    private static float ElevationWorldStep => WorldBalance.ElevationWorldStep; // renderer's step height
    private static float CanopyVirtualSteps => WorldBalance.CanopyVirtualSteps; // indoor walls

    private static void RebuildShadows(WorldState world, float progress)
    {
        world.ShadedTiles.Clear();
        if (progress >= 0.5f)
        {
            world.SunElevationDegrees = 0f;
            world.SunDirection = Float2.Zero;
            return; // night — no sun, shade is moot (UV is 0 anyway)
        }

        var arc = System.MathF.Sin(System.MathF.PI * progress / 0.5f); // 0..1..0
        var azimuth = System.MathF.PI * (progress / 0.5f); // east -> west
        var sunDir = new Float2(System.MathF.Cos(azimuth), -System.MathF.Sin(azimuth));
        var elevationDeg = 8f + 57f * arc;
        world.SunDirection = sunDir;
        world.SunElevationDegrees = elevationDeg;

        // Rise of the sun line per horizontal tile step, in ELEVATION units.
        var stepWorld = HexSpatialMath.HexRadius * HexSpatialMath.Sqrt3;
        var risePerStep = System.MathF.Tan(elevationDeg * System.MathF.PI / 180f)
            * (stepWorld / ElevationWorldStep);

        // Canopies cast from the SAME junction anchor used by the renderer.
        // Treating them as if they stood at TileToWorld(obj.Tile) displaced a
        // palm shadow by the tile-centre→junction offset even though the light
        // direction and height were already shared with Unity (bug #207).
        // A canopy's declared tile remains shaded itself: it is the gameplay
        // cell occupied by the crown/trunk.
        _shadowExtra.Clear();
        _objectShadowCasters.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                def.HasTag("Shade"))
            {
                var anchor = HexSpatialMath.TileToWorld(obj.Tile);
                if (obj.Junctions.Count > 0 &&
                    world.Junctions.Items.TryGetValue(obj.Junctions[0], out var junction))
                {
                    anchor = junction.WorldPosition;
                }

                var baseElevation = world.Tiles.Items.TryGetValue(obj.Tile, out var objectTile)
                    ? objectTile.Elevation
                    : 0;
                _objectShadowCasters.Add(new ObjectShadowCaster(
                    anchor, obj.Tile, baseElevation + def.ShadeSteps));
                world.ShadedTiles.Add(obj.Tile);
            }
        }

        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            if (tile.Flags.HasFlag(TileFlags.Roofed))
            {
                // §35.4 r2: именно КРЫША, а не Indoor. Санктуарные тайлы (двор
                // колонии, стоянка чужака) не несут ни перекрытия, ни стен в
                // кадре — вешать на них вечную тень значило врать и про них, и
                // про их соседей.
                world.ShadedTiles.Add(pair.Key);
                _shadowExtra.TryGetValue(pair.Key, out var prior);
                _shadowExtra[pair.Key] = System.Math.Max(prior, CanopyVirtualSteps);
            }
        }

        foreach (var pair in world.Tiles.Items)
        {
            // §156: САМОЕ дорогое место всей симуляции — луч на каждый тайл
            // мира, и именно оно росло с площадью острова. Спящему тайлу тень
            // не считаем: спрашивают о ней только TemperatureSystem и сушка, и
            // обе — про тайлы NPC и бодрых предметов. Список отбрасывающих тень
            // выше НЕ фильтруется: бодрый тайл обязан лежать в тени спящей
            // пальмы, если она рядом.
            if (world.ShadedTiles.Contains(pair.Key) ||
                !ChunkMath.IsAwake(world, pair.Key))
            {
                continue;
            }

            var origin = HexSpatialMath.TileToWorld(pair.Key);
            var myElev = (float)pair.Value.Elevation;
            for (var k = 1; k <= ShadowRaySteps; k++)
            {
                var sample = new Float2(
                    origin.X + sunDir.X * stepWorld * k,
                    origin.Y + sunDir.Y * stepWorld * k);
                var blockerCoord = WorldToTile(sample);
                if (!world.Tiles.Items.TryGetValue(blockerCoord, out var blocker))
                {
                    continue;
                }

                _shadowExtra.TryGetValue(blockerCoord, out var extra);
                var blockerHeight = blocker.Elevation + extra;
                if (blockerHeight >= myElev + risePerStep * k)
                {
                    world.ShadedTiles.Add(pair.Key);
                    break;
                }
            }
        }

        CastObjectShadows(world, sunDir, elevationDeg, stepWorld);
    }

    private static readonly System.Collections.Generic.Dictionary<TileCoord, float> _shadowExtra = new();

    private static readonly System.Collections.Generic.List<ObjectShadowCaster> _objectShadowCasters = new();

    private readonly struct ObjectShadowCaster
    {
        public ObjectShadowCaster(Float2 anchor, TileCoord tile, float height)
        {
            Anchor = anchor;
            Tile = tile;
            Height = height;
        }

        public Float2 Anchor { get; }
        public TileCoord Tile { get; }
        public float Height { get; }
    }

    private static void CastObjectShadows(
        WorldState world, Float2 sunDir, float elevationDeg, float stepWorld)
    {
        var tanElevation = System.MathF.Tan(elevationDeg * System.MathF.PI / 180f);
        // Half-step samples are no farther apart than a hex apothem, so a ray
        // cannot jump across a tile it visibly crosses. This stays O(casters ×
        // ray length), unlike scanning the whole island once per palm.
        const int samplesPerStep = 2;
        var sampleDistance = stepWorld / samplesPerStep;
        var sampleCount = ShadowRaySteps * samplesPerStep;

        foreach (var caster in _objectShadowCasters)
        {
            for (var sampleIndex = 1; sampleIndex <= sampleCount; sampleIndex++)
            {
                var distance = sampleDistance * sampleIndex;
                var sample = new Float2(
                    caster.Anchor.X - sunDir.X * distance,
                    caster.Anchor.Y - sunDir.Y * distance);
                var targetCoord = HexSpatialMath.WorldToTile(sample);
                if (targetCoord.Equals(caster.Tile) ||
                    !world.Tiles.Items.TryGetValue(targetCoord, out var targetTile))
                {
                    continue;
                }

                var sunLineHeight = targetTile.Elevation +
                    tanElevation * distance / ElevationWorldStep;
                if (caster.Height >= sunLineHeight)
                {
                    world.ShadedTiles.Add(targetCoord);
                }
            }
        }
    }

    // Inverse of HexSpatialMath.TileToWorld (linear) with axial rounding.
    private static TileCoord WorldToTile(Float2 world)
    {
        var r = world.Y / (HexSpatialMath.HexRadius * HexSpatialMath.HexRowStepFactor);
        var q = world.X / (HexSpatialMath.HexRadius * HexSpatialMath.HexWidthFactor) - r * 0.5f;
        // Cube rounding (s = -q-r) picks the nearest hex.
        var s = -q - r;
        var rq = System.MathF.Round(q);
        var rr = System.MathF.Round(r);
        var rs = System.MathF.Round(s);
        var dq = System.MathF.Abs(rq - q);
        var dr = System.MathF.Abs(rr - r);
        var ds = System.MathF.Abs(rs - s);
        if (dq > dr && dq > ds)
        {
            rq = -rr - rs;
        }
        else if (dr > ds)
        {
            rr = -rq - rs;
        }

        return new TileCoord((int)rq, (int)rr);
    }

    public static string FormatClock(float progress)
    {
        var hours = (6f + progress * 24f) % 24f;
        var h = (int)hours;
        var m = (int)((hours - h) * 60f);
        return $"{h:D2}:{m:D2}";
    }

    // Display-only calendar day, 1-based. The tick-day starts at 06:00
    // (tick 0 = Day 1, 06:00), but the CALENDAR day must roll over at
    // midnight — shifting by the quarter-day between 00:00 and 06:00 moves
    // the boundary there. Sim logic (raids, storms, spoilage) stays on raw
    // tick-days; only player-facing readouts use this.
    public static int CalendarDay(int tick)
    {
        return (tick + DayLengthTicks / 4) / DayLengthTicks + 1;
    }
}

}

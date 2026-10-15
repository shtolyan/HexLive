using System.Security.Cryptography;
using System.Text.Json;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;

internal static class MotionReplay
{
    public static void Run(string directory, string outputPath, int npcId, int ticks, int[]? route = null)
    {
        if (ticks is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(ticks));
        SimDataFile.Require(Path.Combine(directory, "simdata.json"));
        using var file = File.OpenRead(Path.Combine(directory, "world.sav"));
        using var reader = new BinaryReader(file);
        if (reader.ReadInt32() != 0x48584C53) throw new InvalidDataException("Expected server HXLS save");
        var version = reader.ReadInt32();
        if (version != 2) throw new InvalidDataException("Expected server save header v2");
        var seed = reader.ReadInt32(); var mode = (GameMode)reader.ReadInt32();
        var savedTick = reader.ReadInt32(); var length = reader.ReadInt32();
        if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("Save exceeds bound");
        var blob = reader.ReadBytes(length);
        if (blob.Length != length || file.Position != file.Length) throw new InvalidDataException("Save length mismatch");
        var config = WorldCreationCodec.Decode(File.ReadAllText(Path.Combine(directory, "creation.xml")));
        if (config.Seed != seed || config.Mode != mode) throw new InvalidDataException("Creation/save mismatch");
        var definition = WorldCreation.Definition(config);
        var world = new WorldStateFactory().Create(definition);
        using (var body = new BinaryReader(new MemoryStream(blob))) WorldSaveSerializer.Read(world, body);
        if (world.Tick != savedTick) throw new InvalidDataException("Tick mismatch");
        var options = new SimulationSettings { TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval, SlowInterval = definition.Simulation.SlowTickInterval };
        var clock = new SimulationClock(); var engine = new SimulationEngine(world, options, clock);
        SimulationSystemRegistry.RegisterDefaults(engine);
        var npc = world.Entities.Npcs.Values.Single(n => n.Id.Value == npcId);
        if (route != null)
        {
            if (route.Length < 2) throw new InvalidDataException("Route needs at least two adjacent nodes");
            var nodes = route.Select(id => world.Junctions.Items[new JunctionId(id)]).ToArray();
            for (var i = 1; i < nodes.Length; i++)
                if (!nodes[i - 1].Neighbors.Contains(nodes[i].Id)) throw new InvalidDataException("Route nodes are not adjacent");
            if (nodes[0].Tiles.Count != 1) throw new InvalidDataException("Start node must have one unambiguous tile");
            // Controlled LOCAL route only. No AI replanning or player command
            // is issued, and the captured save is never modified on disk.
            npc.Position = nodes[0].WorldPosition;
            SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, nodes[0].Tiles[0]);
            npc.Tile = nodes[0].Tiles[0];
            npc.CurrentJunction = nodes[0].Id;
            npc.Mind.WakeGraceUntilTick = 0;
            npc.Mind.CurrentGoal = GoalType.Dress;
            npc.Plan.Goal = GoalType.Dress;
            npc.Plan.Status = PlanStatus.Active;
            npc.Plan.TargetObjectId = null;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Plan.TargetJunctionId = nodes[^1].Id;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.JunctionPath.AddRange(nodes.Select(n => n.Id));
            npc.Movement.PathIndex = 1;
            npc.Movement.IsMoving = true;
            npc.Movement.SetStatus(MovementStatus.Moving);
            npc.Movement.HopArmed = false;
            npc.Movement.HopTimer = 0;
            npc.Movement.HopPathIndex = -1;
            npc.Movement.ClimbPauseTimer = 0;
            npc.Movement.PostTurnDelay = 0;
            npc.Movement.PostTurnTimer = 0;
        }
        var nearby = world.Entities.Objects.Values.Where(o =>
            (o.DefinitionId == ContentIds.DryingRack || o.DefinitionId == ContentIds.PalmStump || ContentIds.IsBed(o.DefinitionId)) &&
            HexSpatialMath.HexDistance(o.Tile, npc.Tile) <= 5).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { type = "save", seed, mode, savedTick, npc = npc.Id.Value,
            topology = TopologyChecksum.Compute(world).ToString("X8"),
            x = npc.Position.X, z = npc.Position.Y, manual = npc.Mind.ManualControl,
            goal = npc.Plan.Goal.ToString(), move = npc.Movement.Status.ToString(),
            controlledRoute = route != null, edgePadding = HexHopTuning.EdgePadding, hopSeconds = HexHopTuning.HopSeconds,
            saveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory,"world.sav")))),
            nearby = nearby.Select(o => new { id = o.Id.Value, definition = o.DefinitionId,
                q = o.Tile.Q, r = o.Tile.R, junctions = o.Junctions.Select(j => j.Value) }) }));
        using var output = new StreamWriter(outputPath);
        var reversals = 0; var previous = npc.Movement.DesiredRotationDegrees;
        var usedJunctions = new HashSet<HexLive.Simulation.Common.JunctionId>();
        var completedTicks = 0;
        var movement = new MovementSystem();
        for (var i = 0; i < ticks; i++)
        {
            if (route == null) engine.Step();
            else { movement.Run(world); world.Tick++; }
            completedTicks++;
            if (Math.Abs(HexLive.Simulation.Common.MathUtil.DeltaAngle(previous, npc.Movement.DesiredRotationDegrees)) > 100f &&
                npc.Movement.IsMoving) reversals++;
            previous = npc.Movement.DesiredRotationDegrees;
            foreach(var j in npc.Movement.JunctionPath) usedJunctions.Add(j);
            output.WriteLine(JsonSerializer.Serialize(new { tick = world.Tick, id = npc.Id.Value,
                x = npc.Position.X, z = npc.Position.Y, rotation = npc.RotationDegrees,
                desired = npc.Movement.DesiredRotationDegrees, move = npc.Movement.Status.ToString(),
                goal = npc.Plan.Goal.ToString(), target = npc.Plan.TargetObjectId?.Value,
                execution = npc.Execution.Status.ToString(), interaction = npc.Execution.CurrentInteraction?.ToString(),
                junction = npc.CurrentJunction?.Value, index = npc.Movement.PathIndex,
                hopArmed = npc.Movement.HopArmed, hopTimer = npc.Movement.HopTimer,
                hopPathIndex = npc.Movement.HopPathIndex, hopLandingIndex = npc.Movement.HopLandingIndex,
                hopFrom = new { x=npc.Movement.HopFrom.X, z=npc.Movement.HopFrom.Y },
                hopTo = new { x=npc.Movement.HopTo.X, z=npc.Movement.HopTo.Y },
                hopFromTile = new { q=npc.Movement.HopFromTile.Q, r=npc.Movement.HopFromTile.R },
                hopTargetTile = new { q=npc.Movement.HopTargetTile.Q, r=npc.Movement.HopTargetTile.R },
                tile = new { q=npc.Tile.Q, r=npc.Tile.R },
                climbPause=npc.Movement.ClimbPauseTimer, postTurnDelay=npc.Movement.PostTurnDelay, postTurnTimer=npc.Movement.PostTurnTimer,
                path = npc.Movement.JunctionPath.Select(j => j.Value).ToArray() }));
            if (i % 500 == 0) Console.WriteLine(JsonSerializer.Serialize(new { type="progress", ticks=i,
                tick=world.Tick, goal=npc.Plan.Goal.ToString(), move=npc.Movement.Status.ToString(), reversals }));
            if (route != null && !npc.Movement.IsMoving) break;
        }
        File.WriteAllText(Path.ChangeExtension(outputPath, ".geometry.json"),
            JsonSerializer.Serialize(new { radius=HexSpatialMath.HexRadius,
                nodes=usedJunctions.Select(id=>world.Junctions.Items[id]).Select(j=>new { id=j.Id.Value,
                    x=j.WorldPosition.X, z=j.WorldPosition.Y, tiles=j.Tiles.Select(t=>new {q=t.Q,r=t.R,elevation=world.Tiles.Items[t].Elevation}) }),
                objects=world.Entities.Objects.Values.Where(o=>nearby.Contains(o)).Select(o=>new{
                    id=o.Id.Value,definition=o.DefinitionId,q=o.Tile.Q,r=o.Tile.R,junctions=o.Junctions.Select(j=>j.Value)}) }));
        Console.WriteLine(JsonSerializer.Serialize(new { type="complete", ticks=completedTicks, finalTick=world.Tick, reversals,
            controlledRoute = route != null, arrived = npc.Movement.Status == MovementStatus.Arrived,
            sourceOnlyLocalReplay=true, productionChanged=false, output=Path.GetFullPath(outputPath) }));
    }
}

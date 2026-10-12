using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Persistence;

namespace HexLive.Server;

public sealed partial class WorldHost
{
    // §171.10: the same serialized between-ticks host boundary as SubmitAdminCommand.
    // A paused host accepts the command too; no background task can mutate a half-stepped world.
    internal (string Error, PlayerCharacterCreationReceipt? Receipt) SubmitCharacterCreation(
        string playerId, string requestId, CharacterCreationConfig character, PlayerCharacterAssignments assignments, int worldGeneration)
    {
        lock (_gate)
        {
            var world = _engine.World;
            var previous = world.PlayerCharacterCreations.FirstOrDefault(r => r.PlayerId == playerId && r.RequestId == requestId);
            if (previous != null)
            {
                assignments.Reconcile(this, playerId, worldGeneration: worldGeneration, accountCharacter: true);
                return ("", previous);
            }
            if (assignments.Reconcile(this, playerId, worldGeneration: worldGeneration, accountCharacter: true).Count != 0)
                return ("character_alive", null);
            if (world.Completed) return ("world_completed", null);
            var ownedBefore = world.PlayerControlledNpcs.ToArray();
            using var backup = new MemoryStream();
            using (var writer = new BinaryWriter(backup, System.Text.Encoding.UTF8, leaveOpen: true))
                WorldSaveSerializer.Write(world, writer);
            PlayerCharacterCreationReceipt receipt;
            try
            {
                var error = PlayerCharacterCreation.TryCreate(world, playerId, requestId, character, out receipt);
                if (error.Length != 0) return (error, null);
                _snapshotTick = -1;
                Save(); // NPC and creation receipt are committed together, before the sidecar projection.
            }
            catch
            {
                backup.Position = 0;
                using var reader = new BinaryReader(backup);
                WorldSaveSerializer.Read(world, reader);
                world.PlayerControlledNpcs.Clear();
                world.PlayerControlledNpcs.UnionWith(ownedBefore);
                _snapshotTick = -1;
                throw;
            }
            // Failure here returns 503; a retry/restart repairs players.json from the committed receipt.
            assignments.Reconcile(this, playerId, worldGeneration: worldGeneration, accountCharacter: true);
            DrainMcpEvents();
            return ("", receipt);
        }
    }
}

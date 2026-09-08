using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.GodMode;

/// <summary>One admission path for MCP and future UI. Pending receipts fail closed after a crash.</summary>
public sealed class AdminCommandBus
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly WorldSupervisor _worlds;
    private readonly AdminAccess _access;
    private readonly string _directory;
    private PlayerCharacterAssignments? _assignments;
    public PlayerCharacterAssignments? Assignments { get => _assignments ?? _worlds.Assignments; init => _assignments = value; }
    public ControlLeases? Leases { get; init; }
    public AgentSessionRegistry? Agents { get; init; }
    private readonly Dictionary<string, Preview> _previews = new();
    public AdminCommandBus(WorldSupervisor worlds, AdminAccess access, string directory)
    { _worlds = worlds; _access = access; _directory = directory; }
    private sealed record Preview(string Client, string CredentialHash, string Command, string Fingerprint, string Epoch, DateTimeOffset Until);
    public sealed record Reply(bool Accepted, string Reason, string OperationId, string ConfirmationId = "", int EntityId = 0, string DefinitionId = "", string Kind = "");

    public object Execute(string client, string token, AdminCommand command, string epoch)
        => _access.WithAuthorization<object>(client, token, () => ExecuteAuthorized(client, token, command, epoch),
            () => new Reply(false, "AdminUnauthorized", command.OperationId));

    private object ExecuteAuthorized(string client, string token, AdminCommand command, string epoch)
    {
        lock (_gate)
        {
            if (!Guid.TryParseExact(command.OperationId, "N", out _)) return new Reply(false, "InvalidOperationId", command.OperationId);
            var session = _worlds.CaptureViewerSession();
            if (session.Host.McpSessionEpoch != epoch) return new Reply(false, "WorldChanged", command.OperationId);
            var serialized = JsonSerializer.Serialize(command, Json);
            var path = ReceiptPath(client, command.OperationId);
            if (File.Exists(path))
            {
                var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), Json)!;
                return receipt.Epoch == epoch && receipt.Command == serialized ? receipt.Reply
                    : new Reply(false, "OperationIdConflict", command.OperationId);
            }
            return session.Host.Read(world =>
            {
                if (session.Lifetime.IsCancellationRequested) return (object)new Reply(false, "WorldChanged", command.OperationId);
                if (AdminWorldCommands.IsDestructive(command.Kind))
                {
                    foreach (var old in _previews.Where(p => p.Value.Until < DateTimeOffset.UtcNow || p.Value.Client == client).ToArray()) _previews.Remove(old.Key);
                    var id = Guid.NewGuid().ToString("N");
                    _previews[id] = new Preview(client, AdminAccess.Hash(token), serialized, Fingerprint(session.Host, command), epoch, DateTimeOffset.UtcNow.AddSeconds(60));
                    return (object)new { accepted = false, reason = "ConfirmationRequired", operationId = command.OperationId,
                        confirmationId = id, command, target = DescribeTarget(session.Host, command, summary: true) };
                }
                return Apply(session.Host, client, command, serialized, epoch);
            });
        }
    }
    // Only the viewer calls this. No MCP tool exposes confirmation.
    public object Confirm(string client, string token, string id, bool accept)
        => _access.WithAuthorization<object>(client, token, () =>
        {
            lock (_gate)
            {
                if (!_previews.TryGetValue(id, out var preview) || preview.Client != client || preview.CredentialHash != AdminAccess.Hash(token)) return new Reply(false, "ConfirmationExpired", "");
                _previews.Remove(id);
                if (!accept) return new Reply(false, "Cancelled", "");
                if (preview.Until < DateTimeOffset.UtcNow) return new Reply(false, "ConfirmationExpired", "");
                var session = _worlds.CaptureViewerSession();
                var host = session.Host;
                var command = JsonSerializer.Deserialize<AdminCommand>(preview.Command, Json)!;
                return host.Read(_ => session.Lifetime.IsCancellationRequested || host.McpSessionEpoch != preview.Epoch || Fingerprint(host, command) != preview.Fingerprint
                    ? new Reply(false, "TargetChanged", command.OperationId)
                    : Apply(host, client, command, preview.Command, preview.Epoch));
            }
        }, () => new Reply(false, "AdminUnauthorized", ""));

    private sealed record Receipt(string Client, string Epoch, string Command, DateTimeOffset CreatedUtc, Reply Reply);
    private Reply Apply(WorldHost host, string client, AdminCommand command, string serialized, string epoch)
    {
        var path = ReceiptPath(client, command.OperationId);
        if (File.Exists(path)) return JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), Json)!.Reply;
        var pending = new Receipt(client, epoch, serialized, DateTimeOffset.UtcNow,
            new Reply(false, "OutcomeUnknownInspectState", command.OperationId));
        AdminAccess.WritePrivate(path, JsonSerializer.Serialize(pending, Json));
        var result = command.Kind == "assign_npc" && Assignments == null
            ? AdminCommandResult.Reject(command, "AssignmentUnavailable") : host.SubmitAdminCommand(command);
        if (result.Accepted)
        {
            if (command.Kind == "assign_npc") Assignments?.AdminAssign(command.Target, command.NpcId);
            if (command.Kind == "delete_npc" || command.Kind == "set_faction") Assignments?.AdminRemoveNpc(command.NpcId);
            host.Save();
            if (command.Kind is "assign_npc" or "delete_npc" or "set_faction")
            {
                // Registry sweeps acquire their lock before reading the world: never invert that ordering here.
                System.Threading.ThreadPool.QueueUserWorkItem(state =>
                {
                    try
                    {
                        Leases?.ForceRelease(command.NpcId, out _);
                        Agents?.AdminDetachNpc(command.NpcId);
                        _worlds.ReconnectViewers();
                    }
                    catch (ObjectDisposedException) { /* Server is shutting down. */ }
                });
            }
        }
        var reply = new Reply(result.Accepted, result.Reason, command.OperationId, EntityId: result.EntityId, DefinitionId: result.DefinitionId, Kind: command.Kind);
        AdminAccess.WritePrivate(path, JsonSerializer.Serialize(pending with { Reply = reply }, Json));
        return reply;
    }
    public object[] RecentHistory()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return Array.Empty<object>();
            return Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path)).OrderByDescending(f => f.LastWriteTimeUtc).Take(100)
                .Select(file =>
                {
                    try { return (object)JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file.FullName), Json)!; }
                    catch (JsonException) { return new { unreadable = file.Name }; }
                }).ToArray();
        }
    }
    private string ReceiptPath(string client, string id) => Path.Combine(_directory, AdminAccess.Hash(client), id + ".json");
    private static object DescribeTarget(WorldHost host, AdminCommand command, bool summary = false) => host.Read(w =>
    {
        if (command.Kind != "demolish_building" && w.Entities.Npcs.TryGetValue(new EntityId(command.NpcId), out var npc))
        {
            if (summary) return (object)new { npcId = npc.Id.Value, name = npc.DisplayName,
                inventoryCount = npc.Inventory.Items.Count, wornCount = npc.WornItems.Count,
                inventory = npc.Inventory.Items.GroupBy(i => i.DefinitionId).Take(20)
                    .Select(g => new { id = g.Key, count = g.Count() }).ToArray() };
            return (object)new { npcId = npc.Id.Value, name = npc.DisplayName,
                worn = npc.WornItems.Select(i => new { id = i.DefinitionId, i.Durability }).ToArray(),
                inventory = npc.Inventory.Items.Select(i => new { id = i.DefinitionId, i.Durability, i.ResourceAmount, i.OwnerId }).ToArray() };
        }
        if (w.Entities.Objects.TryGetValue(new ObjectId(command.ObjectId), out var obj))
        {
            if (summary) return (object)new { objectId = obj.Id.Value, obj.DefinitionId, contentsCount = obj.Contents.Count };
            return new { objectId = obj.Id.Value, obj.DefinitionId, obj.Durability, obj.Contents };
        }
        return new { missing = true };
    });
    private static string Fingerprint(WorldHost host, AdminCommand command) => AdminAccess.Hash(JsonSerializer.Serialize(DescribeTarget(host, command), Json));
}

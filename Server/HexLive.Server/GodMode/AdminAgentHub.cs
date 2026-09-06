using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.GodMode;

public sealed class AdminAgentHub
{
    private readonly object _gate = new();
    private readonly AdminAccess _access;
    private readonly AdminCommandBus _bus;
    private readonly WorldSupervisor _worlds;
    private readonly Dictionary<string, Turn> _turns = new();
    private DateTimeOffset _agentSeen;
    private sealed class Turn
    {
        public string Id = "", Client = "", Token = "", Epoch = "", Text = "", Reply = "", Owner = "";
        public int NpcId;
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
        public bool Done, MutationsStopped;
        public string ConfirmationId = "", ConfirmationText = "";
    }
    public AdminAgentHub(AdminAccess access, AdminCommandBus bus, WorldSupervisor worlds)
    { _access = access; _bus = bus; _worlds = worlds; }
    public object Submit(string client, string token, string id, string text, int npcId)
    {
        if (!_access.Authorized(client, token)) return new { accepted = false, reason = "AdminUnauthorized" };
        lock (_gate)
        {
            Prune();
            if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(text) || text.Length > 2000)
                return new { accepted = false, reason = "InvalidInput" };
            if (_turns.TryGetValue(id, out var old)) return new { accepted = old.Client == client, reason = old.Client == client ? "Duplicate" : "IdConflict" };
            if (DateTimeOffset.UtcNow - _agentSeen > TimeSpan.FromSeconds(15)) return new { accepted = false, reason = "AgentOffline" };
            if (_turns.Values.Any(t => t.Client == client && !t.Done)) return new { accepted = false, reason = "Busy" };
            if (_turns.Count >= 128) return new { accepted = false, reason = "QueueFull" };
            _turns[id] = new Turn { Id = id, Client = client, Token = token, Text = text.Trim(), NpcId = npcId,
                Epoch = _worlds.CaptureViewerSession().Host.McpSessionEpoch };
            return new { accepted = true, reason = "Thinking", messageId = id };
        }
    }
    public object Poll(string client, string token, string id)
    {
        if (!_access.Authorized(client, token)) return new { accepted = false, reason = "AdminUnauthorized" };
        lock (_gate)
        {
            Prune();
            return _turns.TryGetValue(id, out var t) && t.Client == client
                ? new { accepted = true, done = t.Done, text = t.Reply, messageId = id, confirmationId = t.ConfirmationId, confirmationText = t.ConfirmationText }
                : (object)new { accepted = false, reason = "TurnExpired" };
        }
    }
    public object Next(string owner)
    {
        lock (_gate)
        {
            Prune(); _agentSeen = DateTimeOffset.UtcNow;
            var turn = _turns.Values.FirstOrDefault(t => !t.Done && t.Owner == owner)
                ?? _turns.Values.FirstOrDefault(t => !t.Done && t.Owner.Length == 0);
            if (turn == null) return new { idle = true };
            turn.Owner = owner;
            return new { turnId = turn.Id, clientId = turn.Client, text = turn.Text, selectedNpcId = turn.NpcId, epoch = turn.Epoch };
        }
    }
    public object Tool(string owner, string turnId, string name, JsonElement arguments)
    {
        Turn turn;
        lock (_gate)
        {
            Prune(); _agentSeen = DateTimeOffset.UtcNow;
            if (!_turns.TryGetValue(turnId, out turn!) || turn.Done || turn.Owner != owner)
                return new { accepted = false, reason = "TurnExpired" };
        }
        if (!_access.Authorized(turn.Client, turn.Token)) return new { accepted = false, reason = "AdminUnauthorized" };
        var host = _worlds.CaptureViewerSession().Host;
        if (host.McpSessionEpoch != turn.Epoch) return new { accepted = false, reason = "WorldChanged" };
        if (name == "admin_execute")
        {
            if (turn.MutationsStopped) return new { accepted = false, reason = "TurnStopped" };
            var c = arguments.Deserialize<AdminCommand>(AdminCommandBus.Json) ?? throw new ArgumentException("Missing command");
            var result = _bus.Execute(turn.Client, turn.Token, c, turn.Epoch);
            var encoded = JsonSerializer.SerializeToElement(result, AdminCommandBus.Json);
            if (encoded.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False) turn.MutationsStopped = true;
            if (encoded.TryGetProperty("confirmationId", out var confirmation) && confirmation.GetString() is { Length: > 0 } cid)
            {
                lock (_gate)
                {
                    turn.ConfirmationId = cid;
                    turn.ConfirmationText = JsonSerializer.Serialize(new { command = c,
                        target = encoded.TryGetProperty("target", out var target) ? target : default }, AdminCommandBus.Json);
                }
            }
            return result;
        }
        if (name == "admin_inspect") return host.Read(w =>
        {
            var id = arguments.TryGetProperty("npcId", out var n) && n.TryGetInt32(out var selected) ? selected : turn.NpcId;
            if (!w.Entities.Npcs.TryGetValue(new EntityId(id), out var npc)) return (object)new { accepted = false, reason = "NpcNotFound" };
            return new { npcId = id, name = npc.DisplayName, faction = npc.Faction.ToString(), health = npc.Health,
                needs = npc.Needs, attributes = npc.Attributes, inventory = npc.Inventory.Items,
                body = npc.Body.Parts.Select(p => new { part = p.Key.ToString(), health = p.Value, severed = npc.Body.IsSevered(p.Key),
                    prosthetic = npc.Body.Condition(p.Key).Prosthetic }).ToArray() };
        });
        if (name == "admin_catalog") return host.Read(w => (object)new
        {
            presets = new[] { "colonist" }, factions = Enum.GetNames<Faction>(),
            buildings = new[] { ContentIds.Hut1Hex, ContentIds.BedBasic, ContentIds.Workbench, ContentIds.Campfire,
                ContentIds.DryingRack, ContentIds.Wardrobe, ContentIds.WaterCollector },
            objects = w.Entities.Objects.Values.OrderBy(o => o.Id.Value).Take(1000)
                .Select(o => new { id = o.Id.Value, definitionId = o.DefinitionId, buildProduct = o.BuildProduct,
                    tile = o.Tile, durability = o.Durability }).ToArray(),
            commands = AdminWorldCommands.Kinds, needs = AdminWorldCommands.Needs, attributes = Enum.GetNames<AttributeKind>(),
            npcs = w.Entities.Npcs.Values.OrderBy(n => n.Id.Value).Select(n => new { id = n.Id.Value, name = n.DisplayName, faction = n.Faction.ToString() }).Take(500).ToArray(),
            items = w.Content.ObjectDefinitions.Values.OrderBy(d => d.Id).Select(d => new { id = d.Id, name = d.DisplayName }).ToArray()
        });
        return new { accepted = false, reason = "UnknownTool" };
    }
    public object Reply(string owner, string id, string text)
    {
        lock (_gate)
        {
            if (!_turns.TryGetValue(id, out var t) || t.Owner != owner || text.Length > 4000) return new { accepted = false };
            if (!t.Done) { t.Reply = text; t.Done = true; t.Token = ""; }
            return new { accepted = true };
        }
    }
    private void Prune()
    {
        foreach (var pair in _turns.ToArray())
        {
            var age = DateTimeOffset.UtcNow - pair.Value.Created;
            if (age > TimeSpan.FromMinutes(10)) { _turns.Remove(pair.Key); continue; }
            if (!pair.Value.Done && age > TimeSpan.FromMinutes(3))
            { pair.Value.Done = true; pair.Value.Reply = "AgentTimeout"; pair.Value.Token = ""; }
        }
    }
}

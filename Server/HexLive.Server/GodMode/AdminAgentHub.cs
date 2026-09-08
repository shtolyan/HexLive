using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;

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
        public AdminRequestContext Context = new();
        public readonly Dictionary<string, JsonElement> Results = new();
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
        public bool Done, MutationsStopped;
        public string ConfirmationId = "", ConfirmationText = "";
    }
    public AdminAgentHub(AdminAccess access, AdminCommandBus bus, WorldSupervisor worlds)
    { _access = access; _bus = bus; _worlds = worlds; }
    public string Epoch => _worlds.CaptureViewerSession().Host.McpSessionEpoch;
    public bool AgentAvailable { get { lock (_gate) return DateTimeOffset.UtcNow - _agentSeen < TimeSpan.FromSeconds(15); } }
    public object Submit(string client, string token, string id, string text, int npcId, AdminRequestContext? context = null)
    {
        if (!_access.Authorized(client, token)) return new { accepted = false, reason = "AdminUnauthorized" };
        context ??= new AdminRequestContext { PrimaryNpcId = npcId, Epoch = Epoch };
        if (context.Epoch != Epoch) return new { accepted = false, reason = "WorldChanged" };
        if (context.SelectedNpcIds == null || context.SelectedNpcIds.Length > 64 || context.CameraPosition == null || context.CameraForward == null || context.GroundPosition == null ||
            (context.GroundPosition.Length != 0 && context.GroundPosition.Length != 3) ||
            (context.CameraPosition.Length != 0 && context.CameraPosition.Length != 3) || (context.CameraForward.Length != 0 && context.CameraForward.Length != 3) ||
            context.CameraPosition.Concat(context.CameraForward).Concat(context.GroundPosition).Any(v => !float.IsFinite(v)) || context.GroundQ.HasValue != context.GroundR.HasValue)
            return new { accepted = false, reason = "InvalidContext" };
        context.AssignedNpcIds = _bus.Assignments?.AdminAssignedIds(client) ?? Array.Empty<int>();
        lock (_gate)
        {
            Prune();
            if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(text) || text.Length > 2000)
                return new { accepted = false, reason = "InvalidInput" };
            if (_turns.TryGetValue(id, out var old)) return new { accepted = old.Client == client, reason = old.Client == client ? "Duplicate" : "IdConflict" };
            if (DateTimeOffset.UtcNow - _agentSeen > TimeSpan.FromSeconds(15)) return new { accepted = false, reason = "AgentOffline" };
            if (_turns.Values.Any(t => t.Client == client && !t.Done)) return new { accepted = false, reason = "Busy" };
            if (_turns.Count >= 128) return new { accepted = false, reason = "QueueFull" };
            _turns[id] = new Turn { Id = id, Client = client, Token = token, Text = text.Trim(), NpcId = context.PrimaryNpcId, Context = context,
                Epoch = context.Epoch };
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
                ? new { accepted = true, done = t.Done, text = t.Reply, messageId = id, confirmationId = t.ConfirmationId, confirmationText = t.ConfirmationText, success = !t.MutationsStopped && t.Results.Count > 0 && t.Results.Values.All(r => r.TryGetProperty("accepted", out var a) && a.ValueKind == JsonValueKind.True), results = t.Results.Values.ToArray() }
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
            return new { turnId = turn.Id, clientId = turn.Client, text = turn.Text, selectedNpcId = turn.NpcId, context = turn.Context, epoch = turn.Epoch };
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
        lock (turn)
        {
            if (turn.Done) return new { accepted = false, reason = "TurnExpired" };
            return ToolCore(turn, name, arguments);
        }
    }
    private object ToolCore(Turn turn, string name, JsonElement arguments)
    {
        if (!_access.Authorized(turn.Client, turn.Token)) return new { accepted = false, reason = "AdminUnauthorized" };
        var host = _worlds.CaptureViewerSession().Host;
        if (host.McpSessionEpoch != turn.Epoch) { turn.MutationsStopped = true; return new { accepted = false, reason = "WorldChanged" }; }
        if (name == "admin_execute")
        {
            if (turn.MutationsStopped) return new { accepted = false, reason = "TurnStopped" };
            var c = arguments.Deserialize<AdminCommand>(AdminCommandBus.Json) ?? throw new ArgumentException("Missing command");
            if (c.NpcId == 0) c.NpcId = turn.Context.PrimaryNpcId;
            if (c.Kind == "spawn_npc" && string.IsNullOrEmpty(c.Target))
            {
                var factions = host.Read(w => turn.Context.AssignedNpcIds.Where(id => w.Entities.Npcs.ContainsKey(new EntityId(id)))
                    .Select(id => w.Entities.Npcs[new EntityId(id)].Faction).Distinct().ToArray());
                if (factions.Length == 1) c.Target = factions[0].ToString();
            }
            if (c.Location == "camera")
            {
                if (!turn.Context.GroundQ.HasValue) { turn.MutationsStopped = true; return new { accepted = false, reason = "CameraGroundUnavailable" }; }
                c.TileQ = turn.Context.GroundQ; c.TileR = turn.Context.GroundR;
                c.GroundX = turn.Context.GroundPosition.Length == 3 ? turn.Context.GroundPosition[0] : null;
                c.GroundZ = turn.Context.GroundPosition.Length == 3 ? turn.Context.GroundPosition[2] : null;
            }
            var result = _bus.Execute(turn.Client, turn.Token, c, turn.Epoch);
            var encoded = JsonSerializer.SerializeToElement(result, AdminCommandBus.Json);
            lock (_gate) turn.Results[c.OperationId] = encoded.Clone();
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
            if (!w.Entities.Npcs.TryGetValue(new EntityId(id), out var npc)) { turn.MutationsStopped = true; return (object)new { accepted = false, reason = "NpcNotFound" }; }
            return new { npcId = id, name = npc.DisplayName, faction = npc.Faction.ToString(), health = npc.Health, sex = npc.Sex.ToString(), worn = npc.WornItems,
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
            garments = GarmentLibrary.Spawnable.Where(g => w.Content.ObjectDefinitions.ContainsKey(g.Id)).Select(g => new
                { id = g.Id, name = g.DisplayName, category = g.Category.ToString(), kind = AdminGarments.Kind(g), sex = g.Sex.ToString(), layer = g.Layer.ToString(), capacity = g.Capacity }).ToArray(),
            items = w.Content.ObjectDefinitions.Values.OrderBy(d => d.Id).Select(d => new { id = d.Id, name = d.DisplayName }).ToArray()
        });
        return new { accepted = false, reason = "UnknownTool" };
    }
    public object Reply(string owner, string id, string text, bool failed = false)
    {
        Turn turn;
        lock (_gate)
        {
            if (!_turns.TryGetValue(id, out turn!) || turn.Owner != owner || text == null || text.Length > 4000)
                return new { accepted = false };
        }
        lock (turn)
        lock (_gate)
        {
            if (!turn.Done) { turn.MutationsStopped |= failed; turn.Reply = text; turn.Done = true; turn.Token = ""; }
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
            { pair.Value.Done = true; pair.Value.MutationsStopped = true; pair.Value.Reply = "AgentTimeout"; pair.Value.Token = ""; }
        }
    }
}

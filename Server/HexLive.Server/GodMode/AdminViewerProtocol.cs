using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Wire;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.GodMode;

public sealed class AdminViewerProtocol
{
    private readonly AdminAccess _access;
    private readonly AdminCommandBus _bus;
    private readonly AdminAgentHub _hub;
    private readonly DeepgramTokenBroker _stt;
    public AdminViewerProtocol(AdminAccess access, AdminCommandBus bus, AdminAgentHub hub, DeepgramTokenBroker stt)
    { _access = access; _bus = bus; _hub = hub; _stt = stt; }
    public async Task<string> Handle(string client, string json, CancellationToken cancel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string S(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
            var token = S("token"); var kind = S("kind");
            object reply;
            if (kind == "request_access") reply = _access.RequestAccess(client, token);
            else if (kind == "access_status")
            {
                var status = _access.Check(client, token);
                reply = new { status.Accepted, status.State, status.RequestId, status.ExpiresUtc,
                    epoch = _hub.Epoch, sttAvailable = _stt.Available, agentAvailable = _hub.AgentAvailable };
            }
            else if (!_access.Authorized(client, token)) reply = new { accepted = false, reason = "AdminUnauthorized" };
            else switch (kind)
            {
                case "status": reply = new { accepted = true, clientId = client, sttAvailable = _stt.Available }; break;
                case "text":
                    var npcId = root.TryGetProperty("npcId", out var n) && n.TryGetInt32(out var id) ? id : 0;
                    var context = root.TryGetProperty("context", out var ctx) ? ctx.Deserialize<AdminRequestContext>(AdminCommandBus.Json) : null;
                    reply = _hub.Submit(client, token, S("messageId"), S("text"), npcId, context); break;
                case "poll": reply = _hub.Poll(client, token, S("messageId")); break;
                case "debug_command":
                    var command = root.GetProperty("command").Deserialize<AdminCommand>(AdminCommandBus.Json);
                    reply = command == null ? new { accepted = false, reason = "InvalidInput" } : _bus.Execute(client, token, command, S("epoch")); break;
                case "confirm": reply = _bus.Confirm(client, token, S("confirmationId"), root.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.True); break;
                case "stt":
                    var grant = await _stt.GrantAsync("admin:" + client, cancel);
                    reply = new { accepted = grant.Accepted, sttToken = grant.Token, reason = grant.Reason, expiresUtcMilliseconds = grant.ExpiresUtc.ToUnixTimeMilliseconds() }; break;
                default: reply = new { accepted = false, reason = "UnknownRequest" }; break;
            }
            return JsonSerializer.Serialize(new { kind, payload = reply }, AdminCommandBus.Json);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return "{\"kind\":\"error\",\"payload\":{\"accepted\":false,\"reason\":\"BadFrame\"}}"; }
    }
}

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
            if (kind == "bind")
            {
                var secret = _access.Exchange(client, S("code"));
                reply = new { accepted = secret != null, token = secret ?? "", reason = secret == null ? "InvalidBinding" : "Bound" };
            }
            else if (!_access.Authorized(client, token)) reply = new { accepted = false, reason = "AdminUnauthorized" };
            else switch (kind)
            {
                case "status": reply = new { accepted = true, clientId = client, sttAvailable = _stt.Available }; break;
                case "text":
                    var npcId = root.TryGetProperty("npcId", out var n) && n.TryGetInt32(out var id) ? id : 0;
                    reply = _hub.Submit(client, token, S("messageId"), S("text"), npcId); break;
                case "poll": reply = _hub.Poll(client, token, S("messageId")); break;
                case "confirm": reply = _bus.Confirm(client, token, S("confirmationId"), root.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.True); break;
                case "stt":
                    var grant = await _stt.GrantAsync("admin:" + client, cancel);
                    reply = new { accepted = grant.Accepted, sttToken = grant.Token, reason = grant.Reason }; break;
                default: reply = new { accepted = false, reason = "UnknownRequest" }; break;
            }
            return JsonSerializer.Serialize(new { kind, payload = reply }, AdminCommandBus.Json);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { return "{\"kind\":\"error\",\"payload\":{\"accepted\":false,\"reason\":\"BadFrame\"}}"; }
    }
}

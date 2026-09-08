using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

[NonParallelizable]
public sealed class McpPlayerHttpTests
{
    [Test] public async Task PairingAndSessionHeadersCannotBypassPlayerScopeOrRevocation()
    {
        var directory = Directory.CreateTempSubdirectory("mcp-player-http-");
        try
        {
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "SimData/simdata.json"))) root = root.Parent;
            var catalog = new AssetGarmentCatalog(new AssetRegistryStore(Path.Combine(directory.FullName, "assets")),
                Path.Combine(root!.FullName, "SimData/simdata.json"));
            using var worlds = new WorldSupervisor(12345, GameMode.Feud, Path.Combine(directory.FullName, "world.sav"),
                catalog, false, false, new LlmHostOptions(), null, default);
            worlds.Host.PauseAsOperator();
            var ids = worlds.Host.Read(w => w.Entities.Npcs.Keys.Select(x => x.Value).Take(2).ToArray());
            var tokenPath = Path.Combine(directory.FullName, "mcp-token");
            File.WriteAllText(tokenPath, "fixture-administrator-token-only");
            var access = new McpPlayerAccess(Path.Combine(directory.FullName, "access.json"));
            var playerId = Guid.NewGuid().ToString("N");
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Services.AddRouting(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            McpEndpoint.Map(app, worlds, McpAccessToken.LoadOrCreate(tokenPath), new ControlLeases(45), new AgentSessionRegistry(),
                playerAccess: access, playerOwnsNpc: (owner, npc) => owner == playerId && npc == ids[0]);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                using var denied = await client.PostAsJsonAsync("/mcp", Rpc("tools/call", new { name = "world_status", arguments = new { } }));
                Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                var requested = await Call(client, "request_agent_pairing", new { displayName = "Studio test" });
                var ticket = requested.Deserialize<AgentPairingTicket>()!;
                var pending = await Call(client, "poll_agent_pairing", new { pairingId = ticket.Id, pollSecret = ticket.PollSecret });
                Assert.That(pending.GetProperty("State").GetString(), Is.EqualTo("Pending"));
                Assert.That(access.Approve(ticket.Id, ticket.Code, playerId), Is.True);
                var approved = await Call(client, "poll_agent_pairing", new { pairingId = ticket.Id, pollSecret = ticket.PollSecret });
                var credential = approved.GetProperty("Credential").GetString()!;
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                client.DefaultRequestHeaders.Add("Mcp-Session-Id", "invented-session");
                using var forged = await client.PostAsJsonAsync("/mcp", Rpc("tools/call", new { name = "world_status", arguments = new { } }));
                Assert.That(forged.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                client.DefaultRequestHeaders.Remove("Mcp-Session-Id");
                using var initialize = await client.PostAsJsonAsync("/mcp", Rpc("initialize", new { protocolVersion = "2025-06-18" }));
                initialize.EnsureSuccessStatusCode();
                client.DefaultRequestHeaders.Add("Mcp-Session-Id", initialize.Headers.GetValues("Mcp-Session-Id").Single());
                var list = await Call(client, "list_colonists", new { });
                Assert.That(list.GetProperty("colonists").GetArrayLength(), Is.EqualTo(1));
                Assert.That(list.GetProperty("colonists")[0].GetProperty("npcId").GetInt32(), Is.EqualTo(ids[0]));
                var blocked = await Call(client, "describe_colonist", new { npcId = ids[1] });
                Assert.That(blocked.GetProperty("error").GetString(), Is.EqualTo("NpcAccessDenied"));
                Assert.That(access.Revoke(access.Authorize(credential)!.Id, playerId), Is.True);
                using var revoked = await client.PostAsJsonAsync("/mcp", Rpc("tools/call", new { name = "world_status", arguments = new { } }));
                Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            }
            finally { await app.StopAsync(); }
        }
        finally { directory.Delete(true); }
    }
    [Test] public void PairingWireRejectsTrailingBytesAndRoundTrips()
    {
        var bytes = AgentPairingWire.Encode("request", "code", true);
        Assert.That(bytes[0], Is.EqualTo((byte)FrameKind.AgentPairingInput));
        var value = AgentPairingWire.Decode(bytes[1..]);
        Assert.That(value, Is.EqualTo(("request", "code", true)));
        Assert.Throws<InvalidDataException>(() => AgentPairingWire.Decode(bytes[1..].Concat(new byte[] { 0 }).ToArray()));
    }
    private static object Rpc(string method, object parameters) => new { jsonrpc = "2.0", id = 1, method, @params = parameters };
    private static async Task<JsonElement> Call(HttpClient client, string name, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/mcp", Rpc("tools/call", new { name, arguments }));
        response.EnsureSuccessStatusCode();
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(envelope.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        return result.RootElement.Clone();
    }
}

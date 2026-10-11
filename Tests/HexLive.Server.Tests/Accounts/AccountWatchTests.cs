using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Accounts;
using HexLive.Simulation.Wire;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Accounts;

/// <summary>§171.9: real Program, Kestrel upgrade, wire handshake and logout (no external services).</summary>
[NonParallelizable]
public sealed class AccountWatchTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RealWatchUsesSessionOwnerReconnectsAndRevokes(bool browser)
    {
        var directory = Directory.CreateTempSubdirectory("accounts-watch-");
        try
        {
            var options = ServerOptions.Parse(new[] { "--save", Path.Combine(directory.FullName, "world.sav"), "--firebase-project", FirebaseFixture.Project }, _ => null)!;
            var store = new AccountStore(options.AccountsPath, FirebaseFixture.Project);
            var session = store.CreateSession(new("firebase-player", "google.com"));
            var second = store.CreateSession(new("other-player", "apple.com"));
            await CharacterCreationTests.PublishPeople(Path.Combine(directory.FullName, "assets"));
            await using var server = await RunningServer.Start(directory.FullName, true, legacy: !browser);
            using (var beforeCreation = await Open(server.Address, session.SessionToken, second.PlayerId, browser))
                Assert.That((await ReadHandshake(beforeCreation)).AssignedNpcIds, Is.Empty);
            server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.SessionToken);
            using var created = await server.Client.PostAsJsonAsync(AccountEndpoints.Prefix + "/character", CharacterCreationTests.Request());
            Assert.That(created.IsSuccessStatusCode, Is.True, await created.Content.ReadAsStringAsync());
            var createdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("npcId").GetInt32();
            server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.SessionToken);
            using var otherCreated = await server.Client.PostAsJsonAsync(AccountEndpoints.Prefix + "/character", CharacterCreationTests.Request("Kshishtof", "Colony2"));
            Assert.That(otherCreated.IsSuccessStatusCode, Is.True, await otherCreated.Content.ReadAsStringAsync());
            using var socket = await Open(server.Address, session.SessionToken, second.PlayerId, browser);
            var handshake = await ReadHandshake(socket);
            Assert.That(handshake.ControlEnabled, Is.True);
            Assert.That(handshake.ControlOwner, Is.EqualTo("ws:" + session.PlayerId));
            Assert.That(handshake.AssignedNpcIds, Has.Count.EqualTo(1), "--player-characters 8 cannot expand account cap");
            var assigned = handshake.AssignedNpcIds.Single();
            Assert.That(assigned, Is.EqualTo(createdId));
            using var other = await Open(server.Address, second.SessionToken, session.PlayerId, browser);
            var otherHandshake = await ReadHandshake(other);
            Assert.That(otherHandshake.ControlOwner, Is.EqualTo("ws:" + second.PlayerId));
            Assert.That(otherHandshake.AssignedNpcIds, Does.Not.Contain(assigned));
            Assert.That((await Command(socket, assigned, 10)).Accepted, Is.True, "session can control its assigned body");
            Assert.That((await Command(socket, otherHandshake.AssignedNpcIds.Single(), 11)).Reason, Is.EqualTo("NotAssigned"));
            using var reconnect = await Open(server.Address, session.SessionToken, "invalid-client-id", browser);
            var again = await ReadHandshake(reconnect);
            Assert.That(again.AssignedNpcIds, Is.EqualTo(new[] { assigned }));
            server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.SessionToken);
            Assert.That((await server.Client.DeleteAsync(AccountEndpoints.Prefix + "/session")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            await AssertDisconnected(socket); await AssertDisconnected(reconnect);
            using var denied = await Open(server.Address, session.SessionToken, session.PlayerId, browser);
            Assert.That((await ReadHandshake(denied)).ControlEnabled, Is.False);
            server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.SessionToken);
            Assert.That((await server.Client.GetAsync(AccountEndpoints.Prefix + "/me")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            // Explicit legacy mode remains available with exactly the supplied identity and squad limit.
            if (!browser)
            {
                using var legacy = await Open(server.Address, "fixture-shared-player-token", Guid.NewGuid().ToString("N"), browser);
                Assert.That((await ReadHandshake(legacy)).ControlEnabled, Is.True);
            }
            else Assert.That(File.Exists(Path.Combine(directory.FullName, "hexlive-player.txt")), Is.False,
                "Firebase control does not require or generate a shared player token");
        }
        finally { directory.Delete(true); }
    }

    [Test]
    public async Task RealProgramWithoutFlagHasNoAccountEndpointsOrStore()
    {
        var directory = Directory.CreateTempSubdirectory("accounts-disabled-");
        try
        {
            await using var server = await RunningServer.Start(directory.FullName, false);
            Assert.That((await server.Client.GetAsync(AccountEndpoints.Prefix + "/me")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await server.Client.PostAsync(AccountEndpoints.Prefix + "/session", new StringContent("{}"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await server.Client.DeleteAsync(AccountEndpoints.Prefix + "/session")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(File.Exists(Path.Combine(directory.FullName, "hexlive-accounts.json")), Is.False);
        }
        finally { directory.Delete(true); }
    }

    private static async Task<ClientWebSocket> Open(Uri address, string token, string clientId, bool browser)
    {
        var socket = new ClientWebSocket();
        if (browser)
        {
            socket.Options.AddSubProtocol(ViewerUpgradeIdentity.ProtocolMarker);
            socket.Options.AddSubProtocol(ViewerUpgradeIdentity.TokenProtocolPrefix + ViewerUpgradeIdentity.EncodeBase64Url(token));
            socket.Options.AddSubProtocol(ViewerUpgradeIdentity.ClientProtocolPrefix + ViewerUpgradeIdentity.EncodeBase64Url(clientId));
        }
        else
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            socket.Options.SetRequestHeader("X-HexLive-Client-Id", clientId);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/watch" }.Uri, timeout.Token);
        return socket;
    }
    private static async Task<Handshake> ReadHandshake(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var bytes = new MemoryStream(); var buffer = new byte[65536];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            bytes.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        bytes.Position = 0; using var reader = new BinaryReader(bytes);
        Assert.That(reader.ReadByte(), Is.EqualTo((byte)FrameKind.Handshake));
        return Handshake.Read(reader);
    }
    private static async Task AssertDisconnected(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;
            }
        }
        catch (WebSocketException) { return; }
        catch (OperationCanceledException) { Assert.Fail("Revoked account socket remained open."); }
    }

    private static async Task<Frame.CommandResultFrame> Command(ClientWebSocket socket, int npcId, int correlation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = Frame.NpcCommand(correlation, new SetManualControlCommand(new EntityId(npcId), true));
        await socket.SendAsync(new ArraySegment<byte>(command), WebSocketMessageType.Binary, true, timeout.Token);
        var buffer = new byte[65536];
        while (true)
        {
            using var frame = new MemoryStream(); WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                frame.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            var bytes = frame.ToArray();
            if (bytes.Length > 0 && bytes[0] == (byte)FrameKind.CommandResult)
            {
                var result = Frame.ReadCommandResult(bytes[1..]);
                if (result.CorrelationId == correlation) return result;
            }
        }
    }

    private sealed class RunningServer(Process process, HttpClient client, Task stdout, Task stderr) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Uri Address => Client.BaseAddress!;
        public static async Task<RunningServer> Start(string directory, bool accounts, bool legacy = true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            if (legacy) File.WriteAllText(Path.Combine(directory, "hexlive-player.txt"), "fixture-shared-player-token");
            var info = new ProcessStartInfo("dotnet") {
                WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false
            };
            // No inherited provider credentials: the fixture must never contact paid services.
            info.Environment.Clear();
            foreach (var arg in new[] { typeof(Program).Assembly.Location, "--port", port.ToString(), "--save", Path.Combine(directory, "world.sav"),
                "--simdata", AccountHttpTests.RepoFile("SimData/simdata.json"), "--asset-root", Path.Combine(directory, "assets"),
                "--start-paused", "--mode", "bigisland", "--player-characters", "8" }) info.ArgumentList.Add(arg);
            if (legacy) info.ArgumentList.Add("--control");
            if (accounts) { info.ArgumentList.Add("--firebase-project"); info.ArgumentList.Add(FirebaseFixture.Project); }
            var process = Process.Start(info)!;
            // Drain (never print) first-run admin credentials emitted by this isolated test process.
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(2) };
            var server = new RunningServer(process, client, stdout, stderr);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!timeout.IsCancellationRequested)
                {
                    if (process.HasExited) throw new InvalidOperationException("Fixture server exited: " + process.ExitCode);
                    try { using var response = await client.GetAsync("/", timeout.Token); if (response.IsSuccessStatusCode) return server; }
                    catch (HttpRequestException) { }
                    await Task.Delay(100, timeout.Token);
                }
                throw new TimeoutException("Fixture server did not start");
            }
            catch { await server.DisposeAsync(); throw; }
        }
        private static async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() != null) { } }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); process.Dispose();
        }
    }
}

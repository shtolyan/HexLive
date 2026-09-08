using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HexLive.Server.Assets;
using HexLive.Server.GodMode;
using HexLive.Server.Llm;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[NonParallelizable]
public sealed class AdminCommandBusTests
{
    private string _directory = null!, _client = null!, _token = null!;
    private WorldSupervisor _worlds = null!;
    private AdminAccess _access = null!;
    private AdminCommandBus _bus = null!;
    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hexlive-admin-bus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData", "simdata.json"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null);
        var catalog = new AssetGarmentCatalog(new AssetRegistryStore(Path.Combine(_directory, "assets")),
            Path.Combine(dir!.FullName, "SimData", "simdata.json"));
        _worlds = new WorldSupervisor(12345, GameMode.Feud, Path.Combine(_directory, "world.sav"), catalog,
            false, false, new LlmHostOptions(), null, CancellationToken.None);
        _worlds.Host.PauseAsOperator();
        _access = new AdminAccess(Path.Combine(_directory, "access.json"));
        _client = Guid.NewGuid().ToString("N"); _token = new string('A', 64);
        _access.Decide(_access.RequestAccess(_client, _token).RequestId, true, null);
        _bus = new AdminCommandBus(_worlds, _access, Path.Combine(_directory, "receipts"));
    }
    [TearDown]
    public void TearDown()
    {
        _worlds?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    private JsonElement Execute(AdminCommand c, string? token = null) => JsonSerializer.SerializeToElement(
        _bus.Execute(_client, token ?? _token, c, _worlds.Host.McpSessionEpoch), AdminCommandBus.Json);
    private AdminCommand Command(string kind) => new() { Kind = kind, OperationId = Guid.NewGuid().ToString("N"),
        NpcId = _worlds.Host.Read(w => w.Entities.Npcs.Values.First().Id.Value) };
    [Test]
    public void HubRejectsStaleContextAndDoesNotPaintClarificationAsSuccess()
    {
        var hub = new AdminAgentHub(_access, _bus, _worlds); hub.Next("test-agent");
        var context = new HexLive.Simulation.Wire.AdminRequestContext { Epoch = "stale" };
        var result = JsonSerializer.SerializeToElement(hub.Submit(_client, _token, Guid.NewGuid().ToString("N"), "Вылечи", 0, context), AdminCommandBus.Json);
        Assert.That(result.GetProperty("reason").GetString(), Is.EqualTo("WorldChanged"));
        context.Epoch = hub.Epoch; var id = Guid.NewGuid().ToString("N");
        hub.Submit(_client, _token, id, "Вылечи", 0, context); hub.Next("test-agent");
        hub.Reply("test-agent", id, "Кого вылечить?");
        result = JsonSerializer.SerializeToElement(hub.Poll(_client, _token, id), AdminCommandBus.Json);
        Assert.That(result.GetProperty("success").GetBoolean(), Is.False);
    }
    [Test]
    public void HubRecordsPartialFailureAfterAnAppliedAction()
    {
        var hub = new AdminAgentHub(_access, _bus, _worlds); hub.Next("test-agent");
        var id = Guid.NewGuid().ToString("N"); var npc = Command("heal").NpcId;
        hub.Submit(_client, _token, id, "Вылечи", npc); hub.Next("test-agent");
        var cmd = new AdminCommand { Kind = "heal", NpcId = npc, OperationId = Guid.NewGuid().ToString("N") };
        var result = hub.Tool("test-agent", id, "admin_execute", JsonSerializer.SerializeToElement(cmd, AdminCommandBus.Json));
        Assert.That(JsonSerializer.SerializeToElement(result, AdminCommandBus.Json).GetProperty("accepted").GetBoolean(), Is.True);
        hub.Reply("test-agent", id, "Ошибка провайдера", failed: true);
        var poll = JsonSerializer.SerializeToElement(hub.Poll(_client, _token, id), AdminCommandBus.Json);
        Assert.That(poll.GetProperty("success").GetBoolean(), Is.False);
        Assert.That(poll.GetProperty("results").GetArrayLength(), Is.EqualTo(1));
    }

    [Test]
    public void RandomClothingReceiptSurvivesBusRestartWithoutGivingASecondItem()
    {
        var c = Command("give_garment"); c.Category = "skirt"; c.DefinitionId = "random";
        _worlds.Host.Read(w => { var n = w.Entities.Npcs[new HexLive.Simulation.Common.EntityId(c.NpcId)]; n.Inventory.Items.Clear(); n.Inventory.Capacity = 100; return 0; });
        var first = Execute(c);
        Assert.That(first.GetProperty("accepted").GetBoolean(), Is.True);
        _bus = new AdminCommandBus(_worlds, _access, Path.Combine(_directory, "receipts"));
        var repeat = Execute(c);
        Assert.That(repeat.GetProperty("definitionId").GetString(), Is.EqualTo(first.GetProperty("definitionId").GetString()));
        Assert.That(_worlds.Host.Read(w => w.Entities.Npcs[new HexLive.Simulation.Common.EntityId(c.NpcId)].Inventory.Items.Count), Is.EqualTo(1));
    }
    [Test]
    public void ConfirmationCannotBeTransferredToAnotherApprovedDevice()
    {
        var preview = Execute(Command("clear_inventory"));
        var second = new string('B', 64);
        _access.Decide(_access.RequestAccess(_client, second).RequestId, true, null);
        var id = preview.GetProperty("confirmationId").GetString()!;
        var denied = JsonSerializer.SerializeToElement(_bus.Confirm(_client, second, id, true), AdminCommandBus.Json);
        Assert.That(denied.GetProperty("reason").GetString(), Is.EqualTo("ConfirmationExpired"));
        var accepted = JsonSerializer.SerializeToElement(_bus.Confirm(_client, _token, id, true), AdminCommandBus.Json);
        Assert.That(accepted.GetProperty("accepted").GetBoolean(), Is.True);
    }
    [Test]
    public void CameraWithoutGroundCannotSpawnAndStopsLaterActions()
    {
        var hub = new AdminAgentHub(_access, _bus, _worlds); hub.Next("test-agent");
        var id = Guid.NewGuid().ToString("N"); hub.Submit(_client, _token, id, "Создай рядом с камерой", 0);
        hub.Next("test-agent");
        var command = new AdminCommand { Kind = "spawn_npc", Location = "camera", OperationId = Guid.NewGuid().ToString("N") };
        var reply = JsonSerializer.SerializeToElement(hub.Tool("test-agent", id, "admin_execute", JsonSerializer.SerializeToElement(command, AdminCommandBus.Json)), AdminCommandBus.Json);
        Assert.That(reply.GetProperty("reason").GetString(), Is.EqualTo("CameraGroundUnavailable"));
        reply = JsonSerializer.SerializeToElement(hub.Tool("test-agent", id, "admin_execute", JsonSerializer.SerializeToElement(Command("heal"), AdminCommandBus.Json)), AdminCommandBus.Json);
        Assert.That(reply.GetProperty("reason").GetString(), Is.EqualTo("TurnStopped"));
    }

    [Test]
    public async System.Threading.Tasks.Task ViewerRequestsAndChecksAccessWithoutCallingTheAgent()
    {
        using var stt = new DeepgramTokenBroker("");
        var hub = new AdminAgentHub(_access, _bus, _worlds);
        var protocol = new AdminViewerProtocol(_access, _bus, hub, stt);
        var device = new string('C', 64);
        async System.Threading.Tasks.Task<JsonElement> Call(string kind)
        {
            using var doc = JsonDocument.Parse(await protocol.Handle(_client,
                JsonSerializer.Serialize(new { kind, token = device }), CancellationToken.None));
            return doc.RootElement.Clone();
        }
        var request = (await Call("request_access")).GetProperty("payload");
        Assert.That(request.GetProperty("state").GetString(), Is.EqualTo("pending"));
        var repeat = (await Call("request_access")).GetProperty("payload");
        Assert.That(repeat.GetProperty("requestId").GetString(), Is.EqualTo(request.GetProperty("requestId").GetString()));
        _access.Decide(request.GetProperty("requestId").GetString()!, true, null);
        var status = (await Call("access_status")).GetProperty("payload");
        Assert.That(status.GetProperty("accepted").GetBoolean(), Is.True);
        Assert.That(status.GetProperty("agentAvailable").GetBoolean(), Is.False);
        var refused = (await Call("stt")).GetProperty("payload");
        Assert.That(refused.GetProperty("accepted").GetBoolean(), Is.False);
        Assert.That(_access.Authorized(_client, device), Is.True);
    }

    [Test]
    public void UnauthorizedAndRevokedTokensCannotMutate()
    {
        var c = Command("rename_npc"); c.Text = "Forbidden";
        Assert.That(Execute(c, "wrong").GetProperty("reason").GetString(), Is.EqualTo("AdminUnauthorized"));
        _access.Revoke(_client);
        Assert.That(Execute(c).GetProperty("reason").GetString(), Is.EqualTo("AdminUnauthorized"));
    }
    [Test]
    public void AddAttribute_OperationReceiptPreventsDoubleApplicationAndConflictingReuse()
    {
        var c = Command("set_attribute"); c.Target = "Strength"; c.Value = .1f; c.Add = true;
        _worlds.Host.Read(w => { w.Entities.Npcs.Values.First().Attributes.Strength = .2f; return 0; });
        Assert.That(Execute(c).GetProperty("accepted").GetBoolean(), Is.True);
        Assert.That(Execute(c).GetProperty("accepted").GetBoolean(), Is.True);
        Assert.That(_worlds.Host.Read(w => w.Entities.Npcs.Values.First().Attributes.Strength), Is.EqualTo(.3f).Within(.0001f));
        c.Value = .2f;
        Assert.That(Execute(c).GetProperty("reason").GetString(), Is.EqualTo("OperationIdConflict"));
    }
    [Test]
    public void DestructiveCommandRequiresViewerConfirmation_ChangedTargetInvalidatesIt()
    {
        var c = Command("clear_inventory");
        var preview = Execute(c);
        Assert.That(preview.GetProperty("reason").GetString(), Is.EqualTo("ConfirmationRequired"));
        var rename = Command("rename_npc"); rename.Text = "Changed target";
        Execute(rename);
        var result = JsonSerializer.SerializeToElement(_bus.Confirm(_client, _token,
            preview.GetProperty("confirmationId").GetString()!, true), AdminCommandBus.Json);
        Assert.That(result.GetProperty("reason").GetString(), Is.EqualTo("TargetChanged"));
        preview = Execute(c);
        result = JsonSerializer.SerializeToElement(_bus.Confirm(_client, _token,
            preview.GetProperty("confirmationId").GetString()!, true), AdminCommandBus.Json);
        Assert.That(result.GetProperty("accepted").GetBoolean(), Is.True);
        Assert.That(_worlds.Host.Read(w => w.Entities.Npcs.Values.First().Inventory.Items.Count), Is.Zero);
    }
}

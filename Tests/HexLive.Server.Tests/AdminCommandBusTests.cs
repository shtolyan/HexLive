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
        _client = Guid.NewGuid().ToString("N"); _token = _access.Exchange(_client, _access.Issue(_client))!;
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

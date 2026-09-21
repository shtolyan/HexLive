using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Admin;
using HexLive.Server.Assets;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server;

public sealed class WorldLogin { public string Password { get; set; } = string.Empty; }
public sealed class WorldPreviewRequest
{
    public int Seed { get; set; }
    public GameMode Mode { get; set; }
    public string PlayerId { get; set; } = string.Empty;
}
public sealed class WorldCreateRequest
{
    public string RequestId { get; set; } = string.Empty;
    public WorldCreationConfig Config { get; set; } = new();
}
public sealed class WorldOperation
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = "running";
    public string WorldId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
public static class WorldCreationEndpoints
{
    private const string Root = "/api/worlds/v1";
    public static void Map(WebApplication app, WorldSupervisor worlds, AdminAccount account,
        AdminSessions sessions, AssetRegistryStore registry, string? companionProfile, AccessTokenFile? playerToken = null)
    {
        var operations = new ConcurrentDictionary<string, WorldOperation>();
        var requests = new ConcurrentDictionary<string, string>();
        var busy = new SemaphoreSlim(1, 1);
        bool SignedIn(HttpContext c)
        {
            if (c.Items[CentralAdminAccess.Marker] is true) return true;
            var auth = c.Request.Headers.Authorization.ToString();
            return auth.StartsWith("Bearer ", StringComparison.Ordinal) && sessions.IsSignedIn(auth.Substring(7));
        }
        app.MapPost(Root + "/login", (HttpContext c, WorldLogin login) =>
        {
            var source = c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (sessions.LockoutSeconds(source) > 0) return Results.StatusCode(429);
            if (!account.Verify(login.Password)) { sessions.RecordFailure(source); return Results.Unauthorized(); }
            sessions.RecordSuccess(source);
            return Results.Json(new { token = sessions.CreateSession(), playerToken = playerToken?.Value, controlEnabled = playerToken != null });
        });
        app.MapGet(Root + "/capabilities", (HttpContext c) =>
        {
            if (!SignedIn(c)) return Results.Unauthorized();
            var records = registry.ReadCurrentRecords().Where(r => r.State == "active").ToArray();
            return Results.Json(new
            {
                version = 1, registryRevision = worlds.CatalogRegistryRevision,
                modes = Enum.GetValues<GameMode>(),
                bodies = ColonistAppearance.Meshes.Concat(new[] { "Kshishtof", "Tonny" }).Where(id => records.Any(r => r.Type == "actor" && r.Id == id)),
                skins = ColonistAppearance.SkinSets.Where(id => records.Any(r => r.Type == "actor" && r.Id == id)), eyes = ColonistAppearance.EyeColors,
                voices = ColonistAppearance.VoiceBanks.Concat(new[] { "kshishtof", "masha" }),
                attributes = AttributeSet.All.Select(v => v.ToString()), skills = SkillSet.All.Select(v => v.ToString()),
                traits = TraitSet.All.Select(v => v.ToString()),
                hair = records.Where(r => r.Type == "hair"),
                clothing = GarmentLibrary.Spawnable.Select(g => new { id = g.Id, prototypeId = g.PrototypeId, name = g.DisplayName, layer = g.Layer.ToString(), sex = g.Sex.ToString(), covers = g.Covers.Select(p => p.ToString()), slots = WearSlotCatalog.For(g.Id).Select(p => p.ToString()), authoredSlots = WearSlotCatalog.Has(g.Id) }),
            });
        });
        app.MapPost(Root + "/preview", async (HttpContext c, WorldPreviewRequest request) =>
        {
            if (!SignedIn(c)) return Results.Unauthorized();
            if (!Enum.IsDefined(request.Mode) || !PlayerCharacterAssignments.TryNormalizePlayerId(request.PlayerId, out _)) return Results.BadRequest();
            if (!await busy.WaitAsync(0)) return Results.Conflict(new { error = "busy" });
            try
            {
                var result = await Task.Run(() => worlds.WithCatalog(() =>
                {
                    var definition = PrototypeWorldDefinitionFactory.Create(request.Seed, request.Mode);
                    var world = new WorldStateFactory().Create(definition);
                    if (!string.IsNullOrWhiteSpace(companionProfile))
                        foreach (var profile in companionProfile.Split(',')) CharacterPresetRegistry.EnsureSpawned(world, profile.Trim(), out _);
                    var config = WorldCreation.Defaults(world);
                    config.CreatorPlayerId = request.PlayerId;
                    config.CatalogRevision = worlds.CatalogRegistryRevision;
                    var hairRecords = registry.ReadCurrentRecords("hair").Where(r => r.State == "active").ToDictionary(r => r.Id);
                    foreach (var npc in config.Characters)
                    {
                        var colours = hairRecords.TryGetValue(npc.Hair ?? "", out var record) && record.Metadata.TryGetValue("colours", out var options) && options.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? options.EnumerateArray().Select(c => c.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
                        npc.HairColour = colours.Length == 0 ? "prototype" : colours[(int)(unchecked((uint)npc.Id * 2654435761u) % (uint)colours.Length)];
                    }
                    return new
                    {
                        config,
                        camps = definition.FactionHomes.Select(h => new { faction = h.Faction, q = h.TileQ, r = h.TileR }),
                        tiles = definition.Fragments.SelectMany(f => f.Tiles).Select(t => new { q = t.Q, r = t.R, water = t.Water, elevation = t.Elevation }),
                    };
                }));
                return Results.Json(result);
            }
            finally { busy.Release(); }
        });
        List<CreationError> Validate(WorldCreationConfig config)
        {
            if (config == null || !Enum.IsDefined(config.Mode)) return new() { new() { Path = "config", Code = "invalid" } };
            var errors = WorldCreation.Validate(config, PrototypeWorldDefinitionFactory.Create(config.Seed, config.Mode));
            if (config.CatalogRevision != worlds.CatalogRegistryRevision) errors.Add(new() { Path = "config", Code = "catalogChanged" });
            if (errors.Count > 0) return errors;
            var actors = registry.ReadCurrentRecords("actor").Where(r => r.State == "active").Select(r => r.Id).ToHashSet();
            var hairs = registry.ReadCurrentRecords("hair").Where(r => r.State == "active").ToDictionary(r => r.Id);
            for (var i = 0; i < config.Characters.Count; i++)
            {
                var n = config.Characters[i]; var p = "characters[" + i + "]";
                var male = n.Body is "Kshishtof" or "Tonny";
                if (!actors.Contains(n.Body)) errors.Add(new() { Path = p + ".body", Code = "unknown" });
                if (!string.IsNullOrEmpty(n.Skin) && !actors.Contains(n.Skin)) errors.Add(new() { Path = p + ".skin", Code = "unknown" });
                if (male && (!string.IsNullOrEmpty(n.Hair) && n.Hair != "none" || !string.IsNullOrEmpty(n.Eyes)))
                    errors.Add(new() { Path = p + ".body", Code = "incompatible" });
                if (!string.IsNullOrEmpty(n.Skin) && !(male ? n.Skin == n.Body : ColonistAppearance.SkinSets.Contains(n.Skin))) errors.Add(new() { Path = p + ".skin", Code = "incompatible" });
                if (!string.IsNullOrEmpty(n.Eyes) && !ColonistAppearance.EyeColors.Contains(n.Eyes)) errors.Add(new() { Path = p + ".eyes", Code = "unknown" });
                if (!string.IsNullOrEmpty(n.Voice) && !ColonistAppearance.VoiceBanks.Contains(n.Voice) && n.Voice != "kshishtof" && n.Voice != "masha") errors.Add(new() { Path = p + ".voice", Code = "unknown" });
                if (!string.IsNullOrEmpty(n.Hair) && n.Hair != "none" && !hairs.ContainsKey(n.Hair)) errors.Add(new() { Path = p + ".hair", Code = "unknown" });
                if (!string.IsNullOrEmpty(n.HairColour) && n.HairColour != "prototype")
                {
                    var valid = hairs.TryGetValue(n.Hair, out var record) && record.Metadata.TryGetValue("colours", out var colours) && colours.ValueKind == System.Text.Json.JsonValueKind.Array &&
                        colours.EnumerateArray().Any(v => v.TryGetProperty("id", out var id) && id.GetString() == n.HairColour);
                    if (!valid) errors.Add(new() { Path = p + ".hairColour", Code = "unknown" });
                }
            }
            if (errors.Count == 0)
            {
                try { _ = new WorldStateFactory().Create(WorldCreation.Definition(config)); }
                catch (WorldCreationPlacementException ex)
                { errors.Add(new() { Path = "characters[" + config.Characters.FindIndex(n => n.Id == ex.CharacterId) + "].camp", Code = "noFreeSpawn" }); }
            }
            return errors;
        }
        app.MapPost(Root + "/validate", (HttpContext c, WorldCreationConfig config) =>
        {
            if (!SignedIn(c)) return Results.Unauthorized();
            return Results.Json(new { errors = worlds.WithCatalog(() => Validate(config)) });
        });
        app.MapGet(Root, (HttpContext c) => !SignedIn(c) ? Results.Unauthorized() : Results.Json(new { activeId = worlds.Library.ActiveId, worlds = worlds.Library.List() }));
        app.MapGet(Root + "/operations/{id}", (HttpContext c, string id) =>
        {
            if (!SignedIn(c)) return Results.Unauthorized();
            if (operations.TryGetValue(id, out var operation)) return Results.Json(operation);
            var saved = worlds.Library.List(includePreparing: true).FirstOrDefault(w => w.RequestId == id);
            return saved == null ? Results.NotFound() : Results.Json(new WorldOperation { Id = id, State = worlds.Library.CreationSucceeded(saved) ? "complete" : "failed", WorldId = saved.Id, Error = worlds.Library.CreationSucceeded(saved) ? "" : "Interrupted operation; retry creation." });
        });
        app.MapPost(Root, async (HttpContext c, WorldCreateRequest request) =>
        {
            if (!SignedIn(c)) return Results.Unauthorized();
            if (playerToken == null && c.Items[CentralAdminAccess.Marker] is not true) return Results.Conflict(new { error = "controlDisabled" });
            if (!Guid.TryParseExact(request.RequestId, "N", out _)) return Results.BadRequest();
            var requestText = System.Text.Json.JsonSerializer.Serialize(request.Config);
            if (operations.TryGetValue(request.RequestId, out var prior))
            {
                if (!requests.TryGetValue(request.RequestId, out var original) || original != requestText)
                    return Results.Conflict(new { error = "requestIdConflict" });
                if (prior.State != "failed") return Results.Json(prior);
            }
            if (!await busy.WaitAsync(0)) return Results.Conflict(new { error = "busy" });
            var backgroundOwnsSlot = false;
            try
            {
                var completed = worlds.Library.List(true).FirstOrDefault(w => w.RequestId == request.RequestId && worlds.Library.CreationSucceeded(w));
                if (completed != null)
                {
                    if (request.Config == null) return Results.Conflict(new { error = "requestIdConflict" });
                    try
                    {
                        var id = worlds.CreateLibraryWorld(request.Config.Seed, request.Config.Mode, request.Config, request.RequestId);
                        return Results.Json(new WorldOperation { Id = request.RequestId, State = "complete", WorldId = id });
                    }
                    catch (InvalidOperationException) { return Results.Conflict(new { error = "requestIdConflict" }); }
                }
                var errors = worlds.WithCatalog(() => Validate(request.Config));
                if (errors.Count > 0) return Results.BadRequest(new { errors });
                var operation = new WorldOperation { Id = request.RequestId };
                requests[request.RequestId] = requestText;
                operations[request.RequestId] = operation;
                backgroundOwnsSlot = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        operation.WorldId = worlds.CreateLibraryWorld(request.Config.Seed, request.Config.Mode, request.Config, request.RequestId);
                        operation.State = "complete";
                    }
                    catch (Exception ex) { operation.Error = ex.Message; operation.State = "failed"; }
                    finally { busy.Release(); }
                });
                return Results.Json(operation, statusCode: 202);
            }
            finally
            {
                // The background operation owns the semaphore only after registration.
                if (!backgroundOwnsSlot) busy.Release();
            }
        });
        app.MapPost(Root + "/{id}/activate", async (HttpContext c, string id) =>
        {
            if (!SignedIn(c)) return Results.Unauthorized();
            if (!await busy.WaitAsync(0)) return Results.Conflict(new { error = "busy" });
            try { await Task.Run(() => worlds.ActivateLibraryWorld(id)); return Results.Ok(); }
            finally { busy.Release(); }
        });
    }
}

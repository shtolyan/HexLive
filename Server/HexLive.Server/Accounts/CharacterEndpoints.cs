using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexLive.Server.Assets;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server.Accounts;

// Strict allow-list: clothing (even null/empty), ids, budgets and ownership cannot enter the command.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CharacterCreateRequest
{
    public string RequestId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public string Skin { get; set; } = "";
    public string Hair { get; set; } = "none";
    public string? HairColour { get; set; }
    public string Eyes { get; set; } = "";
    public string? Voice { get; set; }
    public List<string> Traits { get; set; } = new();
    public List<CreationValue> Attributes { get; set; } = new();
    public List<CreationValue> Skills { get; set; } = new();
    public string Camp { get; set; } = "";
}

internal static class CharacterEndpoints
{
    private const string Root = AccountEndpoints.Prefix + "/character";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Voices = ColonistAppearance.VoiceBanks.Concat(new[] { "kshishtof", "masha" }).ToArray();

    internal static void Map(WebApplication app, AccountStore accounts, WorldSupervisor worlds)
    {
        var limit = new SessionRateLimit();
        app.MapGet(Root + "/options", (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            if (accounts.Resolve(AccountEndpoints.Bearer(context)) == null) return Error("invalid_credentials", 401);
            return worlds.WithCatalog(() => {
                var records = worlds.AppearanceRecords();
                var bodies = records.Where(r => r.Type == "actor" && r.Id is "Marta" or "Kshishtof").OrderBy(r => r.Id).ToArray();
                var hair = records.Where(r => r.Type == "hair").OrderBy(r => r.Id).Select(r => new {
                    id = r.Id, colours = HairColours(r) }).Prepend(new { id = "none", colours = new[] { "prototype" } }).ToArray();
                return Results.Json(new {
                    bodies = bodies.Select(b => new { id = b.Id, sex = b.Id == "Kshishtof" ? "male" : "female",
                        skins = CharacterAppearanceValidation.Skins(b.Id, records),
                        hair = hair.Select(h => h.id).ToArray(),
                        startingClothing = PlayerCharacterCreation.StartingClothing(b.Id),
                        eyes = CharacterAppearanceValidation.Eyes(b) }),
                    skins = bodies.SelectMany(b => CharacterAppearanceValidation.Skins(b.Id, records)).Distinct().ToArray(),
                    hair, eyes = bodies.SelectMany(CharacterAppearanceValidation.Eyes).Distinct().ToArray(),
                    voices = Voices, traits = TraitSet.All.Select(k => k.ToString()).ToArray(),
                    attributes = AttributeSet.All.Select(k => k.ToString()).ToArray(), skills = SkillSet.All.Select(k => k.ToString()).ToArray(),
                    attributeBudget = WorldCreation.DefaultAttributeBudget, skillBudget = PlayerCharacterCreation.DefaultSkillBudget,
                    // Explicit projection: never serialize a world recipe, roster, resident count or name here.
                    camps = worlds.Host.Read(w => w.FactionHomes.Keys.Where(c => PlayerCharacterCreation.IsAvailableCamp(w, c))
                        .OrderBy(c => (int)c).Select(c => new { id = c.ToString(), nameKey = "lobby.camp",
                            descriptionKey = (string?)null, hasSpace = PlayerCharacterCreation.HasSpace(w, c) }).ToArray())
                });
            });
        });
        app.MapPost(Root, async (HttpContext context) => {
            context.Response.Headers.CacheControl = "no-store";
            var session = accounts.Resolve(AccountEndpoints.Bearer(context));
            if (session == null) return Error("invalid_credentials", 401);
            if (!limit.Allow(session.PlayerId)) return Error("try_later", 429);
            CharacterCreateRequest? request;
            try
            {
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
                {
                    if (body.Length + count > 16384) return Error("invalid_request", 400);
                    body.Write(buffer, 0, count);
                }
                // Reject repeated JSON members as well: one unambiguous command and one request id.
                using var document = JsonDocument.Parse(body.ToArray());
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    document.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Distinct().Count() != document.RootElement.EnumerateObject().Count())
                    return Error("invalid_request", 400);
                request = document.RootElement.Deserialize<CharacterCreateRequest>(JsonOptions);
            }
            catch (JsonException) { return Error("invalid_request", 400); }
            if (request == null || !Guid.TryParseExact(request.RequestId, "N", out var requestGuid)) return Error("invalid_request", 400);
            request.RequestId = requestGuid.ToString("N");
            try
            {
                return worlds.WithCatalog<IResult>(() => {
                    // A successful request always returns its original receipt, including after death.
                    var alreadyCreated = worlds.Host.Read(w => w.PlayerCharacterCreations.Any(r => r.PlayerId == session.PlayerId && r.RequestId == request.RequestId));
                    CharacterCreationConfig character;
                    if (!alreadyCreated)
                    {
                        var errors = Validate(request, worlds.AppearanceRecords(), out character);
                        if (errors.Count != 0) return Results.Json(new { error = "validation_failed", errors }, statusCode: 400);
                    }
                    else character = new();
                    var result = worlds.Host.SubmitCharacterCreation(session.PlayerId, request.RequestId, character, worlds.Assignments, worlds.CaptureViewerSession().WorldGeneration);
                    if (result.Receipt == null) return Error(result.Error, 409);
                    worlds.ReconnectViewers();
                    var receipt = result.Receipt;
                    return Results.Json(new { requestId = receipt.RequestId, worldId = worlds.Host.WorldId,
                        npcId = receipt.NpcId, name = receipt.Name, camp = receipt.Camp.ToString() });
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error("try_later", 503); }
        });
    }

    internal static List<CreationError> Validate(CharacterCreateRequest request, IReadOnlyList<ContentObjectRecord> records,
        out CharacterCreationConfig character)
    {
        var errors = new List<CreationError>();
        void ErrorAt(string path, string code) => errors.Add(new CreationError { Path = path, Code = code });
        var validCamp = Enum.TryParse<Faction>(request.Camp, out var camp) && camp.ToString() == request.Camp;
        character = new CharacterCreationConfig {
            Name = request.Name, Body = request.Body, Skin = request.Skin, Hair = request.Hair,
            HairColour = request.HairColour ?? "", Eyes = request.Eyes, Voice = request.Voice ?? (request.Body == "Kshishtof" ? "kshishtof" : "marta"),
            Camp = camp, Traits = request.Traits, Attributes = request.Attributes, Skills = request.Skills,
            Clothing = PlayerCharacterCreation.StartingClothing(request.Body) };
        if (!validCamp) ErrorAt("camp", "unknown");
        if (request.Body is not ("Marta" or "Kshishtof")) ErrorAt("body", "unknown");
        if (string.IsNullOrEmpty(request.Skin)) ErrorAt("skin", "required");
        if (string.IsNullOrEmpty(request.Hair) || request.Eyes == null || character.Voice.Length == 0) ErrorAt("appearance", "required");
        WorldCreation.ValidateCharacter(character, WorldCreation.DefaultAttributeBudget, PlayerCharacterCreation.DefaultSkillBudget, "character", ErrorAt);
        CharacterAppearanceValidation.Validate(character, "character", records, errors);
        if (request.Traits != null && (request.Traits.Count > TraitSet.All.Length ||
            request.Traits.Any(t => !TraitSet.All.Any(k => k.ToString() == t)) || request.Traits.Distinct().Count() != request.Traits.Count))
            ErrorAt("traits", "unknownOrDuplicate");
        return errors;
    }

    private static string[] HairColours(ContentObjectRecord record) =>
        record.Metadata.TryGetValue("colours", out var colours) && colours.ValueKind == JsonValueKind.Array
            ? colours.EnumerateArray().Where(c => c.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(c => c.GetProperty("id").GetString()!).Concat(HairTintPalette.All.Select(c => c.Id)).Prepend("prototype").Distinct().ToArray()
            : new[] { "prototype" }.Concat(HairTintPalette.All.Select(c => c.Id)).ToArray();
    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);
}

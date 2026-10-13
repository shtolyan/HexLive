using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Server.Assets;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;

namespace HexLive.Server;

// One published-appearance validator for both creation APIs.
internal static class CharacterAppearanceValidation
{
    // §169.5: these skin materials are embedded in the two primal actor payloads,
    // not separate actor records. Keep covered against People prefab/catalog fixtures.
    public static readonly string[] PrimalFemaleSkins = { "Marta", "Molly", "Jana", "Jolly", "Masha", "Rita" };
    public static readonly string[] PrimalMaleSkins = { "Kshishtof", "Tonny" };
    public static bool IsPrimal(ContentObjectRecord? actor) => actor != null &&
        actor.Metadata.TryGetValue("peopleCatalog", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() == "primal-v1";
    public static string[] Skins(string body, IReadOnlyList<ContentObjectRecord> records)
    {
        if (IsPrimal(records.FirstOrDefault(r => r.Type == "actor" && r.Id == body && r.State == "active")))
            return body == "Kshishtof" ? PrimalMaleSkins : PrimalFemaleSkins;
        return records.Where(r => r.Type == "actor" && r.State == "active" &&
            (body is "Kshishtof" or "Tonny" ? r.Id == body : ColonistAppearance.SkinSets.Contains(r.Id))).Select(r => r.Id).ToArray();
    }
    public static void Validate(CharacterCreationConfig n, string p, IReadOnlyList<ContentObjectRecord> records,
        List<CreationError> errors)
    {
        var actors = records.Where(r => r.Type == "actor" && r.State == "active").Select(r => r.Id).ToHashSet();
        var hairs = records.Where(r => r.Type == "hair" && r.State == "active").ToDictionary(r => r.Id);
        var primal = IsPrimal(records.FirstOrDefault(r => r.Type == "actor" && r.Id == n.Body && r.State == "active"));
        var male = n.Body is "Kshishtof" or "Tonny";
        if (!actors.Contains(n.Body)) errors.Add(new CreationError() { Path = p + ".body", Code = "unknown" });
        if (!string.IsNullOrEmpty(n.Skin) && !Skins(n.Body, records).Contains(n.Skin)) errors.Add(new CreationError() { Path = p + ".skin", Code = "unknown" });
        if (male && !string.IsNullOrEmpty(n.Eyes))
            errors.Add(new CreationError() { Path = p + ".body", Code = "incompatible" });
        if (!string.IsNullOrEmpty(n.Skin) && !primal && !(male ? n.Skin == n.Body : ColonistAppearance.SkinSets.Contains(n.Skin))) errors.Add(new CreationError() { Path = p + ".skin", Code = "incompatible" });
        if (!string.IsNullOrEmpty(n.Eyes) && !ColonistAppearance.EyeColors.Contains(n.Eyes)) errors.Add(new CreationError() { Path = p + ".eyes", Code = "unknown" });
        if (!string.IsNullOrEmpty(n.Voice) && !ColonistAppearance.VoiceBanks.Contains(n.Voice) && n.Voice != "kshishtof" && n.Voice != "masha") errors.Add(new CreationError() { Path = p + ".voice", Code = "unknown" });
        if (!string.IsNullOrEmpty(n.Hair) && n.Hair != "none" && !hairs.ContainsKey(n.Hair)) errors.Add(new CreationError() { Path = p + ".hair", Code = "unknown" });
        if (!string.IsNullOrEmpty(n.HairColour) && n.HairColour != "prototype")
        {
            var valid = !string.IsNullOrEmpty(n.Hair) && hairs.TryGetValue(n.Hair, out var record) && record.Metadata.TryGetValue("colours", out var colours) && colours.ValueKind == System.Text.Json.JsonValueKind.Array &&
                colours.EnumerateArray().Any(v => v.TryGetProperty("id", out var id) && id.GetString() == n.HairColour);
            valid |= !string.IsNullOrEmpty(n.Hair) && n.Hair != "none" && hairs.ContainsKey(n.Hair) && HairTintPalette.TryGet(n.HairColour, out _);
            if (!valid) errors.Add(new CreationError() { Path = p + ".hairColour", Code = "unknown" });
        }
    }
}

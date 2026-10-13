using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;

namespace HexLive.UnityPresentation.UI
{
// §171.20: display names are presentation only; published ids remain unchanged.
public sealed class PlayerCreatorAppearance
{
    public readonly JObject Options;
    public readonly CharacterCreationConfig Character;
    public PlayerCreatorAppearance(JObject options, CharacterCreationConfig character = null)
    {
        Options = options;
        Character = character ?? new CharacterCreationConfig { Id = 1, Name = "", Hair = "none", HairColour = "prototype" };
        if (character == null)
        {
            SetSex(false);
            Character.Attributes = Values("attributes").Select(id => new CreationValue { Id = id, Value = .5f }).ToList();
            Character.Skills = Values("skills").Select(id => new CreationValue { Id = id }).ToList();
            Character.Traits.Clear();
            Character.Camp = Enum.TryParse<HexLive.Simulation.Agents.Faction>(
                Options["camps"]?.FirstOrDefault(c => c.Value<bool>("hasSpace"))?.Value<string>("id"), out var camp) ? camp : default;
        }
    }
    public IEnumerable<string> Values(string key) => Options[key]?.Values<string>() ?? Enumerable.Empty<string>();
    public IEnumerable<JToken> Bodies => Options["bodies"] ?? Enumerable.Empty<JToken>();
    public JToken Body => Bodies.FirstOrDefault(b => b.Value<string>("id") == Character.Body);
    public bool IsMale => Body?.Value<string>("sex") == "male";
    public IEnumerable<string> Skins => Body?["skins"]?.Values<string>() ?? Enumerable.Empty<string>();
    public IEnumerable<string> Hair => Body?["hair"]?.Values<string>() ?? Enumerable.Empty<string>();
    public IEnumerable<string> Eyes => Body?["eyes"]?.Values<string>() ?? new[]{""};
    public IEnumerable<string> Colours => Options["hair"]?.FirstOrDefault(h => h.Value<string>("id") == Character.Hair)?["colours"]?.Values<string>() ?? new[] { "prototype" };
    public void SetSex(bool male)
    {
        var body = Bodies.FirstOrDefault(b => b.Value<string>("sex") == (male ? "male" : "female")) ?? Bodies.FirstOrDefault();
        if (body == null) return;
        Character.Body = body.Value<string>("id");
        if (!Skins.Contains(Character.Skin)) Character.Skin = Skins.FirstOrDefault() ?? Character.Body;
        if (!Hair.Contains(Character.Hair)) Character.Hair = Hair.FirstOrDefault() ?? "none";
        if (!Colours.Contains(Character.HairColour)) Character.HairColour = "prototype";
        if(!Eyes.Contains(Character.Eyes)) Character.Eyes=Eyes.FirstOrDefault() ?? "";
        Character.Voice = IsMale ? "kshishtof" : "marta";
        Character.Clothing = body["startingClothing"]?.Values<string>().ToList() ?? PlayerCharacterCreation.StartingClothing(Character.Body);
    }
    public string Label(string kind, string id, int index = 0)
    {
        var key = "creator." + kind + "." + id;
        if (Loc.Has(key)) return Loc.Get(key);
        return string.Format(Loc.Get("creator." + kind + ".fallback"), index + 1);
    }
    public JObject Request(string requestId)
    {
        return new JObject {
            ["requestId"] = requestId, ["name"] = Character.Name.Trim(), ["body"] = Character.Body,
            ["skin"] = Character.Skin, ["hair"] = Character.Hair, ["hairColour"] = Character.HairColour,
            ["eyes"] = Character.Eyes, ["voice"] = Character.Voice, ["camp"] = Character.Camp.ToString(),
            ["traits"] = new JArray(Character.Traits), ["attributes"] = JArray.FromObject(Character.Attributes),
            ["skills"] = JArray.FromObject(Character.Skills)
        };
    }
    public bool ValidBudget(string key)
    {
        var values = key == "attributes" ? Character.Attributes : Character.Skills;
        var ids = Values(key).ToArray();
        return values.Count == ids.Length && values.Select(v => v.Id).Distinct().Count() == ids.Length &&
            values.All(v => ids.Contains(v.Id) && v.Value >= 0 && v.Value <= 1) &&
            values.Sum(v => (double)v.Value) <= Options.Value<double>(key == "attributes" ? "attributeBudget" : "skillBudget") + .000001;
    }
    public bool CanSubmit => !string.IsNullOrWhiteSpace(Character.Name) && Character.Name.Trim().Length <= 64 &&
        Body != null && Skins.Contains(Character.Skin) && Hair.Contains(Character.Hair) && Colours.Contains(Character.HairColour) && Eyes.Contains(Character.Eyes) &&
        ValidBudget("attributes") && ValidBudget("skills") &&
        Options["camps"]?.Any(c => c.Value<string>("id") == Character.Camp.ToString() && c.Value<bool>("hasSpace")) == true;
    public CharacterCreationConfig Look(string skin = null, string hair = null, string colour = null) =>
        new CharacterCreationConfig { Id = Character.Id, Body = Character.Body, Skin = skin ?? Character.Skin,
            Eyes = Character.Eyes, Hair = hair ?? Character.Hair, HairColour = colour ?? Character.HairColour,
            Voice = Character.Voice, Clothing = new List<string>(Character.Clothing) };
}
}

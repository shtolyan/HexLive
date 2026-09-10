using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.Core;

namespace HexLive.Server.Mcp;

/// <summary>§144.14: the same selected-body effects as the character card.</summary>
internal static class McpEffectObservations
{
    // Static I2 data only. There is no state retained for a body, world or session.
    private static readonly Lazy<Dictionary<string, TextPair>> Terms = new(LoadTerms);
    private static readonly Lazy<Dictionary<EffectKind, Definition>> Definitions = new(() =>
    {
        var result = new Dictionary<EffectKind, Definition>();
        foreach (var pair in EffectCatalog.All)
        {
            var def = pair.Value;
            result.Add(pair.Key, new Definition(pair.Key.ToString(), def.Polarity.ToString(),
                def.Category.ToString(), def.TitleKey, def.DescKey, def.Emoji));
        }
        return result;
    });

    public static View Read(WorldState world, NPCState npc)
    {
        var view = new View();
        var sink = new Sink(view);
        EffectReadModel.Visit(world, npc, ref sink);
        return view;
    }

    internal sealed class View
    {
        public List<Status> Effects { get; } = new();
        public List<Impact> Impacts { get; } = new();
        public Dictionary<string, Definition> Definitions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TextPair> Terms { get; } = new(StringComparer.Ordinal);
    }

    internal readonly record struct Status(string kind, float intensity, string detailKey);
    internal readonly record struct Impact(string need, string kind, string direction);
    internal sealed record Definition(string kind, string polarity, string category,
        string titleKey, string descriptionKey, string emoji);
    internal sealed record TextPair(string en, string ru);

    private readonly struct Sink : IEffectSink
    {
        private readonly View _view;
        public Sink(View view) => _view = view;

        public void Add(ActiveEffect effect)
        {
            var definition = Include(effect.Kind);
            _view.Effects.Add(new Status(definition.kind, effect.Intensity, effect.DetailKey));
            if (effect.DetailKey.Length > 0) IncludeTerm(effect.DetailKey);
        }

        public void Add(EffectImpact impact)
        {
            var definition = Include(impact.Kind);
            var need = impact.Need.ToString();
            _view.Impacts.Add(new Impact(need, definition.kind, impact.Direction.ToString()));
            IncludeTerm("need." + need.ToLowerInvariant());
        }

        private Definition Include(EffectKind kind)
        {
            var definition = Definitions.Value[kind];
            if (_view.Definitions.TryAdd(definition.kind, definition))
            {
                IncludeTerm(definition.titleKey);
                IncludeTerm(definition.descriptionKey);
            }
            return definition;
        }

        private void IncludeTerm(string key) => _view.Terms.TryAdd(key, Terms.Value[key]);
    }

    private static Dictionary<string, TextPair> LoadTerms()
    {
        using var resource = typeof(McpEffectObservations).Assembly.GetManifestResourceStream(
            "HexLive.Mcp.EffectTerms.json") ?? throw new InvalidDataException("Missing I2 effect terms resource");
        return JsonSerializer.Deserialize<Dictionary<string, TextPair>>(resource)
            ?? throw new InvalidDataException("Invalid I2 effect terms resource");
    }
}

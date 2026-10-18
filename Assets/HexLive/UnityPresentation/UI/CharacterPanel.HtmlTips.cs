using System;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.Debug;
using Newtonsoft.Json.Linq;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the data behind the page's rich tooltips (tooltip.js), the same
    /// the UI Toolkit card showed: an effect's detailed description, and for a
    /// need the effects acting on it right now — only the rows the simulation
    /// recorded at the real mutation sites (NpcSnapshot.EffectImpacts, §48.7),
    /// never guessed from the final number.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        /// <summary>«hunger» … «thermal» → the effects pushing that need up or down.</summary>
        private JArray HtmlNeedImpacts(NpcSnapshot npc, string needId)
        {
            var impacts = new JArray();
            var kindName = needId == "thermal" ? nameof(NeedKind.Temperature) : needId;
            if (!Enum.TryParse(kindName, true, out NeedKind need)) return impacts;

            var parsed = _effectImpactParseScratch;
            parsed.Clear();
            foreach (var raw in npc.EffectImpacts)
            {
                var fields = raw.Split('\t');
                if (fields.Length < 3 ||
                    !Enum.TryParse(fields[0], out NeedKind rowNeed) || rowNeed != need ||
                    !Enum.TryParse(fields[1], out EffectKind kind) ||
                    !EffectCatalog.TryGet(kind, out _) ||
                    !Enum.TryParse(fields[2], out EffectImpactDirection direction))
                {
                    continue;
                }
                if (!HasVisibleImpact(parsed, kind, direction))
                    parsed.Add(new EffectImpactView { Kind = kind, Direction = direction });
            }

            // ShowNeedTooltip's order: what hurts first.
            parsed.Sort((a, b) =>
            {
                var direction = a.Direction.CompareTo(b.Direction);
                return direction != 0 ? direction : a.Kind.CompareTo(b.Kind);
            });
            foreach (var impact in parsed)
            {
                var def = EffectCatalog.Get(impact.Kind);
                impacts.Add(new JObject
                {
                    ["emoji"] = def.Emoji, ["title"] = def.TitleKey,
                    ["up"] = impact.Direction == EffectImpactDirection.Positive,
                });
            }
            return impacts;
        }
    }
}

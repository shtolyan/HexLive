using System;
using System.Globalization;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.Content;
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
        private JArray HtmlNeedImpacts(WorldSnapshot snapshot, NpcSnapshot npc, string needId)
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
                    ["up"] = (impact.Direction == EffectImpactDirection.Positive) != (need == NeedKind.Stress),
                    ["positive"] = impact.Direction == EffectImpactDirection.Positive,
                });
            }
            if (need == NeedKind.Stress)
            {
                foreach (var source in StressSources(snapshot, npc))
                    impacts.Add(new JObject
                    {
                        ["emoji"] = source.IsMob ? "🐾" : "👤", ["name"] = source.Name,
                        ["up"] = true, ["positive"] = false, ["sourceId"] = source.Id, ["sourceMob"] = source.IsMob
                    });
            }
            return impacts;
        }

        private static System.Collections.Generic.IEnumerable<(bool IsMob, int Id, string Name)>
            StressSources(WorldSnapshot snapshot, NpcSnapshot npc)
        {
            foreach (var row in npc.EffectImpacts)
            {
                if (!row.StartsWith("Stress\tThreatened\tNegative\t", StringComparison.Ordinal)) continue;
                var fields = row.Split('\t');
                if (fields.Length != 5 || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    continue;
                if (fields[3] == "npc" && snapshot.Npcs.Find(other => other.Id.Value == id && other.Health > 0f) is { } person)
                    yield return (false, id, Localization.Loc.NpcName(person.DisplayName));
                else if (fields[3] == "mob" && snapshot.Mobs.Find(mob => mob.Id == id && mob.Health > 0f) is { } animal)
                    yield return (true, id, Localization.Loc.Get("effect.stress_source." +
                        (animal.MobId == MobIds.Dog ? "wolf" : "animal")));
            }
        }

        private void FocusStressSource(int observerId, bool isMob, int id)
        {
            var snapshot = _runner?.CreateSnapshot();
            if (snapshot?.Npcs.Find(npc => npc.Id.Value == observerId) is not { } observer ||
                !System.Linq.Enumerable.Any(StressSources(snapshot, observer),
                    source => source.Id == id && source.IsMob == isMob)) return;
            var camera = FindFirstObjectByType<Input.RtsCameraController>();
            if (isMob && snapshot.Mobs.Find(mob => mob.Id == id) is { } animal)
                camera?.MoveToMapPoint(animal.Position);
            else if (!isMob && snapshot.Npcs.Find(npc => npc.Id.Value == id) is { } person)
                camera?.MoveToMapPoint(person.Position);
        }
    }
}

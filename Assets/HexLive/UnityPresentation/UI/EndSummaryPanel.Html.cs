using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the end of the game as the page's «endSummary» screen. Refresh
    /// still counts the colony, the dead and the raft from the completed
    /// snapshot; on the web it hands the same numbers to the page, and the page's
    /// close is the panel's own Dismiss.
    /// </summary>
    public sealed partial class EndSummaryPanel
    {
        private const string HtmlScreen = "endSummary";
        private string _lastHtmlJson;

        private void OnDestroy() => HtmlUi.Unhandle(HtmlScreen, OnHtmlCommand);

        private void PushHtml(WorldSnapshot snapshot, IReadOnlyList<NpcSnapshot> colony, int total, int finalDay)
        {
            if (!HtmlUi.Active || !IsOpen) return;
            var faces = _runner != null ? CharacterPanel.ItemInformationBuilderFor(_runner) : null;
            var people = new JArray();
            foreach (var npc in colony)
            {
                people.Add(new JObject
                {
                    ["id"] = npc.Id.Value, ["name"] = Loc.NpcName(npc.DisplayName), ["face"] = faces?.HtmlFace(npc.Id.Value),
                    ["health"] = Round(npc.Health), ["blood"] = Round(npc.Blood), ["stamina"] = Round(npc.Stamina),
                    ["wounds"] = npc.Wounds.Count + npc.SeveredParts.Count,
                });
            }

            var dead = new JArray();
            foreach (var death in snapshot.DeathRecords)
            {
                dead.Add(new JObject
                {
                    ["id"] = death.EntityId,
                    ["name"] = string.IsNullOrWhiteSpace(death.DisplayName) ? $"NPC #{death.EntityId}" : Loc.NpcName(death.DisplayName),
                    ["face"] = faces?.HtmlFace(death.EntityId),
                    ["day"] = DayNumber(death.Tick),
                    ["tile"] = $"{death.Tile.Q},{death.Tile.R}",
                    ["cause"] = HumanizeDeathCause(death.Cause),
                });
            }

            var model = new JObject
            {
                ["survivors"] = colony.Count, ["total"] = total, ["fallen"] = snapshot.DeathRecords.Count,
                ["day"] = finalDay, ["time"] = snapshot.Clock,
                ["raft"] = new JObject { ["done"] = snapshot.RaftProgress, ["target"] = snapshot.RaftTarget },
                ["avgHealth"] = colony.Count > 0 ? Round(AverageHealth(colony)) : null,
                ["people"] = people, ["dead"] = dead,
            };
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _lastHtmlJson) return;
            _lastHtmlJson = json;
            HtmlUi.Show(HtmlScreen, model);
        }

        private static float Round(float value) => Mathf.Round(Mathf.Clamp01(value) * 100f) / 100f;

        private void HideHtml()
        {
            if (_lastHtmlJson == null) return;
            _lastHtmlJson = null;
            HtmlUi.Hide(HtmlScreen);
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            if (command == "close" && IsOpen) Dismiss();
        }
    }
}

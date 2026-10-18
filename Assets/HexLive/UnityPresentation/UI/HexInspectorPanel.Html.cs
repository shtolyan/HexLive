#nullable enable
using System.Globalization;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the hex inspector as the page's «inspector» card, left of the
    /// roster. The same snapshot reading as the UI Toolkit card, rebuilt on the
    /// same tick/coord change; the page gets I2 keys for the row labels
    /// (inspect.row.*) and draws them, so nothing English is left in C#.
    /// The world stays live: a click on another hex just moves the card.
    /// </summary>
    public sealed partial class HexInspectorPanel
    {
        private const string HtmlScreen = "inspector";
        private string? _lastHtmlJson;

        private void OnDestroy() => HtmlUi.Unhandle(HtmlScreen, OnHtmlCommand);

        private void OnHtmlCommand(string command, JObject args)
        {
            if (command == "close") HexSelection.Clear();
        }

        private void HideHtml()
        {
            if (_lastHtmlJson == null) return;
            HtmlUi.Hide(HtmlScreen);
            _lastHtmlJson = null;
            Invalidate(); // shown again later = published again
        }

        private void PublishHtml(WorldSnapshot snapshot, TileCoord coord)
        {
            var model = new JObject { ["q"] = coord.Q, ["r"] = coord.R };
            var tile = FindTile(snapshot, coord);
            if (tile == null)
            {
                model["empty"] = true;
            }
            else
            {
                var builder = _runner != null ? CharacterPanel.ItemInformationBuilderFor(_runner) : null;
                var objects = new JArray();
                foreach (var obj in FindObjects(snapshot, coord))
                {
                    var def = ResolveDef(obj.DefinitionId);
                    var info = ResolveItemInfo(obj.DefinitionId, def);
                    objects.Add(new JObject
                    {
                        ["icon"] = builder?.HtmlIcon(obj.DefinitionId), ["emoji"] = info.Emoji,
                        ["name"] = ItemName(def, info), ["category"] = ItemCategoryName(def, info),
                        ["desc"] = ItemDesc(def, info), ["stats"] = ObjectRows(obj, def),
                    });
                }
                model["objects"] = objects;

                var people = new JArray();
                foreach (var npc in FindNpcs(snapshot, coord))
                {
                    // §74: DisplayName is a name ID; the player sees the localized term.
                    people.Add(new JObject
                    {
                        ["name"] = string.IsNullOrEmpty(npc.DisplayName) ? "NPC" : Loc.NpcName(npc.DisplayName),
                        ["id"] = npc.Id.Value,
                    });
                }
                model["people"] = people;

                model["hex"] = new JArray
                {
                    Row("walkable", tile.Walkable, tile.Walkable ? "good" : "bad"),
                    Row("blocked", tile.Blocked, tile.Blocked ? "bad" : "good"),
                    Row("indoor", tile.Indoor, tile.Indoor ? "warn" : "dim"),
                    Row("water", tile.Water, tile.Water ? "water" : "dim"),
                    Row("elevation", tile.Elevation.ToString(CultureInfo.InvariantCulture), null),
                    Row("coord", $"{coord.Q},{coord.R}", "dim"),
                };

                var junctions = new JArray();
                foreach (var j in FindJunctions(snapshot, coord))
                {
                    var text = $"{j.Id.Value} / blocked:{Bool(j.Blocked)} occupied:{Bool(j.Occupied)} reserved:{Bool(j.Reserved)}";
                    if (j.IsClimbSeam) text += " / climb";
                    if (j.IsSwimmable) text += " / swim";
                    junctions.Add(text);
                }
                model["junctions"] = junctions;
            }

            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _lastHtmlJson) return;
            _lastHtmlJson = json;
            HtmlUi.Show(HtmlScreen, model);
        }

        private static JArray ObjectRows(ObjectSnapshot obj, HexLive.Simulation.Content.ObjectDefinition? def)
        {
            var rows = new JArray { Row("id", $"#{obj.Id.Value} / {obj.DefinitionId}", "dim") };
            if (!string.IsNullOrEmpty(obj.BuildProduct))
            {
                rows.Add(Row("build_site", $"{obj.BuildProduct} / {Delivered(obj)}/{Bill(obj)}", "warn"));
                BillRow(rows, "logs", obj.DeliveredLogs, obj.BillLogs);
                BillRow(rows, "stones", obj.DeliveredStones, obj.BillStones);
                BillRow(rows, "leaves", obj.DeliveredLeaves, obj.BillLeaves);
                BillRow(rows, "sticks", obj.DeliveredSticks, obj.BillSticks);
                BillRow(rows, "rope", obj.DeliveredRope, obj.BillRope);
                BillRow(rows, "boards", obj.DeliveredBoards, obj.BillBoards);
            }
            if (obj.ResourceAmount > 0.001f)
                rows.Add(Row("resource", obj.ResourceAmount.ToString("0.##", CultureInfo.InvariantCulture), "warn"));
            if (obj.OwnerNpcId.HasValue) rows.Add(Row("owner", $"#{obj.OwnerNpcId.Value}", "dim"));
            if (!string.IsNullOrEmpty(obj.Variant)) rows.Add(Row("variant", obj.Variant, "dim"));
            if (def != null && def.Tags.Count > 0) rows.Add(Row("tags", string.Join(", ", def.Tags), "dim"));
            return rows;
        }

        // A material the site never needed is noise; one it needs shows even at 0/n.
        private static void BillRow(JArray rows, string key, int delivered, int bill)
        {
            if (bill <= 0 && delivered <= 0) return;
            rows.Add(Row(key, $"{delivered}/{bill}", delivered >= bill ? "good" : "warn"));
        }

        private static JArray Row(string key, JToken value, string? tone) => new() { key, value, tone };
    }
}

#nullable enable
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the build tray as the page's «build» screen. Everything in the
    /// world — ghost, green/red check, grid dots, the placing click, Q/E, Esc —
    /// stays here; the page is the catalogue, the chosen card and the rotate
    /// arrows, and its commands call the same methods the UI Toolkit tray does.
    /// The door into build mode is the HUD's «Строить» pill (SimSpeedBar).
    /// </summary>
    public sealed partial class BuildModePanel
    {
        private const string HtmlScreen = "build";
        private static BuildModePanel? _instance;
        private string _statusTerm = "build.mode.hint";
        private Vector2? _htmlArrows;
        private string? _lastHtmlJson;

        /// <summary>The world can take building orders now (what the B key checks).</summary>
        public static bool CanOpen { get; private set; }

        /// <summary>The HUD pill and the B key: open or close the tray.</summary>
        public static void Toggle()
        {
            var panel = _instance;
            if (panel == null || !CanOpen) return;
            if (IsOpen) panel.Close();
            else panel.Open();
        }

        private void UpdateHtml()
        {
            if (!HtmlUi.Active) return;
            if (!IsOpen)
            {
                _htmlArrows = null;
                if (_lastHtmlJson == null) return;
                _lastHtmlJson = null;
                HtmlUi.Hide(HtmlScreen);
                return;
            }

            var model = HtmlModel();
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _lastHtmlJson) return;
            _lastHtmlJson = json;
            HtmlUi.Show(HtmlScreen, model);
        }

        private JObject HtmlModel()
        {
            var builder = _runner != null ? CharacterPanel.ItemInformationBuilderFor(_runner) : null;
            var categories = new JArray();
            var cards = new JArray();
            void Card(string id, string nameTerm, string descriptionTerm, string glyph) => cards.Add(new JObject
            {
                ["id"] = id, ["name"] = Loc.Get(nameTerm), ["desc"] = Loc.Get(descriptionTerm),
                ["icon"] = builder?.HtmlIcon(id), ["glyph"] = glyph,
            });

            // The same catalogue RebuildCatalogUi lays out.
            if (_catalogMode == BuildCatalogMode.Construction)
            {
                categories.Add(new JObject { ["id"] = "house", ["label"] = Loc.Get("blueprint.category.house") });
                Card(HutCardId, "blueprint.catalog.hut.name", "blueprint.catalog.hut.description", "⌂");
                Card(ProjectCardId, "blueprint.catalog.project.name", "blueprint.catalog.project.description", "✎");
                Card(FreeConstructionCardId, "blueprint.catalog.free.name", "blueprint.catalog.free.description", "⌁");
            }
            else
            {
                var entries = BuildCatalogDefinition.ForMode(BuildCatalogMode.Furniture).ToArray();
                foreach (var categoryId in entries.Select(entry => entry.CategoryId).Distinct())
                    categories.Add(new JObject { ["id"] = categoryId, ["label"] = Loc.Get(CategoryTerm(categoryId)) });
                foreach (var entry in entries.Where(entry => entry.CategoryId == _activeCategoryId))
                    Card(entry.DefinitionId, entry.NameTerm, entry.DescriptionTerm, entry.FallbackGlyph);
            }

            // The chosen card on the cursor, else the marked-out site clicked in the world.
            JObject selection;
            var chosen = _activeCardId.Length > 0 ? _activeCardId : _selectedSiteId is not null ? _selectedSiteProduct : null;
            if (chosen != null)
            {
                var (nameTerm, descriptionTerm) = CardTerms(chosen);
                selection = new JObject
                {
                    ["name"] = Loc.Get(nameTerm), ["desc"] = Loc.Get(descriptionTerm),
                    ["availability"] = _activeCardId.Length > 0 ? (_ghostValid ? "ready" : "blocked") : null,
                    ["canDelete"] = _selectedCanCancel,
                    ["canEdit"] = _selectedSiteId is not null && !string.IsNullOrEmpty(_selectedBlueprintJson),
                };
            }
            else
            {
                selection = new JObject { ["name"] = null, ["desc"] = null, ["availability"] = null, ["canDelete"] = false, ["canEdit"] = false };
            }

            return new JObject
            {
                ["mode"] = _catalogMode == BuildCatalogMode.Construction ? "construction" : "furniture",
                ["categories"] = categories,
                ["category"] = _catalogMode == BuildCatalogMode.Construction ? "house" : _activeCategoryId,
                ["cards"] = cards,
                ["active"] = _activeCardId,
                ["selection"] = selection,
                ["status"] = _statusTerm,
                ["arrows"] = _htmlArrows is { } at ? new JObject { ["x"] = at.x, ["y"] = at.y } : null,
            };
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            if (!IsOpen) return; // queued before Escape or B closed the tray
            switch (command)
            {
                case "mode":
                    SetMode((string?)args["value"] == "construction" ? BuildCatalogMode.Construction : BuildCatalogMode.Furniture);
                    break;
                case "category":
                    if (_catalogMode == BuildCatalogMode.Furniture && (string?)args["id"] is { Length: > 0 } category)
                        SetCategory(category);
                    break;
                case "card":
                    var id = (string?)args["id"] ?? string.Empty;
                    // A second click on the card on the cursor puts it back (Esc/right click do the same).
                    if (id.Length > 0 && id == _activeCardId) DropActiveCard();
                    else if (id.Length > 0) SelectCard(id);
                    break;
                case "rotate":
                    Rotate((int?)args["dir"] < 0 ? -1 : 1);
                    break;
                case "delete":
                    CancelSelectedSite();
                    break;
                case "edit":
                    EditSelectedBuilding();
                    break;
                case "finish":
                    Close();
                    break;
            }
        }
    }
}

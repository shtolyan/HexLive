#nullable enable
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.UI;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.HutTest
{
    /// <summary>
    /// §174.7: the in-game house constructor (§120.8 own project, §120.10 free
    /// construction) as the page's «blueprint» screen — the build tray with an
    /// editor header. The UI Toolkit tray keeps running hidden: RefreshUiState
    /// still fills its labels and button states, and the model reads them back,
    /// so the selection rules (rooms, several pieces, «waiting for a floor»)
    /// are not written twice. Only the world-placed constructor goes to the
    /// page; the HutTest dev scene keeps its UI Toolkit tray.
    /// </summary>
    public sealed partial class HutLayoutDesigner
    {
        private const string HtmlScreen = "blueprint";
        private bool _htmlRegistered;
        private string? _lastHtmlJson;

        private bool Html => WorldMode && HtmlUi.Active;

        private void UpdateHtml()
        {
            if (!Html || _document == null) return;
            if (!_htmlRegistered)
            {
                _htmlRegistered = true;
                HtmlUi.Handle(HtmlScreen, OnHtmlCommand);
            }

            if (_panel != null) _panel.style.display = DisplayStyle.None;
            var model = HtmlModel(_document.rootVisualElement);
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _lastHtmlJson) return;
            _lastHtmlJson = json;
            HtmlUi.Show(HtmlScreen, model);
        }

        private void CloseHtml()
        {
            if (!_htmlRegistered) return;
            _htmlRegistered = false;
            HtmlUi.Unhandle(HtmlScreen, OnHtmlCommand);
            if (_lastHtmlJson != null) HtmlUi.Hide(HtmlScreen);
            _lastHtmlJson = null;
        }

        private JObject HtmlModel(VisualElement root)
        {
            var builder = _runner != null ? CharacterPanel.ItemInformationBuilderFor(_runner) : null;
            var entries = BuildCatalogDefinition.ForMode(_catalogMode).ToArray();
            var categories = new JArray();
            foreach (var categoryId in entries.Select(entry => entry.CategoryId).Distinct())
                categories.Add(new JObject { ["id"] = categoryId, ["label"] = Loc.Get(CategoryTerm(categoryId)) });
            var cards = new JArray();
            foreach (var entry in entries.Where(entry => entry.CategoryId == _activeCategoryId))
            {
                cards.Add(new JObject
                {
                    ["id"] = entry.DefinitionId, ["name"] = Loc.Get(entry.NameTerm), ["desc"] = Loc.Get(entry.DescriptionTerm),
                    ["icon"] = builder?.HtmlIcon(entry.DefinitionId), ["glyph"] = entry.FallbackGlyph,
                });
            }

            // What RefreshUiState wrote into the hidden tray.
            var chosen = _activeCatalogEntry != null || !string.IsNullOrEmpty(_selectedId) || _selectedRoomId > 0;
            var tabs = new JArray("construction");
            if (!DirectWorldConstruction) tabs.Add("furniture");
            return new JObject
            {
                ["mode"] = _catalogMode == BuildCatalogMode.Construction ? "construction" : "furniture",
                ["tabs"] = tabs,
                ["categories"] = categories,
                ["category"] = _activeCategoryId,
                ["cards"] = cards,
                ["active"] = _activeCatalogEntry?.DefinitionId ?? string.Empty,
                ["selection"] = new JObject
                {
                    ["name"] = chosen ? _selectionLabel?.text : null,
                    ["desc"] = chosen ? _selectionDescription?.text : null,
                    ["availability"] = !chosen ? null
                        : _availabilityLabel != null && _availabilityLabel.ClassListContains("waiting") ? "floor" : "ready",
                    ["canDelete"] = root.Q<Button>("delete-selection")?.enabledSelf == true,
                    ["canFinish"] = root.Q<Button>("finish-selection")?.enabledSelf == true,
                    ["canEdit"] = false,
                },
                // A term when SetStatus had one; otherwise text it composed (a validator message).
                ["status"] = string.IsNullOrEmpty(_statusKey) ? null : _statusKey,
                ["statusText"] = string.IsNullOrEmpty(_statusKey) ? _statusLabel?.text : null,
                // The rotation handles of a chosen piece are 3D handles in the world.
                ["arrows"] = null,
                ["editor"] = new JObject
                {
                    ["select"] = _tool == Tool.None && _activeCatalogEntry == null,
                    ["canUndo"] = _history.CanUndo,
                    ["canRedo"] = _history.CanRedo,
                    ["build"] = EditingExisting ? "apply" : !DirectWorldConstruction ? "build" : null,
                },
            };
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            switch (command)
            {
                case "mode":
                    var mode = (string?)args["value"] == "furniture" && !DirectWorldConstruction
                        ? BuildCatalogMode.Furniture : BuildCatalogMode.Construction;
                    SetCatalogMode(mode);
                    break;
                case "category":
                    if ((string?)args["id"] is { Length: > 0 } category) SetCategory(category);
                    break;
                case "card":
                    if (BuildCatalogDefinition.TryGet((string?)args["id"] ?? string.Empty, out var entry) && entry != null &&
                        entry.Mode == _catalogMode)
                    {
                        if (_activeCatalogEntry?.DefinitionId == entry.DefinitionId) FinishSelection();
                        else SelectCatalogEntry(entry);
                    }
                    break;
                case "select":
                case "done":
                    FinishSelection();
                    break;
                case "undo": Undo(); break;
                case "redo": Redo(); break;
                case "delete": DeleteSelected(); break;
                case "exit": CloseWorldMode(); break;
                case "build": BuildAndClose(); break;
            }
        }
    }
}

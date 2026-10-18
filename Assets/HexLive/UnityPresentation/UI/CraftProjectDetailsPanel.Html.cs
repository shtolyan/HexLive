#nullable enable
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
/// <summary>
/// §174.7: the unfinished craft project card as the page's «craftProject»
/// screen. The panel lives as before (one GameObject per opening, RuntimeWindow
/// for the world gate and Escape); on the web its card is registered but not
/// drawn, and Refresh hands the page the same numbers it writes to the labels.
/// </summary>
public sealed partial class CraftProjectDetailsPanel
{
    private const string HtmlScreen = "craftProject";
    private bool _html;
    private string? _lastHtmlJson;

    private void InitializeHtml(VisualElement card)
    {
        if (!HtmlUi.Active) return;
        _html = true;
        card.style.display = DisplayStyle.None;
        if (_windowModal?.Backdrop != null) _windowModal.Backdrop.style.display = DisplayStyle.None;
        HtmlUi.Handle(HtmlScreen, OnHtmlCommand);
    }

    private void PushHtml(ObjectSnapshot project, bool ready, bool working, string? workerName)
    {
        if (!_html || _runner == null) return;
        var builder = CharacterPanel.ItemInformationBuilderFor(_runner);
        var item = builder?.HtmlItem(project.Id.Value.ToString(), project.DefinitionId, 1);
        var model = new JObject
        {
            ["name"] = ItemName(_runner, project.DefinitionId),
            ["icon"] = item?["icon"],
            ["emoji"] = item?["emoji"],
            ["batch"] = project.CraftBatchCount,
            ["status"] = ready ? "ready" : project.CraftActive ? "active" : "paused",
            ["worker"] = workerName == null ? null : new JObject { ["name"] = workerName, ["current"] = working },
            ["progress"] = Mathf.Round(Mathf.Clamp01((float)project.CraftWorkDone / project.CraftWorkRequired) * 100f) / 100f,
            ["remaining"] = Mathf.Max(0, project.CraftWorkRequired - project.CraftWorkDone),
            ["required"] = project.CraftWorkRequired,
        };
        var json = model.ToString(Newtonsoft.Json.Formatting.None);
        if (json == _lastHtmlJson) return;
        _lastHtmlJson = json;
        HtmlUi.Show(HtmlScreen, model);
    }

    private void CloseHtml()
    {
        if (!_html) return;
        _html = false;
        HtmlUi.Unhandle(HtmlScreen, OnHtmlCommand);
        if (_lastHtmlJson != null) HtmlUi.Hide(HtmlScreen);
        _lastHtmlJson = null;
    }

    private void OnHtmlCommand(string command, JObject args)
    {
        if (command == "close" && ReferenceEquals(_instance, this)) Close();
    }
}
}

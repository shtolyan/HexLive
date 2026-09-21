#nullable enable
using System;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
[RequireComponent(typeof(UIDocument))]
public sealed class CraftProjectDetailsPanel : MonoBehaviour
{
    private static CraftProjectDetailsPanel? _instance;
    private static int _closedFrame = -1;
    private ISimulationSource? _runner;
    private int _projectId;
    private PanelSettings? _settings;
    private Label _title = null!;
    private Label _status = null!;
    private Label _worker = null!;
    private Label _remaining = null!;
    private ProgressBar _progress = null!;
    private Button _close = null!;
    private float _nextRefresh;

    public static bool IsOpen => _instance != null;
    public static bool BlocksWorldInput => IsOpen || _closedFrame == Time.frameCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _closedFrame = -1; }

    public static void Open(ISimulationSource runner, int projectId, Transform owner)
    {
        Close();
        var host = new GameObject("CraftProjectDetailsPanel");
        host.transform.SetParent(owner, false);
        var panel = host.AddComponent<CraftProjectDetailsPanel>();
        panel._runner = runner;
        panel._projectId = projectId;
        _instance = panel;
        panel.Initialize();
        panel.Refresh();
    }

    public static void Close()
    {
        if (_instance == null) return;
        var panel = _instance;
        _instance = null;
        _closedFrame = Time.frameCount;
        panel.gameObject.SetActive(false);
        Destroy(panel.gameObject);
    }

    private void Initialize()
    {
        var document = GetComponent<UIDocument>();
        var template = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        if (template != null)
        {
            _settings = Instantiate(template);
            _settings.name = "CraftProjectDetailsSettings";
            _settings.sortingOrder = 210;
            document.panelSettings = _settings;
        }
        var root = document.rootVisualElement;
        root.AddToClassList("craft-project-overlay");
        var sheet = Resources.Load<StyleSheet>("HexLive/UI/CraftProjectDetails");
        if (sheet != null) root.styleSheets.Add(sheet);
        var card = new VisualElement();
        card.AddToClassList("craft-project-card");
        _title = new Label(); _title.AddToClassList("craft-project-title");
        _status = new Label();
        _worker = new Label();
        _progress = new ProgressBar { lowValue = 0, highValue = 100 };
        _remaining = new Label();
        _close = new Button(Close);
        card.Add(_title); card.Add(_status); card.Add(_worker);
        card.Add(_progress); card.Add(_remaining); card.Add(_close);
        root.Add(card);
        root.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        root.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
        root.RegisterCallback<WheelEvent>(evt => evt.StopPropagation());
        _close.Focus();
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 0.2f;
        Refresh();
    }

    private void Refresh()
    {
        if (_runner == null || !_runner.IsReady) { Close(); return; }
        var snapshot = _runner.CreateSnapshot();
        if (snapshot == null) return;
        ObjectSnapshot? project = null;
        foreach (var candidate in snapshot.Objects)
            if (candidate.Id.Value == _projectId) { project = candidate; break; }
        if (project == null || project.CraftWorkRequired <= 0) { Close(); return; }
        var ready = project.CraftWorkDone >= project.CraftWorkRequired;
        var percent = Mathf.Clamp01((float)project.CraftWorkDone / project.CraftWorkRequired) * 100f;
        _title.text = ItemName(_runner, project.DefinitionId) +
            (project.CraftBatchCount > 1 ? " × " + project.CraftBatchCount : string.Empty);
        _status.text = Loc.Get(ready ? "craft.project.ready" :
            project.CraftActive ? "craft.project.active" : "craft.project.paused");
        var currentWorker = project.CraftActive ? project.CraftCurrentWorkerId : null;
        var workerId = currentWorker ?? project.CraftLastWorkerId;
        var workerName = Loc.Get("craft.project.unknown_worker");
        if (workerId.HasValue)
        {
            foreach (var npc in snapshot.Npcs)
                if (npc.Id.Value == workerId.Value) { workerName = Loc.NpcName(npc.DisplayName); break; }
            foreach (var corpse in snapshot.Corpses)
                if (corpse.Id.Value == workerId.Value) { workerName = Loc.NpcName(corpse.DisplayName); break; }
        }
        _worker.text = string.Format(Loc.Get(currentWorker.HasValue
            ? "craft.project.current_worker" : "craft.project.last_worker"), workerName);
        _progress.value = percent;
        _progress.title = Math.Floor(percent).ToString("0") + "%";
        _remaining.text = string.Format(Loc.Get("craft.project.remaining"),
            Math.Max(0, project.CraftWorkRequired - project.CraftWorkDone), project.CraftWorkRequired);
        _close.text = Loc.Get("craft.project.close");
    }

    public static string ItemName(ISimulationSource runner, string definitionId)
    {
        var key = "item." + ItemInfo.Slug(definitionId) + ".name";
        if (Loc.Has(key)) return Loc.Get(key);
        return runner.TryGetObjectDefinition(definitionId, out var definition) && definition != null
            ? definition.DisplayName : definitionId;
    }

    private void OnDisable()
    {
        if (_instance != this) return;
        _instance = null;
        _closedFrame = Time.frameCount;
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (_settings != null) Destroy(_settings);
    }
}
}

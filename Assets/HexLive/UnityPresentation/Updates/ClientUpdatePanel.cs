using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.Updates
{
    // §166: one panel survives scene changes, using the game's existing theme and localization.
    public sealed class ClientUpdatePanel : MonoBehaviour
    {
        private VisualElement _root;
        private ClientUpdateService _service;
        private string _dismissed;
        private void Start()
        {
            _service = ClientUpdateService.Instance;
            _dismissed = _service.FailedRelease;
            var document = gameObject.AddComponent<UIDocument>();
            var settings = Instantiate(Resources.Load<PanelSettings>("HexLive/DebugPanelSettings"));
            settings.sortingOrder = 900;
            document.panelSettings = settings;
            document.rootVisualElement.pickingMode = PickingMode.Ignore;
            Resources.Load<VisualTreeAsset>("HexLive/Updates/ClientUpdatePanel").CloneTree(document.rootVisualElement);
            _root = document.rootVisualElement.Q("updateRoot");
            _root.Q<Button>("check").clicked += () => { _ = _service.Check(); };
            _root.Q<Button>("download").clicked += () => { _ = _service.Download(); };
            _root.Q<Button>("install").clicked += () => { _ = _service.Install(); };
            _root.Q<Button>("cancel").clicked += _service.Cancel;
            _root.Q<Button>("later").clicked += () => { _dismissed = _service.Release?.sha256; Refresh(); };
            _service.Changed += Refresh; Loc.LanguageChanged += Refresh;
            Refresh();
        }
        private void Update()
        {
            if (_root == null) return;
            var menu = LoadingScreen.IsActive || GameMenu.IsOpen;
            var notify = _service.Release != null && _service.Release.sha256 != _dismissed;
            _root.EnableInClassList("update-hidden", !menu && !notify && !_service.Busy);
        }
        private void Refresh()
        {
            if (_root == null) return;
            _root.Q<Label>("status").text = Loc.Get(_service.StatusKey);
            var r = _service.Release;
            _root.Q<Label>("details").text = r == null ? "" : $"{Application.version} → {r.version} · {r.size / (1024 * 1024)} MB\n" +
                (Loc.Current == Language.Russian ? r.notesRu : r.notesEn);
            var progress = _root.Q<ProgressBar>("progress"); progress.value = _service.Progress * 100;
            progress.EnableInClassList("update-hidden", !_service.Busy && !_service.Ready);
            Button("check", "update.check", !_service.Busy && _service.Configured, true);
            Button("download", "update.download", !_service.Busy, r != null && !_service.Ready);
            Button("install", "update.install", !_service.Busy, _service.Ready);
            Button("cancel", "update.cancel", true, _service.StatusKey == "update.downloading");
            Button("later", "update.later", !_service.Busy, r != null && !_service.Required);
        }
        private void Button(string name, string term, bool enabled, bool visible)
        {
            var button = _root.Q<Button>(name); button.text = Loc.Get(term);
            button.SetEnabled(enabled); button.EnableInClassList("update-hidden", !visible);
        }
        private void OnDestroy()
        {
            if (_service != null) _service.Changed -= Refresh;
            Loc.LanguageChanged -= Refresh;
        }
    }
}

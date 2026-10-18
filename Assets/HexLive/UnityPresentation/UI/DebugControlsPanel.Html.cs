using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the developer tools as the page's «debug» drawer, opened by the
    /// HUD's DEBUG button (SimSpeedBar). Every action is the panel's own method;
    /// the page shows which switches are on. The body edits (wounds, dirt, tan,
    /// clothes) only exist where the world lives in this process
    /// (SupportsDirectWorldMutation) — elsewhere they are shown disabled with
    /// the reason, instead of buttons that silently do nothing.
    /// </summary>
    public sealed partial class DebugControlsPanel
    {
        private const string HtmlScreen = "debug";
        private static DebugControlsPanel _instance;
        private bool _htmlOpen;
        private bool _htmlApplied;
        private float _htmlNext;
        private string _lastHtmlJson;

        /// <summary>The HUD's DEBUG button state: null while there is no world.</summary>
        public static bool? HtmlOpen => _instance != null && InWorld(_instance._runner) ? _instance._htmlOpen : null;

        public static void ToggleHtml()
        {
            if (_instance == null) return;
            _instance._htmlOpen = !_instance._htmlOpen;
            _instance._htmlNext = 0f;
        }

        /// <summary>The HUD's bug button: the quick report, if this player may file one.</summary>
        public static void OpenBugReport()
        {
            if (_instance == null || !CanReportBugs) return;
            _instance._bugReportPanel?.ToggleQuick();
        }

        public static bool CanReportBugs => !ClosedTestAccess.Enabled || ClosedTestAccess.Can("bugs.create");

        /// <summary>Fixes waiting for the player's test; 0 until the tracker was loaded once.</summary>
        public static int ReadyForTest =>
            BugReportStore.IsLoaded ? BugReportStore.CountWithStatus(BugReportStore.StatusReadyForTest) : 0;

        private static bool InWorld(SimulationRunnerBehaviour runner) =>
            runner != null && runner.IsReady && !LoadingScreen.IsActive;

        private void OnDestroy()
        {
            HtmlUi.Unhandle(HtmlScreen, OnHtmlCommand);
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        private void UpdateHtml()
        {
            if (!HtmlUi.Active) return;
            if (!_htmlApplied)
            {
                _htmlApplied = true;
                ApplyCollapsed(); // the UI Toolkit box and tab never show on the web
            }

            if (!_htmlOpen || !InWorld(_runner))
            {
                if (_lastHtmlJson != null) { HtmlUi.Hide(HtmlScreen); _lastHtmlJson = null; }
                return;
            }

            if (Time.unscaledTime < _htmlNext) return;
            _htmlNext = Time.unscaledTime + 0.25f;
            var model = new JObject
            {
                ["target"] = TargetName(),
                ["mutable"] = _runner.SupportsDirectWorldMutation && _runner.Engine != null,
                ["on"] = new JObject
                {
                    ["clothesHidden"] = HideClothing, ["fog"] = FogOfWar, ["fogSelected"] = FogOfWarSelectedOnly,
                    ["culling"] = PerceptionCulling, ["trace"] = SimTrace.Enabled, ["hexInspector"] = HexSelection.Enabled,
                },
                ["shade"] = Rendering.HexWorldRenderer.ShadeStyle.ToString(),
                ["ready"] = ReadyForTest,
                ["canReport"] = CanReportBugs,
                ["canManage"] = !ClosedTestAccess.Enabled || ClosedTestAccess.Can("bugs.read"),
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                ["console"] = true,
#else
                ["console"] = false,
#endif
            };
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _lastHtmlJson) return;
            _lastHtmlJson = json;
            HtmlUi.Show(HtmlScreen, model);
        }

        private string TargetName()
        {
            if (!NpcSelection.HasSelection || _runner == null) return null;
            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null) return null;
            foreach (var npc in snapshot.Npcs)
                if (npc.Id.Value == NpcSelection.SelectedId) return Loc.NpcName(npc.DisplayName);
            return null;
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            _htmlNext = 0f;
            if (command == "close") { _htmlOpen = false; return; }
            if (command != "act") return;
            switch ((string)args["id"])
            {
                case "wound": AddRandomWound(); break;
                case "bruise": AddRandomBruise(); break;
                case "clearWounds": ClearWounds(); break;
                case "dirtPlus": AdjustHygiene(-0.25f); break;
                case "dirtMinus": AdjustHygiene(+0.25f); break;
                case "tanPlus": AdjustTan(+0.25f); break;
                case "tanMinus": AdjustTan(-0.25f); break;
                case "sweatPlus": SweatOverride = 0.6f; break;
                case "sweatMinus": SweatOverride = -1f; break;
                case "tear": TearClothes(); break;
                case "clothesHidden": ToggleClothes(); break;
                case "fog": ToggleFogOfWar(); break;
                case "fogSelected": ToggleFogSelectedOnly(); break;
                case "culling": TogglePerceptionCulling(); break;
                case "shade": CycleMemoryShade(); break;
                case "trace": ToggleTrace(); break;
                case "hexInspector": ToggleHexInspector(); break;
                case "adminVoice":
                    if (_runner == null) _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
                    _runner?.GetComponent<AdminVoicePanel>()?.OpenDebug();
                    break;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                case "console": _runtimeConsole?.Toggle(); break;
#endif
                case "report": if (CanReportBugs) _bugReportPanel?.ToggleQuick(); break;
                case "manager":
                    if (!ClosedTestAccess.Enabled || ClosedTestAccess.Can("bugs.read")) _bugReportPanel?.ToggleManager();
                    break;
            }
        }
    }
}

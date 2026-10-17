using System;
using System.Collections;
using System.Text;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using HexLive.UnityPresentation.Bootstrap;
using EntityId = HexLive.Simulation.Common.EntityId;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed partial class CharacterPanel
    {
        private enum CharacterControlMode { Ai, Manual, Call }
        private VisualElement _controlCallSegment;
        private VectorIcon _controlCallGlyph;
        private Label _controlCallState;
        private bool _controlModePending;
        private readonly string _controlClientInstance = Guid.NewGuid().ToString("N");
        private long _controlIntentSequence;
        private bool _callPreparing;
        private int _lastCallControlNpc = -1;
        private static int _phoneControlNpc = -1;
        private static int _controlInputNpc = -1;
        private static bool _controlTargetManual;
        public static bool PhoneControls(int npcId) => npcId >= 0 && _phoneControlNpc == npcId;
        public static bool BlocksManualInput(int npcId) => PhoneControls(npcId) || (npcId >= 0 && _controlInputNpc == npcId);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetPhoneControl() { _phoneControlNpc = -1; _controlInputNpc = -1; }

        private VisualElement BuildPhoneControlSegment()
        {
            _controlCallSegment = new VisualElement { name = "control-call", tooltip = Loc.Get("agent.call.button") };
            _controlCallSegment.style.width = Length.Percent(33.333f);
            _controlCallSegment.style.height = Length.Percent(100f);
            _controlCallSegment.style.alignItems = Align.Center;
            _controlCallSegment.style.justifyContent = Justify.Center;
            _controlCallGlyph = new VectorIcon(VectorIcon.Kind.Phone, TextMute);
            _controlCallGlyph.style.width = 23; _controlCallGlyph.style.height = 23;
            _controlCallSegment.Add(_controlCallGlyph);
            _controlCallState = new Label { name = "control-call-status", pickingMode = PickingMode.Ignore };
            _controlCallState.style.position = Position.Absolute;
            _controlCallState.style.left = 12; _controlCallState.style.top = 49;
            _controlCallState.style.width = 160; _controlCallState.style.fontSize = 9;
            _controlCallState.style.whiteSpace = WhiteSpace.Normal;
            _controlCallState.style.color = TextMute;
            _controlCallSegment.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                SelectCharacterControlMode(ServerVoiceActive || _callPreparing ? CharacterControlMode.Ai : CharacterControlMode.Call);
                e.StopPropagation();
            });
            return _controlCallSegment;
        }

        private void SelectCharacterControlMode(CharacterControlMode mode)
        {
            if (_runner == null || !_runner.SupportsNpcCommands || NpcSelection.Count != 1 ||
                _agentVoiceNpc?.Health <= 0f || !_runner.IsAssignedNpc(new EntityId(NpcSelection.PrimaryId))) return;
            if (mode == CharacterControlMode.Call && !ServerVoiceEligible) return;
            var npc = NpcSelection.PrimaryId;
            var wasCall = ServerVoiceActive || _callPreparing || _lastCallControlNpc == npc;
            if (mode != CharacterControlMode.Call && !_controlAvailable && !wasCall) return;
            var intent = ++_controlIntentSequence;
            _serverVoice?.Stop(); ReleaseServerCallActor();
            _callPreparing = mode == CharacterControlMode.Call;
            _phoneControlNpc = _callPreparing ? npc : -1;
            // Keep stale snapshots from renewing manual control while the chosen
            // mode is being applied. The matching authoritative snapshot clears it.
            _controlInputNpc = npc;
            _controlTargetManual = mode == CharacterControlMode.Manual;
            ClearServerVoiceFailure();
            var server = WebVoiceAgent.PageServerHttpBase();
            if (WebVoiceAgent.Supported && server != null && (mode == CharacterControlMode.Call || wasCall))
            {
                _controlModePending = true;
                _lastCallControlNpc = npc;
                StartCoroutine(SelectServerControlMode(npc, mode, server, SessionConfig.ControlToken, intent));
            }
            else
            {
                _controlModePending = false;
                _runner.EnqueueCommand(new SetManualControlCommand(new EntityId(npc), mode == CharacterControlMode.Manual));
                _lastCallControlNpc = -1;
            }
            RenderServerVoiceButton();
        }

        [Serializable] private sealed class ControlModeResponse { public long controlEpoch; public string mode; }
        private IEnumerator SelectServerControlMode(int npc, CharacterControlMode mode, string server, string bearer, long intent)
        {
            var modeText = mode == CharacterControlMode.Call ? "call" : mode == CharacterControlMode.Manual ? "manual" : "ai";
            using var request = new UnityWebRequest(server.TrimEnd('/') + "/api/voice/v1/control", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{\"npcId\":"+npc+",\"mode\":\""+modeText+
                "\",\"clientInstance\":\""+_controlClientInstance+"\",\"clientSequence\":"+intent+"}"));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 10;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + bearer);
            request.SetRequestHeader("X-HexLive-Client-Id", SessionConfig.ClientId);
            yield return request.SendWebRequest();
            if (intent != _controlIntentSequence) yield break;
            _controlModePending = false; _callPreparing = false;
            if (NpcSelection.PrimaryId != npc || SessionConfig.ControlToken != bearer || _runner == null || !_runner.IsAssignedNpc(new EntityId(npc)))
            { _phoneControlNpc = -1; _controlInputNpc = -1; yield break; }
            if (request.result != UnityWebRequest.Result.Success)
            {
                _phoneControlNpc = -1;
                if (request.responseCode == 404) _lastCallControlNpc = -1;
                FailServerVoice("session:" + request.responseCode); yield break;
            }
            _refreshedTick = -1; _manualEdgeNpcId = -1;
            if (mode != CharacterControlMode.Call) { _phoneControlNpc = -1; _lastCallControlNpc = -1; yield break; }
            ControlModeResponse response = null;
            try { response = JsonUtility.FromJson<ControlModeResponse>(request.downloadHandler.text); } catch (ArgumentException) { }
            if (response == null || response.mode != "call" || !ServerVoiceEligible)
            { _phoneControlNpc = -1; FailServerVoice("control_unavailable"); yield break; }
            StartServerVoice(response.controlEpoch);
        }
    }
}

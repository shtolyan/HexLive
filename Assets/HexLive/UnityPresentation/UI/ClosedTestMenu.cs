using System;
using System.Collections;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    internal sealed class ClosedTestMenu : VisualElement
    {
        [Serializable] private sealed class Identity { public string accountId; public string[] permissions; }
        [Serializable] private sealed class ServerStatus { public string status; }
        private readonly MonoBehaviour _owner;
        private readonly Action<string, string> _connect;
        private readonly Label _message = new Label();
        private readonly VisualElement _body = new VisualElement();
        private string _key;
        private int _generation;

        public ClosedTestMenu(MonoBehaviour owner, Action<string, string> connect)
        {
            _owner = owner; _connect = connect;
            AddToClassList("closed-test");
            styleSheets.Add(Resources.Load<StyleSheet>("HexLive/UI/ClosedTestMenu"));
            var title = new Label("HEX LIVE"); title.AddToClassList("closed-test-title"); Add(title);
            Add(new Label(Loc.Get("closedtest.title")));
            Add(_body); _message.AddToClassList("closed-test-message"); Add(_message);
            Add(new Button(() => Application.Quit()) { text = Loc.Get("menu.quit") });
            RegisterCallback<DetachFromPanelEvent>(_ => ++_generation);
            _owner.StartCoroutine(CheckCompatibility());
        }

        private IEnumerator CheckCompatibility()
        {
            _message.text = Loc.Get("closedtest.checking");
            var generation = ++_generation;
            var requests = new UnityWebRequest[ClosedTestAccess.Servers.Length];
            try
            {
                for (var i = 0; i < requests.Length; ++i)
                {
                    requests[i] = UnityWebRequest.Get(ClosedTestAccess.Servers[i] + "/api/client/v1/compatibility");
                    requests[i].timeout = 5; requests[i].redirectLimit = 0;
                    requests[i].SendWebRequest();
                }
                foreach (var request in requests)
                {
                    while (!request.isDone) yield return null;
                    if (generation != _generation) yield break;
                    var protocol = ProtocolUpdatePanel.ReadProtocol(request);
                    if (protocol > 0 && protocol != HexLive.Simulation.Wire.Handshake.ProtocolVersion)
                    {
                        Clear(); Add(new ProtocolUpdatePanel(protocol)); yield break;
                    }
                }
            }
            finally { foreach (var request in requests) request?.Dispose(); }
            _message.text = "";
            try { _key = ClosedTestAccess.ReadKey(); }
            catch (Exception) { _message.text = Loc.Get("closedtest.storage"); }
            if (string.IsNullOrEmpty(_key)) ShowKey();
            else yield return Validate(_key, false);
        }

        private void ShowKey()
        {
            ++_generation; _body.Clear();
            _body.Add(new Label(Loc.Get("closedtest.enter")));
            var field = new TextField { isPasswordField = true, maxLength = 100, value = _key ?? "" };
            _body.Add(field);
            var submit = new Button(() => _owner.StartCoroutine(Validate(field.value.Trim(), true))) {
                text = Loc.Get("closedtest.activate") };
            _body.Add(submit);
        }

        private IEnumerator Validate(string key, bool save)
        {
            var generation = ++_generation;
            _body.SetEnabled(false);
            _message.text = Loc.Get("closedtest.checking");
            using var request = new UnityWebRequest(ClosedTestAccess.Authority + "/api/identity/v1/validate", "POST");
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 8; request.redirectLimit = 0;
            if (key.Length == 72 && key.StartsWith("hexlive_", StringComparison.Ordinal))
            {
                request.SetRequestHeader("Authorization", "Bearer " + key);
                yield return request.SendWebRequest();
            }
            if (generation != _generation) yield break;
            _body.SetEnabled(true);
            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
            {
                var invalid = request.responseCode == 401 || key.Length != 72 || !key.StartsWith("hexlive_", StringComparison.Ordinal);
                _message.text = Loc.Get(invalid ? "closedtest.invalid" : "closedtest.unavailable");
                ShowKey(); yield break;
            }
            Identity identity = null;
            try { identity = JsonUtility.FromJson<Identity>(request.downloadHandler.text); }
            catch (Exception) { }
            if (identity == null || !Guid.TryParseExact(identity.accountId, "N", out _))
            { _message.text = Loc.Get("closedtest.unavailable"); ShowKey(); yield break; }
            try
            {
                if (save) ClosedTestAccess.SaveKey(key);
                SessionConfig.UseAccount(identity.accountId);
                ClosedTestAccess.Permissions = identity.permissions ?? Array.Empty<string>();
            }
            catch (Exception) { _message.text = Loc.Get("closedtest.storage"); ShowKey(); yield break; }
            _key = key; _message.text = ""; ShowServers();
        }

        private void ShowServers()
        {
            var generation = ++_generation; _body.Clear();
            _body.Add(new Label(Loc.Get("closedtest.choose")));
            for (var i = 0; i < ClosedTestAccess.Servers.Length; ++i)
            {
                var root = ClosedTestAccess.Servers[i];
                var name = Loc.Get("closedtest.singapore");
                var button = new Button(() => _owner.StartCoroutine(ConnectCompatible(root)));
                button.AddToClassList("server-choice"); _body.Add(button);
                _owner.StartCoroutine(Poll(root, name, button, generation));
            }
            var nyc = new Button { text = Loc.Get("closedtest.nyc") + " · " + Loc.Get("closedtest.unavailable") };
            nyc.AddToClassList("server-choice"); nyc.AddToClassList("unavailable"); nyc.SetEnabled(false); _body.Add(nyc);
            _body.Add(new Button(() => { _message.text = ""; ShowKey(); }) { text = Loc.Get("closedtest.change") });
        }

        private IEnumerator ConnectCompatible(string root)
        {
            _body.SetEnabled(false);
            using var request = UnityWebRequest.Get(root + "/api/client/v1/compatibility");
            request.timeout = 5; request.redirectLimit = 0;
            yield return request.SendWebRequest();
            var protocol = ProtocolUpdatePanel.ReadProtocol(request);
            if (protocol > 0 && protocol != HexLive.Simulation.Wire.Handshake.ProtocolVersion)
            { ++_generation; Clear(); Add(new ProtocolUpdatePanel(protocol)); yield break; }
            _body.SetEnabled(true);
            _connect(root.Replace("https://", "wss://") + "/watch", _key);
        }

        private IEnumerator Poll(string root, string name, Button button, int generation)
        {
            button.text = name + " · " + Loc.Get("closedtest.checking"); button.SetEnabled(false);
            while (generation == _generation)
            {
                using (var request = UnityWebRequest.Get(root + "/api/servers/v1/status"))
                {
                    request.timeout = 5; request.redirectLimit = 0;
                    yield return request.SendWebRequest();
                    if (generation != _generation) yield break;
                    var status = "unavailable";
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        try { status = JsonUtility.FromJson<ServerStatus>(request.downloadHandler.text)?.status ?? status; }
                        catch (Exception) { }
                    }
                    if (status != "available" && status != "busy") status = "unavailable";
                    foreach (var value in new[] { "available", "busy", "unavailable" }) button.EnableInClassList(value, value == status);
                    button.text = name + " · " + Loc.Get("closedtest." + status);
                    button.SetEnabled(status != "unavailable");
                }
                yield return new WaitForSecondsRealtime(10);
            }
        }
    }
}

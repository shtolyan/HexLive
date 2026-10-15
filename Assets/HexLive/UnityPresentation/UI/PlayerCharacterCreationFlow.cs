using System;
using System.Collections;
using System.Collections.Generic;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
// §171.20: no new NPC until explicit Submit; all ownership remains server-authoritative.
public sealed class PlayerCharacterCreationFlow : IDisposable
{
    public delegate IEnumerator Transport(string route, JObject body, Action<JObject, long> result);
    private readonly VisualElement _parent, _loading;
    private readonly MonoBehaviour _runner;
    private readonly string _origin, _token, _server, _previousServer, _previousToken;
    private readonly Action _connect, _back;
    private readonly Transport _transport;
    private PlayerCharacterCreator _editor;
    private PlayerCreatorAppearance _appearance;
    private UnityWebRequest _request;
    private Coroutine _work;
    private JObject _pending;
    private string _scope, _attemptKey, _lockKey;
    private bool _disposed, _connected, _dirty, _locked, _lockStarted;
    private static string T(string key) => Loc.Get("creator." + key);
    public PlayerCharacterCreationFlow(VisualElement parent, MonoBehaviour runner, string server, string token,
        Action connect, Action back, Transport transport = null)
    {
        _parent = parent; _runner = runner; _server = server; _token = token; _connect = connect; _back = back;
        var address = new Uri(server);
        _origin = new UriBuilder(address) { Scheme = address.Scheme == "wss" ? "https" : "http", Path = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Authority);
        _previousServer = SessionConfig.ServerUrl; _previousToken = SessionConfig.ControlToken;
        SessionConfig.UseServer(server, token);
        _transport = transport ?? Request;
        _loading = Resources.Load<VisualTreeAsset>("HexLive/UI/PlayerCharacterCreator").CloneTree();
        _loading.AddToClassList("creator-overlay"); parent.Add(_loading);
        _loading.Q<Label>("title").text = T("title"); _loading.Q<Label>("eyebrow").text = T("eyebrow");
        _loading.Q<VisualElement>("body").AddToClassList("creator-hidden");
        _loading.Q<Button>("retry").text = T("retry");
        _loading.Q<Button>("retry").AddToClassList("creator-hidden");
        _loading.Q<Button>("retry").clicked += () => Run(Boot());
        _loading.Q<Button>("back").text = T("back"); _loading.Q<Button>("back").clicked += back;
        _loading.Q<Button>("continue").text = T("signIn");
        _loading.Q<Button>("continue").clicked += () => Application.OpenURL(_origin + "/login/");
        if (string.IsNullOrEmpty(token))
        { _loading.Q<Label>("status").text = T("signInHint"); return; }
        _loading.Q<Button>("continue").SetEnabled(false);
        Run(Boot());
    }
    private IEnumerator Request(string route, JObject body, Action<JObject, long> done)
    {
        using var request = new UnityWebRequest(_origin + "/api/accounts/v1" + route, body == null ? "GET" : "POST")
        { downloadHandler = new DownloadHandlerBuffer(), timeout = 20, redirectLimit = 0 };
        _request = request; request.SetRequestHeader("Authorization", "Bearer " + _token);
        if (body != null)
        {
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
            request.SetRequestHeader("Content-Type", "application/json");
        }
        yield return request.SendWebRequest();
        if (_disposed) yield break;
        JObject value;
        try { value = JObject.Parse(request.downloadHandler.text); }
        catch (JsonException) { value = new JObject { ["error"] = "network" }; }
        _request = null;
        done(value, request.responseCode);
    }
    private void Run(IEnumerator routine)
    {
        if (_disposed || _work != null) return;
        _work = _runner.StartCoroutine(Work(routine));
    }
    private IEnumerator Work(IEnumerator routine)
    {
        yield return null;
        // Unity advances a yielded child iterator outside the parent's catch.
        // Advance nested routines here so a parsing/transport failure cannot
        // leave the loading screen and its retry guard permanently busy.
        var pending = new Stack<IEnumerator>();
        pending.Push(routine);
        Exception failure = null;
        try
        {
            while (pending.Count > 0 && !_disposed)
            {
                object step = null;
                var advanced = false;
                try
                {
                    advanced = pending.Peek().MoveNext();
                    if (advanced) step = pending.Peek().Current;
                    else (pending.Pop() as IDisposable)?.Dispose();
                }
                catch (Exception ex) { failure = ex; break; }
                if (!advanced) continue;
                if (step is IEnumerator nested) pending.Push(nested);
                else yield return step;
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                try { (pending.Pop() as IDisposable)?.Dispose(); }
                catch (Exception ex) { if (failure == null) failure = ex; }
            }
            _request = null;
            _work = null;
            if (!_disposed && failure != null)
            {
                _connected = false;
                SetState(false, T("error.network"));
                // Never log transport text, request bodies or credentials.
                Debug.LogError("Player character creation stopped: " + failure.GetType().Name);
            }
        }
        if (_connected && !_disposed) _connect();
    }
    private static bool HasLivingCharacter(JObject me) =>
        me?["character"] is JObject character && character.Value<bool>("alive") &&
        !me.Value<bool>("needsNewCharacter");
    private IEnumerator Boot()
    {
        _editor?.Dispose(); _editor = null;
        if (_loading.parent == null) _parent.Add(_loading);
        SetState(true, T("loading"));
        JObject me = null; long status = 0;
        yield return _transport("/me", null, (value, code) => { me = value; status = code; });
        if (_disposed) yield break;
        if (status != 200 || me == null) { SetState(false, Error(me, status)); yield break; }
        var player = me.Value<string>("playerId");
        if (string.IsNullOrEmpty(player)) { SetState(false, T("error.network")); yield break; }
        _scope = "hexlive.creator." + Hash128.Compute(_origin + "|" + player);
        _attemptKey = "hexlive.characterAttempt." + player;
        _lockKey = "hexlive.character." + player;
        if (HasLivingCharacter(me))
        { ClearPending(); _connected = true; yield break; }
        JObject options = null;
        yield return _transport("/character/options", null, (value, code) => { options = value; status = code; });
        if (_disposed) yield break;
        if (status != 200 || options?["bodies"] is not JArray bodies || bodies.Count == 0 ||
            options["attributes"] is not JArray || options["skills"] is not JArray || options["camps"] is not JArray)
        { SetState(false, Error(options, status)); yield break; }
        HexLive.Simulation.Bootstrap.CharacterCreationConfig draft = null;
        try
        {
            var saved = WebPage.CreatorRead(_scope);
            if (!string.IsNullOrEmpty(saved)) draft = JsonConvert.DeserializeObject<HexLive.Simulation.Bootstrap.CharacterCreationConfig>(saved);
            var frozen = WebPage.CreatorRead(_attemptKey);
            if (!string.IsNullOrEmpty(frozen)) _pending = JObject.Parse(frozen);
        }
        catch (JsonException) { draft = null; }
        if (draft?.Name == null || draft.Attributes == null || draft.Skills == null || draft.Traits == null || draft.Clothing == null) draft = null;
        _appearance = new PlayerCreatorAppearance(options, draft);
        if (draft != null)
        {
            var body = _appearance.Body;
            _appearance.SetSex(body?.Value<string>("sex") == "male");
        }
        _loading.RemoveFromHierarchy();
        _editor = new PlayerCharacterCreator(_parent, _runner, _appearance, () => _dirty = true,
            () => Run(Submit()), _back, () => { if (_pending != null) Run(Submit()); else Run(Reload()); });
        _editor.Root.schedule.Execute(() => {
            if (!_disposed && _dirty) SaveDraft();
        }).Every(500);
        SetState(false, _pending != null ? T("pending") : me.Value<bool>("needsNewCharacter") ? T("characterDied") : "");
    }
    private IEnumerator Reload()
    {
        SaveDraft(); _editor?.Dispose(); _editor = null;
        yield return Boot();
    }
    private bool SaveDraft()
    {
        if (_appearance == null || _scope == null) return true;
        if (!WebPage.CreatorWrite(_scope, JsonConvert.SerializeObject(_appearance.Character)))
        { SetState(false, T("error.storage")); return false; }
        _dirty = false; return true;
    }
    private IEnumerator Submit()
    {
        if (_appearance == null || (_pending == null && (!_appearance.CanSubmit || !_editor.PreviewReady))) yield break;
        SetState(true, T("creating"));
        var state = WebPage.CreatorAcquire(_lockKey);
        _lockStarted = state != 2;
        var deadline = Time.realtimeSinceStartup + 10;
        while (state == 0 && !_disposed && Time.realtimeSinceStartup < deadline)
        { yield return null; state = WebPage.CreatorLockState(_lockKey); }
        if (_disposed) yield break;
        if (state != 1)
        { if (_lockStarted) WebPage.CreatorRelease(_lockKey); _lockStarted = false; SetState(false, T("error.anotherTab")); yield break; }
        _locked = true;
        try
        {
            var frozen = WebPage.CreatorRead(_attemptKey);
            if (!string.IsNullOrEmpty(frozen))
            {
                try { _pending = JObject.Parse(frozen); }
                catch (JsonException) { SetState(false, T("error.storage")); yield break; }
            }
            if (_pending == null)
            {
                if (!SaveDraft()) yield break;
                _pending = _appearance.Request(Guid.NewGuid().ToString("N"));
                if (!WebPage.CreatorWrite(_attemptKey, _pending.ToString(Formatting.None)))
                { _pending = null; SetState(false, T("error.storage")); yield break; }
            }
            JObject reply = null; long status = 0;
            yield return _transport("/character", _pending, (value, code) => { reply = value; status = code; });
            if (_disposed) yield break;
            if (status == 400 || status == 409)
            {
                ClearPending();
                if (reply?.Value<string>("error") == "character_alive") { yield return Boot(); yield break; }
                SetState(false, Error(reply, status)); yield break;
            }
            if (status != 200) { SetState(false, Error(reply, status)); yield break; }
            // Creation may have committed even when its reply was lost; only a successful
            // authoritative read can switch into play. Retries keep the same immutable attempt.
            JObject me = null;
            yield return _transport("/me", null, (value, code) => { me = value; status = code; });
            if (_disposed) yield break;
            if (status != 200) { SetState(false, Error(me, status)); yield break; }
            if (HasLivingCharacter(me))
            { ClearPending(); _connected = true; }
            else { ClearPending(); SetState(false, T("characterDied")); }
        }
        finally { if (_locked) { WebPage.CreatorRelease(_lockKey); _locked = false; _lockStarted = false; } }
    }
    private void ClearPending()
    { if (_attemptKey != null) WebPage.CreatorWrite(_attemptKey, ""); _pending = null; }
    private static string Error(JObject value, long status)
    {
        var code = value?.Value<string>("error") ?? "network";
        var key = status == 401 ? "signInHint" : "error." + code;
        return Loc.Has("creator." + key) ? T(key) : T("error.network");
    }
    private void SetState(bool busy, string message)
    {
        _editor?.SetState(busy, _pending != null, message);
        _loading.Q<Label>("status").text = message;
        _loading.Q<Button>("continue").SetEnabled(!busy);
        _loading.Q<Button>("retry").EnableInClassList("creator-hidden", busy || string.IsNullOrEmpty(_token));
    }
    public void Dispose()
    {
        if (_disposed) return;
        SaveDraft(); _disposed = true; _request?.Abort();
        if (_work != null) _runner.StopCoroutine(_work);
        _request?.Dispose(); _request = null;
        if (_lockStarted) WebPage.CreatorRelease(_lockKey);
        _editor?.Dispose(); _loading.RemoveFromHierarchy();
        if (!_connected) SessionConfig.UseServer(_previousServer, _previousToken);
    }
}
}

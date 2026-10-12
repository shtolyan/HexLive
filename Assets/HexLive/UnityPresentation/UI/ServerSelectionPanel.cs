using System;
using System.Collections;
using System.Collections.Generic;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed class ServerSelectionPanel : VisualElement
    {
        private sealed class Row
        {
            public ServerDirectoryEntry Entry;
            public Button Button;
            public Label Name, Ping, Action;
            public ServerProbeResult Result = new ServerProbeResult(-1, 0);
            public bool Pending = true;
        }
        private sealed class Probe
        {
            public IEnumerator Routine;
            public Coroutine Coroutine;
        }
        private readonly MonoBehaviour _owner;
        private readonly Action<ServerDirectoryEntry> _connect;
        private readonly Func<ServerDirectoryEntry, Action<ServerProbeResult>, IEnumerator> _probe;
        private readonly string _pageSocket;
        private readonly List<Row> _rows = new();
        private readonly List<Probe> _probes = new();
        private readonly Label _title = new(), _subtitle = new(), _message = new();
        private readonly ScrollView _list = new(ScrollViewMode.Vertical);
        private readonly Button _refresh;
        private Coroutine _directoryLoad;
        private IEnumerator _directoryRoutine;
        private int _generation;
        private bool _connecting;

        public ServerSelectionPanel(MonoBehaviour owner, Action<ServerDirectoryEntry> connect,
            string pageSocket, Func<ServerDirectoryEntry, Action<ServerProbeResult>, IEnumerator> probe = null)
        {
            _owner = owner; _connect = connect; _pageSocket = pageSocket;
            _probe = probe ?? ServerLatencyProbe.Run;
            name = "serverSelection"; AddToClassList("server-selection");
            styleSheets.Add(Resources.Load<StyleSheet>("HexLive/UI/ServerSelection"));
            var card = new VisualElement(); card.AddToClassList("server-selection-card"); Add(card);
            _title.AddToClassList("server-selection-title"); card.Add(_title);
            _subtitle.AddToClassList("server-selection-subtitle"); card.Add(_subtitle);
            _list.AddToClassList("server-selection-list"); card.Add(_list);
            _message.AddToClassList("server-selection-message"); card.Add(_message);
            _refresh = new Button(Refresh); _refresh.AddToClassList("server-refresh"); card.Add(_refresh);
            RegisterCallback<AttachToPanelEvent>(_ => {
                Loc.LanguageChanged += Localize;
                Populate(ServerDirectory.Read(Resources.Load<TextAsset>(ServerDirectory.ResourcePath).text));
                if (Platform.WebPage.IsWeb)
                {
                    _directoryRoutine = LoadDirectory();
                    _directoryLoad = _owner.StartCoroutine(_directoryRoutine);
                }
            });
            RegisterCallback<DetachFromPanelEvent>(_ => {
                Loc.LanguageChanged -= Localize; StopProbes();
                if (_directoryLoad != null) _owner.StopCoroutine(_directoryLoad);
                (_directoryRoutine as IDisposable)?.Dispose();
                _directoryLoad = null; _directoryRoutine = null;
            });
            Localize();
        }

        private IEnumerator LoadDirectory()
        {
            if (!Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out var page)) yield break;
            using var request = UnityWebRequest.Get(new Uri(page, ServerDirectory.FileName).AbsoluteUri);
            request.timeout = 3; request.redirectLimit = 0;
            request.SetRequestHeader("Cache-Control", "no-cache");
            yield return request.SendWebRequest();
            if (_connecting || panel == null || request.result != UnityWebRequest.Result.Success) yield break;
            ServerDirectoryEntry[] entries = null;
            try { entries = ServerDirectory.Read(request.downloadHandler.text); }
            catch (Exception) { Debug.LogWarning("[ServerMenu] Invalid public directory; keeping bundled servers."); }
            if (entries != null && request.downloadHandler.text.Trim() != Resources.Load<TextAsset>(ServerDirectory.ResourcePath).text.Trim())
                Populate(entries);
        }

        private void Populate(ServerDirectoryEntry[] entries)
        {
            StopProbes(); _rows.Clear(); _list.Clear();
            foreach (var entry in entries)
            {
                var row = new Row { Entry = entry, Button = new Button(), Name = new Label(), Ping = new Label(), Action = new Label() };
                row.Button.name = "server-" + entry.id; row.Button.AddToClassList("server-card");
                var dot = new VisualElement(); dot.AddToClassList("server-quality-dot"); row.Button.Add(dot);
                var labels = new VisualElement(); labels.AddToClassList("server-card-labels"); row.Button.Add(labels);
                row.Name.AddToClassList("server-name"); row.Ping.AddToClassList("server-ping"); labels.Add(row.Name); labels.Add(row.Ping);
                row.Action.AddToClassList("server-action"); row.Button.Add(row.Action);
                row.Button.clicked += () => Select(row);
                _rows.Add(row); _list.Add(row.Button);
            }
            Refresh();
        }

        private void Select(Row row)
        {
            if (_connecting || row.Pending || row.Result.PingMilliseconds < 0 || !row.Button.enabledSelf) return;
            _connecting = true; StopProbes();
            foreach (var value in _rows) value.Button.SetEnabled(false);
            _refresh.SetEnabled(false); _message.text = Loc.Get("menu.servers.connecting");
            _connect(row.Entry);
        }

        private void Refresh()
        {
            if (_connecting) return;
            StopProbes(); var generation = _generation;
            _message.text = "";
            foreach (var row in _rows)
            {
                row.Pending = true; Render(row);
                var routine = _probe(row.Entry, result => {
                    if (generation != _generation || _connecting) return;
                    row.Result = result; row.Pending = false; Render(row);
                });
                _probes.Add(new Probe { Routine = routine, Coroutine = _owner.StartCoroutine(routine) });
            }
        }

        private void StopProbes()
        {
            ++_generation;
            foreach (var probe in _probes)
            {
                if (probe.Coroutine != null) _owner.StopCoroutine(probe.Coroutine);
                // StopCoroutine does not dispose C# iterators: explicitly release sockets and requests.
                (probe.Routine as IDisposable)?.Dispose();
            }
            _probes.Clear();
        }

        private void Localize()
        {
            _title.text = Loc.Get("menu.servers.title"); _subtitle.text = Loc.Get("menu.servers.subtitle");
            _refresh.text = Loc.Get("menu.servers.refresh");
            if (_connecting) _message.text = Loc.Get("menu.servers.connecting");
            foreach (var row in _rows) Render(row);
        }

        private void Render(Row row)
        {
            row.Name.text = !string.IsNullOrEmpty(row.Entry.nameTerm) && Loc.Has(row.Entry.nameTerm) ? Loc.Get(row.Entry.nameTerm) : row.Entry.name;
            var quality = row.Pending ? "pending" : ServerDirectory.PingClass(row.Result.PingMilliseconds);
            foreach (var value in new[] { "pending", "offline", "good", "fair", "poor" }) row.Button.EnableInClassList(value, value == quality);
            var incompatible = row.Entry.IsPageServer(_pageSocket) && row.Result.ProtocolVersion > 0 && row.Result.ProtocolVersion != Handshake.ProtocolVersion;
            row.Ping.text = row.Pending ? Loc.Get("menu.servers.checking") : row.Result.PingMilliseconds < 0 ? Loc.Get("closedtest.unavailable") :
                string.Format(Loc.Get("menu.servers.ping"), row.Result.PingMilliseconds);
            row.Action.text = !row.Entry.enabled ? Loc.Get("menu.servers.notready") : incompatible ? Loc.Get("menu.servers.update") : Loc.Get("menu.servers.play");
            row.Button.tooltip = Loc.Get("menu.servers.ping.description");
            row.Button.SetEnabled(row.Entry.enabled && !_connecting && !row.Pending && row.Result.PingMilliseconds >= 0 && !incompatible);
        }
    }
}

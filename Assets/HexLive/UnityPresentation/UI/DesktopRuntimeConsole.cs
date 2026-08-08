#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && (UNITY_EDITOR || UNITY_STANDALONE_OSX || UNITY_STANDALONE_WIN)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// UI Toolkit runtime console for desktop development builds. The threaded
    /// callback only queues immutable records; the main thread drains at most
    /// one hundred per frame.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    internal sealed class DesktopRuntimeConsole : MonoBehaviour, IRuntimeConsole
    {
        private const int Capacity = 2048;
        private const int MaxDrainPerFrame = 100;

        private readonly ConcurrentQueue<PendingEntry> _incoming = new();
        private readonly List<Entry> _entries = new(Capacity);

        private UIDocument _document;
        private VisualElement _window;
        private ScrollView _rows;
        private TextField _search;
        private Toggle _logs;
        private Toggle _warnings;
        private Toggle _errors;
        private Button _collapseButton;
        private Label _count;
        private TextField _stack;
        private Entry _selected;
        private bool _collapse = true;
        private bool _dirty;
        private int _queuedCount;

        public bool IsVisible => _window != null && _window.style.display == DisplayStyle.Flex;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
            if (baseSettings != null)
            {
                var settings = Instantiate(baseSettings);
                settings.name = "DesktopRuntimeConsolePanelSettings";
                settings.sortingOrder = 260;
                _document.panelSettings = settings;
            }

            Build();
            Hide();
        }

        private void OnEnable()
        {
            Application.logMessageReceivedThreaded += ReceiveThreaded;
            Loc.LanguageChanged += Relocalize;
        }

        private void OnDisable()
        {
            Application.logMessageReceivedThreaded -= ReceiveThreaded;
            Loc.LanguageChanged -= Relocalize;
        }

        private void Update()
        {
            if (Keyboard.current?.backquoteKey.wasPressedThisFrame == true)
            {
                Toggle();
            }

            var drained = 0;
            while (drained < MaxDrainPerFrame && _incoming.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _queuedCount);
                if (_entries.Count == Capacity)
                {
                    _entries.RemoveAt(0);
                }

                _entries.Add(new Entry(pending.Message, pending.StackTrace, pending.Type));
                drained++;
            }

            if (drained > 0)
            {
                _dirty = true;
            }

            if (_dirty && IsVisible)
            {
                RefreshRows();
            }
        }

        public void Show()
        {
            if (_window == null)
            {
                return;
            }

            _window.style.display = DisplayStyle.Flex;
            _dirty = true;
        }

        public void Hide()
        {
            if (_window != null)
            {
                _window.style.display = DisplayStyle.None;
            }
        }

        public void Toggle()
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
            }
        }

        private void ReceiveThreaded(string message, string stackTrace, LogType type)
        {
            _incoming.Enqueue(new PendingEntry(message ?? string.Empty, stackTrace ?? string.Empty, type));
            if (Interlocked.Increment(ref _queuedCount) > Capacity &&
                _incoming.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedCount);
            }
        }

        private void Build()
        {
            var root = _document.rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1f;
            root.pickingMode = PickingMode.Ignore;

            _window = new VisualElement();
            _window.pickingMode = PickingMode.Position;
            _window.style.position = Position.Absolute;
            _window.style.left = 60f;
            _window.style.right = 60f;
            _window.style.top = 55f;
            _window.style.bottom = 55f;
            _window.style.paddingLeft = 10f;
            _window.style.paddingRight = 10f;
            _window.style.paddingTop = 10f;
            _window.style.paddingBottom = 10f;
            _window.style.backgroundColor = new Color(0.035f, 0.043f, 0.052f, 0.98f);
            SetBorder(_window, new Color(1f, 1f, 1f, 0.18f));
            root.Add(_window);

            var header = Row();
            var title = new Label(Loc.Get("console.title"));
            title.style.fontSize = 18f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.flexGrow = 1f;
            header.Add(title);

            _count = new Label();
            _count.style.color = new Color(0.68f, 0.72f, 0.76f);
            _count.style.marginRight = 10f;
            header.Add(_count);
            header.Add(ActionButton(Loc.Get("console.copy"), CopySelection));
            header.Add(ActionButton(Loc.Get("console.clear"), Clear));
            header.Add(ActionButton(Loc.Get("console.close"), Hide));
            _window.Add(header);

            var controls = Row();
            _logs = FilterToggle(Loc.Get("console.log"), true);
            _warnings = FilterToggle(Loc.Get("console.warning"), true);
            _errors = FilterToggle(Loc.Get("console.error"), true);
            controls.Add(_logs);
            controls.Add(_warnings);
            controls.Add(_errors);

            _collapseButton = ActionButton(
                Loc.Get(_collapse ? "console.collapse.on" : "console.collapse.off"),
                ToggleCollapse);
            controls.Add(_collapseButton);

            _search = new TextField(Loc.Get("console.search"));
            _search.style.flexGrow = 1f;
            _search.style.marginLeft = 8f;
            _search.tooltip = Loc.Get("console.search.hint");
            _search.RegisterValueChangedCallback(_ => MarkDirty());
            controls.Add(_search);
            _window.Add(controls);

            var content = new TwoPaneSplitView(0, 320f, TwoPaneSplitViewOrientation.Vertical);
            content.style.flexGrow = 1f;
            content.style.marginTop = 6f;

            _rows = new ScrollView(ScrollViewMode.Vertical);
            _rows.style.flexGrow = 1f;
            content.Add(_rows);

            _stack = new TextField(Loc.Get("console.stack"));
            _stack.multiline = true;
            _stack.isReadOnly = true;
            _stack.style.height = 145f;
            _stack.style.whiteSpace = WhiteSpace.Normal;
            content.Add(_stack);
            _window.Add(content);
        }

        private Toggle FilterToggle(string label, bool value)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.style.color = Color.white;
            toggle.style.marginRight = 8f;
            toggle.RegisterValueChangedCallback(_ => MarkDirty());
            return toggle;
        }

        private void RefreshRows()
        {
            _dirty = false;
            _rows.Clear();

            var search = _search.value?.Trim() ?? string.Empty;
            var groups = _collapse ? new Dictionary<EntryKey, Group>() : null;
            var visible = 0;

            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (!Passes(entry, search))
                {
                    continue;
                }

                visible++;
                if (groups == null)
                {
                    AddRow(entry, 1);
                    continue;
                }

                var key = new EntryKey(entry.Message, entry.StackTrace, entry.Type);
                if (groups.TryGetValue(key, out var group))
                {
                    group.Count++;
                    groups[key] = group;
                }
                else
                {
                    groups.Add(key, new Group(entry, 1));
                }
            }

            if (groups != null)
            {
                foreach (var group in groups.Values)
                {
                    AddRow(group.Entry, group.Count);
                }
            }

            _count.text = $"{visible}/{_entries.Count} · queue {Volatile.Read(ref _queuedCount)}";
        }

        private bool Passes(Entry entry, string search)
        {
            var typePasses = entry.Type switch
            {
                LogType.Warning => _warnings.value,
                LogType.Error or LogType.Exception or LogType.Assert => _errors.value,
                _ => _logs.value
            };

            if (!typePasses)
            {
                return false;
            }

            return search.Length == 0 ||
                   entry.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.StackTrace.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddRow(Entry entry, int repetitions)
        {
            var row = new Button(() => Select(entry));
            row.text = repetitions > 1 ? $"×{repetitions}  {entry.Message}" : entry.Message;
            row.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.style.whiteSpace = WhiteSpace.Normal;
            row.style.marginBottom = 2f;
            row.style.color = entry.Type switch
            {
                LogType.Warning => new Color(1f, 0.78f, 0.30f),
                LogType.Error or LogType.Exception or LogType.Assert => new Color(1f, 0.40f, 0.38f),
                _ => new Color(0.84f, 0.87f, 0.90f)
            };
            _rows.Add(row);
        }

        private void Select(Entry entry)
        {
            _selected = entry;
            _stack.value = string.IsNullOrWhiteSpace(entry.StackTrace)
                ? entry.Message
                : entry.Message + "\n\n" + entry.StackTrace;
        }

        private void CopySelection()
        {
            if (_selected != null)
            {
                GUIUtility.systemCopyBuffer = string.IsNullOrWhiteSpace(_selected.StackTrace)
                    ? _selected.Message
                    : _selected.Message + "\n\n" + _selected.StackTrace;
            }
        }

        private void Clear()
        {
            _entries.Clear();
            while (_incoming.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedCount);
            }

            _selected = null;
            _stack.value = string.Empty;
            MarkDirty();
        }

        private void ToggleCollapse()
        {
            _collapse = !_collapse;
            _collapseButton.text = _collapse
                ? Loc.Get("console.collapse.on")
                : Loc.Get("console.collapse.off");
            MarkDirty();
        }

        private void Relocalize()
        {
            var wasVisible = IsVisible;
            Build();
            if (!wasVisible)
            {
                Hide();
            }
            else
            {
                _dirty = true;
            }
        }

        private void MarkDirty()
        {
            _dirty = true;
            if (IsVisible)
            {
                RefreshRows();
            }
        }

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        private static Button ActionButton(string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.style.marginLeft = 4f;
            return button;
        }

        private static void SetBorder(VisualElement element, Color color)
        {
            element.style.borderTopWidth = 1f;
            element.style.borderRightWidth = 1f;
            element.style.borderBottomWidth = 1f;
            element.style.borderLeftWidth = 1f;
            element.style.borderTopColor = color;
            element.style.borderRightColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
        }

        private readonly struct PendingEntry
        {
            public readonly string Message;
            public readonly string StackTrace;
            public readonly LogType Type;

            public PendingEntry(string message, string stackTrace, LogType type)
            {
                Message = message;
                StackTrace = stackTrace;
                Type = type;
            }
        }

        private sealed class Entry
        {
            public readonly string Message;
            public readonly string StackTrace;
            public readonly LogType Type;

            public Entry(string message, string stackTrace, LogType type)
            {
                Message = message;
                StackTrace = stackTrace;
                Type = type;
            }
        }

        private readonly struct EntryKey : IEquatable<EntryKey>
        {
            private readonly string _message;
            private readonly string _stackTrace;
            private readonly LogType _type;

            public EntryKey(string message, string stackTrace, LogType type)
            {
                _message = message;
                _stackTrace = stackTrace;
                _type = type;
            }

            public bool Equals(EntryKey other) =>
                _type == other._type && _message == other._message && _stackTrace == other._stackTrace;

            public override bool Equals(object obj) => obj is EntryKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_message, _stackTrace, (int)_type);
        }

        private struct Group
        {
            public Entry Entry;
            public int Count;

            public Group(Entry entry, int count)
            {
                Entry = entry;
                Count = count;
            }
        }
    }
}
#endif

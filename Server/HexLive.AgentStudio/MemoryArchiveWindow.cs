using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HexLive.AgentHost;
using System.Text.Json;

namespace HexLive.AgentStudio;

/// <summary>§165: the same evidence reader as the agent, without starting a model.</summary>
public sealed class MemoryArchiveWindow : Window
{
    private readonly AgentMemorySearch _search;
    private readonly StudioStrings _strings;
    private readonly string _root;
    private readonly TextBox _query = new();
    private readonly TextBox _episode = new();
    private readonly TextBox _person = new();
    private readonly TextBox _from = new();
    private readonly TextBox _until = new();
    private readonly TextBox _gameDay = new();
    private readonly ComboBox _kind = new() { SelectedIndex = 0 };
    private readonly ComboBox _order = new() { SelectedIndex = 0 };
    private readonly ListBox _results = new();
    private readonly TextBox _source = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _next = new();
    private readonly Button _readNext = new();
    private readonly Button _find = new();
    private readonly CancellationTokenSource _closed = new();
    private int? _offset;
    private int? _readOffset;
    private string? _selected;
    private MashaArchive _state = new();
    private static readonly string[] Kinds = ["", "conversation", "event", "action", "diary", "note", "import", "gap"];
    public MemoryArchiveWindow(string root, StudioStrings strings)
    {
        _root = root; _strings = strings; _search = new(root);
        Title = strings["MemoryArchive"]; Width = 1000; Height = 700; MinWidth = 760; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _query.PlaceholderText = strings["MemoryQuery"]; _episode.PlaceholderText = strings["MemoryEpisode"];
        _person.PlaceholderText = strings["MemoryPerson"]; _from.PlaceholderText = strings["MemoryFrom"]; _until.PlaceholderText = strings["MemoryUntil"];
        _gameDay.PlaceholderText = strings["MemoryGameDayFilter"];
        _kind.ItemsSource = Kinds.Select(k => strings["MemoryKind_" + k]).ToArray();
        _order.ItemsSource = new[] { strings["MemoryRelevance"], strings["MemoryOldest"], strings["MemoryNewest"] };
        _find.Content = strings["MemoryFind"]; _next.Content = strings["MemoryNext"];
        _readNext.Content = strings["MemoryReadNext"]; _readNext.IsEnabled = false;
        var trace = new Button { Content = strings["MemoryLastSearch"] };
        var top = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        top.Children.Add(_query); Grid.SetColumn(_find, 1); top.Children.Add(_find); Grid.SetColumn(trace, 2); top.Children.Add(trace);
        var filters = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 8 };
        foreach (var (control, index) in new Control[] { _kind, _order, _person, _episode }.Select((c, i) => (c, i)))
        { Grid.SetColumn(control, index); filters.Children.Add(control); }
        var dates = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8 };
        dates.Children.Add(_from); Grid.SetColumn(_until, 1); dates.Children.Add(_until); Grid.SetColumn(_gameDay, 2); dates.Children.Add(_gameDay);
        var split = new Grid { ColumnDefinitions = new("2*,3*"), ColumnSpacing = 12 };
        split.Children.Add(_results); Grid.SetColumn(_source, 1); split.Children.Add(_source);
        _results.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MemoryHit>((hit, _) => new TextBlock {
            Text = hit == null ? "" : Describe(hit.Record, false), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8) });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _next, _readNext } };
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 10 };
        foreach (var (control, index) in new Control[] { top, filters, dates, split, _status, footer }.Select((c, i) => (c, i)))
        { Grid.SetRow(control, index); layout.Children.Add(control); }
        Content = layout;
        _find.Click += async (_, _) => await Find(0);
        _query.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await Find(0); } };
        _next.Click += async (_, _) => { if (_offset is int n) await Find(n); };
        _results.SelectionChanged += (_, _) => {
            if (_results.SelectedItem is MemoryHit hit) { _selected = hit.Record.Id; Read(0); }
        };
        _readNext.Click += (_, _) => { if (_readOffset is int n) Read(n); };
        trace.Click += (_, _) => ShowTrace();
        Opened += async (_, _) => await Find(0);
        Closed += (_, _) => _closed.Cancel();
    }
    private bool Allowed(AgentMemoryRecord r) => !MemoryDocumentEdits.IsSuppressedInContext(_state,
        r.Speaker == "legacy" ? _state.PrimarySpeakerKey ?? "" : r.Speaker, r.Text);
    private async Task Find(int offset)
    {
        if (!_find.IsEnabled) return;
        _find.IsEnabled = false; _next.IsEnabled = false; _status.Text = _strings["MemoryIndexing"];
        try
        {
            DateTimeOffset? Date(string? text) => string.IsNullOrWhiteSpace(text) ? null : DateTimeOffset.Parse(text);
            var filter = new MemoryFilter(Kinds[Math.Max(0, _kind.SelectedIndex)], _episode.Text, _person.Text, Date(_from.Text), Date(_until.Text), string.IsNullOrWhiteSpace(_gameDay.Text) ? null : long.Parse(_gameDay.Text));
            var query = _query.Text ?? "";
            var order = _order.SelectedIndex switch { 1 => "oldest", 2 => "newest", _ => "relevance" };
            var page = await Task.Run(() => {
                var path = new AgentMemoryArchive(_root).SafePath(".state/state.json");
                if (File.Exists(path)) _state = JsonSerializer.Deserialize<MashaArchive>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
                var archive = new AgentMemoryArchive(_root);
                if (!File.Exists(archive.SafePath(".state/archive-migration-v1.json")))
                {
                    using var lease = new FileStream(archive.SafePath(".agent-studio.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    archive.Migrate(_state);
                }
                // Preview pending author exclusions without consuming the runtime's edit journal.
                MemoryDocumentEdits.ApplyPending(_root, _state);
                _search.Refresh(); return _search.Search(query, filter, order, offset, Allowed);
            }, _closed.Token);
            if (_closed.IsCancellationRequested) return;
            _results.ItemsSource = page.Hits; _offset = page.NextOffset; _next.IsEnabled = _offset != null;
            _status.Text = string.Format(_strings["MemoryFound"], page.Total) + " " + _strings["MemoryCoverage"];
        }
        catch (OperationCanceledException) { }
        catch (FormatException) { _status.Text = _strings["MemoryInvalidDate"]; }
        catch { _status.Text = _strings["ActivityReadError"]; }
        finally { _find.IsEnabled = true; }
    }
    private void Read(int offset)
    {
        if (_selected == null) return;
        var result = _search.Read(_selected, offset, Allowed);
        _readOffset = result.NextOffset; _readNext.IsEnabled = _readOffset != null;
        // Evidence is JSON internally; display readable source cards whenever a complete page fits.
        try
        {
            var cards = result.Text.Split("\n\n").Select(t => JsonSerializer.Deserialize<AgentMemoryRecord>(t, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            _source.Text = string.Join("\n\n", cards.Select(r => Describe(r, true)));
        }
        catch { _source.Text = result.Text; }
    }
    private string Describe(AgentMemoryRecord r, bool full)
    {
        var date = r.OccurredUtc?.ToLocalTime().ToString("g") ?? _strings["MemoryUnknownDate"];
        var game = r.Tick.HasValue && r.DayLengthTicks > 0 ? " · " + string.Format(_strings["MemoryGameDay"], r.Tick / r.DayLengthTicks) : "";
        var text = full || r.Text.Length < 220 ? r.Text : r.Text[..220] + "…";
        return date + game + "\n" + r.Source + " · " + r.Episode +
            (r.Incomplete ? "\n" + _strings["MemoryIncomplete"] : "") + "\n" + text;
    }
    private void ShowTrace()
    {
        try
        {
            var path = new AgentMemoryArchive(_root).SafePath(".state/last-memory-search.json");
            if (!File.Exists(path)) { _source.Text = _strings["NoSavedActivity"]; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            _source.Text = root.GetProperty("query").GetString() + "\n\n" +
                string.Format(_strings["MemoryTraceCounts"], root.GetProperty("trace").GetArrayLength(), root.GetProperty("readCharacters").GetInt32(), root.GetProperty("contextCharacters").GetInt32()) + "\n\n" +
                string.Join("\n\n", root.GetProperty("sentSourceIds").EnumerateArray().SelectMany(x => _search.Read(x.GetString()!, allowed: Allowed).Text.Split("\n\n")).Distinct().Select(t => Describe(JsonSerializer.Deserialize<AgentMemoryRecord>(t, new JsonSerializerOptions(JsonSerializerDefaults.Web))!, true)));
        }
        catch { _source.Text = _strings["ActivityReadError"]; }
    }
}

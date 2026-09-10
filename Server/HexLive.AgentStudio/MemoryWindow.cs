using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed class MemoryWindow : Window
{
    private readonly WorkspaceDocuments _documents;
    private readonly string _root;
    private readonly StudioStrings _strings;
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _status = new();
    private WorkspaceDocument? _original;
    public MemoryWindow(string root, StudioStrings strings, string? initialDocument = null)
    {
        _root = root; _strings = strings; _documents = new(root);
        Title = strings["Consciousness"]; Width = 900; Height = 650; MinWidth = 700; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var open = new Button { Content = "…", Width = 42 };
        ToolTip.SetTip(open, strings["ChooseFile"]);
        var save = new Button { Content = strings["Save"] };
        var cancel = new Button { Content = strings["Cancel"] };
        var history = new Button { Content = strings["History"] };
        var activity = new Button { Content = strings["Activity"] };
        var relationships = new Button { Content = strings["Relationships"] };
        var archive = new Button { Content = strings["MemoryArchive"] };
        archive.Click += async (_, _) => await new MemoryArchiveWindow(root, strings).ShowDialog(this);
        relationships.Click += async (_, _) =>
        {
            var view = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            try
            {
                var entries = await WorkspaceRelationships.ReadAsync(root);
                view.Text = entries.Count == 0 ? strings["NoRelationships"] : string.Join("\n\n", entries.Select(e =>
                    e.VoiceName + "\n" + string.Format(strings["RelationshipMetrics"], e.Familiarity, e.Trust, e.Sympathy) +
                    "\n" + e.Reason + "\n" + e.SpeakerKey));
            }
            catch { view.Text = strings["ActivityReadError"]; }
            await new Window { Title = strings["Relationships"], Width = 640, Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Grid { Margin = new Thickness(24), Children = { view } } }.ShowDialog(this);
        };
        activity.Click += async (_, _) =>
        {
            var view = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            try
            {
                var entries = await WorkspaceActivity.ReadAsync(root);
                view.Text = entries.Count == 0 ? strings["NoSavedActivity"] : string.Join("\n\n", entries.Select(e =>
                    e.Timestamp.ToLocalTime().ToString("g") + " · " + e.World + "\n" + e.Text));
            }
            catch { view.Text = strings["ActivityReadError"]; }
            await new Window { Title = strings["Activity"], Width = 720, Height = 550,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Grid { Margin = new Thickness(24), Children = { view } } }.ShowDialog(this);
        };
        history.Click += async (_, _) =>
        {
            if (_original == null) return;
            if (_editor.Text != _original.Text) { _status.Text = strings["UnsavedChanges"]; return; }
            try
            {
                var list = new ListBox { ItemsSource = _documents.History(_original.RelativePath) };
                var restore = new Button { Content = strings["LoadRevision"] };
                var dialog = new Window { Title = strings["History"], Width = 420, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                restore.Click += (_, _) => { if (list.SelectedItem is WorkspaceRevision revision) dialog.Close(revision); };
                dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children = { list, restore } };
                var selected = await dialog.ShowDialog<WorkspaceRevision?>(this);
                if (selected != null) _editor.Text = await _documents.ReadRevisionAsync(_original.RelativePath, selected.Id);
            }
            catch { _status.Text = strings["Error"]; }
        };
        open.Click += async (_, _) => await OpenDocument();
        save.Click += async (_, _) =>
        {
            if (_original == null) return;
            try { _original = await _documents.SaveAsync(_original, _editor.Text ?? ""); _status.Text = strings["Saved"]; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { _status.Text = strings["Error"]; }
        };
        cancel.Click += (_, _) => _editor.Text = _original?.Text ?? "";
        save.Classes.Add("primary");
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12), Children = { open, history, activity, relationships, archive } };
        foreach (var control in toolbar.Children) control.Margin = new Thickness(0, 0, 10, 8);
        layout.Children.Add(toolbar);
        Grid.SetRow(_editor, 1); layout.Children.Add(_editor);
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 10); Grid.SetRow(_status, 2); layout.Children.Add(_status);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } };
        Grid.SetRow(footer, 3); layout.Children.Add(footer);
        var browser = new ListBox { ItemsSource = _documents.ListDocuments(), Margin = new Thickness(8, 20) };
        browser.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((path, _) =>
        {
            var label = new TextBlock { Text = path, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(label, path); return label;
        });
        browser.SelectionChanged += async (_, _) =>
        {
            if (browser.SelectedItem is not string path || path == _original?.RelativePath) return;
            if (_original != null && _editor.Text != _original.Text)
            { _status.Text = strings["UnsavedChanges"]; browser.SelectedItem = _original.RelativePath; return; }
            await LoadDocument(path);
        };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        var sidebar = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = browser };
        sidebar.Bind(Border.BackgroundProperty, this.GetResourceObservable("StudioSidebar"));
        sidebar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("StudioBorder"));
        columns.Children.Add(sidebar); Grid.SetColumn(layout, 1); columns.Children.Add(layout); Content = columns;
        if (initialDocument != null) Opened += (_, _) => browser.SelectedItem = initialDocument;
        Closing += (_, e) =>
        {
            if (_original != null && _editor.Text != _original.Text)
            {
                e.Cancel = true;
                _status.Text = strings["UnsavedChanges"];
            }
        };
    }
    private async Task OpenDocument()
    {
        if (_original != null && _editor.Text != _original.Text)
        {
            _status.Text = _strings["UnsavedChanges"];
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _strings["ChooseFile"], AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_root),
            FileTypeFilter = new[] { new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        await LoadDocument(Path.GetRelativePath(_root, path));
    }
    private async Task LoadDocument(string relativePath)
    {
        try
        {
            _original = await _documents.ReadAsync(relativePath);
            _editor.Text = _original.Text; _status.Text = _original.RelativePath;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _status.Text = _strings["Error"]; }
    }
}

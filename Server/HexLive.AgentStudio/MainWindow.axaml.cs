using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Platform.Storage;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;
    public StudioStrings Strings { get; } = new();
    public ObservableCollection<AgentProfile> Profiles { get; } = new();
    private readonly string _configurationRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HexLive", "AgentStudio");
    private StudioConfigurationStore? _configurationStore;
    private StudioConfigurationSnapshot? _configuration;
    private readonly ISecretStore _secrets = new OperatingSystemSecretStore();
    private string _configurationStatus = "";
    public string ConfigurationStatus => _configurationStatus;
    private string _selectedName = "Agent Studio";
    public string SelectedName => _selectedName;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
        InitializeAgentControls();
        Opened += async (_, _) =>
        {
            try
            {
                _configurationStore = new(_configurationRoot);
                _configuration = await _configurationStore.ReadAsync();
                foreach (var profile in _configuration.Configuration.Agents) Profiles.Add(profile);
                foreach (var server in _configuration.Configuration.Servers) Servers.Add(server);
                if (Profiles.Count > 0) this.FindControl<ListBox>("ProfilesList")!.SelectedItem = Profiles[0];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            { SetConfigurationStatus(Strings["ConfigurationError"]); }
        };
    }
    private void SelectProfile(object? sender, SelectionChangedEventArgs args)
    {
        _selectedName = ((sender as ListBox)?.SelectedItem as AgentProfile)?.Name ?? "Agent Studio";
        PropertyChanged?.Invoke(this, new(nameof(SelectedName)));
        RefreshSelection();
    }
    private async void NewProfile(object? sender, RoutedEventArgs args)
    {
        if (_configuration == null || _configurationStore == null) { SetConfigurationStatus(Strings["ConfigurationError"]); return; }
        var id = Guid.NewGuid();
        var workspace = Path.Combine(_configurationRoot, "workspaces", id.ToString("N"));
        var name = new TextBox { PlaceholderText = Strings["ProfileName"], MaxLength = 48 };
        var path = new TextBlock { Text = workspace, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var existing = new Button { Content = Strings["UseExistingMemory"] };
        var dialog = new Window { Title = Strings["NewAgent"], Width = 520, Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        existing.Click += async (_, _) =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings["OpenFolder"], AllowMultiple = false });
            var selected = folders.FirstOrDefault()?.TryGetLocalPath();
            if (selected != null) { workspace = selected; path.Text = workspace; }
        };
        var save = new Button { Content = Strings["Save"] };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.Close(name.Text.Trim()); };
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children = { name, existing, path, save } };
        var result = await dialog.ShowDialog<string?>(this);
        if (result == null) return;
        var profile = new AgentProfile(id, result, workspace, Guid.Empty, "", 0, new(ModelProviderKind.Codex, "codex", ""));
        try
        {
            _configuration = await _configurationStore.SaveAsync(_configuration,
                _configuration.Configuration with { Agents = _configuration.Configuration.Agents.Append(profile).ToArray() });
            Profiles.Add(profile); this.FindControl<ListBox>("ProfilesList")!.SelectedItem = profile;
            SetConfigurationStatus(Strings["ProfileSaved"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { SetConfigurationStatus(Strings["ConfigurationError"]); }
    }
    private void SetConfigurationStatus(string status)
    {
        _configurationStatus = status;
        PropertyChanged?.Invoke(this, new(nameof(ConfigurationStatus)));
    }
    private async void EditProfileSettings(object? sender, RoutedEventArgs args)
    {
        if (_configuration == null || _configurationStore == null ||
            this.FindControl<ListBox>("ProfilesList")!.SelectedItem is not AgentProfile profile) return;
        if (await IsAgentActive(profile.Id)) { SetConfigurationStatus(Strings["StopBeforeEdit"]); return; }
        var codex = OperatingSystem.IsMacOS() ? "/Applications/ChatGPT.app/Contents/Resources/codex" : "codex.exe";
        var updated = await new ProfileSettingsWindow(profile, Strings, _secrets, codex).ShowDialog<AgentProfile?>(this);
        if (updated == null) return;
        try
        {
            _configuration = await _configurationStore.SaveAsync(_configuration,
                _configuration.Configuration with { Agents = _configuration.Configuration.Agents.Select(x => x.Id == updated.Id ? updated : x).ToArray() });
            var index = Profiles.IndexOf(profile); Profiles[index] = updated;
            this.FindControl<ListBox>("ProfilesList")!.SelectedItem = updated;
            SetConfigurationStatus(Strings["ProfileSaved"]);
        }
        catch { SetConfigurationStatus(Strings["ConfigurationError"]); }
    }
    private void ToggleTheme(object? sender, RoutedEventArgs args) => Application.Current!.RequestedThemeVariant =
        Application.Current.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    private void ToggleLanguage(object? sender, RoutedEventArgs args)
    {
        Strings.SetLanguage(Strings.Language == "ru" ? "en" : "ru");
        PropertyChanged?.Invoke(this, new(nameof(Strings)));
    }
    private async void ShowServers(object? sender, RoutedEventArgs args)
    {
        if (_configuration == null || _configurationStore == null) return;
        var server = await new ServerPairingWindow(Strings, _secrets).ShowDialog<ServerProfile?>(this);
        if (server == null) return;
        try
        {
            _configuration = await _configurationStore.SaveAsync(_configuration,
                _configuration.Configuration with { Servers = _configuration.Configuration.Servers.Append(server).ToArray() });
            Servers.Add(server);
            _serverSelection.SelectedItem = server;
        }
        catch { SetConfigurationStatus(Strings["ConfigurationError"]); }
    }
    private async void ShowIntegrations(object? sender, RoutedEventArgs args)
    {
        await new IntegrationsWindow(Strings, _secrets).ShowDialog(this);
    }
    private async Task Notice(string title, string text)
    {
        var window = new Window { Title = title, Width = 480, Height = 220, Content =
            new TextBlock { Margin = new Thickness(28), Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } };
        await window.ShowDialog(this);
    }
    private async void OpenMemory(object? sender, RoutedEventArgs args)
    {
        if (SelectedProfile is { } profile)
        {
            try
            {
                if (!Directory.Exists(profile.Workspace) || !File.Exists(Path.Combine(profile.Workspace, "SOUL.md")))
                {
                    Directory.CreateDirectory(profile.Workspace);
                    using var lease = new FileStream(Path.Combine(profile.Workspace, ".agent-studio.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _ = new HexLive.AgentHost.MashaMemoryStore(profile.Workspace, new HexLive.AgentHost.MashaIdentity
                        { Id = profile.Id.ToString("N"), Name = profile.Name, Age = 23, Traits = [] });
                }
                await new MemoryWindow(profile.Workspace, Strings).ShowDialog(this);
            }
            catch { SetConfigurationStatus(Strings["ConfigurationError"]); }
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = Strings["OpenFolder"], AllowMultiple = false });
        var root = folders.FirstOrDefault()?.TryGetLocalPath();
        if (root != null) await new MemoryWindow(root, Strings).ShowDialog(this);
    }
}

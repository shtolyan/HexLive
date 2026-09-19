using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed partial class MainWindow
{
    public ObservableCollection<ServerProfile> Servers { get; } = new();
    private AgentFleet _fleet = null!;
    private ServerRoster? _roster;
    private Guid _rosterServer;
    private readonly ComboBox _serverSelection = new();
    private readonly ComboBox _characterSelection = new();
    private bool _operation, _polling, _closing, _allowClose, _updatingProfile, _refreshingCharacters;
    private bool _connecting;
    private CancellationTokenSource? _connectionCancellation;
    public bool IsConnecting => _connecting;
    public bool CanConnect => !_connecting;
    public string ConnectButtonText => Strings[_connecting ? "ConnectingNow" : "Connect"];
    private void SetConnecting(bool value)
    {
        _connecting = value;
        foreach (var property in new[] { nameof(IsConnecting), nameof(CanConnect), nameof(ConnectButtonText) })
            PropertyChanged?.Invoke(this, new(property));
    }
    private string ConnectedStatus() => string.Format(Strings["ConnectedCharacters"],
        _roster?.Characters.Count(c => c.Available) ?? 0) +
        (_roster?.Paused == true ? " " + Strings["StateWorldPaused"] : "");
    private string _selectionSummary = "", _activityStatus = "";
    public string SelectionSummary => _selectionSummary;
    public string ModelSummary => SelectedProfile is { } p ? $"{p.Model.Provider} · {p.Model.ModelId}" : Strings["NoModel"];
    public string ReasoningSummary => SelectedProfile?.Model.Reasoning ?? Strings["NoReasoning"];
    public string VoiceSummary => SelectedProfile?.Voice is { } v ? $"ElevenLabs · {v.VoiceId}" : Strings["NoVoice"];
    public string IntervalSummary => SelectedProfile?.HeartbeatSeconds.ToString() ?? "—";
    public string ActivityStatus => _activityStatus;
    private AgentProfile? SelectedProfile => this.FindControl<ListBox>("ProfilesList")!.SelectedItem as AgentProfile;

    private void InitializeAgentControls()
    {
        _serverSelection.ItemsSource = Servers;
        _characterSelection.SelectionChanged += ChooseCharacter;
        var codex = OperatingSystem.IsMacOS() ? "/Applications/ChatGPT.app/Contents/Resources/codex" : "codex.exe";
        var factory = new AgentSessionFactory(_secrets, codex);
        _fleet = new((profile, server, token) => { OfficialGameAccess.RequireOfficial(server); return factory.ConnectAsync(profile, server, token); });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += async (_, _) =>
        {
            if (_polling) return;
            _polling = true;
            try
            {
                var states = await _fleet.SnapshotAsync();
                var selectedStates = states.Where(s => s.ProfileId == SelectedProfile?.Id).ToArray();
                _activityStatus = selectedStates.Length == 0 ? Strings["StoppedNoRequests"] : string.Join(Environment.NewLine, selectedStates.Select(s =>
                    (Profiles.FirstOrDefault(p => p.Id == s.ProfileId)?.Name ?? s.ProfileId.ToString()) + ": " +
                    Strings["State" + s.State] + (s.ErrorCode == null ? "" : " · " + s.ErrorCode) +
                    (s.DiagnosticsErrorCode.Length == 0 ? "" : " · " + Strings["DiagnosticsUnavailable"]) +
                    (string.IsNullOrEmpty(s.IntentSummary) ? "" : Environment.NewLine + s.IntentSummary)));
                PropertyChanged?.Invoke(this, new(nameof(ActivityStatus)));
            }
            finally { _polling = false; }
        };
        timer.Start(); Closed += (_, _) => timer.Stop();
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            try
            {
                var dialog = new CloseConfirmationWindow(Strings);
                if (!await dialog.ShowDialog<bool>(this)) return;
                await _fleet.DisposeAsync(); _allowClose = true; Close();
            }
            finally { _closing = false; }
        };
    }
    private async Task<bool> IsAgentActive(Guid id) => (await _fleet.SnapshotAsync()).Any(x => x.ProfileId == id && x.State is not (AgentRunState.Stopped or AgentRunState.Error));
    private void RefreshSelection()
    {
        var profile = SelectedProfile;
        _selectionSummary = profile == null ? "" : $"{profile.Model.Provider} · {profile.Model.ModelId} · {profile.Model.Reasoning}\n" +
            (profile.Voice == null ? Strings["NoVoice"] : $"ElevenLabs · {profile.Voice.VoiceId}") + $"\nNPC{profile.NpcId} · {profile.WorldId}";
        PropertyChanged?.Invoke(this, new(nameof(SelectionSummary)));
        foreach (var property in new[] { nameof(ModelSummary), nameof(ReasoningSummary), nameof(VoiceSummary), nameof(IntervalSummary) })
            PropertyChanged?.Invoke(this, new(property));
        _serverSelection.SelectedItem = Servers.FirstOrDefault(x => x.Id == profile?.ServerId);
        RefreshCharacters();
    }
    private void RefreshCharacters()
    {
        _refreshingCharacters = true;
        try
        {
            var profile = SelectedProfile;
            if (profile == null || _rosterServer != profile.ServerId || _roster?.WorldId != profile.WorldId)
            { _roster = null; _characterSelection.ItemsSource = null; return; }
            var characters = _roster.Characters.Where(c => c.Available).ToArray();
            _characterSelection.ItemsSource = characters;
            _characterSelection.SelectedItem = characters.FirstOrDefault(c => c.NpcId == profile.NpcId);
            _characterSelection.PlaceholderText = Strings[characters.Length == 0 ? "NoAvailableCharacters" : "ChooseCharacter"];
        }
        finally { _refreshingCharacters = false; }
    }
    private async Task SaveProfile(AgentProfile profile)
    {
        if (_configuration == null || _configurationStore == null) throw new InvalidOperationException();
        _configuration = await _configurationStore.SaveAsync(_configuration,
            _configuration.Configuration with { Agents = _configuration.Configuration.Agents.Select(x => x.Id == profile.Id ? profile : x).ToArray() });
        var index = Profiles.ToList().FindIndex(x => x.Id == profile.Id);
        var selected = SelectedProfile?.Id == profile.Id;
        // Replacing a selected record raises a transient null selection. It must not
        // discard the roster while persisting the character chosen from that roster.
        _updatingProfile = true;
        try
        {
            Profiles[index] = profile;
            if (selected) this.FindControl<ListBox>("ProfilesList")!.SelectedItem = profile;
        }
        finally { _updatingProfile = false; }
        if (selected) RefreshSelection();
    }
    private async void ConnectServer(object? sender, RoutedEventArgs args)
    {
        if (_operation) return;
        if (SelectedProfile is not { } profile) { SetConfigurationStatus(Strings["SelectProfileFirst"]); return; }
        if (_serverSelection.SelectedItem is not ServerProfile server) { SetConfigurationStatus(Strings["NoServer"]); return; }
        _operation = true;
        SetConnecting(true);
        SetConfigurationStatus(Strings["ReadingServerCredential"]);
        using var cancellation = new CancellationTokenSource();
        _connectionCancellation = cancellation;
        try
        {
            if (await IsAgentActive(profile.Id)) { SetConfigurationStatus(Strings["StopBeforeEdit"]); return; }
            await _secrets.RetryFailedReadsAsync(cancellation.Token);
            var credential = await _secrets.ReadAsync(server.CredentialId, cancellation.Token).WaitAsync(cancellation.Token);
            if (credential == null)
            {
                SetConfigurationStatus(Strings["ServerNeedsAccess"]);
                cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                var owner = (sender as Control) is { } control ? TopLevel.GetTopLevel(control) as Window : null;
                var approved = await new ServerPairingWindow(Strings, _secrets, server)
                    .ShowDialog<ServerProfile?>(owner ?? this);
                if (approved == null) { SetConfigurationStatus(Strings["ConnectionCancelled"]); return; }
                await SaveServer(approved);
                server = approved;
                SetConfigurationStatus(Strings["ReadingServerCredential"]);
                credential = await _secrets.ReadAsync(server.CredentialId, cancellation.Token).WaitAsync(cancellation.Token)
                    ?? throw new InvalidOperationException("MissingServerCredential");
            }
            cancellation.CancelAfter(TimeSpan.FromSeconds(35));
            SetConfigurationStatus(Strings["CheckingServer"]);
            var roster = await new AgentServerConnection(_secrets).ReadWithCredentialAsync(server, credential, cancellation.Token);
            if (SelectedProfile?.Id != profile.Id) return;
            await SaveProfile(profile with { ServerId = server.Id, WorldId = roster.WorldId,
                NpcId = profile.ServerId == server.Id && profile.WorldId == roster.WorldId ? profile.NpcId : 0 });
            _roster = roster; _rosterServer = server.Id;
            RefreshCharacters();
            SetConfigurationStatus(ConnectedStatus() + "\n" + (_characterSelection.SelectedItem is AvailableCharacter current
                ? string.Format(Strings["CharacterSelected"], current)
                : Strings[roster.Characters.Any(c => c.Available) ? "SelectCharacter" : "NoAvailableCharacters"]));
        }
        catch (InvalidDataException ex) when (ex.Message == "ServerApiIncompatible")
        { SetConfigurationStatus(Strings["ServerApiIncompatible"]); }
        catch (OperationCanceledException) { SetConfigurationStatus(Strings["ConnectionTimeout"]); }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        { SetConfigurationStatus(Strings["ConnectionDenied"]); }
        catch (CredentialStoreException ex)
        { SetConfigurationStatus(Strings[ex.NativeStatus == -25293 ? "KeychainAuthenticationFailed" : "CredentialAccessDenied"]); }
        catch { SetConfigurationStatus(Strings["ConnectionFailed"]); }
        finally { _connectionCancellation = null; _operation = false; SetConnecting(false); }
    }
    private async void ChooseCharacter(object? sender, SelectionChangedEventArgs args)
    {
        if (_operation || _refreshingCharacters || _roster == null || SelectedProfile is not { } profile ||
            (sender as ComboBox)?.SelectedItem is not AvailableCharacter npc ||
            profile.ServerId != _rosterServer || profile.WorldId != _roster.WorldId) return;
        _operation = true;
        try
        {
            if (!await IsAgentActive(profile.Id))
            {
                await SaveProfile(profile with { NpcId = npc.NpcId });
                SetConfigurationStatus(ConnectedStatus() + "\n" + string.Format(Strings["CharacterSelected"], npc));
            }
        }
        catch { SetConfigurationStatus(Strings["ConfigurationError"]); }
        finally { _operation = false; }
    }
    private async void StartAgent(object? sender, RoutedEventArgs args)
    {
        if (_operation || SelectedProfile is not { } profile || Servers.FirstOrDefault(s => s.Id == profile.ServerId) is not { } server) return;
        _operation = true;
        try { await _fleet.StartAsync(profile, server); }
        catch { SetConfigurationStatus(Strings["StartFailed"]); }
        finally { _operation = false; }
    }
    private async void StopAgent(object? sender, RoutedEventArgs args)
    { if (SelectedProfile is { } profile) await _fleet.StopAsync(profile.Id); }
    private async void StopAllAgents(object? sender, RoutedEventArgs args) => await _fleet.StopAllAsync();
}

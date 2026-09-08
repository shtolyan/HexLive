using Avalonia;
using Avalonia.Controls;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed class ProfileSettingsWindow : Window
{
    private readonly CancellationTokenSource _closed = new();
    public ProfileSettingsWindow(AgentProfile original, StudioStrings strings, ISecretStore secrets, string codexExecutable)
    {
        Title = original.Name + " — " + strings["Settings"];
        Width = 600; Height = 740; MinWidth = 440; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var provider = new ComboBox { ItemsSource = Enum.GetValues<ModelProviderKind>(), SelectedItem = original.Model.Provider };
        var integration = new TextBox { Text = original.Model.IntegrationId };
        var model = new ComboBox { ItemsSource = new[] { original.Model.ModelId }, SelectedItem = original.Model.ModelId };
        var reasoning = new ComboBox { ItemsSource = original.Model.Reasoning == null ? Array.Empty<string>() : new[] { original.Model.Reasoning }, SelectedItem = original.Model.Reasoning };
        var refresh = new Button { Content = strings["RefreshModels"] };
        var status = new TextBlock { Text = strings["CatalogNotChecked"], TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var voiceEnabled = new CheckBox { Content = "ElevenLabs", IsChecked = original.Voice != null };
        var voiceIntegration = new TextBox { Text = original.Voice?.IntegrationId ?? "voice.ElevenLabs" };
        var voiceId = new ComboBox { ItemsSource = original.Voice == null ? Array.Empty<VoiceDescriptor>() : new[] { new VoiceDescriptor(original.Voice.VoiceId, original.Voice.VoiceId) }, SelectedIndex = original.Voice == null ? -1 : 0 };
        var refreshVoices = new Button { Content = strings["RefreshVoices"] };
        var voiceModel = new TextBox { Text = original.Voice?.ModelId ?? "eleven_multilingual_v2" };
        var interval = new NumericUpDown { Minimum = 5, Maximum = 3600, Value = original.HeartbeatSeconds };
        var save = new Button { Content = strings["Save"] };
        var models = new List<ModelDescriptor>();
        var loadingModels = false;
        var loadingVoices = false;
        provider.SelectionChanged += async (_, _) =>
        {
            if (provider.SelectedItem is not ModelProviderKind kind) return;
            provider.IsEnabled = false;
            try
            {
            integration.Text = kind == original.Model.Provider ? original.Model.IntegrationId : "model." + kind;
            if (kind == ModelProviderKind.Codex) integration.Text = "codex";
            if (kind == ModelProviderKind.Grok && string.IsNullOrEmpty(await secrets.ReadAsync(integration.Text, _closed.Token)) &&
                !string.IsNullOrEmpty(await secrets.ReadAsync("model.masha.grok", _closed.Token))) integration.Text = "model.masha.grok";
            models.Clear(); model.ItemsSource = Array.Empty<string>(); reasoning.ItemsSource = Array.Empty<string>();
            await LoadModels();
            }
            catch (OperationCanceledException) { }
            catch { status.Text = strings["CatalogError"]; }
            finally { provider.IsEnabled = true; }
        };
        model.SelectionChanged += (_, _) =>
        {
            var supported = models.FirstOrDefault(x => x.Id == model.SelectedItem as string)?.ReasoningModes;
            if (supported == null) return;
            reasoning.ItemsSource = new[] { strings["ProviderDefault"] }.Concat(supported).ToArray();
            reasoning.SelectedItem = supported.Contains(original.Model.Reasoning ?? "") ? original.Model.Reasoning : strings["ProviderDefault"];
            reasoning.IsEnabled = supported.Count > 0;
        };
        async Task LoadModels()
        {
            if (loadingModels || provider.SelectedItem is not ModelProviderKind kind) return;
            loadingModels = true;
            status.Text = strings["CatalogLoading"];
            var selectedIntegration = integration.Text?.Trim() ?? "";
            var previousModel = model.SelectedItem as string;
            refresh.IsEnabled = provider.IsEnabled = integration.IsEnabled = model.IsEnabled = reasoning.IsEnabled = save.IsEnabled = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            IDisposable? disposable = null;
            try
            {
                IModelCatalog catalog;
                if (kind == ModelProviderKind.Codex) catalog = new CodexModelCatalog(codexExecutable);
                else
                {
                    if (string.IsNullOrWhiteSpace(await secrets.ReadAsync(selectedIntegration, deadline.Token)))
                    { status.Text = strings["MissingIntegrationKey"]; return; }
                    var adapter = new HttpModelAdapter(kind, selectedIntegration, async t =>
                        await secrets.ReadAsync(selectedIntegration, t) ?? throw new InvalidOperationException("MissingCredential"));
                    catalog = adapter; disposable = adapter;
                }
                var loaded = await catalog.ListAsync(deadline.Token);
                models = loaded.ToList();
                model.ItemsSource = models.Select(x => x.Id).ToArray();
                model.SelectedItem = models.Any(x => x.Id == previousModel) ? previousModel : null;
                status.Text = strings["CatalogLoaded"];
            }
            catch (OperationCanceledException) { if (!_closed.IsCancellationRequested) status.Text = strings["CatalogError"]; }
            catch { status.Text = strings["CatalogError"]; }
            finally
            {
                disposable?.Dispose(); loadingModels = false;
                refresh.IsEnabled = provider.IsEnabled = integration.IsEnabled = model.IsEnabled = true;
                reasoning.IsEnabled = models.FirstOrDefault(m => m.Id == model.SelectedItem as string)?.ReasoningModes.Count > 0;
                save.IsEnabled = !loadingVoices;
            }
        }
        refresh.Click += async (_, _) => await LoadModels();
        async Task LoadVoices()
        {
            if (loadingVoices || voiceEnabled.IsChecked != true) return;
            loadingVoices = true; save.IsEnabled = false;
            var selectedIntegration = voiceIntegration.Text?.Trim() ?? "";
            refreshVoices.IsEnabled = voiceIntegration.IsEnabled = false;
            try
            {
                using var catalog = new ElevenLabsVoiceCatalog(async t => await secrets.ReadAsync(selectedIntegration, t)
                    ?? throw new InvalidOperationException("MissingVoiceCredential"));
                var loaded = await catalog.ListAsync(_closed.Token);
                var previous = (voiceId.SelectedItem as VoiceDescriptor)?.Id;
                voiceId.ItemsSource = loaded;
                voiceId.SelectedItem = loaded.FirstOrDefault(v => v.Id == previous);
                status.Text = strings["VoicesLoaded"];
            }
            catch (OperationCanceledException) { }
            catch { status.Text = strings["CatalogError"]; }
            finally { loadingVoices = false; save.IsEnabled = !loadingModels; refreshVoices.IsEnabled = voiceIntegration.IsEnabled = true; }
        }
        refreshVoices.Click += async (_, _) => await LoadVoices();
        Opened += async (_, _) => await Task.WhenAll(LoadModels(), LoadVoices());
        save.Click += (_, _) =>
        {
            if (provider.SelectedItem is not ModelProviderKind kind || model.SelectedItem is not string modelId ||
                string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(integration.Text) ||
                (voiceEnabled.IsChecked == true && (voiceId.SelectedItem is not VoiceDescriptor || string.IsNullOrWhiteSpace(voiceModel.Text) || string.IsNullOrWhiteSpace(voiceIntegration.Text))))
            { status.Text = strings["SelectModelAndVoice"]; return; }
            Close(original with
            {
                Model = new(kind, integration.Text.Trim(), modelId, reasoning.SelectedItem as string == strings["ProviderDefault"] ? null : reasoning.SelectedItem as string),
                Voice = voiceEnabled.IsChecked == true ? new(voiceIntegration.Text!.Trim(), ((VoiceDescriptor)voiceId.SelectedItem!).Id, voiceModel.Text!.Trim()) : null,
                HeartbeatSeconds = (int)(interval.Value ?? 30),
            });
        };
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        content.Children.Add(new TextBlock { Text = original.Name, Classes = { "heading" }, Margin = new Thickness(0, 0, 0, 12) });
        void Field(string term, Control control) { content.Children.Add(new TextBlock { Text = strings[term] }); content.Children.Add(control); }
        Field("Provider", provider); Field("IntegrationId", integration); Field("Model", model);
        content.Children.Add(refresh); Field("Reasoning", reasoning); content.Children.Add(status);
        content.Children.Add(new Separator { Margin = new Thickness(0, 16) });
        content.Children.Add(voiceEnabled); Field("IntegrationId", voiceIntegration); Field("VoiceId", voiceId);
        content.Children.Add(refreshVoices);
        Field("VoiceModel", voiceModel); Field("Interval", interval);
        save.Classes.Add("primary");
        foreach (var button in new[] { refresh, refreshVoices }) button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        var cancel = new Button { Content = strings["Cancel"] };
        cancel.Click += (_, _) => Close();
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = content });
        var footer = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(24, 12), Children = { cancel, save } };
        Grid.SetRow(footer, 1); layout.Children.Add(footer); Content = layout;
        Closed += (_, _) => _closed.Cancel();
    }
}

using Avalonia;
using Avalonia.Controls;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

/// <summary>Credentials are written directly to the OS store, never profile configuration.</summary>
public sealed class IntegrationsWindow : Window
{
    public IntegrationsWindow(StudioStrings strings, ISecretStore secrets)
    {
        Title = strings["Integrations"]; Width = 780; Height = 590; MinWidth = 660; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var choices = new ListBox { ItemsSource = new[] { "OpenAI", "Grok", "Claude", "DeepSeek", "ElevenLabs", "ChatGPT / Codex" }, SelectedIndex = 0 };
        var heading = new TextBlock { Text = "OpenAI", Classes = { "heading" } };
        var id = new TextBox { Text = "model.OpenAI", MaxLength = 128 };
        var key = new TextBox { PasswordChar = '●', MaxLength = 2560, PlaceholderText = strings["NewKey"] };
        var save = new Button { Content = strings["SaveKey"] };
        var check = new Button { Content = strings["CheckStoredKey"] };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var busy = false;
        choices.SelectionChanged += (_, _) =>
        {
            key.Text = "";
            var name = choices.SelectedItem as string;
            heading.Text = name;
            var codex = name == "ChatGPT / Codex";
            key.IsEnabled = save.IsEnabled = id.IsEnabled = check.IsEnabled = !codex;
            id.Text = name == "ElevenLabs" ? "voice.ElevenLabs" : "model." + name;
            status.Text = codex ? strings["CodexLoginNotice"] : "";
        };
        async Task Perform(bool write)
        {
            if (busy) return;
            busy = true; choices.IsEnabled = save.IsEnabled = check.IsEnabled = id.IsEnabled = false;
            var selected = id.Text?.Trim() ?? "";
            try
            {
                if (write)
                {
                    await secrets.WriteAsync(selected, key.Text?.Trim() ?? "", default);
                    key.Text = ""; status.Text = strings["KeySaved"];
                }
                else status.Text = await secrets.ReadAsync(selected, default) is { Length: > 0 }
                    ? strings["KeyPresent"] : strings["KeyMissing"];
            }
            catch { key.Text = ""; status.Text = strings["KeyStoreError"]; }
            finally { busy = false; choices.IsEnabled = save.IsEnabled = check.IsEnabled = id.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Perform(true);
        check.Click += async (_, _) => await Perform(false);
        Closed += (_, _) => key.Text = "";
        save.Classes.Add("primary");
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*") };
        var sidebar = new Border { Padding = new Thickness(12, 24), BorderThickness = new Thickness(0, 0, 1, 0), Child = choices };
        sidebar.Bind(Border.BackgroundProperty, this.GetResourceObservable("StudioSidebar"));
        sidebar.Bind(Border.BorderBrushProperty, this.GetResourceObservable("StudioBorder"));
        layout.Children.Add(sidebar);
        var details = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(28), Spacing = 18, Children =
        {
            heading, new TextBlock { Text = strings["CredentialsNotice"], Classes = { "caption" } },
            new TextBlock { Text = strings["IntegrationId"] }, id, key,
            new WrapPanel { Children = { save, check } }, status
        } } };
        save.Margin = new Thickness(0, 0, 10, 8); check.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetColumn(details, 1); layout.Children.Add(details); Content = layout;
    }
}

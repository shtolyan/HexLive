using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed class ServersWindow : Window
{
    public ServersWindow(StudioStrings s, ISecretStore secrets,
        System.Collections.ObjectModel.ObservableCollection<ServerProfile> servers, Func<ServerProfile, Task> save, Func<Task> beforeChange)
    {
        Title = s["Servers"]; Width = 540; Height = 380; MinWidth = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var status = new TextBlock { Text = s["OfficialServersHint"], TextWrapping = TextWrapping.Wrap };
        var edit = new Button { Content = s["ChangeAccessKey"], Classes = { "primary" } };
        edit.Click += async (_, _) => {
            try
            {
            edit.IsEnabled = false;
            await beforeChange();
            var current = servers.FirstOrDefault(x => x.McpEndpoint == OfficialGameAccess.Singapore);
            var result = await new ServerPairingWindow(s, secrets, current).ShowDialog<ServerProfile?>(this);
            if (result == null) return;
            try { await save(result); status.Text = s["AccessSaved"]; }
            catch { status.Text = s["ConfigurationError"]; }
            }
            catch { status.Text = s["ConfigurationError"]; }
            finally { edit.IsEnabled = true; }
        };
        Content = new StackPanel { Margin = new Thickness(28), Spacing = 18, Children = {
            new TextBlock { Text = s["Servers"], Classes = { "heading" } },
            new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
                CornerRadius = new CornerRadius(10), Child = new TextBlock { Text = s["Singapore"], FontSize = 18 } },
            new Button { Content = s["NewYorkUnavailable"], IsEnabled = false }, status, edit
        }};
    }
}

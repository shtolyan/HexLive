using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed class ServersWindow : Window
{
    public ServersWindow(StudioStrings s, ISecretStore secrets,
        System.Collections.ObjectModel.ObservableCollection<ServerProfile> servers, Func<ServerProfile, Task> save)
    {
        Title = s["Servers"]; Width = 620; Height = 380; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var list = new ListBox { ItemsSource = servers,
            ItemTemplate = new FuncDataTemplate<ServerProfile>((p, _) => new StackPanel { Spacing = 5, Children =
            { new TextBlock { Text = p?.Name, FontSize = 18 },
              new TextBlock { Text = p?.McpEndpoint.ToString(), TextWrapping = TextWrapping.Wrap, Classes = { "caption" } } } }) };
        var status = new TextBlock { Text = s["ServersHint"], TextWrapping = TextWrapping.Wrap };
        var add = new Button { Content = s["AddServer"], Classes = { "primary" } };
        var edit = new Button { Content = s["EditServer"], IsEnabled = false };
        list.SelectionChanged += (_, _) => edit.IsEnabled = list.SelectedItem is ServerProfile;
        async Task Edit(ServerProfile? current)
        {
            var result = await new ServerPairingWindow(s, secrets, current).ShowDialog<ServerProfile?>(this);
            if (result == null) return;
            try { await save(result); list.SelectedItem = result; status.Text = s["AccessSaved"]; }
            catch { status.Text = s["ConfigurationError"]; }
        }
        add.Click += async (_, _) => await Edit(null);
        edit.Click += async (_, _) => { if (list.SelectedItem is ServerProfile p) await Edit(p); };
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var heading = new TextBlock { Text = s["Servers"], Classes = { "heading" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0,16), Children = { add, edit } };
        Grid.SetRow(buttons, 1); Grid.SetRow(list, 2); Grid.SetRow(status, 3); status.Margin = new Thickness(0,16,0,0);
        layout.Children.Add(heading); layout.Children.Add(buttons); layout.Children.Add(list); layout.Children.Add(status);
        Content = layout;
    }
}

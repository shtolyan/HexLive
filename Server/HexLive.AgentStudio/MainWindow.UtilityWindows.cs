using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed partial class MainWindow
{
    private async void ShowConnection(object? sender, RoutedEventArgs args)
    {
        if (SelectedProfile == null) { SetConfigurationStatus(Strings["SelectProfileFirst"]); return; }
        var dialog = new Window { Title = Strings["Connect"], Width = 480, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _serverSelection.PlaceholderText = Strings["NoServer"];
        _serverSelection.ItemTemplate = new FuncDataTemplate<ServerProfile>((s, _) => new TextBlock { Text = s?.Name });
        _characterSelection.PlaceholderText = Strings["NoCharacter"];
        var connect = new Button { Content = Strings["Connect"], Classes = { "primary" } };
        connect.Click += ConnectServer;
        var add = new Button { Content = Strings["Servers"] };
        add.Click += ShowServers;
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ConfigurationStatus)) { Source = this });
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
        { new TextBlock { Text = Strings["Server"] }, _serverSelection, add, connect,
          new TextBlock { Text = Strings["Character"] }, _characterSelection, status } };
        dialog.Content = new ScrollViewer { Content = content };
        await dialog.ShowDialog(this);
        content.Children.Remove(_serverSelection); content.Children.Remove(_characterSelection);
    }
    private async void ShowStudioSettings(object? sender, RoutedEventArgs args)
    {
        var dialog = new Window { Title = Strings["Settings"], Width = 380, Height = 470,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        void Action(string text, EventHandler<RoutedEventArgs> action, bool close = true)
        {
            var button = new Button { Content = text, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            button.Click += (s, e) => { if (close) dialog.Close(); action(s, e); }; content.Children.Add(button);
        }
        Action(Strings["Integrations"], ShowIntegrations);
        Action(Strings["Servers"], ShowServers);
        Action(Strings["NewAgent"], NewProfile);
        Action(Strings["StopAll"], StopAllAgents);
        Action(Strings["Theme"], ToggleTheme, false);
        Action("RU / EN", ToggleLanguage);
        dialog.Content = content; await dialog.ShowDialog(this);
    }
}

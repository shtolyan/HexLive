using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HexLive.AgentStudio;

public sealed partial class MainWindow
{
    private async void ShowConsciousness(object? sender, RoutedEventArgs args)
    {
        if (SelectedProfile is not { } profile) { SetConfigurationStatus(Strings["SelectProfileFirst"]); return; }
        try { await new MemoryWindow(profile.Workspace, Strings, "SOUL.md").ShowDialog(this); }
        catch { SetConfigurationStatus(Strings["DocumentReadError"]); }
    }
}

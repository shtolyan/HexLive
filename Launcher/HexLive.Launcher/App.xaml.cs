using System.Windows;
using System;

namespace HexLive.Launcher;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            await new LauncherService().UninstallAsync(e.Args.Length > 1 ? e.Args[1] : LauncherPaths.DefaultInstallRoot);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        try { new MainWindow(UpdateRequest.Parse(e.Args)).Show(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "HexLive"); Shutdown(1); }
    }
}

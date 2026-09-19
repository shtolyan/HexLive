using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed class ServerPairingWindow : Window
{
    public ServerPairingWindow(StudioStrings strings, ISecretStore secrets, ServerProfile? existing = null)
    {
        Title = strings["UnifiedAccess"];
        Width = 520; MinWidth = 400; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new CancellationTokenSource();
        Closed += (_, _) => cancel.Cancel();
        var key = new TextBox { PasswordChar = '●', MaxLength = 100, PlaceholderText = "hexlive_…" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        var save = new Button { Content = strings["AccessContinue"], Classes = { "primary" } };
        var reveal = new CheckBox { Content = strings["ShowAccessKey"] };
        reveal.IsCheckedChanged += (_, _) => key.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        var close = new Button { Content = strings["Cancel"] };
        close.Click += (_, _) => Close();
        Closed += (_, _) => key.Text = "";
        save.Click += async (_, _) => {
            save.IsEnabled = key.IsEnabled = false;
            try {
                var credential = key.Text?.Trim() ?? "";
                status.Text = strings["CheckingServer"];
                await OfficialGameAccess.ValidateAsync(credential, cancel.Token);
                await secrets.WriteAsync(OfficialGameAccess.CredentialId, credential, cancel.Token);
                key.Text = "";
                if (!cancel.IsCancellationRequested) Close(OfficialGameAccess.SingaporeProfile(existing));
            }
            catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) status.Text = strings["IdentityUnavailable"]; }
            catch (InvalidDataException e) { status.Text = strings[e.Message is "InvalidAccessKey" or "GamePermissionRequired" ? e.Message : "IdentityUnavailable"]; }
            catch (CredentialStoreException) { status.Text = strings["CredentialAccessDenied"]; }
            catch { status.Text = strings["IdentityUnavailable"]; }
            finally { save.IsEnabled = key.IsEnabled = true; }
        };
        Content = new StackPanel { Margin = new Thickness(28), Spacing = 16, Children = {
            new TextBlock { Text = strings["UnifiedAccess"], Classes = { "heading" } },
            new TextBlock { Text = strings["UnifiedAccessHint"], TextWrapping = TextWrapping.Wrap },
            key, reveal, status,
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { close, save } }
        }};
    }
}

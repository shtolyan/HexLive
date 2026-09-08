using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

// A connection is just endpoint + existing game token. Identity comes from the game.
public sealed class ServerPairingWindow : Window
{
    public ServerPairingWindow(StudioStrings strings, ISecretStore secrets, ServerProfile? existing = null)
    {
        Title = strings[existing == null ? "AddServer" : "EditServer"];
        Width = 560; MinWidth = 460; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new CancellationTokenSource();
        Closed += (_, _) => cancel.Cancel();
        var endpoint = new TextBox { Text = existing?.McpEndpoint.ToString(), PlaceholderText = "https://server.example/mcp" };
        var key = new TextBox { PasswordChar = '●', MaxLength = 256, PlaceholderText = strings["ServerToken"] };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = strings["SaveAndConnect"], Classes = { "primary" } };
        var close = new Button { Content = strings["Cancel"] };
        close.Click += (_, _) => Close();
        Closed += (_, _) => key.Text = "";
        save.Click += async (_, _) =>
        {
            save.IsEnabled = endpoint.IsEnabled = key.IsEnabled = false;
            try
            {
                var text = endpoint.Text?.Trim() ?? "";
                if (!text.Contains("://")) text = "https://" + text;
                var uri = new Uri(text);
                if (uri.AbsolutePath == "/") uri = new Uri(uri, "/mcp");
                var id = existing?.Id ?? Guid.NewGuid();
                var credential = key.Text?.Trim() ?? "";
                if (credential.Length == 0 && existing != null && uri == existing.McpEndpoint)
                    credential = await secrets.ReadAsync(existing.CredentialId, cancel.Token) ?? "";
                if (credential.Length == 0) { status.Text = strings["ServerTokenRequired"]; return; }
                var player = credential.StartsWith("hexmcp_", StringComparison.Ordinal) ? null
                    : await GameClientIdentity.ReadAsync(cancel.Token);
                var server = new ServerProfile(id, existing?.Name ?? uri.Host, uri, "server." + Guid.NewGuid().ToString("N"))
                    { PlayerClientId = player };
                server.Validate();
                status.Text = strings["CheckingServer"];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                await new AgentServerConnection(secrets).ImportServerTokenAsync(server, credential, deadline.Token);
                key.Text = "";
                if (!cancel.IsCancellationRequested) Close(server);
            }
            catch (InvalidDataException e) when (e.Message == "GameClientIdentityMissing")
            { status.Text = strings["GameIdentityMissing"]; }
            catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) status.Text = strings["ConnectionFailed"]; }
            catch { status.Text = strings["TokenConnectionFailed"]; }
            finally { save.IsEnabled = endpoint.IsEnabled = key.IsEnabled = true; }
        };
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children =
        {
            new TextBlock { Text = strings["ServerConnection"], Classes = { "heading" } },
            new TextBlock { Text = strings["ServerAddress"] }, endpoint,
            new TextBlock { Text = strings["ServerToken"] }, key,
            new TextBlock { Text = strings["DirectTokenNotice"], TextWrapping = TextWrapping.Wrap },
            status, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { close, save } }
        }};
    }
}

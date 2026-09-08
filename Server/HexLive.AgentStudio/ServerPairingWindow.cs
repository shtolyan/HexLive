using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input.Platform;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentStudio;

public sealed class ServerPairingWindow : Window
{
    public ServerPairingWindow(StudioStrings strings, ISecretStore secrets)
    {
        Title = strings["Servers"]; Width = 600; Height = 540; MinWidth = 460; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new CancellationTokenSource(); Closed += (_, _) => cancel.Cancel();
        var name = new TextBox { PlaceholderText = strings["Server"], MaxLength = 48 };
        var endpoint = new TextBox { PlaceholderText = "https://server.example/mcp" };
        var administrator = new CheckBox { Content = strings["AdministratorAccess"] };
        var adminKey = new TextBox { PasswordChar = '●', MaxLength = 128, IsVisible = false,
            PlaceholderText = strings["AdministratorKey"] };
        var notice = new TextBlock { Text = strings["AdministratorNotice"], TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var ticketField = new TextBox { IsReadOnly = true };
        var copy = new Button { Content = strings["CopyCode"], IsEnabled = false };
        copy.Click += async (_, _) => { if (Clipboard != null) await Clipboard.SetTextAsync(ticketField.Text); };
        var connect = new Button { Content = strings["PairServer"] };
        administrator.IsCheckedChanged += (_, _) =>
        {
            var enabled = administrator.IsChecked == true;
            adminKey.IsVisible = notice.IsVisible = enabled;
            ticketField.IsVisible = copy.IsVisible = !enabled;
            connect.Content = strings[enabled ? "VerifyAndSaveAccess" : "PairServer"];
            status.Text = "";
        };
        Closed += (_, _) => adminKey.Text = "";
        connect.Click += async (_, _) =>
        {
            connect.IsEnabled = false;
            administrator.IsEnabled = name.IsEnabled = endpoint.IsEnabled = false;
            try
            {
                var id = Guid.NewGuid();
                var server = new ServerProfile(id, name.Text?.Trim() ?? "", new Uri(endpoint.Text?.Trim() ?? ""), "server." + id.ToString("N"));
                server.Validate();
                if (administrator.IsChecked == true)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(35));
                    await new AgentServerConnection(secrets).ImportAdministratorAsync(server, adminKey.Text ?? "", deadline.Token);
                    adminKey.Text = "";
                    if (!cancel.IsCancellationRequested) Close(server);
                    return;
                }
                var ticket = await AgentServerConnection.RequestPairingAsync(server.McpEndpoint, "Agent Studio", cancel.Token);
                ticketField.Text = ticket.Id + ":" + ticket.Code; copy.IsEnabled = true;
                status.Text = strings["PairInstructions"];
                await new AgentServerConnection(secrets).CompletePairingAsync(server, ticket, cancel.Token);
                if (!cancel.IsCancellationRequested) Close(server);
            }
            catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) status.Text = strings["ConnectionFailed"]; }
            catch { status.Text = strings[administrator.IsChecked == true ? "AdminAccessFailed" : "PairFailed"]; }
            finally { connect.IsEnabled = administrator.IsEnabled = name.IsEnabled = endpoint.IsEnabled = true; }
        };
        connect.Classes.Add("primary");
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(28), Spacing = 16, Children =
        {
            new TextBlock { Text = strings["Servers"], Classes = { "heading" } },
            new TextBlock { Text = strings["PairInstructions"], Classes = { "caption" } },
            new TextBlock { Text = strings["Server"] }, name, endpoint, administrator, notice, adminKey, connect, status, ticketField, copy
        } } };
    }
}

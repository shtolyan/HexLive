using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HexLive.AgentStudio;

public sealed class CloseConfirmationWindow : Window
{
    public CloseConfirmationWindow(StudioStrings strings)
    {
        Title = strings["Title"];
        Width = 500; MinWidth = 440; CanResize = false; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var cancel = new Button { Content = strings["Cancel"], MinWidth = 110 };
        var stop = new Button { Content = strings["StopAndExit"], Classes = { "danger" } };
        cancel.Click += (_, _) => Close(false);
        stop.Click += (_, _) => Close(true);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
        Opened += (_, _) => cancel.Focus();
        var title = new TextBlock { Text = strings["CloseTitle"], FontSize = 21, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap };
        var icon = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Background = Brush.Parse("#513039"),
            Child = new Avalonia.Controls.Shapes.Rectangle { Width = 15, Height = 15, RadiusX = 2, RadiusY = 2,
                Fill = Brush.Parse("#F08D99"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(icon);
        var copy = new StackPanel { Margin = new Thickness(16, 0, 0, 0), Spacing = 10, Children =
        {
            title, new TextBlock { Text = strings["CloseExplanation"], Classes = { "caption" }, FontSize = 14, TextWrapping = TextWrapping.Wrap }
        } };
        Grid.SetColumn(copy, 1); header.Children.Add(copy);
        Content = new StackPanel { Margin = new Thickness(28), Spacing = 28, Children =
        {
            header,
            new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, stop } }
        } };
        cancel.Margin = new Thickness(0, 0, 10, 0);
    }
}

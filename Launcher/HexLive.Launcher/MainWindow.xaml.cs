using Microsoft.Win32;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HexLive.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherService _service = new();
    private RemoteRelease? _latest;
    private InstallState? _installed;
    private bool _busy;
    private readonly UpdateRequest? _update;

    public MainWindow(UpdateRequest? update = null)
    {
        _update = update;
        InitializeComponent();
        InstallPathText.Text = update == null ? LauncherPaths.DefaultInstallRoot : UpdateRequest.InstallRoot(Environment.ProcessPath!);
        Loaded += async (_, _) => {
            try
            {
                if (_update != null) { SetBusy(true, "Ожидаем закрытия игры…"); await _update.WaitForGameAsync(); }
                await RefreshAsync();
                if (_update != null && _latest != null) await InstallOrPlayAsync();
            }
            catch (Exception ex) { ErrorText.Text = ex.Message; SetBusy(false); }
        };
    }

    private async Task RefreshAsync()
    {
        SetBusy(true, "Проверяем обновления…");
        try
        {
            _installed = LauncherService.ReadInstallState(InstallPathText.Text);
            _latest = null;
            var latest = await _service.GetLatestAsync();
            LauncherService.RequireProtocol(latest, _update?.RequiredProtocol ?? 0);
            _latest = latest;
            PrimaryButton.Content = _installed is null ? "Установить" :
                !LauncherService.NeedsUpdate(_installed, _latest) ? "Играть" : "Обновить";
            UninstallButton.Visibility = _installed is null ? Visibility.Collapsed : Visibility.Visible;
            SetProgress(!LauncherService.NeedsUpdate(_installed, _latest) ? 1 : 0);
            StageText.Text = _installed is null ? $"Доступна HexLive {_latest.Version}" :
                !LauncherService.NeedsUpdate(_installed, _latest) ? "Игра готова" :
                $"Доступно обновление {_installed.Version} → {_latest.Version}";
            DetailText.Text = "Player и весь Windows-контент будут проверены перед запуском";
            ErrorText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            StageText.Text = "Не удалось проверить release";
            PrimaryButton.Content = "Повторить";
        }
        finally { SetBusy(false); }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_latest is null) { await RefreshAsync(); return; }
        await InstallOrPlayAsync();
    }

    private async Task InstallOrPlayAsync()
    {
        if (_latest == null) return;
        SetBusy(true, _installed is null ? "Устанавливаем HexLive" : "Обновляем HexLive");
        ErrorText.Text = string.Empty;
        try
        {
            if (!LauncherService.NeedsUpdate(_installed, _latest))
            {
                _service.Launch(_installed!);
                if (_update != null) Close();
                return;
            }
            var progress = new Progress<InstallProgress>(value =>
            {
                StageText.Text = value.Stage;
                DetailText.Text = $"{Format(value.CompletedBytes)} / {Format(value.TotalBytes)}  ·  {Format(value.BytesPerSecond)}/с";
                SetProgress(value.Fraction);
            });
            _installed = await _service.InstallAsync(_latest, InstallPathText.Text, progress);
            PrimaryButton.Content = "Играть";
            UninstallButton.Visibility = Visibility.Visible;
            StageText.Text = "Игра и контент готовы";
            DetailText.Text = $"HexLive {_installed.Version}";
            SetProgress(1);
            if (_update != null) { _service.Launch(_installed); Close(); }
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            PrimaryButton.Content = "Повторить";
        }
        finally { SetBusy(false); }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = InstallPathText.Text, Multiselect = false };
        if (dialog.ShowDialog() == true) InstallPathText.Text = dialog.FolderName;
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Удалить Player и ярлыки? Сохранения и кэш останутся.", "HexLive",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true, "Удаляем игру…");
        try { await _service.UninstallAsync(InstallPathText.Text); Close(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; SetBusy(false); }
    }

    private void SetBusy(bool value, string? stage = null)
    {
        _busy = value; PrimaryButton.IsEnabled = !value; BrowseButton.IsEnabled = !value;
        UninstallButton.IsEnabled = !value; InstallPathText.IsEnabled = !value;
        if (stage is not null) StageText.Text = stage;
    }

    private void SetProgress(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1); PercentText.Text = $"{fraction:P0}";
        const double radius = 84; var center = new Point(90, 90);
        if (fraction <= 0) { ProgressArc.Data = Geometry.Empty; return; }
        if (fraction >= .9999) { ProgressArc.Data = new EllipseGeometry(center, radius, radius); return; }
        var start = new Point(90, 6); var angle = fraction * Math.PI * 2 - Math.PI / 2;
        var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > .5,
            SweepDirection.Clockwise, true)); ProgressArc.Data = new PathGeometry(new[] { figure });
    }

    private static string Format(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:F1} ГБ" :
        bytes >= 1_048_576 ? $"{bytes / 1_048_576d:F1} МБ" : $"{bytes / 1024d:F0} КБ";
}

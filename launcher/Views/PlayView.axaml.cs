using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

using Avalonia.Media;
using KenshiLauncher.Services;
using KenshiLauncher.ViewModels;

namespace KenshiLauncher.Views;

public partial class PlayView : UserControl
{
    private static readonly SolidColorBrush GreenBrush = new(Color.Parse("#4a9e6a"));
    private static readonly SolidColorBrush AccentBrush = new(Color.Parse("#c8a052"));
    private static readonly SolidColorBrush RedBrush = new(Color.Parse("#9a3a3a"));
    private static readonly SolidColorBrush Text2Brush = new(Color.Parse("#6b6b6b"));
    private static readonly SolidColorBrush Text3Brush = new(Color.Parse("#3a3a3a"));

    public PlayView()
    {
        InitializeComponent();

        var playVer = this.FindControl<TextBlock>("PlayVersion");
        if (playVer != null) playVer.Text = Program.Version;

        DataContextChanged += (_, _) =>
        {
            if (DataContext is PlayViewModel vm)
            {
                UpdateStatusDot(vm.DllStatus);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.DllStatus))
                        UpdateStatusDot(vm.DllStatus);
                };
            }
        };
    }

    private void UpdateStatusDot(DllStatus status)
    {
        var dllVer = GitHubUpdater.StartupDllVersion;
        var updateStatus = GitHubUpdater.StartupDllStatus;

        switch (status)
        {
            case DllStatus.Ready:
                StatusDot.Fill = GreenBrush;
                StatusLabel.Text = "ready";
                StatusLabel.Foreground = Text2Brush;
                if (!string.IsNullOrEmpty(dllVer))
                    StatusSubLabel.Text = $"DLL {dllVer} | {updateStatus}";
                else
                    StatusSubLabel.Text = $"kenshi_multiplayer.dll v{Program.Version}";
                break;
            case DllStatus.Outdated:
                StatusDot.Fill = AccentBrush;
                StatusLabel.Text = "update available";
                StatusLabel.Foreground = Text2Brush;
                StatusSubLabel.Text = "newer DLL available, downloads on connect";
                break;
            case DllStatus.Missing:
                StatusDot.Fill = RedBrush;
                StatusLabel.Text = "DLL not found";
                StatusLabel.Foreground = Text2Brush;
                StatusSubLabel.Text = "downloads automatically on connect";
                break;
            case DllStatus.Corrupted:
                StatusDot.Fill = RedBrush;
                StatusLabel.Text = "DLL corrupted";
                StatusLabel.Foreground = RedBrush;
                StatusSubLabel.Text = "will re-download on connect";
                break;
        }
    }
}

// fix = green (bug fixes), new = tan/gold (new features), wip = gray
public class TagBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush FixBg = new(Color.Parse("#161e16"));
    private static readonly SolidColorBrush NewBg = new(Color.Parse("#1a1508"));
    private static readonly SolidColorBrush WipBg = new(Color.Parse("#1a1a1a"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "fix" => FixBg,
            "new" => NewBg,
            "wip" => WipBg,
            _ => WipBg,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class TagForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush FixFg = new(Color.Parse("#4a9e6a"));
    private static readonly SolidColorBrush NewFg = new(Color.Parse("#8a6a28"));
    private static readonly SolidColorBrush WipFg = new(Color.Parse("#505050"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "fix" => FixFg,
            "new" => NewFg,
            "wip" => WipFg,
            _ => WipFg,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

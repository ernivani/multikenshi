using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
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

        // Click-to-dismiss for overlay backgrounds
        var joinOverlay = this.FindControl<Border>("JoinOverlay");
        if (joinOverlay != null)
            joinOverlay.PointerPressed += OnOverlayPointerPressed;

        var hostOverlay = this.FindControl<Border>("HostOverlay");
        if (hostOverlay != null)
            hostOverlay.PointerPressed += OnOverlayPointerPressed;
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is PlayViewModel vm)
        {
            vm.CloseJoinModalCommand.Execute(null);
            vm.CloseHostModalCommand.Execute(null);
        }
    }

    private void UpdateStatusDot(DllStatus status)
    {
        switch (status)
        {
            case DllStatus.Ready:
                StatusDot.Fill = GreenBrush;
                StatusLabel.Text = "injector ready";
                StatusLabel.Foreground = Text2Brush;
                StatusSubLabel.Text = "nexus_mp.dll v0.2 loaded";
                break;
            case DllStatus.Outdated:
                StatusDot.Fill = AccentBrush;
                StatusLabel.Text = "DLL out of date";
                StatusLabel.Foreground = Text2Brush;
                StatusSubLabel.Text = "nexus_mp.dll v0.1 found \u2014 v0.2 required";
                break;
            case DllStatus.Missing:
                StatusDot.Fill = RedBrush;
                StatusLabel.Text = "DLL not found";
                StatusLabel.Foreground = Text2Brush;
                StatusSubLabel.Text = "nexus_mp.dll missing from Kenshi directory";
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

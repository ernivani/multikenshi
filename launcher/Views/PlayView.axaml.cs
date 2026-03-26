using System;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.VisualTree;
using KenshiLauncher.ViewModels;

namespace KenshiLauncher.Views;

public partial class PlayView : UserControl
{
    public PlayView()
    {
        InitializeComponent();

        // Auto-scroll log and update DLL status
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PlayViewModel vm)
            {
                UpdateDllStatus(vm.DllExists);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.DllExists))
                        UpdateDllStatus(vm.DllExists);
                    if (e.PropertyName == nameof(vm.IsPlaying))
                        PlayButton.Content = vm.IsPlaying ? "LAUNCHING..." : "PLAY MULTIPLAYER";
                };
            }

            // Hook into parent's log for auto-scroll
            var mainVm = (this.FindAncestorOfType<Window>()?.DataContext as MainViewModel);
            if (mainVm != null)
            {
                mainVm.LogLines.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add)
                        PlayLogScroll?.ScrollToEnd();
                };
            }
        };
    }

    private static readonly SolidColorBrush GreenDot = new(Color.Parse("#3D9E6A"));
    private static readonly SolidColorBrush RedDot = new(Color.Parse("#9E3D3D"));

    private void UpdateDllStatus(bool exists)
    {
        DllDot.Fill = exists ? GreenDot : RedDot;
        DllStatusText.Text = exists ? "kenshi_multiplayer.dll ready" : "kenshi_multiplayer.dll not found";
    }
}

public class LogColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Red = new(Color.Parse("#9E3D3D"));
    private static readonly SolidColorBrush Green = new(Color.Parse("#3D9E6A"));
    private static readonly SolidColorBrush Yellow = new(Color.Parse("#9E8B3D"));
    private static readonly SolidColorBrush Gray = new(Color.Parse("#444444"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "Red" => Red,
            "Green" => Green,
            "Yellow" => Yellow,
            _ => Gray,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

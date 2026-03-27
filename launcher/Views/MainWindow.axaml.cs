using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KenshiLauncher.ViewModels;

namespace KenshiLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var joinOverlay = this.FindControl<Border>("JoinOverlay");
        if (joinOverlay != null)
            joinOverlay.PointerPressed += OnOverlayPointerPressed;

        var hostOverlay = this.FindControl<Border>("HostOverlay");
        if (hostOverlay != null)
            hostOverlay.PointerPressed += OnOverlayPointerPressed;

        var savePickerOverlay = this.FindControl<Border>("SavePickerOverlay");
        if (savePickerOverlay != null)
            savePickerOverlay.PointerPressed += OnSavePickerOverlayPressed;
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is MainViewModel vm)
        {
            vm.Play.CloseJoinModalCommand.Execute(null);
            vm.Play.CloseHostModalCommand.Execute(null);
        }
    }

    private void OnSavePickerOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is MainViewModel vm)
        {
            vm.Play.CancelSavePickCommand.Execute(null);
        }
    }

    private void SavePickerNewSession_Click(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Play.SelectedSaveForHost = null;
        }
    }

    private void SavePickerItem_Click(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is SaveSummaryViewModel save && DataContext is MainViewModel vm)
        {
            vm.Play.SelectedSaveForHost = save;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}

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
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is MainViewModel vm)
        {
            vm.Play.CloseJoinModalCommand.Execute(null);
            vm.Play.CloseHostModalCommand.Execute(null);
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

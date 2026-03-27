using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KenshiLauncher.ViewModels;

namespace KenshiLauncher.Views;

public partial class HostWindow : Window
{
    public HostWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Auto-scroll log when new entries are added
        if (DataContext is HostViewModel vm)
        {
            vm.ServerLog.CollectionChanged += OnLogChanged;

            // Update status dot
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.IsRunning))
                    UpdateStatusDisplay(vm);
            };
            UpdateStatusDisplay(vm);
        }
    }

    private void UpdateStatusDisplay(HostViewModel vm)
    {
        var dot = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatusDot");
        var text = this.FindControl<TextBlock>("StatusText");
        if (dot != null)
            dot.Fill = vm.IsRunning
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4a9e6a"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9a3a3a"));
        if (text != null)
        {
            var label = vm.IsRunning
                ? $"Running on port {vm.ServerPort}"
                : "Stopped";
            text.Text = label;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var scroller = this.FindControl<ScrollViewer>("LogScroller");
        if (scroller != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                scroller.ScrollToEnd();
            });
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HostViewModel vm && vm.IsRunning)
            vm.ToggleServerCommand.Execute(null);
        Close();
    }

    private void CommandInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is HostViewModel vm)
        {
            vm.SendCommandCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is HostViewModel vm)
            vm.ServerLog.CollectionChanged -= OnLogChanged;
        base.OnClosed(e);
    }
}

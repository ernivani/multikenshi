using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using KenshiLauncher.ViewModels;

namespace KenshiLauncher.Views;

public partial class HostView : UserControl
{
    private static readonly SolidColorBrush GreenBrush = new(Color.Parse("#3D9E6A"));
    private static readonly SolidColorBrush GrayBrush = new(Color.Parse("#444444"));
    private static readonly SolidColorBrush TextBrush = new(Color.Parse("#F0F0F0"));
    private static readonly SolidColorBrush MutedBrush = new(Color.Parse("#444444"));

    public HostView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is HostViewModel vm)
            {
                UpdateUI(vm);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(vm.IsRunning) or nameof(vm.PlayerCount))
                        UpdateUI(vm);
                };
            }

            var mainVm = this.FindAncestorOfType<Window>()?.DataContext as MainViewModel;
            if (mainVm != null)
            {
                mainVm.LogLines.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add)
                        HostLogScroll?.ScrollToEnd();
                };
            }
        };
    }

    private void UpdateUI(HostViewModel vm)
    {
        if (vm.IsRunning)
        {
            ServerButton.Classes.Set("danger", true);
            ServerButton.Classes.Set("accent", false);
            ServerButton.Content = "STOP SERVER";
            StatusDot.Fill = GreenBrush;
            StatusText.Text = "Server running";
            StatusText.Foreground = TextBrush;
            PlayerCountText.Text = $"Players connected: {vm.PlayerCount}";
            PlayerCountText.IsVisible = true;
        }
        else
        {
            ServerButton.Classes.Set("danger", false);
            ServerButton.Classes.Set("accent", true);
            ServerButton.Content = "START SERVER";
            StatusDot.Fill = GrayBrush;
            StatusText.Text = "Server stopped";
            StatusText.Foreground = MutedBrush;
            PlayerCountText.IsVisible = false;
        }
    }
}

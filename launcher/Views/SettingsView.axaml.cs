using Avalonia.Controls;
using Avalonia.Media;
using KenshiLauncher.ViewModels;

namespace KenshiLauncher.Views;

public partial class SettingsView : UserControl
{
    private static readonly SolidColorBrush GreenBrush = new(Color.Parse("#3D9E6A"));
    private static readonly SolidColorBrush RedBrush = new(Color.Parse("#9E3D3D"));

    public SettingsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                UpdateUI(vm);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(vm.ExeFound) or nameof(vm.DllExists) or nameof(vm.KenshiPath))
                        UpdateUI(vm);
                };
            }
        };
    }

    private void UpdateUI(SettingsViewModel vm)
    {
        // Exe status
        if (!string.IsNullOrEmpty(vm.KenshiPath))
        {
            ExeStatus.IsVisible = true;
            ExeDot.Fill = vm.ExeFound ? GreenBrush : RedBrush;
            ExeText.Text = vm.ExeFound ? "kenshi_x64.exe found" : "kenshi_x64.exe not found in this path";
            ExeText.Foreground = vm.ExeFound ? GreenBrush : RedBrush;
        }
        else
        {
            ExeStatus.IsVisible = false;
        }

        // DLL status
        DllDot.Fill = vm.DllExists ? GreenBrush : RedBrush;
        DllStatusLabel.Text = vm.DllExists ? "ready" : "missing";
        DllStatusLabel.Foreground = vm.DllExists ? GreenBrush : RedBrush;
    }
}

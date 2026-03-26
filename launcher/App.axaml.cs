using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KenshiLauncher.ViewModels;
using KenshiLauncher.Views;

namespace KenshiLauncher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };
            desktop.ShutdownRequested += (_, _) => mainVm.OnShutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

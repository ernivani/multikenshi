using Avalonia;
using System;
using System.Threading.Tasks;
using KenshiLauncher.Services;

namespace KenshiLauncher;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Clean up old launcher from previous update
        try
        {
            var oldExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName + ".old";
            if (oldExe != null && System.IO.File.Exists(oldExe))
                System.IO.File.Delete(oldExe);
        }
        catch { }

        // Check for launcher self-update (blocks briefly at startup)
        if (!GitHubUpdater.IsDevMode())
        {
            var updateTask = GitHubUpdater.CheckLauncherUpdate(msg => Console.WriteLine(msg));
            updateTask.Wait(TimeSpan.FromSeconds(10));
            if (updateTask.IsCompletedSuccessfully && updateTask.Result)
            {
                // Updated and restarting — exit this instance
                return;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

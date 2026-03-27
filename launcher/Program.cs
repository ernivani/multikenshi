using Avalonia;
using System;
using System.Linq;
using KenshiLauncher.Services;

namespace KenshiLauncher;

class Program
{
    public const string Version = "0.5.1";

    [STAThread]
    public static void Main(string[] args)
    {
        // If launched by the updater with --updated-from <pid>, kill the old instance
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--updated-from" && int.TryParse(args[i + 1], out var oldPid))
            {
                try
                {
                    var old = System.Diagnostics.Process.GetProcessById(oldPid);
                    old.Kill();
                    old.WaitForExit(3000);
                }
                catch { }
            }
        }

        // Setup AppData directories and migrate old files
        Paths.EnsureDirectories();
        Paths.MigrateFromLauncherDir();

        // Clean up old launcher from previous update
        try
        {
            var oldExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName + ".old";
            if (oldExe != null && System.IO.File.Exists(oldExe))
                System.IO.File.Delete(oldExe);
        }
        catch { }

        // Check for launcher self-update (silent, no popup)
        if (!GitHubUpdater.IsDevMode())
        {
            var updateTask = GitHubUpdater.CheckLauncherUpdate(msg => Console.WriteLine(msg));
            updateTask.Wait(TimeSpan.FromSeconds(15));

            if (updateTask.IsCompletedSuccessfully && updateTask.Result)
                return; // new instance launched, this one exits
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

using Avalonia;
using System;
using System.Linq;
using KenshiLauncher.Services;

namespace KenshiLauncher;

class Program
{
    public const string Version = "0.5.4";

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

        // Auto-update at startup (skip in dev mode)
        if (!GitHubUpdater.IsDevMode())
        {
            Console.WriteLine($"[Startup] MultiKenshi v{Version}");

            // 1. Check launcher update
            var launcherTask = GitHubUpdater.UpdateLauncher(msg => Console.WriteLine(msg));
            launcherTask.Wait(TimeSpan.FromSeconds(15));
            if (launcherTask.IsCompletedSuccessfully && launcherTask.Result)
                return; // restarting with new launcher

            // 2. Check DLL update
            var dllTask = GitHubUpdater.UpdateDll(msg => Console.WriteLine(msg));
            dllTask.Wait(TimeSpan.FromSeconds(15));
        }
        else
        {
            Console.WriteLine($"[Startup] MultiKenshi v{Version} (dev mode)");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

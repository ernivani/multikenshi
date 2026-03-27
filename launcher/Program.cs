using Avalonia;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KenshiLauncher.Services;

namespace KenshiLauncher;

class Program
{
    public const string Version = "0.4.5";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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

            // Quick check — did it finish fast?
            updateTask.Wait(TimeSpan.FromSeconds(3));

            if (!updateTask.IsCompleted)
            {
                // Still downloading — tell the user
                MessageBoxW(IntPtr.Zero,
                    "Downloading update, please wait...\nThe launcher will restart automatically.",
                    "MultiKenshi — Updating", 0x00000040); // MB_ICONINFORMATION
                updateTask.Wait(TimeSpan.FromSeconds(30));
            }

            if (updateTask.IsCompletedSuccessfully && updateTask.Result)
                return; // restarting
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

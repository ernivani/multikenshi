using System.Diagnostics;
using System.IO;
using System.Linq;

namespace KenshiLauncher.Services;

public static class ProcessLauncher
{
    public static Process? FindKenshiProcess()
    {
        return Process.GetProcessesByName("kenshi_x64").FirstOrDefault();
    }

    public static Process? LaunchKenshi(string kenshiDir)
    {
        var exePath = Path.Combine(kenshiDir, "kenshi_x64.exe");
        if (!File.Exists(exePath))
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = kenshiDir,
            UseShellExecute = false,
        };

        return Process.Start(psi);
    }

    public static string GetDllPath()
    {
        var dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
        return Path.Combine(dir, "kenshi_multiplayer.dll");
    }

    public static bool DllExists()
    {
        return File.Exists(GetDllPath());
    }
}

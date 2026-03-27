using System;
using System.IO;

namespace KenshiLauncher.Services;

/// <summary>
/// Central path management. All data lives in %APPDATA%/MultiKenshi/
/// </summary>
public static class Paths
{
    public static readonly string AppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MultiKenshi");

    public static readonly string DllPath = FindDllPath();

    /// <summary>
    /// In dev mode, use the local build output. In published mode, use AppData.
    /// </summary>
    private static string FindDllPath()
    {
        // Check for local build output (dev mode)
        var dir = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
        while (dir != null)
        {
            var buildDll = Path.Combine(dir, "kenshi_multiplayer", "x64", "Release", "kenshi_multiplayer.dll");
            if (File.Exists(buildDll)) return buildDll; // dev mode — use build output directly
            dir = Path.GetDirectoryName(dir);
        }
        // Published mode — use AppData
        return Path.Combine(AppData, "kenshi_multiplayer.dll");
    }
    public static readonly string ConfigPath = Path.Combine(AppData, "launcher.ini");
    public static readonly string SavesDir = Path.Combine(AppData, "saves");

    /// <summary>
    /// Ensure the AppData directory exists. Call at startup.
    /// </summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(SavesDir);
    }

    /// <summary>
    /// Migrate old files from next to the launcher exe to AppData.
    /// </summary>
    public static void MigrateFromLauncherDir()
    {
        var launcherDir = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
        if (launcherDir == null) return;

        // Migrate DLL
        var oldDll = Path.Combine(launcherDir, "kenshi_multiplayer.dll");
        if (File.Exists(oldDll) && !File.Exists(DllPath))
        {
            File.Move(oldDll, DllPath);
            var oldHash = oldDll + ".sha256";
            if (File.Exists(oldHash)) File.Move(oldHash, DllPath + ".sha256");
        }

        // Migrate config
        var oldConfig = Path.Combine(launcherDir, "launcher.ini");
        if (File.Exists(oldConfig) && !File.Exists(ConfigPath))
            File.Move(oldConfig, ConfigPath);

        // Migrate saves
        var oldSaves = Path.Combine(launcherDir, "saves");
        if (Directory.Exists(oldSaves) && !Directory.Exists(SavesDir))
            Directory.Move(oldSaves, SavesDir);
    }
}

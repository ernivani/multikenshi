using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KenshiLauncher.Services;

public static class GameConfigWriter
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern bool WritePrivateProfileStringA(string section, string key, string value, string filePath);

    private const string ModDirName = "kenshi-online";
    private const string ModFileName = "kenshi-online.mod";

    public static void Write(string kenshiDir, string ip, string port, string steamName = "", string steamId = "")
    {
        var path = Path.Combine(kenshiDir, "kenshi_mp.ini");
        WritePrivateProfileStringA("Server", "IP", ip, path);
        WritePrivateProfileStringA("Server", "Port", port, path);
        WritePrivateProfileStringA("Identity", "SteamName", steamName, path);
        WritePrivateProfileStringA("Identity", "SteamId", steamId, path);

        // Mod auto-install disabled — using vanilla starts for now
        // EnsureMod(kenshiDir);
    }

    /// <summary>
    /// Ensures the multiplayer mod is installed and up to date.
    /// 1. Adds entry to mods.cfg if missing
    /// 2. Installs or updates the mod file if a newer source exists
    /// Returns (updated, message) for UI feedback.
    /// </summary>
    public static (bool Updated, string Message) EnsureMod(string kenshiDir)
    {
        bool updated = false;
        string message = "";

        // Step 1: Ensure mods.cfg has our entry
        var modsCfgPath = Path.Combine(kenshiDir, "data", "mods.cfg");
        bool hasEntry = false;
        if (File.Exists(modsCfgPath))
        {
            var content = File.ReadAllText(modsCfgPath);
            hasEntry = content.Contains(ModFileName);
        }

        if (!hasEntry)
        {
            var existing = File.Exists(modsCfgPath) ? File.ReadAllText(modsCfgPath).TrimEnd() : "";
            var newContent = string.IsNullOrEmpty(existing) ? ModFileName : existing + "\n" + ModFileName;
            File.WriteAllText(modsCfgPath, newContent + "\n");
            updated = true;
            message = "Added mod to mods.cfg";
        }

        // Step 2: Find the best source for the mod file
        var sourceMod = FindModSource();
        if (sourceMod == null)
            return (updated, updated ? message : "Mod source not found");

        // Step 3: Install or update the mod file
        var modDir = Path.Combine(kenshiDir, "mods", ModDirName);
        var installedMod = Path.Combine(modDir, ModFileName);

        if (!File.Exists(installedMod))
        {
            // Fresh install
            Directory.CreateDirectory(modDir);
            File.Copy(sourceMod, installedMod, overwrite: true);
            return (true, "Mod installed");
        }

        // Compare hashes to detect updates
        var sourceHash = ComputeHash(sourceMod);
        var installedHash = ComputeHash(installedMod);

        if (!string.Equals(sourceHash, installedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceMod, installedMod, overwrite: true);
            return (true, "Mod updated");
        }

        return (updated, updated ? message : "Mod up to date");
    }

    /// <summary>
    /// Search for the mod file in known locations (same pattern as DLL discovery).
    /// Priority: next to launcher exe, then walk up repo tree.
    /// </summary>
    private static string? FindModSource()
    {
        var launcherDir = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");

        if (launcherDir == null) return null;

        // 1. Next to launcher exe (bundled distribution)
        var beside = Path.Combine(launcherDir, ModFileName);
        if (File.Exists(beside)) return beside;

        // 2. Walk up the directory tree to find repo root, check known locations
        var dir = launcherDir;
        while (dir != null)
        {
            // Check Kenshi-Online/ subdirectory (cloned repo)
            var inRepo = Path.Combine(dir, "Kenshi-Online", ModFileName);
            if (File.Exists(inRepo)) return inRepo;

            // Check Kenshi-Online/dist/
            var inDist = Path.Combine(dir, "Kenshi-Online", "dist", ModFileName);
            if (File.Exists(inDist)) return inDist;

            // Check launcher/ directory
            var inLauncher = Path.Combine(dir, "launcher", ModFileName);
            if (File.Exists(inLauncher)) return inLauncher;

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }
}

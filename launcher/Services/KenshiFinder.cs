using System;
using System.IO;
using Microsoft.Win32;

namespace KenshiLauncher.Services;

public static class KenshiFinder
{
    public static string? FindKenshiPath()
    {
        string? steamPath = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            steamPath = key?.GetValue("InstallPath") as string;
        }
        catch { }

        if (string.IsNullOrEmpty(steamPath))
            return null;

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            return null;

        foreach (var line in File.ReadLines(vdfPath))
        {
            var pathPos = line.IndexOf("\"path\"", StringComparison.Ordinal);
            if (pathPos < 0) continue;

            // Find the value after "path"
            var firstQ = line.IndexOf('"', pathPos + 6);
            var lastQ = line.LastIndexOf('"');
            if (firstQ < 0 || lastQ <= firstQ) continue;

            var libPath = line.Substring(firstQ + 1, lastQ - firstQ - 1);

            // Unescape double backslashes from VDF format
            libPath = libPath.Replace("\\\\", "\\");

            var kenshiPath = Path.Combine(libPath, "steamapps", "common", "Kenshi");
            if (Directory.Exists(kenshiPath))
                return kenshiPath;
        }

        return null;
    }
}

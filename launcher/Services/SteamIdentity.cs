using System;
using System.IO;
using Microsoft.Win32;

namespace KenshiLauncher.Services;

public static class SteamIdentity
{
    public static (string Name, string SteamId64) GetCurrentUser()
    {
        try
        {
            var steamPath = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam",
                "SteamPath", null) as string;

            if (string.IsNullOrEmpty(steamPath))
                return ("Player", "");

            var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(vdfPath))
                return ("Player", "");

            return ParseLoginUsers(File.ReadAllLines(vdfPath));
        }
        catch
        {
            return ("Player", "");
        }
    }

    private static (string Name, string SteamId64) ParseLoginUsers(string[] lines)
    {
        // loginusers.vdf structure:
        // "users"
        // {
        //     "76561198012345678"
        //     {
        //         "AccountName"   "username"
        //         "PersonaName"   "Display Name"
        //         "MostRecent"    "1"
        //     }
        // }

        string? currentId = null;
        string? currentName = null;
        bool isMostRecent = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // A top-level steam ID line: just a quoted number
            if (line.StartsWith("\"") && line.EndsWith("\"") && !line.Contains("\t") && !line.Contains(" "))
            {
                var val = Unquote(line);
                if (val.Length > 10 && long.TryParse(val, out _))
                {
                    // Save previous user if it was most recent
                    if (isMostRecent && currentId != null && currentName != null)
                        return (currentName, currentId);

                    currentId = val;
                    currentName = null;
                    isMostRecent = false;
                }
            }
            else if (currentId != null)
            {
                var (key, value) = ParseKV(line);
                if (key == null) continue;

                if (key.Equals("PersonaName", StringComparison.OrdinalIgnoreCase))
                    currentName = value;
                else if (key.Equals("MostRecent", StringComparison.OrdinalIgnoreCase))
                    isMostRecent = value == "1";
            }
        }

        // Check last user block
        if (isMostRecent && currentId != null && currentName != null)
            return (currentName, currentId);

        return ("Player", "");
    }

    private static (string? Key, string? Value) ParseKV(string line)
    {
        // Format: "Key"    "Value" (tab-separated quoted strings)
        var first = line.IndexOf('"');
        if (first < 0) return (null, null);

        var second = line.IndexOf('"', first + 1);
        if (second < 0) return (null, null);

        var third = line.IndexOf('"', second + 1);
        if (third < 0) return (null, null);

        var fourth = line.IndexOf('"', third + 1);
        if (fourth < 0) return (null, null);

        var key = line.Substring(first + 1, second - first - 1);
        var value = line.Substring(third + 1, fourth - third - 1);
        return (key, value);
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            return s.Substring(1, s.Length - 2);
        return s;
    }
}

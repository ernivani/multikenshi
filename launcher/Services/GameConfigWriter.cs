using System.IO;
using System.Runtime.InteropServices;

namespace KenshiLauncher.Services;

public static class GameConfigWriter
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern bool WritePrivateProfileStringA(string section, string key, string value, string filePath);

    public static void Write(string kenshiDir, string ip, string port, string steamName = "", string steamId = "")
    {
        var path = Path.Combine(kenshiDir, "kenshi_mp.ini");
        WritePrivateProfileStringA("Server", "IP", ip, path);
        WritePrivateProfileStringA("Server", "Port", port, path);
        WritePrivateProfileStringA("Identity", "SteamName", steamName, path);
        WritePrivateProfileStringA("Identity", "SteamId", steamId, path);
    }
}

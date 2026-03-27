using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace KenshiLauncher.Services;

public class ConfigManager
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern int GetPrivateProfileStringA(string section, string key, string defaultValue, byte[] returnedString, int size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern bool WritePrivateProfileStringA(string section, string key, string value, string filePath);

    private readonly string _iniPath = Paths.ConfigPath;

    public string KenshiPath { get; set; } = "";
    public string ServerPort { get; set; } = "8080";
    public string ClientIP { get; set; } = "127.0.0.1";
    public string ClientPort { get; set; } = "8080";

    public void Load()
    {
        KenshiPath = ReadIni("Settings", "KenshiPath", "");
        ServerPort = ReadIni("Settings", "ServerPort", "8080");
        ClientIP = ReadIni("Settings", "ClientIP", "127.0.0.1");
        ClientPort = ReadIni("Settings", "ClientPort", "8080");
    }

    public void Save()
    {
        WritePrivateProfileStringA("Settings", "KenshiPath", KenshiPath, _iniPath);
        WritePrivateProfileStringA("Settings", "ServerPort", ServerPort, _iniPath);
        WritePrivateProfileStringA("Settings", "ClientIP", ClientIP, _iniPath);
        WritePrivateProfileStringA("Settings", "ClientPort", ClientPort, _iniPath);
    }

    private string ReadIni(string section, string key, string defaultValue)
    {
        var buffer = new byte[512];
        int len = GetPrivateProfileStringA(section, key, defaultValue, buffer, buffer.Length, _iniPath);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, len);
    }
}

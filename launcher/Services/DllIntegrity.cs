using System;
using System.IO;
using System.Security.Cryptography;

namespace KenshiLauncher.Services;

public static class DllIntegrity
{
    public static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }

    public static void WriteHash(string dllPath)
    {
        var hash = ComputeHash(dllPath);
        File.WriteAllText(dllPath + ".sha256", hash);
    }

    public static (bool Valid, string Reason) Verify(string dllPath)
    {
        if (!File.Exists(dllPath))
            return (false, "missing");

        var hashPath = dllPath + ".sha256";
        if (!File.Exists(hashPath))
            return (false, "no hash — reinstall recommended");

        var expected = File.ReadAllText(hashPath).Trim();
        var actual = ComputeHash(dllPath);

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            return (false, "corrupted or wrong version");

        return (true, "");
    }
}

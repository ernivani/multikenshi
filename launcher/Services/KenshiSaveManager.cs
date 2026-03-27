using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace KenshiLauncher.Services;

public class KenshiSaveInfo
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public DateTime LastModified { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>
/// Manages Kenshi game saves (not server session saves).
/// Saves are in %LOCALAPPDATA%/Kenshi/save/
/// </summary>
public static class KenshiSaveManager
{
    private const string MultiplayerSaveName = "multiplayer";

    public static string GetSaveDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kenshi", "save");
    }

    public static List<KenshiSaveInfo> ListSaves()
    {
        var saveDir = GetSaveDir();
        if (!Directory.Exists(saveDir))
            return new List<KenshiSaveInfo>();

        return Directory.GetDirectories(saveDir)
            .Select(d => new KenshiSaveInfo
            {
                Name = Path.GetFileName(d),
                FullPath = d,
                LastModified = Directory.GetLastWriteTime(d),
                SizeBytes = GetDirSize(d)
            })
            .OrderByDescending(s => s.LastModified)
            .ToList();
    }

    /// <summary>
    /// Export a Kenshi save to a zip file for sharing.
    /// </summary>
    public static string ExportSave(string saveName)
    {
        var saveDir = Path.Combine(GetSaveDir(), saveName);
        if (!Directory.Exists(saveDir))
            throw new DirectoryNotFoundException($"Save '{saveName}' not found");

        var exportPath = Path.Combine(Paths.AppData, $"{saveName}.zip");
        if (File.Exists(exportPath)) File.Delete(exportPath);

        ZipFile.CreateFromDirectory(saveDir, exportPath, CompressionLevel.Fastest, false);
        return exportPath;
    }

    /// <summary>
    /// Import a save zip into the Kenshi save directory as "multiplayer".
    /// Overwrites if exists.
    /// </summary>
    public static string ImportSave(string zipPath)
    {
        var targetDir = Path.Combine(GetSaveDir(), MultiplayerSaveName);

        // Clean existing multiplayer save
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, true);

        Directory.CreateDirectory(targetDir);
        ZipFile.ExtractToDirectory(zipPath, targetDir);

        return targetDir;
    }

    /// <summary>
    /// Import save from raw bytes (downloaded from server).
    /// </summary>
    public static string ImportSaveBytes(byte[] zipBytes)
    {
        var tempZip = Path.Combine(Paths.AppData, "incoming_save.zip");
        File.WriteAllBytes(tempZip, zipBytes);

        try
        {
            return ImportSave(tempZip);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private static long GetDirSize(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }
}

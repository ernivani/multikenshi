using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KenshiLauncher.Models;

namespace KenshiLauncher.Services;

public class SaveSummary
{
    public string FolderName { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastModifiedUtc { get; set; }
    public int PlayerCount { get; set; }
    public TimeSpan TotalSessionTime { get; set; }
    public int Port { get; set; }
}

public class SaveManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new TimeSpanConverter() }
    };

    private readonly string _savesRoot;

    public SaveManager()
    {
        var exeDir = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;
        _savesRoot = Path.Combine(exeDir, "saves");
    }

    public List<SaveSummary> ListSaves()
    {
        if (!Directory.Exists(_savesRoot))
            return new List<SaveSummary>();

        var results = new List<SaveSummary>();
        foreach (var dir in Directory.GetDirectories(_savesRoot))
        {
            var jsonPath = Path.Combine(dir, "save.json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var json = File.ReadAllText(jsonPath);
                var data = JsonSerializer.Deserialize<SaveData>(json, JsonOptions);
                if (data == null) continue;

                results.Add(new SaveSummary
                {
                    FolderName = Path.GetFileName(dir),
                    Name = data.Name,
                    LastModifiedUtc = data.LastModifiedUtc,
                    PlayerCount = data.Players.Count,
                    TotalSessionTime = data.TotalSessionTime,
                    Port = data.Server.Port
                });
            }
            catch { /* skip corrupt saves */ }
        }

        return results.OrderByDescending(s => s.LastModifiedUtc).ToList();
    }

    public string CreateSave(string name, int port, int maxPlayers, string password)
    {
        var folderName = SanitizeFolderName(name);
        var folderPath = Path.Combine(_savesRoot, folderName);
        Directory.CreateDirectory(folderPath);

        var data = new SaveData
        {
            Name = name,
            Server = new ServerConfig
            {
                Port = port,
                MaxPlayers = maxPlayers,
                Password = password
            }
        };

        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(Path.Combine(folderPath, "save.json"), json);
        return folderName;
    }

    public SaveData? LoadSave(string folderName)
    {
        var jsonPath = Path.Combine(_savesRoot, folderName, "save.json");
        if (!File.Exists(jsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jsonPath);
            return JsonSerializer.Deserialize<SaveData>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string folderName, SaveData data)
    {
        var folderPath = Path.Combine(_savesRoot, folderName);
        Directory.CreateDirectory(folderPath);

        data.LastModifiedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(Path.Combine(folderPath, "save.json"), json);
    }

    public void DeleteSave(string folderName)
    {
        var folderPath = Path.Combine(_savesRoot, folderName);
        if (Directory.Exists(folderPath))
            Directory.Delete(folderPath, true);
    }

    public string? RenameSave(string oldFolderName, string newName)
    {
        var data = LoadSave(oldFolderName);
        if (data == null) return null;

        var newFolderName = SanitizeFolderName(newName);
        if (newFolderName == oldFolderName)
        {
            // Same folder, just update the display name
            data.Name = newName;
            Save(oldFolderName, data);
            return oldFolderName;
        }

        var oldPath = Path.Combine(_savesRoot, oldFolderName);
        var newPath = Path.Combine(_savesRoot, newFolderName);

        if (Directory.Exists(newPath))
            return null; // target already exists

        Directory.Move(oldPath, newPath);
        data.Name = newName;
        Save(newFolderName, data);
        return newFolderName;
    }

    private string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(sanitized)) sanitized = "save";

        var baseName = sanitized;
        var folderPath = Path.Combine(_savesRoot, sanitized);
        int suffix = 1;
        while (Directory.Exists(folderPath))
        {
            sanitized = $"{baseName}_{suffix}";
            folderPath = Path.Combine(_savesRoot, sanitized);
            suffix++;
        }

        return sanitized;
    }

    /// <summary>
    /// Custom converter for TimeSpan since System.Text.Json doesn't handle it by default.
    /// </summary>
    private class TimeSpanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var str = reader.GetString();
            return str != null ? TimeSpan.Parse(str) : TimeSpan.Zero;
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}

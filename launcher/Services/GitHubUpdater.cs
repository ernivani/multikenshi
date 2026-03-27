using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace KenshiLauncher.Services;

public static class GitHubUpdater
{
    private const string Repo = "ernivani/multikenshi";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string DllName = "kenshi_multiplayer.dll";
    private const string ModName = "kenshi-online.mod";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static GitHubUpdater()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MultiKenshi", "0.4"));
    }

    /// <summary>
    /// Check GitHub releases for updates and download DLL + mod if needed.
    /// Returns (dllUpdated, modUpdated, message).
    /// </summary>
    public static async Task<(bool DllUpdated, bool ModUpdated, string Message)> CheckAndUpdate(
        Action<string>? log = null)
    {
        bool dllUpdated = false;
        bool modUpdated = false;

        try
        {
            log?.Invoke("Checking for updates...");

            var json = await _http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<JsonElement>(json);

            var tag = release.GetProperty("tag_name").GetString() ?? "";
            log?.Invoke($"Latest release: {tag}");

            // Find assets
            var assets = release.GetProperty("assets");
            string? dllUrl = null;
            string? modUrl = null;
            long dllSize = 0;
            long modSize = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var size = asset.GetProperty("size").GetInt64();

                if (name == DllName) { dllUrl = url; dllSize = size; }
                if (name == ModName) { modUrl = url; modSize = size; }
            }

            // Download DLL if missing or different size
            var launcherDir = Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "")!;

            if (dllUrl != null)
            {
                var localDll = Path.Combine(launcherDir, DllName);
                bool needDownload = !File.Exists(localDll);
                if (!needDownload && dllSize > 0)
                {
                    var localSize = new FileInfo(localDll).Length;
                    needDownload = localSize != dllSize;
                }

                if (needDownload)
                {
                    log?.Invoke("Downloading DLL...");
                    var bytes = await _http.GetByteArrayAsync(dllUrl);
                    File.WriteAllBytes(localDll, bytes);
                    DllIntegrity.WriteHash(localDll);
                    dllUpdated = true;
                    log?.Invoke($"DLL updated ({bytes.Length / 1024}KB).");
                }
            }

            // Download mod if missing or different size
            if (modUrl != null)
            {
                var localMod = Path.Combine(launcherDir, ModName);
                bool needDownload = !File.Exists(localMod);
                if (!needDownload && modSize > 0)
                {
                    var localSize = new FileInfo(localMod).Length;
                    needDownload = localSize != modSize;
                }

                if (needDownload)
                {
                    log?.Invoke("Downloading mod...");
                    var bytes = await _http.GetByteArrayAsync(modUrl);
                    File.WriteAllBytes(localMod, bytes);
                    modUpdated = true;
                    log?.Invoke($"Mod updated ({bytes.Length / 1024}KB).");
                }
            }

            if (!dllUpdated && !modUpdated)
                return (false, false, "Up to date.");

            return (dllUpdated, modUpdated, "Updated successfully.");
        }
        catch (Exception ex)
        {
            return (false, false, $"Update check failed: {ex.Message}");
        }
    }
}

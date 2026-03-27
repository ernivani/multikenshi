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
    /// Returns true if we're running in dev mode (dotnet run / Debug build).
    /// In dev mode, local build files take priority over GitHub downloads.
    /// </summary>
    public static bool IsDevMode()
    {
        // Check if local C++ build output exists (dev has the repo)
        var dir = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
        while (dir != null)
        {
            var buildDll = Path.Combine(dir, "kenshi_multiplayer", "x64", "Release", DllName);
            if (File.Exists(buildDll)) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    /// <summary>
    /// Check GitHub releases for updates and download DLL + mod if needed.
    /// Skips download in dev mode (uses local build files instead).
    /// Returns (dllUpdated, modUpdated, message).
    /// </summary>
    public static async Task<(bool DllUpdated, bool ModUpdated, string Message)> CheckAndUpdate(
        Action<string>? log = null)
    {
        // Dev mode: skip GitHub, use local files
        if (IsDevMode())
        {
            log?.Invoke("Dev mode — using local build files.");
            return (false, false, "Dev mode");
        }

        try
        {
            log?.Invoke("Checking for updates...");

            var json = await _http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<JsonElement>(json);

            var tag = release.GetProperty("tag_name").GetString() ?? "";
            log?.Invoke($"Latest release: {tag}");

            // Find asset URLs
            var assets = release.GetProperty("assets");
            string? dllUrl = null, modUrl = null;
            long dllSize = 0, modSize = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var size = asset.GetProperty("size").GetInt64();

                if (name == DllName) { dllUrl = url; dllSize = size; }
                if (name == ModName) { modUrl = url; modSize = size; }
            }

            var launcherDir = Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "")!;

            // Check what needs downloading
            bool needDll = false, needMod = false;

            if (dllUrl != null)
            {
                var localDll = Path.Combine(launcherDir, DllName);
                needDll = !File.Exists(localDll) ||
                          (dllSize > 0 && new FileInfo(localDll).Length != dllSize);
            }

            if (modUrl != null)
            {
                var localMod = Path.Combine(launcherDir, ModName);
                needMod = !File.Exists(localMod) ||
                          (modSize > 0 && new FileInfo(localMod).Length != modSize);
            }

            if (!needDll && !needMod)
                return (false, false, "Up to date.");

            // Download in parallel
            var tasks = new System.Collections.Generic.List<Task>();
            bool dllUpdated = false, modUpdated = false;

            if (needDll && dllUrl != null)
            {
                log?.Invoke("Downloading DLL...");
                tasks.Add(Task.Run(async () =>
                {
                    var bytes = await _http.GetByteArrayAsync(dllUrl);
                    var localDll = Path.Combine(launcherDir, DllName);
                    File.WriteAllBytes(localDll, bytes);
                    DllIntegrity.WriteHash(localDll);
                    dllUpdated = true;
                }));
            }

            if (needMod && modUrl != null)
            {
                log?.Invoke("Downloading mod...");
                tasks.Add(Task.Run(async () =>
                {
                    var bytes = await _http.GetByteArrayAsync(modUrl);
                    File.WriteAllBytes(Path.Combine(launcherDir, ModName), bytes);
                    modUpdated = true;
                }));
            }

            await Task.WhenAll(tasks);

            var parts = new System.Collections.Generic.List<string>();
            if (dllUpdated) parts.Add("DLL");
            if (modUpdated) parts.Add("mod");
            var msg = $"Downloaded {string.Join(" + ", parts)}.";
            log?.Invoke(msg);

            return (dllUpdated, modUpdated, msg);
        }
        catch (Exception ex)
        {
            return (false, false, $"Update check failed: {ex.Message}");
        }
    }
}

using System;
using System.Diagnostics;
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

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static GitHubUpdater()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MultiKenshi", Program.Version));
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
    public static async Task<(bool DllUpdated, string Message)> CheckAndUpdate(
        Action<string>? log = null)
    {
        if (IsDevMode())
        {
            log?.Invoke("Dev mode — using local build files.");
            return (false, "Dev mode");
        }

        try
        {
            log?.Invoke("Checking for updates...");

            var json = await _http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<JsonElement>(json);

            var tag = release.GetProperty("tag_name").GetString() ?? "";
            log?.Invoke($"Latest release: {tag}");

            string? dllUrl = null;
            long dllSize = 0;

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name == DllName)
                {
                    dllUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    dllSize = asset.GetProperty("size").GetInt64();
                }
            }

            if (dllUrl == null)
                return (false, "No DLL in release.");

            var launcherDir = Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "")!;
            var localDll = Path.Combine(launcherDir, DllName);

            bool needDll = !File.Exists(localDll) ||
                           (dllSize > 0 && new FileInfo(localDll).Length != dllSize);

            if (!needDll)
                return (false, "Up to date.");

            log?.Invoke("Downloading DLL...");
            var bytes = await _http.GetByteArrayAsync(dllUrl);
            File.WriteAllBytes(localDll, bytes);
            DllIntegrity.WriteHash(localDll);

            log?.Invoke($"DLL updated ({bytes.Length / 1024}KB).");
            return (true, "DLL updated.");
        }
        catch (Exception ex)
        {
            return (false, $"Update check failed: {ex.Message}");
        }
    }

    private const string LauncherName = "KenshiLauncher.exe";

    /// <summary>
    /// Check if a newer launcher is available on GitHub.
    /// If yes, download it, swap the exe, and restart.
    /// Call this at startup before anything else.
    /// </summary>
    public static async Task<bool> CheckLauncherUpdate(Action<string>? log = null)
    {
        if (IsDevMode()) return false;

        try
        {
            var json = await _http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<JsonElement>(json);
            var assets = release.GetProperty("assets");

            string? launcherUrl = null;
            long remoteSize = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name == LauncherName)
                {
                    launcherUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    remoteSize = asset.GetProperty("size").GetInt64();
                    break;
                }
            }

            if (launcherUrl == null) return false;

            // Compare version tag with local version
            var remoteTag = release.GetProperty("tag_name").GetString() ?? "";
            var remoteVersion = remoteTag.TrimStart('v');
            if (remoteVersion == Program.Version)
            {
                log?.Invoke($"Launcher v{Program.Version} is up to date.");
                return false;
            }

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe == null) return false;

            log?.Invoke($"Updating launcher: v{Program.Version} -> v{remoteVersion}...");
            var bytes = await _http.GetByteArrayAsync(launcherUrl);

            // Swap: rename current exe to .old, write new exe, restart
            var dir = Path.GetDirectoryName(currentExe)!;
            var newExe = Path.Combine(dir, LauncherName + ".new");
            var oldExe = currentExe + ".old";

            File.WriteAllBytes(newExe, bytes);

            // On Windows you can rename a running exe but not overwrite it
            try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
            File.Move(currentExe, oldExe);
            File.Move(newExe, currentExe);

            log?.Invoke("Launcher updated — restarting...");

            // Launch new version with our PID so it can kill us
            var myPid = Process.GetCurrentProcess().Id;
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                Arguments = $"--updated-from {myPid}",
                UseShellExecute = true
            });

            return true; // caller should exit
        }
        catch (Exception ex)
        {
            log?.Invoke($"Launcher update check failed: {ex.Message}");
            return false;
        }
    }
}

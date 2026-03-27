using System;
using System.Diagnostics;
using System.IO;
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
    private const string LauncherName = "KenshiLauncher.exe";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static GitHubUpdater()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MultiKenshi", Program.Version));
    }

    // ---- Dev mode detection ----

    public static bool IsDevMode()
    {
        var dir = Path.GetDirectoryName(
            Process.GetCurrentProcess().MainModule?.FileName ?? "");
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "kenshi_multiplayer", "x64", "Release", DllName)))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    // ---- Fetch release info (cached per session) ----

    private static JsonElement? _cachedRelease;
    private static string _cachedTag = "";

    private static async Task<(string tag, JsonElement release)?> FetchRelease(Action<string>? log)
    {
        if (_cachedRelease != null)
            return (_cachedTag, _cachedRelease.Value);

        try
        {
            log?.Invoke("[Update] Fetching latest release...");
            var json = await _http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<JsonElement>(json);
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            log?.Invoke($"[Update] Latest release: {tag}");

            _cachedRelease = release;
            _cachedTag = tag;
            return (tag, release);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Update] ERROR fetching release: {ex.Message}");
            return null;
        }
    }

    private static (string? url, long size) FindAsset(JsonElement release, string assetName)
    {
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name == assetName)
            {
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                var size = asset.GetProperty("size").GetInt64();
                return (url, size);
            }
        }
        return (null, 0);
    }

    // ---- DLL Update ----

    /// <summary>
    /// Get the installed DLL version tag (e.g., "v0.5.2"). Empty if not installed.
    /// </summary>
    public static string GetInstalledDllVersion()
    {
        var versionFile = Paths.DllPath + ".version";
        if (File.Exists(versionFile))
            return File.ReadAllText(versionFile).Trim();
        return "";
    }

    public static async Task<(bool Updated, string Message)> UpdateDll(Action<string>? log = null)
    {
        if (IsDevMode())
        {
            log?.Invoke("[DLL] Dev mode — using local build.");
            return (false, "Dev mode");
        }

        var info = await FetchRelease(log);
        if (info == null) return (false, "Cannot reach GitHub");

        var (tag, release) = info.Value;
        var (dllUrl, dllSize) = FindAsset(release, DllName);

        if (dllUrl == null)
        {
            log?.Invoke("[DLL] No DLL asset in release.");
            return (false, "No DLL in release");
        }

        var localDll = Paths.DllPath;
        var localVersion = GetInstalledDllVersion();

        log?.Invoke($"[DLL] Local: '{localVersion}' | Remote: '{tag}' | Exists: {File.Exists(localDll)}");

        if (File.Exists(localDll) && localVersion == tag)
        {
            log?.Invoke("[DLL] Up to date.");
            return (false, "Up to date");
        }

        // Download
        log?.Invoke($"[DLL] Downloading {tag} ({dllSize / 1024}KB)...");
        try
        {
            var bytes = await _http.GetByteArrayAsync(dllUrl);

            // Validate
            if (bytes.Length < 1024)
            {
                log?.Invoke("[DLL] ERROR: Downloaded file too small, aborting.");
                return (false, "Download corrupted");
            }

            // Write
            Directory.CreateDirectory(Path.GetDirectoryName(localDll)!);
            File.WriteAllBytes(localDll, bytes);
            DllIntegrity.WriteHash(localDll);
            File.WriteAllText(localDll + ".version", tag);

            // Verify
            if (!File.Exists(localDll) || new FileInfo(localDll).Length != bytes.Length)
            {
                log?.Invoke("[DLL] ERROR: Write verification failed.");
                return (false, "Write failed");
            }

            log?.Invoke($"[DLL] Updated to {tag} ({bytes.Length / 1024}KB). Verified OK.");
            return (true, $"DLL updated to {tag}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[DLL] Download failed: {ex.Message}");
            return (false, $"Download failed: {ex.Message}");
        }
    }

    // ---- Launcher Update ----

    public static async Task<bool> UpdateLauncher(Action<string>? log = null)
    {
        if (IsDevMode()) return false;

        var info = await FetchRelease(log);
        if (info == null) return false;

        var (tag, release) = info.Value;
        var (launcherUrl, remoteSize) = FindAsset(release, LauncherName);

        if (launcherUrl == null)
        {
            log?.Invoke("[Launcher] No launcher asset in release.");
            return false;
        }

        // Compare version string (not file size)
        var remoteVersion = tag.TrimStart('v');
        log?.Invoke($"[Launcher] Local: v{Program.Version} | Remote: v{remoteVersion}");

        if (remoteVersion == Program.Version)
        {
            log?.Invoke("[Launcher] Up to date.");
            return false;
        }

        var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
        if (currentExe == null) return false;

        log?.Invoke($"[Launcher] Downloading v{remoteVersion} ({remoteSize / 1024}KB)...");
        try
        {
            var bytes = await _http.GetByteArrayAsync(launcherUrl);

            if (bytes.Length < 1024)
            {
                log?.Invoke("[Launcher] ERROR: Downloaded file too small.");
                return false;
            }

            // Swap exe
            var dir = Path.GetDirectoryName(currentExe)!;
            var newExe = Path.Combine(dir, LauncherName + ".new");
            var oldExe = currentExe + ".old";

            File.WriteAllBytes(newExe, bytes);

            try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
            File.Move(currentExe, oldExe);
            File.Move(newExe, currentExe);

            log?.Invoke($"[Launcher] Updated v{Program.Version} -> v{remoteVersion}. Restarting...");

            var myPid = Process.GetCurrentProcess().Id;
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                Arguments = $"--updated-from {myPid}",
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Launcher] Update failed: {ex.Message}");
            return false;
        }
    }
}

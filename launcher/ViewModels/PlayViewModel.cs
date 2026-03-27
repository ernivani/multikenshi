using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class PlayViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly MainViewModel _main;
    private readonly RelayServer _server;
    private readonly HostViewModel _host;
    private readonly SaveManager _saveManager;
    private Process? _kenshiProcess;
    private Timer? _processTimer;

    // DLL status
    [ObservableProperty]
    private DllStatus _dllStatus;

    // Modal state
    [ObservableProperty]
    private bool _isJoinModalOpen;

    [ObservableProperty]
    private bool _isHostModalOpen;

    // Save picker modal
    [ObservableProperty]
    private bool _isSavePickerOpen;

    [ObservableProperty]
    private SaveSummaryViewModel? _selectedSaveForHost;

    public ObservableCollection<SaveSummaryViewModel> SavePickerSaves { get; } = new();

    // Join modal fields
    [ObservableProperty]
    private string _joinIP;

    [ObservableProperty]
    private string _joinPort;

    [ObservableProperty]
    private string _joinPassword;

    // Host modal fields
    [ObservableProperty]
    private string _hostPort;

    [ObservableProperty]
    private string _hostMaxPlayers;

    [ObservableProperty]
    private string _hostPassword;

    // Play state
    [ObservableProperty]
    private bool _isPlaying;

    // Install feedback (shown inline below Install button)
    [ObservableProperty]
    private string _installMessage = "";

    [ObservableProperty]
    private bool _isInstalling;

    public string StatusText
    {
        get
        {
            return DllStatus switch
            {
                DllStatus.Ready => "Ready to play",
                DllStatus.Outdated => "Update available",
                DllStatus.Missing => "DLL not found",
                DllStatus.Corrupted => "DLL corrupted",
                _ => ""
            };
        }
    }

    public string StatusSubText
    {
        get
        {
            return DllStatus switch
            {
                DllStatus.Ready => "kenshi_multiplayer.dll detected",
                DllStatus.Outdated => "A newer version is available",
                DllStatus.Missing => "Install the multiplayer DLL to play",
                DllStatus.Corrupted => "DLL corrupted — reinstall to fix",
                _ => ""
            };
        }
    }

    public bool IsPlayReady => DllStatus == DllStatus.Ready || DllStatus == DllStatus.Outdated;
    public bool ShowPlay => !IsPlaying && DllStatus == DllStatus.Ready;
    public bool ShowUpdate => !IsPlaying && DllStatus == DllStatus.Outdated;
    public bool ShowInstall => !IsPlaying && !IsPlayReady;
    public bool ShowStop => IsPlaying;

    public ObservableCollection<ChangelogEntry> Changelog { get; } = new();

    public PlayViewModel(ConfigManager config, MainViewModel main, RelayServer server, HostViewModel host, SaveManager saveManager)
    {
        _config = config;
        _main = main;
        _server = server;
        _host = host;
        _saveManager = saveManager;

        _joinIP = config.ClientIP;
        _joinPort = config.ClientPort;
        _joinPassword = "";
        _hostPort = config.ServerPort;
        _hostMaxPlayers = "8";
        _hostPassword = "";

        RefreshDllStatus();
        LoadChangelog();
    }

    partial void OnDllStatusChanged(DllStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusSubText));
        OnPropertyChanged(nameof(IsPlayReady));
        NotifyButtonVisibility();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        NotifyButtonVisibility();
    }

    private void NotifyButtonVisibility()
    {
        OnPropertyChanged(nameof(ShowPlay));
        OnPropertyChanged(nameof(ShowUpdate));
        OnPropertyChanged(nameof(ShowInstall));
        OnPropertyChanged(nameof(ShowStop));
    }

    private void LoadChangelog()
    {
        Changelog.Add(new ChangelogEntry
        {
            Version = "0.2",
            Date = "today",
            Lines = new List<ChangelogLine>
            {
                new("new", "Peer-to-peer session system rewrite"),
                new("new", "Steam identity passed to session handshake"),
                new("fix", "Desync on squad movement across zone boundaries"),
                new("fix", "DLL injection stability on Kenshi 1.0.55"),
            }
        });
        Changelog.Add(new ChangelogEntry
        {
            Version = "0.1",
            Date = "3w ago",
            Lines = new List<ChangelogLine>
            {
                new("new", "Initial DLL injector and hook system"),
                new("new", "Host / join flow with direct IP"),
                new("wip", "Voice chat \u2014 not yet functional"),
            }
        });
    }

    [RelayCommand]
    private void OpenJoinModal()
    {
        IsJoinModalOpen = true;
    }

    [RelayCommand]
    private void CloseJoinModal()
    {
        IsJoinModalOpen = false;
    }

    [RelayCommand]
    private void OpenHostModal()
    {
        IsHostModalOpen = true;
    }

    [RelayCommand]
    private void CloseHostModal()
    {
        IsHostModalOpen = false;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsPlaying) return;

        IsJoinModalOpen = false;

        _config.ClientIP = JoinIP;
        _config.ClientPort = JoinPort;
        _config.Save();

        if (string.IsNullOrWhiteSpace(_config.KenshiPath))
        {
            _main.PostLog("ERROR: Set the Kenshi path in Settings first.");
            return;
        }

        IsPlaying = true;
        bool injected = false;

        try
        {
            await Task.Run(async () =>
            {
                var (sName, sId) = SteamIdentity.GetCurrentUser();
                GameConfigWriter.Write(_config.KenshiPath, JoinIP, JoinPort, sName, sId);

                // Auto-download DLL + mod from GitHub releases if needed
                _main.PostLog($"MultiKenshi v{Program.Version}");
                var (dllUp, modUp, updateMsg) = await GitHubUpdater.CheckAndUpdate(_main.PostLog);
                if (dllUp || modUp)
                    _main.PostLog(updateMsg);

                // Install mod to Kenshi's mods directory (copies from launcher dir)
                GameConfigWriter.EnsureMod(_config.KenshiPath);

                var process = ProcessLauncher.FindKenshiProcess();
                int pid;

                if (process != null)
                {
                    pid = process.Id;
                    _main.PostLog($"Found running Kenshi (PID {pid}).");
                }
                else
                {
                    _main.PostLog("Launching Kenshi...");
                    process = ProcessLauncher.LaunchKenshi(_config.KenshiPath);
                    if (process == null)
                    {
                        _main.PostLog("ERROR: kenshi_x64.exe not found in the specified path.");
                        return;
                    }

                    pid = process.Id;
                    _main.PostLog($"Kenshi started (PID {pid}). Waiting 5s...");
                    await Task.Delay(5000);
                }

                var dllPath = ProcessLauncher.GetDllPath();
                if (!System.IO.File.Exists(dllPath))
                {
                    _main.PostLog("ERROR: kenshi_multiplayer.dll not found next to launcher.");
                    return;
                }

                _main.PostLog("Injecting DLL...");
                if (DllInjector.Inject(pid, dllPath, _main.PostLog))
                {
                    _main.PostLog("DLL injected successfully!");
                    _kenshiProcess = process;
                    injected = true;
                }
                else
                {
                    _main.PostLog("DLL injection failed.");
                }
            });
        }
        finally
        {
            if (injected)
                StartProcessMonitor();
            else
                IsPlaying = false;
        }
    }

    [RelayCommand]
    private void StopGame()
    {
        bool killed = false;

        // Try the stored handle first
        if (_kenshiProcess != null && !_kenshiProcess.HasExited)
        {
            try
            {
                _kenshiProcess.Kill();
                killed = true;
            }
            catch { }
        }

        // Fallback: find by name (handles the case where stored handle is stale)
        if (!killed)
        {
            var proc = ProcessLauncher.FindKenshiProcess();
            if (proc != null)
            {
                try
                {
                    proc.Kill();
                    killed = true;
                }
                catch (System.Exception ex)
                {
                    _main.PostLog($"ERROR: Could not stop Kenshi — {ex.Message}");
                    _main.CopyToClipboard(ex.ToString());
                }
            }
        }

        if (killed)
            _main.PostLog("Kenshi process terminated.");

        CleanupProcess();
    }

    private void StartProcessMonitor()
    {
        _processTimer?.Dispose();
        _processTimer = new Timer(_ =>
        {
            bool exited = _kenshiProcess == null || _kenshiProcess.HasExited;
            if (!exited)
            {
                // Also check by name in case our handle went stale
                exited = ProcessLauncher.FindKenshiProcess() == null;
            }

            if (exited)
            {
                // Read crash log before posting to UI
                string? crashInfo = ReadCrashLog();

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (crashInfo != null)
                    {
                        _main.PostLog("Kenshi CRASHED:");
                        _main.PostLog(crashInfo);
                        _main.CopyToClipboard(crashInfo);
                    }
                    else
                    {
                        _main.PostLog("Kenshi has exited.");
                    }
                    CleanupProcess();
                });
            }
        }, null, 2000, 2000);
    }

    private void CleanupProcess()
    {
        _processTimer?.Dispose();
        _processTimer = null;
        _kenshiProcess = null;
        IsPlaying = false;
    }

    private string? ReadCrashLog()
    {
        if (string.IsNullOrEmpty(_config.KenshiPath)) return null;

        var logPath = System.IO.Path.Combine(_config.KenshiPath, "kenshi_mp_crash.log");
        if (!System.IO.File.Exists(logPath)) return null;

        try
        {
            var content = System.IO.File.ReadAllText(logPath);
            if (content.Contains("=== CRASH ==="))
            {
                // Extract from CRASH marker onwards
                int idx = content.IndexOf("=== CRASH ===");
                return content.Substring(idx).Trim();
            }
        }
        catch { }

        return null;
    }

    [RelayCommand]
    private void StartServer()
    {
        IsHostModalOpen = false;

        _config.ServerPort = HostPort;
        _config.Save();

        // Populate save picker
        SavePickerSaves.Clear();
        foreach (var s in _saveManager.ListSaves())
            SavePickerSaves.Add(new SaveSummaryViewModel(s));

        SelectedSaveForHost = null;
        IsSavePickerOpen = true;
    }

    [RelayCommand]
    private void ConfirmSavePick()
    {
        IsSavePickerOpen = false;

        string folderName;
        if (SelectedSaveForHost != null)
        {
            folderName = SelectedSaveForHost.FolderName;
        }
        else
        {
            // Create new save
            var name = $"Session {DateTime.Now:yyyy-MM-dd HH.mm}";
            int port = int.TryParse(HostPort, out var p) ? p : 8080;
            int max = int.TryParse(HostMaxPlayers, out var m) ? m : 8;
            folderName = _saveManager.CreateSave(name, port, max, HostPassword);
        }

        _host.LoadSave(folderName);
        _host.ServerPort = HostPort;
        _host.ToggleServerCommand.Execute(null);

        if (_host.IsRunning)
        {
            var window = new Views.HostWindow { DataContext = _host };
            window.Show();
        }
    }

    [RelayCommand]
    private void CancelSavePick()
    {
        IsSavePickerOpen = false;
    }

    [RelayCommand]
    private async Task InstallDll()
    {
        IsInstalling = true;
        InstallMessage = "Checking for updates...";

        // Try downloading from GitHub first
        var (dllUp, _, msg) = await GitHubUpdater.CheckAndUpdate(_main.PostLog);
        if (dllUp)
        {
            InstallMessage = "Downloaded from GitHub";
            RefreshDllStatus();
            _main.Settings.RefreshStatus();
            IsInstalling = false;
            ClearInstallMessageAfterDelay();
            return;
        }

        InstallMessage = "Searching for DLL...";
        var dest = ProcessLauncher.GetDllPath();
        var source = FindLocalDll(dest);

        if (source == null)
        {
            InstallMessage = $"DLL not found — {msg}";
            IsInstalling = false;
            return;
        }

        try
        {
            InstallMessage = "Copying DLL...";
            var oldPath = dest + ".old";

            // If the DLL is locked (e.g. injected into Kenshi), rename it first
            if (System.IO.File.Exists(dest))
            {
                try
                {
                    if (System.IO.File.Exists(oldPath))
                        System.IO.File.Delete(oldPath);
                }
                catch { }

                try
                {
                    System.IO.File.Move(dest, oldPath);
                }
                catch
                {
                    // Move failed too — file is truly locked, try direct overwrite as last resort
                }
            }

            System.IO.File.Copy(source, dest, overwrite: true);
            Services.DllIntegrity.WriteHash(dest);

            // Clean up .old file
            try { if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath); }
            catch { /* still locked, will be cleaned up next time */ }

            InstallMessage = "Installed successfully";
            _main.PostLog($"Installed DLL from {source}");
            RefreshDllStatus();
            _main.Settings.RefreshStatus();
            ClearInstallMessageAfterDelay();
        }
        catch (System.Exception ex)
        {
            InstallMessage = $"Failed: {ex.Message}";
            _main.PostLog($"ERROR: Failed to copy DLL {ex.Message}");
            _main.CopyToClipboard(ex.ToString());
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async void ClearInstallMessageAfterDelay()
    {
        await Task.Delay(3000);
        InstallMessage = "";
    }

    private string? FindLocalDll(string? excludePath = null)
    {
        bool IsExcluded(string path) =>
            excludePath != null && string.Equals(
                System.IO.Path.GetFullPath(path),
                System.IO.Path.GetFullPath(excludePath),
                System.StringComparison.OrdinalIgnoreCase);

        // 1. Check the vcxproj output location (Kenshi directory)
        if (!string.IsNullOrEmpty(_config.KenshiPath))
        {
            var kenshiDll = System.IO.Path.Combine(_config.KenshiPath, "kenshi_multiplayer.dll");
            if (System.IO.File.Exists(kenshiDll) && !IsExcluded(kenshiDll))
                return kenshiDll;
        }

        // 2. Walk up from the launcher exe to find the repo,
        //    check C++ build output + any manual drops.
        var dir = System.IO.Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);

        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(
                dir, "kenshi_multiplayer", "x64", "Release", "kenshi_multiplayer.dll");
            if (System.IO.File.Exists(candidate) && !IsExcluded(candidate))
                return candidate;

            var direct = System.IO.Path.Combine(dir, "kenshi_multiplayer.dll");
            if (System.IO.File.Exists(direct) && !IsExcluded(direct))
                return direct;

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return null;
    }

    public void RefreshDllStatus()
    {
        var dllPath = ProcessLauncher.GetDllPath();
        var (valid, reason) = Services.DllIntegrity.Verify(dllPath);

        if (!valid)
        {
            DllStatus = reason == "missing" ? DllStatus.Missing : DllStatus.Corrupted;
            return;
        }

        // Check if a newer build exists in the C++ build output
        var buildDll = FindBuildOutputDll();
        if (buildDll != null)
        {
            try
            {
                var installedHash = Services.DllIntegrity.ComputeHash(dllPath);
                var buildHash = Services.DllIntegrity.ComputeHash(buildDll);
                if (!string.Equals(installedHash, buildHash, System.StringComparison.OrdinalIgnoreCase))
                {
                    DllStatus = DllStatus.Outdated;
                    return;
                }
            }
            catch { }
        }

        DllStatus = DllStatus.Ready;
    }

    private string? FindBuildOutputDll()
    {
        // Walk up from the launcher exe to find kenshi_multiplayer/x64/Release/
        var dir = System.IO.Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);

        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(
                dir, "kenshi_multiplayer", "x64", "Release", "kenshi_multiplayer.dll");
            if (System.IO.File.Exists(candidate))
                return candidate;

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return null;
    }
}

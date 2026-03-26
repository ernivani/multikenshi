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

    public ObservableCollection<ChangelogEntry> Changelog { get; } = new();

    public PlayViewModel(ConfigManager config, MainViewModel main, RelayServer server, HostViewModel host)
    {
        _config = config;
        _main = main;
        _server = server;
        _host = host;

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
                GameConfigWriter.Write(_config.KenshiPath, JoinIP, JoinPort);

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
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _main.PostLog("Kenshi has exited.");
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

    [RelayCommand]
    private void StartServer()
    {
        IsHostModalOpen = false;

        _config.ServerPort = HostPort;
        _config.Save();

        // Delegate to HostViewModel's toggle logic
        _host.ServerPort = HostPort;
        _host.ToggleServerCommand.Execute(null);

        // Open the dedicated server window
        if (_host.IsRunning)
        {
            var window = new Views.HostWindow { DataContext = _host };
            window.Show();
        }
    }

    [RelayCommand]
    private void InstallDll()
    {
        IsInstalling = true;
        InstallMessage = "Searching for DLL...";

        var dest = ProcessLauncher.GetDllPath();
        var source = FindLocalDll();

        if (source == null)
        {
            InstallMessage = "DLL not found — build the C++ project first";
            IsInstalling = false;
            return;
        }

        try
        {
            InstallMessage = "Copying DLL...";
            System.IO.File.Copy(source, dest, overwrite: true);
            Services.DllIntegrity.WriteHash(dest);
            InstallMessage = "Installed successfully";
            _main.PostLog($"Installed DLL from {source}");
            RefreshDllStatus();
            _main.Settings.RefreshStatus();
            ClearInstallMessageAfterDelay();
        }
        catch (System.Exception ex)
        {
            InstallMessage = $"Failed: {ex.Message}";
            _main.PostLog($"ERROR: Failed to copy DLL — {ex.Message}");
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

    private string? FindLocalDll()
    {
        // 1. Check the vcxproj output location (Kenshi directory)
        if (!string.IsNullOrEmpty(_config.KenshiPath))
        {
            var kenshiDll = System.IO.Path.Combine(_config.KenshiPath, "kenshi_multiplayer.dll");
            if (System.IO.File.Exists(kenshiDll))
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
            if (System.IO.File.Exists(candidate))
                return candidate;

            var direct = System.IO.Path.Combine(dir, "kenshi_multiplayer.dll");
            if (System.IO.File.Exists(direct))
                return direct;

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return null;
    }

    public void RefreshDllStatus()
    {
        var dllPath = ProcessLauncher.GetDllPath();
        var (valid, reason) = Services.DllIntegrity.Verify(dllPath);

        if (valid)
            DllStatus = DllStatus.Ready;
        else if (reason == "missing")
            DllStatus = DllStatus.Missing;
        else
            DllStatus = DllStatus.Corrupted;
    }
}

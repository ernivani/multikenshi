using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
                }
                else
                {
                    _main.PostLog("DLL injection failed.");
                }
            });
        }
        finally
        {
            IsPlaying = false;
        }
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
            InstallMessage = "Installed successfully";
            _main.PostLog($"Installed DLL from {source}");
            RefreshDllStatus();
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
        DllStatus = ProcessLauncher.DllExists() ? DllStatus.Ready : DllStatus.Missing;
    }
}

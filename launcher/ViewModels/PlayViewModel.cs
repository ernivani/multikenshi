using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class PlayViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly MainViewModel _main;

    [ObservableProperty]
    private string _clientIP;

    [ObservableProperty]
    private string _clientPort;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _dllExists;

    public PlayViewModel(ConfigManager config, MainViewModel main)
    {
        _config = config;
        _main = main;
        _clientIP = config.ClientIP;
        _clientPort = config.ClientPort;
        _dllExists = ProcessLauncher.DllExists();
    }

    [RelayCommand]
    private async Task PlayMultiplayerAsync()
    {
        if (IsPlaying) return;

        _config.ClientIP = ClientIP;
        _config.ClientPort = ClientPort;
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
                // Write config for the DLL
                GameConfigWriter.Write(_config.KenshiPath, ClientIP, ClientPort);

                // Check if Kenshi is already running
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

                // Check DLL exists
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
    private async Task SingleplayerAsync()
    {
        if (IsPlaying) return;

        _config.Save();

        if (string.IsNullOrWhiteSpace(_config.KenshiPath))
        {
            _main.PostLog("ERROR: Set the Kenshi path in Settings first.");
            return;
        }

        IsPlaying = true;

        try
        {
            await Task.Run(() =>
            {
                _main.PostLog("Launching Kenshi (singleplayer)...");
                var process = ProcessLauncher.FindKenshiProcess();

                if (process != null)
                {
                    _main.PostLog($"Kenshi already running (PID {process.Id}).");
                }
                else
                {
                    process = ProcessLauncher.LaunchKenshi(_config.KenshiPath);
                    if (process != null)
                    {
                        _main.PostLog($"Kenshi started (PID {process.Id}).");
                    }
                    else
                    {
                        _main.PostLog("ERROR: Failed to launch Kenshi.");
                    }
                }
            });
        }
        finally
        {
            IsPlaying = false;
        }
    }

    public void RefreshDllStatus()
    {
        DllExists = ProcessLauncher.DllExists();
    }
}

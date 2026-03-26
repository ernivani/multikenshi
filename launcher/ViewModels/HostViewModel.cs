using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class HostViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly RelayServer _server;
    private readonly MainViewModel _main;
    private Timer? _statusTimer;

    [ObservableProperty]
    private string _serverPort;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private int _playerCount;

    [ObservableProperty]
    private string _commandText = "";

    public ObservableCollection<ClientInfo> Clients => _server.Clients;
    public ObservableCollection<string> ServerLog => _server.ServerLog;

    public HostViewModel(ConfigManager config, RelayServer server, MainViewModel main)
    {
        _config = config;
        _server = server;
        _main = main;
        _serverPort = config.ServerPort;

        // Poll server status periodically
        _statusTimer = new Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsRunning = _server.IsRunning;
                PlayerCount = _server.PlayerCount;
            });
        }, null, 0, 1000);
    }

    [RelayCommand]
    public void ToggleServer()
    {
        if (_server.IsRunning)
        {
            _server.Stop();
            IsRunning = false;
        }
        else
        {
            _config.ServerPort = ServerPort;
            _config.Save();

            if (!int.TryParse(ServerPort, out int port) || port <= 0 || port > 65535)
            {
                _main.PostLog("ERROR: Invalid server port.");
                return;
            }

            _server.Start(port);
            IsRunning = true;
        }
    }

    [RelayCommand]
    private void SendCommand()
    {
        if (string.IsNullOrWhiteSpace(CommandText)) return;
        _server.ExecuteCommand(CommandText);
        CommandText = "";
    }
}

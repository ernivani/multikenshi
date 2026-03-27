using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Models;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class PlayerRecordViewModel : ObservableObject
{
    public string DisplayName { get; }
    public string Faction { get; }
    public string LastSeenText { get; }
    public string PlaytimeText { get; }

    [ObservableProperty]
    private bool _isOnline;

    public PlayerRecordViewModel(PlayerRecord record)
    {
        DisplayName = string.IsNullOrEmpty(record.Name) ? $"Slot {record.SlotId}" : record.Name;
        Faction = record.Faction;
        IsOnline = record.IsOnline;

        var pt = record.TotalPlaytime;
        if (pt.TotalHours >= 1)
            PlaytimeText = $"{pt.TotalHours:F1}h";
        else if (pt.TotalMinutes >= 1)
            PlaytimeText = $"{pt.TotalMinutes:F0}m";
        else
            PlaytimeText = "< 1m";

        if (record.IsOnline)
        {
            LastSeenText = "now";
        }
        else
        {
            var ago = DateTime.UtcNow - record.LastSeen;
            if (ago.TotalMinutes < 1)
                LastSeenText = "just now";
            else if (ago.TotalHours < 1)
                LastSeenText = $"{ago.TotalMinutes:F0}m ago";
            else if (ago.TotalDays < 1)
                LastSeenText = $"{ago.TotalHours:F0}h ago";
            else
                LastSeenText = $"{ago.TotalDays:F0}d ago";
        }
    }
}

public partial class HostViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly RelayServer _server;
    private readonly MainViewModel _main;
    private readonly SaveManager _saveManager;
    private Timer? _statusTimer;
    private Timer? _autoSaveTimer;
    private DateTime _sessionStartUtc;
    private SaveData? _activeSaveData;

    [ObservableProperty]
    private string _serverPort;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private int _playerCount;

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private string _loadedSaveName = "";

    [ObservableProperty]
    private string _lastSavedText = "";

    [ObservableProperty]
    private string _activeSaveFolderName = "";

    public ObservableCollection<ClientInfo> Clients => _server.Clients;
    public ObservableCollection<string> ServerLog => _server.ServerLog;
    public ObservableCollection<PlayerRecordViewModel> PlayerRecords { get; } = new();

    public HostViewModel(ConfigManager config, RelayServer server, MainViewModel main, SaveManager saveManager)
    {
        _config = config;
        _server = server;
        _main = main;
        _saveManager = saveManager;
        _serverPort = config.ServerPort;

        _server.OnManualSaveRequested = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(PerformSave);
        };

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

    public void LoadSave(string folderName)
    {
        ActiveSaveFolderName = folderName;
        _activeSaveData = _saveManager.LoadSave(folderName);

        if (_activeSaveData != null)
        {
            LoadedSaveName = _activeSaveData.Name;
            _server.RestoreState(_activeSaveData.GameState);
            RefreshPlayerRecords();
        }
        else
        {
            LoadedSaveName = folderName;
        }
    }

    private void RefreshPlayerRecords()
    {
        PlayerRecords.Clear();
        if (_activeSaveData == null) return;

        // Mark all as offline, then mark connected ones online
        foreach (var p in _activeSaveData.Players)
        {
            p.IsOnline = Clients.Any(c => c.Id == p.SlotId);
            PlayerRecords.Add(new PlayerRecordViewModel(p));
        }
    }

    public void PerformSave()
    {
        if (string.IsNullOrEmpty(ActiveSaveFolderName) || _activeSaveData == null) return;

        // Capture current game state
        _activeSaveData.GameState = _server.CaptureState();
        _activeSaveData.TotalSessionTime += DateTime.UtcNow - _sessionStartUtc;
        _sessionStartUtc = DateTime.UtcNow; // reset for next interval

        // Update online status of tracked players from connected clients
        foreach (var player in _activeSaveData.Players)
        {
            player.IsOnline = Clients.Any(c => c.Id == player.SlotId);
            if (player.IsOnline)
                player.LastSeen = DateTime.UtcNow;
        }

        // Add new clients not yet in save
        foreach (var client in Clients)
        {
            if (!_activeSaveData.Players.Any(p => p.SlotId == client.Id))
            {
                _activeSaveData.Players.Add(new PlayerRecord
                {
                    SlotId = client.Id,
                    LastIP = client.IP,
                    FirstSeen = client.ConnectedAt.ToUniversalTime(),
                    LastSeen = DateTime.UtcNow,
                    IsOnline = true
                });
            }
        }

        _saveManager.Save(ActiveSaveFolderName, _activeSaveData);
        LastSavedText = $"Saved {DateTime.Now:HH:mm:ss}";
        RefreshPlayerRecords();
    }

    [RelayCommand]
    public void ToggleServer()
    {
        if (_server.IsRunning)
        {
            // Final save before stopping
            PerformSave();
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;

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

            bool hasRestore = _activeSaveData != null;
            _server.Start(port, restoreFromSave: hasRestore);
            IsRunning = true;
            _sessionStartUtc = DateTime.UtcNow;

            // Start auto-save timer (30s)
            _autoSaveTimer = new Timer(_ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(PerformSave);
            }, null, 30000, 30000);
        }
    }

    [RelayCommand]
    private void SendCommand()
    {
        if (string.IsNullOrWhiteSpace(CommandText)) return;
        _server.ExecuteCommand(CommandText);
        CommandText = "";
    }

    [RelayCommand]
    private void ClearPlayers()
    {
        if (_activeSaveData == null) return;
        _activeSaveData.Players.Clear();
        PlayerRecords.Clear();
        _saveManager.Save(ActiveSaveFolderName, _activeSaveData);
    }
}

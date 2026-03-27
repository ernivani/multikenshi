using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Models;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class SquadMemberViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _position = "";

    [ObservableProperty]
    private string _faction = "";
}

public partial class PlayerCardViewModel : ObservableObject
{
    [ObservableProperty]
    private int _playerId;

    [ObservableProperty]
    private string _steamName = "";

    [ObservableProperty]
    private string _steamId = "";

    [ObservableProperty]
    private bool _isHost;

    [ObservableProperty]
    private string _faction = "";

    [ObservableProperty]
    private string _position = "";

    [ObservableProperty]
    private int _squadCount;

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<SquadMemberViewModel> SquadMembers { get; } = new();

    public string HeaderText => IsHost ? $"#{PlayerId} {SteamName} (Host)" : $"#{PlayerId} {SteamName}";

    partial void OnPlayerIdChanged(int value) => OnPropertyChanged(nameof(HeaderText));
    partial void OnSteamNameChanged(string value) => OnPropertyChanged(nameof(HeaderText));
    partial void OnIsHostChanged(bool value) => OnPropertyChanged(nameof(HeaderText));

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}

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

    public ObservableCollection<ConnectedPlayer> ConnectedPlayers => _server.Players;
    public ObservableCollection<string> ServerLog => _server.ServerLog;
    public ObservableCollection<PlayerRecordViewModel> PlayerRecords { get; } = new();
    public ObservableCollection<PlayerCardViewModel> PlayerCards { get; } = new();

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

        // Poll server status and update player cards periodically
        _statusTimer = new Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsRunning = _server.IsRunning;
                PlayerCount = _server.PlayerCount;
                RefreshPlayerCards();
            });
        }, null, 0, 1000);
    }

    private void RefreshPlayerCards()
    {
        var serverPlayers = ConnectedPlayers.ToList();

        // Remove cards for players no longer connected
        for (int i = PlayerCards.Count - 1; i >= 0; i--)
        {
            if (!serverPlayers.Any(p => p.Id == PlayerCards[i].PlayerId))
                PlayerCards.RemoveAt(i);
        }

        // Add or update cards
        foreach (var sp in serverPlayers)
        {
            var existing = PlayerCards.FirstOrDefault(c => c.PlayerId == sp.Id);
            if (existing == null)
            {
                existing = new PlayerCardViewModel();
                PlayerCards.Add(existing);
            }

            existing.PlayerId = sp.Id;
            existing.SteamName = sp.SteamName;
            existing.SteamId = sp.SteamId;
            existing.IsHost = sp.IsHost;
            existing.SquadCount = sp.Squad.Count;

            if (sp.Squad.Count > 0)
            {
                var leader = sp.Squad[0];
                existing.Faction = leader.Faction;
                existing.Position = $"{leader.X:F0}, {leader.Y:F0}, {leader.Z:F0}";
            }

            // Update squad members
            existing.SquadMembers.Clear();
            foreach (var c in sp.Squad)
            {
                existing.SquadMembers.Add(new SquadMemberViewModel
                {
                    Name = c.Name,
                    Position = $"{c.X:F1}, {c.Y:F1}, {c.Z:F1}",
                    Faction = c.Faction
                });
            }
        }
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

        foreach (var p in _activeSaveData.Players)
        {
            p.IsOnline = ConnectedPlayers.Any(c => c.Id == p.SlotId);
            PlayerRecords.Add(new PlayerRecordViewModel(p));
        }
    }

    public void PerformSave()
    {
        if (string.IsNullOrEmpty(ActiveSaveFolderName) || _activeSaveData == null) return;

        _activeSaveData.GameState = _server.CaptureState();
        _activeSaveData.TotalSessionTime += DateTime.UtcNow - _sessionStartUtc;
        _sessionStartUtc = DateTime.UtcNow;

        foreach (var player in _activeSaveData.Players)
        {
            player.IsOnline = ConnectedPlayers.Any(c => c.Id == player.SlotId);
            if (player.IsOnline)
                player.LastSeen = DateTime.UtcNow;
        }

        foreach (var cp in ConnectedPlayers)
        {
            if (!_activeSaveData.Players.Any(p => p.SlotId == cp.Id))
            {
                _activeSaveData.Players.Add(new PlayerRecord
                {
                    SlotId = cp.Id,
                    Name = cp.SteamName,
                    SteamId = cp.SteamId,
                    LastIP = cp.IP,
                    FirstSeen = cp.ConnectedAt.ToUniversalTime(),
                    LastSeen = DateTime.UtcNow,
                    IsOnline = true,
                    Squad = cp.Squad.Select(c => new CharacterSnapshot
                    {
                        Name = c.Name, X = c.X, Y = c.Y, Z = c.Z, Faction = c.Faction
                    }).ToList()
                });
            }
            else
            {
                var existing = _activeSaveData.Players.First(p => p.SlotId == cp.Id);
                if (!string.IsNullOrEmpty(cp.SteamName)) existing.Name = cp.SteamName;
                if (!string.IsNullOrEmpty(cp.SteamId)) existing.SteamId = cp.SteamId;
                existing.Squad = cp.Squad.Select(c => new CharacterSnapshot
                {
                    Name = c.Name, X = c.X, Y = c.Y, Z = c.Z, Faction = c.Faction
                }).ToList();
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

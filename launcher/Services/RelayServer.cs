using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using KenshiLauncher.Models;

namespace KenshiLauncher.Services;

public class ConnectedPlayer
{
    public int Id { get; set; }
    public string SteamName { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string IP { get; set; } = "";
    public bool IsHost { get; set; }
    public DateTime ConnectedAt { get; set; }
    public List<CharacterState> Squad { get; set; } = new();
}

public class CharacterState
{
    [JsonPropertyName("n")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    [JsonPropertyName("fn")]
    public string Faction { get; set; } = "";
}

public class BuildingState
{
    [JsonPropertyName("n")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    [JsonPropertyName("cond")]
    public float Condition { get; set; }
}

public class RelayServer
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<TcpClient> _tcpClients = new();
    private readonly Dictionary<int, TcpClient> _tcpMap = new();
    private readonly object _lock = new();
    private int _nextId = 1;

    // World state (server-authoritative)
    private readonly Dictionary<int, ConnectedPlayer> _players = new();
    private List<CharacterState> _hostNpcs = new();
    private List<BuildingState> _hostBuildings = new();
    private float _speed = 1.0f;
    private int _hostId = -1;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsRunning { get; private set; }
    public Action<string>? Log { get; set; }
    public Action? OnManualSaveRequested { get; set; }
    public ObservableCollection<ConnectedPlayer> Players { get; } = new();
    public ObservableCollection<string> ServerLog { get; } = new();

    public int PlayerCount
    {
        get { lock (_lock) return _players.Count; }
    }

    public void Start(int port, bool restoreFromSave = false)
    {
        if (IsRunning) return;

        if (!restoreFromSave)
        {
            _speed = 1.0f;
        }
        _nextId = 1;
        _hostId = -1;

        lock (_lock)
        {
            _players.Clear();
            _hostNpcs.Clear();
            _hostBuildings.Clear();
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Players.Clear();
            ServerLog.Clear();
        });

        _cts = new CancellationTokenSource();
        IsRunning = true;

        Task.Run(() => ListenLoop(port, _cts.Token));
    }

    public GameStateData CaptureState()
    {
        lock (_lock)
        {
            var state = new GameStateData { Speed = _speed };

            // Capture all players' squads
            foreach (var kvp in _players)
            {
                state.PlayerSquads.Add(new PlayerSquadSnapshot
                {
                    PlayerId = kvp.Value.Id,
                    SteamName = kvp.Value.SteamName,
                    SteamId = kvp.Value.SteamId,
                    Squad = kvp.Value.Squad.Select(c => new CharacterSnapshot
                    {
                        Name = c.Name, X = c.X, Y = c.Y, Z = c.Z, Faction = c.Faction
                    }).ToList()
                });
            }

            return state;
        }
    }

    public void RestoreState(GameStateData state)
    {
        _speed = state.Speed;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _cts?.Cancel();

        try { _listener?.Stop(); } catch { }

        lock (_lock)
        {
            foreach (var c in _tcpClients)
            {
                try { c.Close(); } catch { }
            }
            _tcpClients.Clear();
            _tcpMap.Clear();
            _players.Clear();
            _hostNpcs.Clear();
            _hostBuildings.Clear();
            _hostId = -1;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => Players.Clear());
        PostLog("Server stopped.");
    }

    private async Task ListenLoop(int port, CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(5);
            PostLog($"Server listening on port {port}.");
        }
        catch (Exception ex)
        {
            PostLog($"ERROR: Bind failed on port {port}. {ex.Message}");
            IsRunning = false;
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClient(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (IsRunning)
                PostLog($"ERROR: Listen error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        int clientId = -1;
        string clientIP = "unknown";

        try
        {
            var ep = client.Client.RemoteEndPoint as IPEndPoint;
            if (ep != null) clientIP = ep.Address.ToString();

            var stream = client.GetStream();
            var accumulator = new StringBuilder();
            var buffer = new byte[65536];

            // Step 1: Read handshake
            var handshakeOpt = await ReadJsonMessage(stream, accumulator, buffer, ct);
            if (handshakeOpt == null)
            {
                PostLog($"No handshake from {clientIP}, disconnecting.");
                client.Close();
                return;
            }
            var handshake = handshakeOpt.Value;
            if (handshake.GetProperty("t").GetString() != "hello")
            {
                PostLog($"Bad handshake from {clientIP}, disconnecting.");
                client.Close();
                return;
            }

            var steamName = handshake.TryGetProperty("steamName", out var sn) ? sn.GetString() ?? "" : "";
            var steamId = handshake.TryGetProperty("steamId", out var si) ? si.GetString() ?? "" : "";
            var version = handshake.TryGetProperty("v", out var v) ? v.GetString() ?? "" : "";

            // Step 2: Assign ID and host
            bool isHost;
            lock (_lock)
            {
                clientId = _nextId++;
                isHost = _hostId == -1;
                if (isHost) _hostId = clientId;

                _tcpClients.Add(client);
                _tcpMap[clientId] = client;

                var player = new ConnectedPlayer
                {
                    Id = clientId,
                    SteamName = steamName,
                    SteamId = steamId,
                    IP = clientIP,
                    IsHost = isHost,
                    ConnectedAt = DateTime.Now
                };
                _players[clientId] = player;

                Avalonia.Threading.Dispatcher.UIThread.Post(() => Players.Add(player));
            }

            PostLog($"Player {clientId} '{steamName}' connected from {clientIP} (v{version}){(isHost ? " [HOST]" : "")}.");

            // Step 3: Send welcome
            var welcome = new Dictionary<string, object>
            {
                ["t"] = "welcome",
                ["id"] = clientId,
                ["isHost"] = isHost
            };
            await SendJson(stream, welcome, ct);

            // Step 4: Main relay loop
            int msgCount = 0;
            DateTime lastStatusLog = DateTime.MinValue;
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var msg = await ReadJsonMessage(stream, accumulator, buffer, ct);
                if (msg == null) break;

                var type = msg.Value.GetProperty("t").GetString();
                msgCount++;

                if (type == "ps")
                {
                    // Player state update
                    int squadCount = 0;
                    lock (_lock)
                    {
                        if (_players.TryGetValue(clientId, out var p))
                        {
                            if (msg.Value.TryGetProperty("speed", out var sp))
                            {
                                if (sp.TryGetSingle(out var spVal))
                                    _speed = spVal;
                            }

                            if (msg.Value.TryGetProperty("squad", out var sq))
                            {
                                p.Squad = JsonSerializer.Deserialize<List<CharacterState>>(sq.GetRawText(), _jsonOpts)
                                          ?? new List<CharacterState>();
                                squadCount = p.Squad.Count;
                            }
                        }
                    }

                    // Log first ps and then periodically
                    if (msgCount == 1)
                        PostLog($"[{clientId}] First ps: {squadCount} squad, speed={_speed:F1}");
                }
                else if (type == "ws")
                {
                    // World state from host only
                    int npcCount = 0, bldCount = 0;
                    lock (_lock)
                    {
                        if (clientId == _hostId)
                        {
                            if (msg.Value.TryGetProperty("npcs", out var npcs))
                            {
                                _hostNpcs = JsonSerializer.Deserialize<List<CharacterState>>(npcs.GetRawText(), _jsonOpts)
                                            ?? new List<CharacterState>();
                                npcCount = _hostNpcs.Count;
                            }

                            if (msg.Value.TryGetProperty("buildings", out var blds))
                            {
                                _hostBuildings = JsonSerializer.Deserialize<List<BuildingState>>(blds.GetRawText(), _jsonOpts)
                                                 ?? new List<BuildingState>();
                                bldCount = _hostBuildings.Count;
                            }
                        }
                    }

                    // Log first ws and then periodically
                    if (msgCount <= 2)
                        PostLog($"[{clientId}] First ws: {npcCount} npcs, {bldCount} buildings");
                }

                // Periodic status log (every 30s)
                if ((DateTime.UtcNow - lastStatusLog).TotalSeconds >= 30)
                {
                    lastStatusLog = DateTime.UtcNow;
                    lock (_lock)
                    {
                        if (_players.TryGetValue(clientId, out var sp))
                            PostLog($"[{clientId}] Relay: {msgCount} msgs, {sp.Squad.Count} squad, {_hostNpcs.Count} npcs, speed={_speed:F1}");
                    }
                }

                // Build and send world update to this client
                var wu = BuildWorldUpdate(clientId);
                await SendJson(stream, wu, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            string disconnectedName;
            lock (_lock)
            {
                _tcpClients.Remove(client);
                if (clientId > 0)
                {
                    _tcpMap.Remove(clientId);
                    _players.TryGetValue(clientId, out var removedPlayer);
                    disconnectedName = removedPlayer?.SteamName ?? $"#{clientId}";
                    _players.Remove(clientId);

                    // If host disconnected, reassign
                    if (clientId == _hostId)
                    {
                        _hostId = -1;
                        if (_players.Count > 0)
                        {
                            var newHost = _players.Values.First();
                            _hostId = newHost.Id;
                            newHost.IsHost = true;
                            PostLog($"Host reassigned to player {newHost.Id} '{newHost.SteamName}'.");
                        }
                    }
                }
                else
                {
                    disconnectedName = "unknown";
                }
            }

            try { client.Close(); } catch { }

            if (clientId > 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var existing = Players.FirstOrDefault(p => p.Id == clientId);
                    if (existing != null) Players.Remove(existing);
                });
                PostLog($"Player {clientId} '{disconnectedName}' disconnected.");
            }
        }
    }

    private Dictionary<string, object> BuildWorldUpdate(int excludeClientId)
    {
        lock (_lock)
        {
            var playerList = new List<object>();
            foreach (var kvp in _players)
            {
                if (kvp.Key == excludeClientId) continue;
                var p = kvp.Value;
                playerList.Add(new Dictionary<string, object>
                {
                    ["id"] = p.Id,
                    ["sn"] = p.SteamName,
                    ["sid"] = p.SteamId,
                    ["host"] = p.IsHost,
                    ["squad"] = p.Squad
                });
            }

            var wu = new Dictionary<string, object>
            {
                ["t"] = "wu",
                ["speed"] = _speed,
                ["players"] = playerList,
                ["npcs"] = _hostNpcs,
                ["buildings"] = _hostBuildings
            };

            return wu;
        }
    }

    private static async Task<JsonElement?> ReadJsonMessage(
        NetworkStream stream, StringBuilder accumulator, byte[] buffer, CancellationToken ct)
    {
        while (true)
        {
            // Check if we already have a complete message in the accumulator
            var data = accumulator.ToString();
            int newlineIdx = data.IndexOf('\n');
            if (newlineIdx >= 0)
            {
                var line = data.Substring(0, newlineIdx).Trim();
                accumulator.Remove(0, newlineIdx + 1);

                if (line.Length == 0) continue; // skip empty lines

                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(line);
                }
                catch
                {
                    continue; // skip malformed JSON
                }
            }

            // Need more data
            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            }
            catch
            {
                return null;
            }

            if (bytesRead <= 0) return null;
            accumulator.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
        }
    }

    private static async Task SendJson(NetworkStream stream, object obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes, 0, bytes.Length, ct);
    }

    private void PostLog(string message)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Log?.Invoke(stamped);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ServerLog.Add(stamped);
            if (ServerLog.Count > 500)
                ServerLog.RemoveAt(0);
        });
    }

    public void ExecuteCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        var parts = command.Trim().Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case "/speed":
                if (float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                {
                    _speed = s;
                    PostLog($"Speed set to {s}.");
                }
                else
                    PostLog("Usage: /speed <value>");
                break;

            case "/kick":
                if (int.TryParse(arg, out var kickId))
                {
                    TcpClient? target;
                    string kickName;
                    lock (_lock)
                    {
                        _tcpMap.TryGetValue(kickId, out target);
                        _players.TryGetValue(kickId, out var kickPlayer);
                        kickName = kickPlayer?.SteamName ?? $"#{kickId}";
                    }
                    if (target != null)
                    {
                        try { target.Close(); } catch { }
                        PostLog($"Kicked player {kickId} '{kickName}'.");
                    }
                    else
                        PostLog($"Player {kickId} not found.");
                }
                else
                    PostLog("Usage: /kick <id>");
                break;

            case "/players":
                lock (_lock)
                {
                    if (_players.Count == 0)
                    {
                        PostLog("No players connected.");
                    }
                    else
                    {
                        foreach (var p in _players.Values)
                        {
                            var hostTag = p.IsHost ? " [HOST]" : "";
                            var squadCount = p.Squad.Count;
                            PostLog($"  #{p.Id} '{p.SteamName}' ({p.SteamId}) - {squadCount} squad members{hostTag}");
                        }
                    }
                }
                break;

            case "/host":
                if (int.TryParse(arg, out var newHostId))
                {
                    lock (_lock)
                    {
                        if (_players.TryGetValue(newHostId, out var newHost))
                        {
                            // Remove old host flag
                            foreach (var p in _players.Values)
                                p.IsHost = false;
                            newHost.IsHost = true;
                            _hostId = newHostId;
                            PostLog($"Host reassigned to player {newHostId} '{newHost.SteamName}'.");
                        }
                        else
                            PostLog($"Player {newHostId} not found.");
                    }
                }
                else
                    PostLog("Usage: /host <id>");
                break;

            case "/save":
                OnManualSaveRequested?.Invoke();
                PostLog("Manual save triggered.");
                break;

            case "/stop":
                Stop();
                break;

            case "/help":
                PostLog("Commands: /speed <val>, /kick <id>, /host <id>, /players, /save, /stop, /help");
                break;

            default:
                PostLog($"Unknown command: {cmd}");
                break;
        }
    }
}

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
    public string Faction { get; set; } = "";
    public List<CharacterState> Squad { get; set; } = new();
    public DateTime LastMessageTime { get; set; } = DateTime.UtcNow;
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
    private readonly Dictionary<int, NetworkStream> _streamMap = new();
    private readonly Dictionary<string, int> _steamIdToPlayerId = new(); // persist IDs across reconnects
    private readonly object _lock = new();
    private int _nextId = 1;

    // World state (server-authoritative)
    private readonly Dictionary<int, ConnectedPlayer> _players = new();
    private List<BuildingState> _hostBuildings = new();
    private float _speed = 1.0f;
    private float? _speedOverride = null; // set by /speed command, overrides client-reported speed
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
                        Name = c.Name, X = c.X, Y = c.Y, Z = c.Z, Faction = kvp.Value.Faction
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
            _streamMap.Clear();
            _steamIdToPlayerId.Clear();
            _players.Clear();
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

        // Start heartbeat monitor
        _ = Task.Run(() => HeartbeatLoop(ct), ct);

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

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10000, ct).ConfigureAwait(false);

            List<(int id, NetworkStream stream)> toCheck;
            lock (_lock)
            {
                toCheck = _players
                    .Where(kvp => _streamMap.ContainsKey(kvp.Key))
                    .Select(kvp => (kvp.Key, _streamMap[kvp.Key]))
                    .ToList();
            }

            var now = DateTime.UtcNow;
            foreach (var (id, stream) in toCheck)
            {
                DateTime lastMsg;
                lock (_lock)
                {
                    if (!_players.TryGetValue(id, out var p)) continue;
                    lastMsg = p.LastMessageTime;
                }

                // If no message for 45s, disconnect
                if ((now - lastMsg).TotalSeconds >= 45)
                {
                    PostLog($"[{id}] Heartbeat timeout, disconnecting.");
                    TcpClient? client;
                    lock (_lock) { _tcpMap.TryGetValue(id, out client); }
                    if (client != null)
                        try { client.Close(); } catch { }
                    continue;
                }

                // If no message for 30s, send ping
                if ((now - lastMsg).TotalSeconds >= 30)
                {
                    try
                    {
                        await SendJson(stream, new Dictionary<string, object> { ["t"] = "ping" }, ct);
                    }
                    catch { }
                }
            }
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

            // Step 2: Assign ID and host (reuse ID for returning players)
            bool isHost;
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(steamId) && _steamIdToPlayerId.TryGetValue(steamId, out var prevId))
                    clientId = prevId; // returning player — reuse their ID
                else
                {
                    clientId = _nextId++;
                    if (!string.IsNullOrEmpty(steamId))
                        _steamIdToPlayerId[steamId] = clientId;
                }

                isHost = _hostId == -1;
                if (isHost) _hostId = clientId;

                _tcpClients.Add(client);
                _tcpMap[clientId] = client;
                _streamMap[clientId] = stream;

                var player = new ConnectedPlayer
                {
                    Id = clientId,
                    SteamName = steamName,
                    SteamId = steamId,
                    IP = clientIP,
                    IsHost = isHost,
                    ConnectedAt = DateTime.Now,
                    LastMessageTime = DateTime.UtcNow
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

                // Update last message time
                lock (_lock)
                {
                    if (_players.TryGetValue(clientId, out var pl))
                        pl.LastMessageTime = DateTime.UtcNow;
                }

                if (type == "ps")
                {
                    // Merged player state: chars + buildings + speed + faction
                    int charCount = 0;
                    lock (_lock)
                    {
                        if (_players.TryGetValue(clientId, out var p))
                        {
                            // Speed — only accept from HOST, ignore guests
                            if (clientId == _hostId && msg.Value.TryGetProperty("speed", out var sp))
                            {
                                if (sp.TryGetSingle(out var spVal))
                                    _speed = spVal;
                            }

                            // Player faction
                            if (msg.Value.TryGetProperty("pf", out var pf))
                                p.Faction = pf.GetString() ?? "";

                            // Characters (all visible chars from this client)
                            if (msg.Value.TryGetProperty("chars", out var ch))
                            {
                                p.Squad = JsonSerializer.Deserialize<List<CharacterState>>(ch.GetRawText(), _jsonOpts)
                                          ?? new List<CharacterState>();
                                charCount = p.Squad.Count;
                            }

                            // Buildings (accepted from all clients, but only host's are authoritative)
                            if (clientId == _hostId && msg.Value.TryGetProperty("buildings", out var bl))
                            {
                                _hostBuildings = JsonSerializer.Deserialize<List<BuildingState>>(bl.GetRawText(), _jsonOpts)
                                                 ?? new List<BuildingState>();
                            }
                        }
                    }

                    // Log first message and then periodically
                    if (msgCount == 1)
                        PostLog($"[{clientId}] First ps: {charCount} chars, speed={_speed:F1}");
                }
                else if (type == "pong")
                {
                    // Heartbeat response — LastMessageTime already updated above
                }

                // Periodic status log (every 10s)
                if ((DateTime.UtcNow - lastStatusLog).TotalSeconds >= 10)
                {
                    lastStatusLog = DateTime.UtcNow;
                    lock (_lock)
                    {
                        if (_players.TryGetValue(clientId, out var sp))
                            PostLog($"[{clientId}] Relay: {msgCount} msgs, {sp.Squad.Count} chars, {_hostBuildings.Count} buildings, speed={_speed:F1}");
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
            bool wasHost = false;
            int newHostId = -1;
            NetworkStream? newHostStream = null;

            lock (_lock)
            {
                _tcpClients.Remove(client);
                if (clientId > 0)
                {
                    _tcpMap.Remove(clientId);
                    _streamMap.Remove(clientId);
                    _players.TryGetValue(clientId, out var removedPlayer);
                    disconnectedName = removedPlayer?.SteamName ?? $"#{clientId}";
                    _players.Remove(clientId);

                    // If host disconnected, reassign
                    if (clientId == _hostId)
                    {
                        wasHost = true;
                        _hostId = -1;
                        if (_players.Count > 0)
                        {
                            var newHost = _players.Values.First();
                            _hostId = newHost.Id;
                            newHost.IsHost = true;
                            newHostId = newHost.Id;
                            _streamMap.TryGetValue(newHostId, out newHostStream);
                            PostLog($"Host reassigned to player {newHost.Id} '{newHost.SteamName}'.");
                        }
                    }
                }
                else
                {
                    disconnectedName = "unknown";
                }
            }

            // Send host change notification outside the lock
            if (wasHost && newHostId > 0 && newHostStream != null)
            {
                try
                {
                    var hostChange = new Dictionary<string, object>
                    {
                        ["t"] = "hostChange",
                        ["isHost"] = true
                    };
                    await SendJson(newHostStream, hostChange, CancellationToken.None);
                    PostLog($"[{newHostId}] Notified of host promotion.");
                }
                catch { }
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
                    ["pf"] = p.Faction,
                    ["chars"] = p.Squad
                });
            }

            // Speed: for host, only send if there's a server override
            // (so host controls naturally unless admin overrides).
            // For guests, always send host's speed.
            bool isForHost = excludeClientId != _hostId ? false :
                             _players.ContainsKey(excludeClientId);
            // Actually: excludeClientId IS the recipient (we exclude their chars).
            // Wait — excludeClientId is the client we're building this for.
            // So if excludeClientId == _hostId, this wu is FOR the host.
            bool recipientIsHost = (excludeClientId == _hostId);
            float? speedToSend = null;
            if (_speedOverride != null)
                speedToSend = _speedOverride;       // override → send to everyone
            else if (!recipientIsHost)
                speedToSend = _speed;               // no override, guest → send host speed

            var wu = new Dictionary<string, object>
            {
                ["t"] = "wu",
                ["players"] = playerList,
                ["buildings"] = _hostBuildings
            };

            // Only include speed field when we need to control the client
            if (speedToSend != null)
                wu["speed"] = speedToSend.Value;

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
                if (string.IsNullOrEmpty(arg) || arg == "reset")
                {
                    _speedOverride = null;
                    PostLog("Speed override cleared — using client speed.");
                }
                else if (float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                {
                    if (s > 5) { PostLog($"Speed capped to 5 (requested {s})."); s = 5; }
                    _speedOverride = s;
                    _speed = s;
                    PostLog($"Speed override set to {s}. Use '/speed reset' to clear.");
                }
                else
                    PostLog("Usage: /speed <value> | /speed reset");
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
                        PostLog($"{_players.Count} player(s), speed={_speed:F1}, {_hostBuildings.Count} buildings:");
                        foreach (var p in _players.Values)
                        {
                            var hostTag = p.IsHost ? " [HOST]" : "";
                            var factionTag = string.IsNullOrEmpty(p.Faction) ? "" : p.Faction;
                            var uptime = (DateTime.Now - p.ConnectedAt);
                            var uptimeStr = uptime.TotalMinutes < 1 ? $"{uptime.Seconds}s" : $"{uptime.TotalMinutes:F0}m";
                            PostLog($"  #{p.Id} '{p.SteamName}' ({p.SteamId}){hostTag}");
                            PostLog($"     Faction: {(string.IsNullOrEmpty(factionTag) ? "unknown" : factionTag)} | Chars: {p.Squad.Count} | Up: {uptimeStr}");
                            if (p.Squad.Count > 0)
                            {
                                var leader = p.Squad[0];
                                PostLog($"     Pos: ({leader.X:F0}, {leader.Y:F0}, {leader.Z:F0})");
                                var names = string.Join(", ", p.Squad.Select(c => c.Name).Take(10));
                                if (p.Squad.Count > 10) names += $" +{p.Squad.Count - 10} more";
                                PostLog($"     Squad: {names}");
                            }
                        }
                    }
                }
                break;

            case "/host":
                if (int.TryParse(arg, out var newHostId))
                {
                    NetworkStream? newHostStream = null;
                    lock (_lock)
                    {
                        if (_players.TryGetValue(newHostId, out var newHost))
                        {
                            // Remove old host flag
                            foreach (var p in _players.Values)
                                p.IsHost = false;
                            newHost.IsHost = true;
                            _hostId = newHostId;
                            _streamMap.TryGetValue(newHostId, out newHostStream);
                            PostLog($"Host reassigned to player {newHostId} '{newHost.SteamName}'.");
                        }
                        else
                            PostLog($"Player {newHostId} not found.");
                    }

                    // Notify new host outside lock
                    if (newHostStream != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await SendJson(newHostStream,
                                    new Dictionary<string, object> { ["t"] = "hostChange", ["isHost"] = true },
                                    CancellationToken.None);
                            }
                            catch { }
                        });
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

            case "/export":
                if (string.IsNullOrEmpty(arg))
                {
                    var saves = KenshiSaveManager.ListSaves();
                    if (saves.Count == 0)
                    {
                        PostLog("No Kenshi saves found.");
                    }
                    else
                    {
                        PostLog("Kenshi saves:");
                        foreach (var s in saves)
                            PostLog($"  {s.Name} ({s.SizeBytes / 1024}KB, {s.LastModified:MMM d HH:mm})");
                        PostLog("Use: /export <name> to create a zip for sharing");
                    }
                }
                else
                {
                    try
                    {
                        var zipPath = KenshiSaveManager.ExportSave(arg);
                        PostLog($"Exported to: {zipPath}");
                        PostLog("Share this file with your friends. They import via /import.");
                    }
                    catch (Exception ex)
                    {
                        PostLog($"ERROR: {ex.Message}");
                    }
                }
                break;

            case "/import":
                if (string.IsNullOrEmpty(arg))
                {
                    PostLog("Usage: /import <path to zip>");
                }
                else
                {
                    try
                    {
                        KenshiSaveManager.ImportSave(arg);
                        PostLog("Save imported as 'multiplayer'. Load it in Kenshi.");
                    }
                    catch (Exception ex)
                    {
                        PostLog($"ERROR: {ex.Message}");
                    }
                }
                break;

            case "/help":
                PostLog("Commands: /speed <val>, /kick <id>, /host <id>, /players, /export [save], /import <zip>, /save, /stop, /help");
                break;

            default:
                PostLog($"Unknown command: {cmd}");
                break;
        }
    }
}

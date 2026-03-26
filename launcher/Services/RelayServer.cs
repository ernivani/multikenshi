using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KenshiLauncher.Services;

public class RelayServer
{
    private static readonly string[] Factions =
    {
        "204-gamedata.base", "10-multiplayr.mod", "12-multiplayr.mod"
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<TcpClient> _clients = new();
    private readonly object _lock = new();
    private int _clientIdCounter = 1;

    private float _speed = 1.0f;
    private (float x, float y, float z) _plr1 = (-5139.11f, 158.019f, 345.631f);
    private (float x, float y, float z) _plr2 = (-5139.11f, 158.019f, 345.631f);

    public bool IsRunning { get; private set; }
    public Action<string>? Log { get; set; }

    public int PlayerCount
    {
        get { lock (_lock) return _clients.Count; }
    }

    public void Start(int port)
    {
        if (IsRunning) return;

        _plr1 = (-5139.11f, 158.019f, 345.631f);
        _plr2 = (-5139.11f, 158.019f, 345.631f);
        _speed = 1.0f;
        _clientIdCounter = 1;

        _cts = new CancellationTokenSource();
        IsRunning = true;

        Task.Run(() => ListenLoop(port, _cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _cts?.Cancel();

        try { _listener?.Stop(); } catch { }

        lock (_lock)
        {
            foreach (var c in _clients)
            {
                try { c.Close(); } catch { }
            }
            _clients.Clear();
        }

        Log?.Invoke("Server stopped.");
    }

    private async Task ListenLoop(int port, CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(5);
            Log?.Invoke($"Server listening on port {port}.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"ERROR: Bind failed on port {port}. {ex.Message}");
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
                Log?.Invoke($"ERROR: Listen error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        int clientId;
        lock (_lock)
        {
            _clients.Add(client);
            clientId = _clientIdCounter++;
            if (_clientIdCounter >= Factions.Length)
                _clientIdCounter = 1;
        }

        Log?.Invoke("Client connected.");

        try
        {
            var stream = client.GetStream();

            // Send initial speed
            var initMsg = $"1\n{_speed.ToString(CultureInfo.InvariantCulture)}\n";
            var initBytes = Encoding.ASCII.GetBytes(initMsg);
            await stream.WriteAsync(initBytes, ct);

            var buffer = new byte[1024];
            var accumulator = new StringBuilder();

            while (!ct.IsCancellationRequested && client.Connected)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                }
                catch { break; }

                if (bytesRead <= 0) break;

                accumulator.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));

                // Parse key-value pairs from accumulated data
                var data = accumulator.ToString();
                var lines = data.Split('\n');

                // Process complete pairs (need at least key + value)
                int processed = 0;
                for (int i = 0; i + 1 < lines.Length; i += 2)
                {
                    var key = lines[i].Trim();
                    var value = lines[i + 1].Trim();

                    if (key == "2" && clientId == 1 && value != "0,0,0")
                        _plr1 = ParseVector(value);
                    else if (key == "3" && clientId == 2 && value != "0,0,0")
                        _plr2 = ParseVector(value);
                    else if (key == "1")
                    {
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                            _speed = s;
                    }
                    processed = i + 2;
                }

                // Keep any unparsed remainder
                if (processed > 0 && processed < lines.Length)
                {
                    accumulator.Clear();
                    accumulator.Append(lines[processed]);
                }
                else if (processed > 0)
                {
                    accumulator.Clear();
                }

                // Build reply
                var reply = new StringBuilder();
                reply.Append($"0\n{Factions[clientId]}\n");
                reply.Append($"1\n{_speed.ToString(CultureInfo.InvariantCulture)}\n");
                reply.Append($"2\n{FormatVector(_plr1)}\n");
                reply.Append($"3\n{FormatVector(_plr2)}\n");

                var replyBytes = Encoding.ASCII.GetBytes(reply.ToString());
                try
                {
                    await stream.WriteAsync(replyBytes, ct);
                }
                catch { break; }
            }
        }
        catch (Exception) { }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(client);
            }
            try { client.Close(); } catch { }
            Log?.Invoke("Client disconnected.");
        }
    }

    private static (float x, float y, float z) ParseVector(string s)
    {
        var parts = s.Split(',');
        if (parts.Length >= 3 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return (x, y, z);
        }
        return (0, 0, 0);
    }

    private static string FormatVector((float x, float y, float z) v)
        => $"{v.x.ToString(CultureInfo.InvariantCulture)},{v.y.ToString(CultureInfo.InvariantCulture)},{v.z.ToString(CultureInfo.InvariantCulture)}";
}

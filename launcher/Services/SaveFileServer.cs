using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KenshiLauncher.Services;

/// <summary>
/// Simple HTTP server that serves the host's save file to guests.
/// Runs on relay port + 1.
/// </summary>
public class SaveFileServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private byte[]? _saveZip;
    public bool IsRunning { get; private set; }
    public Action<string>? Log { get; set; }

    public void Start(int port)
    {
        if (IsRunning) return;

        // Export the latest Kenshi save
        var saves = KenshiSaveManager.ListSaves();
        if (saves.Count == 0)
        {
            Log?.Invoke("No Kenshi saves found — guests won't be able to download.");
            return;
        }

        var latest = saves[0]; // sorted by LastModified desc
        try
        {
            var zipPath = KenshiSaveManager.ExportSave(latest.Name);
            _saveZip = File.ReadAllBytes(zipPath);
            Log?.Invoke($"Save ready: '{latest.Name}' ({_saveZip.Length / 1024}KB)");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Failed to export save: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
            Log?.Invoke($"Save server on port {port}");
            _ = Task.Run(() => ListenLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Save server failed: {ex.Message}. Guests must import saves manually.");
            IsRunning = false;
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                if (_saveZip != null)
                {
                    ctx.Response.ContentType = "application/zip";
                    ctx.Response.ContentLength64 = _saveZip.Length;
                    ctx.Response.Headers.Add("X-Save-Size", _saveZip.Length.ToString());
                    await ctx.Response.OutputStream.WriteAsync(_saveZip, 0, _saveZip.Length, ct);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch { }
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _saveZip = null;
    }
}

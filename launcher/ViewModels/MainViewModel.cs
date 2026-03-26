using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigManager _config = new();
    private readonly RelayServer _server = new();

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _activeTab = "";

    [ObservableProperty]
    private string _steamName = "Player";

    public ObservableCollection<LogEntry> LogLines { get; } = new();

    public PlayViewModel Play { get; }
    public HostViewModel Host { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel()
    {
        _config.Load();
        _server.Log = PostLog;

        var (name, _) = SteamIdentity.GetCurrentUser();
        SteamName = name;

        Host = new HostViewModel(_config, _server, this);
        Play = new PlayViewModel(_config, this, _server, Host);
        Settings = new SettingsViewModel(_config, this);

        // Auto-detect Kenshi path if not set
        if (string.IsNullOrEmpty(_config.KenshiPath))
        {
            var detected = KenshiFinder.FindKenshiPath();
            if (!string.IsNullOrEmpty(detected))
            {
                _config.KenshiPath = detected;
                _config.Save();
                Settings.KenshiPath = detected;
                PostLog("Auto-detected Kenshi path.");
            }
            else
            {
                PostLog("Could not auto-detect Kenshi. Use Settings to set the path.");
            }
        }

        PostLog("Ready.");

        // Start on Play page
        ActiveTab = "Play";
        CurrentPage = Play;
    }

    public void PostLog(string message)
    {
        var trimmed = message.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(trimmed)) return;

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            AddLogLine(trimmed);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AddLogLine(trimmed));
        }
    }

    public void CopyToClipboard(string text)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var lifetime = Avalonia.Application.Current?.ApplicationLifetime as
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var clipboard = lifetime?.MainWindow?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(text);
            }
            catch { }
        });
    }

    private void AddLogLine(string text)
    {
        LogLines.Add(new LogEntry(text));
        if (LogLines.Count > 500)
            LogLines.RemoveAt(0);
    }

    [RelayCommand]
    private void NavigateTo(string page)
    {
        switch (page)
        {
            case "Play":
                ActiveTab = "Play";
                CurrentPage = Play;
                Play.RefreshDllStatus();
                break;
            case "Settings":
                ActiveTab = "Settings";
                CurrentPage = Settings;
                Settings.RefreshStatus();
                break;
        }
    }

    public void OnShutdown()
    {
        _config.Save();
        _server.Stop();
    }
}

public class LogEntry
{
    public string Text { get; }
    public string Color { get; }

    public LogEntry(string text)
    {
        Text = text;

        if (text.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase))
            Color = "Red";
        else if (text.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("injected", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("listening", StringComparison.OrdinalIgnoreCase))
            Color = "Green";
        else if (text.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Waiting", StringComparison.OrdinalIgnoreCase))
            Color = "Yellow";
        else
            Color = "Gray";
    }
}

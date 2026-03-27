using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly MainViewModel _main;

    [ObservableProperty]
    private string _kenshiPath;

    [ObservableProperty]
    private bool _exeFound;

    [ObservableProperty]
    private bool _dllExists;

    [ObservableProperty]
    private bool _modInstalled;

    [ObservableProperty]
    private bool _modOutdated;

    [ObservableProperty]
    private string _uninstallMessage = "";

    public SettingsViewModel(ConfigManager config, MainViewModel main)
    {
        _config = config;
        _main = main;
        _kenshiPath = config.KenshiPath;
        RefreshStatus();
    }

    partial void OnKenshiPathChanged(string value)
    {
        _config.KenshiPath = value;
        _config.Save();
        RefreshStatus();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Kenshi installation folder",
                AllowMultiple = false
            });

        if (result.Count > 0)
        {
            KenshiPath = result[0].Path.LocalPath;
            _main.PostLog("Kenshi path updated.");
        }
    }

    [RelayCommand]
    private void UninstallMod()
    {
        int removed = 0;

        // Remove DLL
        var dllPath = ProcessLauncher.GetDllPath();
        if (File.Exists(dllPath))
        {
            try { File.Delete(dllPath); removed++; }
            catch (Exception ex)
            {
                UninstallMessage = $"Failed: {ex.Message}";
                _main.CopyToClipboard(ex.ToString());
                return;
            }
        }

        // Remove hash file
        var hashPath = dllPath + ".sha256";
        if (File.Exists(hashPath))
        {
            try { File.Delete(hashPath); removed++; }
            catch { /* non-critical */ }
        }

        // Remove kenshi_mp.ini from Kenshi directory
        if (!string.IsNullOrEmpty(_config.KenshiPath))
        {
            var iniPath = Path.Combine(_config.KenshiPath, "kenshi_mp.ini");
            if (File.Exists(iniPath))
            {
                try { File.Delete(iniPath); removed++; }
                catch { /* non-critical */ }
            }
        }

        RefreshStatus();
        _main.Play.RefreshDllStatus();

        if (removed > 0)
        {
            UninstallMessage = "Mod files removed";
            _main.PostLog("Mod uninstalled — DLL and config removed.");
        }
        else
        {
            UninstallMessage = "Nothing to remove";
        }

        ClearUninstallMessageAfterDelay();
    }

    private async void ClearUninstallMessageAfterDelay()
    {
        await Task.Delay(3000);
        UninstallMessage = "";
    }

    public void RefreshStatus()
    {
        if (!string.IsNullOrEmpty(KenshiPath))
        {
            var exePath = Path.Combine(KenshiPath, "kenshi_x64.exe");
            ExeFound = File.Exists(exePath);

            // Check mod status (read-only, no auto-install)
            var modPath = Path.Combine(KenshiPath, "mods", "kenshi-online", "kenshi-online.mod");
            ModInstalled = File.Exists(modPath);
            ModOutdated = false;
        }
        else
        {
            ExeFound = false;
            ModInstalled = false;
            ModOutdated = false;
        }

        DllExists = ProcessLauncher.DllExists();
    }
}

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

    public SettingsViewModel(ConfigManager config, MainViewModel main)
    {
        _config = config;
        _main = main;
        _kenshiPath = config.KenshiPath;
        UpdateStatus();
    }

    partial void OnKenshiPathChanged(string value)
    {
        _config.KenshiPath = value;
        _config.Save();
        UpdateStatus();
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

    private void UpdateStatus()
    {
        if (!string.IsNullOrEmpty(KenshiPath))
        {
            var exePath = Path.Combine(KenshiPath, "kenshi_x64.exe");
            ExeFound = File.Exists(exePath);
        }
        else
        {
            ExeFound = false;
        }

        DllExists = ProcessLauncher.DllExists();
    }
}

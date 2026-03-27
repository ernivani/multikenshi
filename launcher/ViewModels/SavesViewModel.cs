using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KenshiLauncher.Services;

namespace KenshiLauncher.ViewModels;

public partial class SaveSummaryViewModel : ObservableObject
{
    public string FolderName { get; }
    public string Name { get; }
    public string PlayerCountText { get; }
    public string SessionTimeText { get; }
    public string LastModifiedText { get; }
    public int Port { get; }

    public SaveSummaryViewModel(SaveSummary summary)
    {
        FolderName = summary.FolderName;
        Name = summary.Name;
        Port = summary.Port;
        PlayerCountText = summary.PlayerCount == 1 ? "1 player" : $"{summary.PlayerCount} players";

        var t = summary.TotalSessionTime;
        if (t.TotalHours >= 1)
            SessionTimeText = $"{t.TotalHours:F1}h played";
        else if (t.TotalMinutes >= 1)
            SessionTimeText = $"{t.TotalMinutes:F0}m played";
        else
            SessionTimeText = "< 1m played";

        var ago = DateTime.UtcNow - summary.LastModifiedUtc;
        if (ago.TotalMinutes < 1)
            LastModifiedText = "just now";
        else if (ago.TotalHours < 1)
            LastModifiedText = $"{ago.TotalMinutes:F0}m ago";
        else if (ago.TotalDays < 1)
            LastModifiedText = $"{ago.TotalHours:F0}h ago";
        else
            LastModifiedText = $"{ago.TotalDays:F0}d ago";
    }
}

public partial class SavesViewModel : ObservableObject
{
    private readonly SaveManager _saveManager;

    public ObservableCollection<SaveSummaryViewModel> Saves { get; } = new();

    [ObservableProperty]
    private SaveSummaryViewModel? _selectedSave;

    [ObservableProperty]
    private string _newSaveName = "";

    [ObservableProperty]
    private bool _isDeleteConfirmOpen;

    [ObservableProperty]
    private bool _isRenameOpen;

    [ObservableProperty]
    private string _renameText = "";

    public SavesViewModel(SaveManager saveManager)
    {
        _saveManager = saveManager;
    }

    public void RefreshSaves()
    {
        var selectedFolder = SelectedSave?.FolderName;
        Saves.Clear();
        foreach (var s in _saveManager.ListSaves())
            Saves.Add(new SaveSummaryViewModel(s));

        // Re-select if still exists
        if (selectedFolder != null)
            SelectedSave = Saves.FirstOrDefault(s => s.FolderName == selectedFolder);
    }

    [RelayCommand]
    private void CreateSave()
    {
        var name = string.IsNullOrWhiteSpace(NewSaveName) ? "New Save" : NewSaveName.Trim();
        _saveManager.CreateSave(name, 7777, 8, "");
        NewSaveName = "";
        RefreshSaves();
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (SelectedSave == null) return;
        IsDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private void DeleteSave()
    {
        if (SelectedSave == null) return;
        _saveManager.DeleteSave(SelectedSave.FolderName);
        IsDeleteConfirmOpen = false;
        SelectedSave = null;
        RefreshSaves();
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmOpen = false;
    }

    [RelayCommand]
    private void OpenRename()
    {
        if (SelectedSave == null) return;
        RenameText = SelectedSave.Name;
        IsRenameOpen = true;
    }

    [RelayCommand]
    private void ApplyRename()
    {
        if (SelectedSave == null || string.IsNullOrWhiteSpace(RenameText)) return;
        var newFolder = _saveManager.RenameSave(SelectedSave.FolderName, RenameText.Trim());
        IsRenameOpen = false;
        RefreshSaves();
        if (newFolder != null)
            SelectedSave = Saves.FirstOrDefault(s => s.FolderName == newFolder);
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenameOpen = false;
    }
}

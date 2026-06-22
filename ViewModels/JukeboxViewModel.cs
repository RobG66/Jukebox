using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel : ViewModelBase, IDisposable
{
    #region Startup Properties
    public int     InitialVolume   { get; set; } = 100;
    public string? InitialFile     { get; set; }
    public bool    NoRecurse       { get; set; }
    public bool    StayOnTop       { get; set; }
    public int     ShowPlayingTimeout { get; set; } = 10;
    #endregion

    #region Sub-ViewModels
    public JukeboxPlaylistViewModel  PlaylistViewModel  { get; } = new();
    public JukeboxEqViewModel        EqViewModel        { get; } = new();
    public JukeboxVisualizerViewModel VisualizerViewModel { get; } = new();
    #endregion

    public Jukebox.Services.IStorageService? StorageService { get; set; }

    #region UI State
    [ObservableProperty] private bool    _isPlaylistVisible;
    [ObservableProperty] private bool    _isPickerVisible;
    [ObservableProperty] private bool    _isEqVisible       = false;
    [ObservableProperty] private bool    _isLoopEnabled;
    [ObservableProperty] private bool    _isRepeatEnabled   = false;
    [ObservableProperty] private bool    _isRandomPlayback  = false;
    [ObservableProperty] private bool    _hasMultipleTracks = true;
    [ObservableProperty] private bool    _isAutoHideEnabled = false;
    [ObservableProperty] private string? _playlistLogo;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _playlistLogoBitmap;
    [ObservableProperty] private double  _controlBarHeight  = 65;
    #endregion

    #region Show Playing OSD
    private CancellationTokenSource? _showPlayingCts;
    [ObservableProperty] private bool   _isShowPlayingEnabled = true;
    [ObservableProperty] private string _showPlayingText    = "";
    [ObservableProperty] private bool   _isShowPlayingVisible = false;
    [ObservableProperty] private double _showPlayingOpacity = 0.5;
    #endregion

    public JukeboxViewModel()
    {
        Volume = InitialVolume;
        PlaylistViewModel.PlaylistCleared += OnPlaylistCleared;
        PlaylistViewModel.Playlist.CollectionChanged += (s, e) => 
        {
            if (!CanPause && !CanStop) // Stopped
            {
                CanPlay = PlaylistViewModel.Playlist.Count > 0;
            }
        };
    }

    private void OnPlaylistCleared(object? sender, EventArgs e)
    {
        Stop();
        CurrentTrack = new JukeboxTrack { DisplayName = "GUI Design Mode - No Track Loaded" };
        CanPlay = false;
    }

    partial void OnIsPlaylistVisibleChanged(bool value)
    {
        if (value) IsPickerVisible = false;
    }

    partial void OnIsPickerVisibleChanged(bool value)
    {
        if (value) IsPlaylistVisible = false;
    }

    partial void OnPlaylistLogoChanged(string? value)
    {
        if (string.IsNullOrEmpty(value) || !System.IO.File.Exists(value))
        {
            PlaylistLogoBitmap = null;
            return;
        }

        try
        {
            PlaylistLogoBitmap = new Avalonia.Media.Imaging.Bitmap(value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load playlist logo: {ex.Message}");
            PlaylistLogoBitmap = null;
        }
    }

    partial void OnIsShowPlayingEnabledChanged(bool value)
    {
        if (value && CurrentTrack != null)
        {
            ShowPlayingText = CurrentTrack.DisplayName;
            _ = TriggerShowPlayingOSDAsync();
        }
        else if (!value)
        {
            _showPlayingCts?.Cancel();
            _showPlayingCts?.Dispose();
            _showPlayingCts = null;
            IsShowPlayingVisible = false;
        }
    }

    private async Task TriggerShowPlayingOSDAsync()
    {
        _showPlayingCts?.Cancel();
        _showPlayingCts?.Dispose();
        _showPlayingCts = new CancellationTokenSource();
        var token = _showPlayingCts.Token;

        ShowPlayingOpacity = 0.5;
        IsShowPlayingVisible = true;

        try
        {
            await Task.Delay(3000, token);

            const int steps = 60;
            const int delay = 3000 / steps;
            double stepDrop = 0.5 / steps;

            for (int i = 0; i < steps; i++)
            {
                await Task.Delay(delay, token);
                ShowPlayingOpacity -= stepDrop;
            }

            ShowPlayingOpacity = 0.0;
            IsShowPlayingVisible = false;
        }
        catch (TaskCanceledException) { }
    }

    #region UI Toggle Commands
    [RelayCommand] private void TogglePlaylist()  => IsPlaylistVisible  = !IsPlaylistVisible;
    [RelayCommand] private void ToggleEq()        => IsEqVisible        = !IsEqVisible;
    [RelayCommand] private void TogglePicker()    => IsPickerVisible    = !IsPickerVisible;
    partial void OnIsRandomPlaybackChanged(bool value)
    {
        if (value) _playedIndices.Clear();
    }
    
    [RelayCommand] private void ToggleAutoHide()  => IsAutoHideEnabled  = !IsAutoHideEnabled;

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (StorageService == null) return;
        var files = await StorageService.OpenFileDialogAsync(
            "Select Media Files",
            allowMultiple: true,
            audioExtensions: Constants.AudioExtensions,
            videoExtensions: Constants.VideoExtensions
        );
        if (files != null && files.Count > 0)
        {
            await PlaylistViewModel.ProcessAndAddFilesAsync(files, NoRecurse);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (StorageService == null) return;
        var folderPath = await StorageService.OpenFolderDialogAsync("Select Folder to Add");
        if (!string.IsNullOrEmpty(folderPath))
        {
            await PlaylistViewModel.ProcessAndAddFilesAsync(new List<string> { folderPath }, NoRecurse);
        }
    }
    #endregion

    public void Dispose()
    {
        PlaylistViewModel.PlaylistCleared -= OnPlaylistCleared;
        if (_showPlayingCts != null)
        {
            try { _showPlayingCts.Cancel(); } catch { }
            _showPlayingCts.Dispose();
            _showPlayingCts = null;
        }
        VisualizerViewModel?.Dispose();

        DisposePlayback();
    }
}

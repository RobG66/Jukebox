using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel : ViewModelBase, IDisposable
{
    #region Startup Properties
    public int     InitialVolume   { get; set; } = 100;
    public string? InitialFile     { get; set; }
    public bool    ForceVisualizer { get; set; }
    public bool    NoRecurse       { get; set; }
    public bool    StayOnTop       { get; set; }
    public bool    IsKioskMode     { get; set; }
    public int     ShowPlayingTimeout { get; set; } = 10;
    #endregion

    #region Sub-ViewModels
    public JukeboxPlaylistViewModel  PlaylistViewModel  { get; } = new();
    public JukeboxEqViewModel        EqViewModel        { get; } = new();
    public JukeboxVisualizerViewModel VisualizerViewModel { get; } = new();
    #endregion

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
        PlaylistViewModel.PlaylistCleared += (s, e) => Stop();
        InitializePlayback();
        _ = VisualizerViewModel.LoadVisualizersAsync();
    }

    partial void OnIsPlaylistVisibleChanged(bool value)
    {
        if (value) IsPickerVisible = false;
    }

    partial void OnIsPickerVisibleChanged(bool value)
    {
        if (value) IsPlaylistVisible = false;
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

    [RelayCommand] private void PlaySelectedTrack() { }
    [RelayCommand] private void ApplyPreset()       { }
    [RelayCommand] private void ToggleMiniPlayer()  { }
    [RelayCommand] private void ToggleVisualizer()  { }
    #endregion

    public void Dispose()
    {
        _showPlayingCts?.Cancel();
        _showPlayingCts?.Dispose();

        VisualizerViewModel?.Dispose();

        DisposePlayback();
    }
}

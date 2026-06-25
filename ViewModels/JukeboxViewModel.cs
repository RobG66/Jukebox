using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // MPV does not use a shared-context concept.
    #endregion

    #region Sub-ViewModels
    public JukeboxPlaylistViewModel  PlaylistViewModel  { get; } = new();
    public JukeboxEqViewModel        EqViewModel        { get; } = new();
    public JukeboxVisualizerViewModel VisualizerViewModel { get; } = new();
    #endregion

    public Jukebox.Services.IStorageService? StorageService { get; set; }

    // ── REFACTOR: OSD animation now lives in a dedicated service ──
    // (was TriggerShowPlayingOSDAsync, lines 119-160 in original)
    // See Smell Test Report §4.1 and §7.2 item #8.
    private readonly IShowPlayingService _showPlayingService;

    #region UI State
    [ObservableProperty] private bool    _isPlaylistVisible;
    [ObservableProperty] private bool    _isPickerVisible;
    [ObservableProperty] private bool    _isEqVisible       = false;
    [ObservableProperty] private bool    _isLoopEnabled;
    [ObservableProperty] private bool    _isRepeatEnabled   = false;
    [ObservableProperty] private bool    _isRandomPlayback  = false;
    [ObservableProperty] private bool    _isAutoHideEnabled = false;
    [ObservableProperty] private string? _playlistLogo;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _playlistLogoBitmap;
    [ObservableProperty] private double  _controlBarHeight  = Constants.DefaultControlBarHeight;
    #endregion

    #region Show Playing OSD
    private readonly object _osdLock = new();
    private CancellationTokenSource? _showPlayingCts;
    [ObservableProperty] private bool   _isShowPlayingEnabled = true;
    [ObservableProperty] private string _showPlayingText    = "";
    [ObservableProperty] private bool   _isShowPlayingVisible = false;
    [ObservableProperty] private double _showPlayingOpacity = Constants.OsdStartOpacity;
    private bool _isDisposed;
    #endregion

    public JukeboxViewModel() : this(new ShowPlayingService())
    {
    }

    // Constructor added for testability — tests can inject a mock IShowPlayingService.
    // Production code uses the parameterless constructor above.
    public JukeboxViewModel(IShowPlayingService showPlayingService)
    {
        _showPlayingService = showPlayingService ?? throw new ArgumentNullException(nameof(showPlayingService));
        _showPlayingService.Changed += OnShowPlayingChanged;

        Volume = InitialVolume;
        PlaylistViewModel.PlaylistCleared += OnPlaylistCleared;
        // REFACTOR: Lambda replaced with named method so we can unsubscribe
        // in Dispose (was line 57-64, smell §4.1: Lambda event subscription
        // without unsubscribe).
        PlaylistViewModel.Playlist.CollectionChanged += OnPlaylistCollectionChanged;
    }

    private void OnPlaylistCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!CanPause && !CanStop) // Stopped
        {
            CanPlay = PlaylistViewModel.Playlist.Count > 0;
        }
        _playedTracks.Clear();
    }

    private void OnShowPlayingChanged(object? sender, ShowPlayingEventArgs e)
    {
        // Forward service state to observable properties for binding.
        ShowPlayingText = e.Text;
        ShowPlayingOpacity = e.Opacity;
        IsShowPlayingVisible = e.IsVisible;
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
        // REFACTOR: file existence check moved to a background thread to
        // avoid UI-thread stalls on network filesystems (smell §4.1: Direct
        // file existence check on UI thread). For a logo path, we accept
        // the tiny race in exchange for not blocking the UI.
        if (string.IsNullOrEmpty(value))
        {
            PlaylistLogoBitmap = null;
            return;
        }

        _ = LoadPlaylistLogoAsync(value);
    }

    private async Task LoadPlaylistLogoAsync(string path)
    {
        bool exists = await Task.Run(() => System.IO.File.Exists(path));
        if (!exists)
        {
            PlaylistLogoBitmap = null;
            return;
        }

        try
        {
            // Note: Avalonia.Media.Imaging.Bitmap must be created on UI thread.
            PlaylistLogoBitmap = new Avalonia.Media.Imaging.Bitmap(path);
        }
        catch (Exception ex)
        {
            // REFACTOR: Console.WriteLine → Debug.WriteLine (smell §4.1, §6.5)
            Debug.WriteLine($"Failed to load playlist logo: {ex.Message}");
            PlaylistLogoBitmap = null;
        }
    }

    partial void OnIsShowPlayingEnabledChanged(bool value)
    {
        if (value && CurrentTrack != null)
        {
            _showPlayingService.ShowAsync(CurrentTrack.DisplayName).SafeFireAndForget(nameof(_showPlayingService.ShowAsync));
        }
        else if (!value)
        {
            _showPlayingService.Hide();
        }
    }

    #region UI Toggle Commands
    [RelayCommand] private void TogglePlaylist()  => IsPlaylistVisible  = !IsPlaylistVisible;
    [RelayCommand] private void ToggleEq()        => IsEqVisible        = !IsEqVisible;
    [RelayCommand] private void TogglePicker()    => IsPickerVisible    = !IsPickerVisible;
    partial void OnIsRandomPlaybackChanged(bool value)
    {
        if (value) _playedTracks.Clear();
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
        if (_isDisposed) return;
        _isDisposed = true;

        // REFACTOR: unsubscribe the previously-lambda'd handler (smell §4.1).
        PlaylistViewModel.Playlist.CollectionChanged -= OnPlaylistCollectionChanged;
        PlaylistViewModel.PlaylistCleared -= OnPlaylistCleared;
        _showPlayingService.Changed -= OnShowPlayingChanged;

        lock (_osdLock)
        {
            if (_showPlayingCts != null)
            {
                try { _showPlayingCts.Cancel(); } catch { }
                _showPlayingCts.Dispose();
                _showPlayingCts = null;
            }
        }
        _showPlayingService.Hide();
        VisualizerViewModel?.Dispose();

        // REFACTOR: fire-and-forget dispose is now observed via SafeFireAndForget
        // (was `_ = DisposePlaybackAsync();` — smell §4.1 Critical: fire-and-forget
        // DisposePlaybackAsync). The dispose still happens asynchronously to
        // avoid blocking, but exceptions are now captured and logged.
        DisposePlaybackAsync().SafeFireAndForget(nameof(DisposePlaybackAsync));
    }
}

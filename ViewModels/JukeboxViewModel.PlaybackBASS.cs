using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ManagedBass;
using ManagedBass.DirectX8;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel
{
    #region Fields
    private int _bassStream;
    private readonly int[] _eqFxHandles = new int[Constants.EqBandCount];
    private DSPProcedure? _dspProcedure;
    private SyncProcedure? _endSyncProcedure;
    private int _dspHandle;
    private int _endSyncHandle;
    private bool _ownsBassContext;

    // Tracks whether we successfully preloaded bass.dll / libbass.so from
    // the lib/ folder so ManagedBass's internal LoadLibrary call finds it.
    private static bool _bassPreloaded;
    private static IntPtr _bassNativeHandle;

    public event EventHandler<short[]>? PcmDataAvailable;
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _isBassAvailable;
    #endregion

    #region Initialization
    private void InitializeBass()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Initializing ManagedBass...");
        try
        {
            // Preload bass.dll / libbass.so from the lib/ drop-in folder
            // BEFORE Bass.Init() — ManagedBass's internal P/Invoke will
            // then find the already-loaded library in the OS loader cache.
            PreloadBassNative();

            _dspProcedure = new DSPProcedure(OnDsp);
            _endSyncProcedure = new SyncProcedure(OnBassEndSync);

            bool bassOk = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            if (bassOk || Bass.LastError == Errors.Already)
            {
                Dispatcher.UIThread.Post(() => IsBassAvailable = true);
                _ownsBassContext = bassOk;
                Debug.WriteLine(bassOk
                    ? $"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ManagedBass initialized successfully in {sw.ElapsedMilliseconds}ms."
                    : $"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Using shared ManagedBass initialization.");
            }
            else
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ManagedBass failed to initialize. Error: {Bass.LastError}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ManagedBass Init Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Preload the native BASS library from <c>&lt;appdir&gt;/lib/</c>
    /// (flat — bass.dll on Windows, libbass.so on Linux). Once loaded
    /// into the process, ManagedBass's internal P/Invoke
    /// <c>LoadLibrary("bass.dll")</c> / <c>dlopen("libbass.so")</c>
    /// will resolve to the already-cached handle, no matter what
    /// directory the OS would otherwise search.
    ///
    /// If the file is missing from <c>lib/</c>, falls back to the OS
    /// default search path (lets Linux users install libbass system-wide
    /// if they prefer).
    /// </summary>
    private static void PreloadBassNative()
    {
        if (_bassPreloaded) return;
        _bassPreloaded = true; // only attempt once per process

        string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "bass.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "libbass.so"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "libbass.dylib"
                    : "bass";

        string fullPath = Path.Combine(libDir, fileName);
        if (File.Exists(fullPath))
        {
            _bassNativeHandle = NativeLibrary.Load(fullPath, typeof(JukeboxViewModel).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            if (_bassNativeHandle != IntPtr.Zero)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Preloaded BASS native library from: {fullPath}");
                return;
            }
        }

        // Fall back to OS default search path.
        _bassNativeHandle = NativeLibrary.Load(fileName);
        if (_bassNativeHandle != IntPtr.Zero)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Loaded BASS native library from OS search path: {fileName}");
        }
        else
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] BASS native library not found. Looked in: {fullPath} and OS default search path.");
        }
    }
    #endregion

    #region Playback
    private async Task PlayAudioAsync()
    {
        if (!IsBassAvailable)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Audio Error",
                "Audio playback is unavailable. ManagedBass failed to initialize.");
            return;
        }

        if (CurrentTrack == null) return;

        _bassStream = Bass.CreateStream(CurrentTrack.FilePath, 0, 0, BassFlags.Default);

        // REFACTOR: silent failure on CreateStream returning 0 → check
        // Bass.LastError and surface a user-facing dialog (was smell §4.3
        // Warning: Silent failure on Bass.CreateStream returning 0).
        if (_bassStream == 0)
        {
            var error = Bass.LastError;
            Debug.WriteLine($"[BASS] CreateStream failed for '{CurrentTrack.FilePath}'. Error: {error}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Audio Error",
                $"Could not open audio file:\n{CurrentTrack.FilePath}\n\nReason: {error}");
            return;
        }

        long byteLength = Bass.ChannelGetLength(_bassStream);
        double durationSeconds = Bass.ChannelBytes2Seconds(_bassStream, byteLength);
        UpdateTrackDuration(durationSeconds * 1000.0);
        if (CurrentTrack.Length == TimeSpan.Zero)
            CurrentTrack.Length = TimeSpan.FromSeconds(durationSeconds);

        Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, Volume / 100.0);

        // REFACTOR: EQ is Windows-only via DXParamEQ. On Linux/macOS, the EQ
        // sliders will silently have no effect — log a one-time warning so
        // the issue is diagnosable. Future enhancement: switch to BASS_FX
        // PeakEQ (cross-platform) once the ManagedBass.Fx NuGet package is
        // added (was smell §4.3 Warning: Platform-conditional EQ).
        // See Smell Test Report §4.3 and §7.2 item #13.
        if (OperatingSystem.IsWindows())
        {
            for (int i = 0; i < Constants.EqBandCount; i++)
            {
                if (EqViewModel.EqBands.Count > i)
                {
                    _eqFxHandles[i] = Bass.ChannelSetFX(_bassStream, EffectType.DXParamEQ, 0);
                    var p = new DXParamEQParameters
                    {
                        fBandwidth = 18f,
                        fCenter = EqViewModel.EqBands[i].CenterFrequency,
                        fGain = (float)EqViewModel.EqBands[i].Gain
                    };
                    Bass.FXSetParameters(_eqFxHandles[i], p);
                }
            }
        }
        else
        {
            // One-time warning per process — guarded with a static flag so
            // we don't spam the log on every track change.
            if (!_eqUnavailableWarned)
            {
                _eqUnavailableWarned = true;
                Debug.WriteLine($"[BASS] EQ is not available on this platform ({Environment.OSVersion}). " +
                                "Audio will play without equalization. To enable cross-platform EQ, " +
                                "add the ManagedBass.Fx NuGet package and switch to EffectType.PeakEQ.");
            }
        }

        _dspHandle = Bass.ChannelSetDSP(_bassStream, _dspProcedure!, IntPtr.Zero, 0);
        _endSyncHandle = Bass.ChannelSetSync(_bassStream, SyncFlags.End, 0, _endSyncProcedure!, IntPtr.Zero);

        Bass.ChannelPlay(_bassStream);
        SetPlayingState();
    }

    private static bool _eqUnavailableWarned = false;

    private void ResumeBass()
    {
        if (_bassStream != 0) Bass.ChannelPlay(_bassStream);
    }

    private void PauseBass()
    {
        if (_bassStream != 0) Bass.ChannelPause(_bassStream);
    }

    private void StopBass()
    {
        if (_bassStream != 0)
        {
            Bass.StreamFree(_bassStream);
            _bassStream = 0;
            Array.Clear(_eqFxHandles, 0, _eqFxHandles.Length);
            _dspHandle = 0;
            _endSyncHandle = 0;
        }
    }

    private void SeekBass(double positionMs)
    {
        if (_bassStream != 0)
            Bass.ChannelSetPosition(_bassStream, Bass.ChannelSeconds2Bytes(_bassStream, positionMs / 1000.0));
    }

    private double GetBassPositionMs()
    {
        if (_bassStream == 0) return -1;
        var pos = Bass.ChannelGetPosition(_bassStream);
        return TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(_bassStream, pos)).TotalMilliseconds;
    }

    private void ApplyBassVolume(double volume)
    {
        if (_bassStream != 0)
            Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, volume / 100.0);
    }
    #endregion

    #region Callbacks
    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length > 0 && PcmDataAvailable != null)
        {
            int count = length / 2;
            short[] pcm = new short[count];
            System.Runtime.InteropServices.Marshal.Copy(buffer, pcm, 0, count);
            PcmDataAvailable.Invoke(this, pcm);
        }
    }

    private void OnBassEndSync(int handle, int channel, int data, IntPtr user)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (IsRepeatEnabled)
                await StartTrackAsync();
            else
                await NextAsync();
        });
    }

    private void OnEqBandUpdated(object? sender, EqSliderViewModel band)
    {
        if (_bassStream != 0)
        {
            int index = EqViewModel.EqBands.IndexOf(band);
            if (index >= 0 && index < Constants.EqBandCount && _eqFxHandles[index] != 0)
            {
                var p = new DXParamEQParameters
                {
                    fBandwidth = 18f,
                    fCenter = band.CenterFrequency,
                    fGain = (float)band.Gain
                };
                Bass.FXSetParameters(_eqFxHandles[index], p);
            }
        }
    }
    #endregion

    #region Dispose
    private void DisposeBass()
    {
        PcmDataAvailable = null;

        if (_bassStream != 0)
        {
            if (_dspHandle != 0)
            {
                Bass.ChannelRemoveDSP(_bassStream, _dspHandle);
                _dspHandle = 0;
            }
            if (_endSyncHandle != 0)
            {
                Bass.ChannelRemoveSync(_bassStream, _endSyncHandle);
                _endSyncHandle = 0;
            }
            Bass.StreamFree(_bassStream);
            _bassStream = 0;
            Array.Clear(_eqFxHandles, 0, _eqFxHandles.Length);
        }

        if (IsBassAvailable && _ownsBassContext)
            Bass.Free();

        IsBassAvailable = false;
    }
    #endregion
}

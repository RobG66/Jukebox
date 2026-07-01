using Avalonia.Threading;
using Jukebox.Models;
using ManagedBass;
using ManagedBass.DirectX8;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Jukebox.Services;

public sealed class BassPlaybackEngine : IMediaPlayerEngine
{
    #region Fields & Constants
    private int _bassStream;
    private readonly int[] _eqFxHandles = new int[Constants.EqBandCount];
    private DSPProcedure? _dspProcedure;
    private SyncProcedure? _endSyncProcedure;
    private int _dspHandle;
    private int _endSyncHandle;
    private bool _ownsBassContext;
    private bool _eqUnavailableWarned;
    private double _volume = 100;

    private static bool _bassPreloaded;
    private static IntPtr _bassNativeHandle;
    #endregion

    #region Public Properties
    public bool IsAvailable { get; private set; }
    #endregion

    #region Public Events
    public event EventHandler? PlaybackEnded;
    public event EventHandler<double>? DurationChanged;
    public event EventHandler<short[]>? PcmDataAvailable;
    #endregion

    #region Constructor
    public BassPlaybackEngine()
    {
        _dspProcedure = new DSPProcedure(OnDsp);
        _endSyncProcedure = new SyncProcedure(OnBassEndSync);
    }
    #endregion

    #region Public Methods
    public void Initialize()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Initializing ManagedBass...");
        try
        {
            PreloadBassNative();

            bool bassOk = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            if (bassOk || Bass.LastError == Errors.Already)
            {
                IsAvailable = true;
                _ownsBassContext = bassOk;
                Debug.WriteLine(bassOk
                    ? $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] ManagedBass initialized successfully in {sw.ElapsedMilliseconds}ms."
                    : $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Using shared ManagedBass initialization.");
            }
            else
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] ManagedBass failed to initialize. Error: {Bass.LastError}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] ManagedBass Init Exception: {ex.Message}");
        }
    }

    public async Task PlayAsync(JukeboxTrack track)
    {
        if (!IsAvailable)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Audio Error",
                "Audio playback is unavailable. ManagedBass failed to initialize.");
            return;
        }

        Stop();

        _bassStream = Bass.CreateStream(track.FilePath, 0, 0, BassFlags.Default);

        if (_bassStream == 0)
        {
            var error = Bass.LastError;
            Debug.WriteLine($"[BASS Engine] CreateStream failed for '{track.FilePath}'. Error: {error}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Audio Error",
                $"Could not open audio file:\n{track.FilePath}\n\nReason: {error}");
            return;
        }

        long byteLength = Bass.ChannelGetLength(_bassStream);
        double durationSeconds = Bass.ChannelBytes2Seconds(_bassStream, byteLength);
        DurationChanged?.Invoke(this, durationSeconds * 1000.0);

        if (track.Length == TimeSpan.Zero)
        {
            track.Length = TimeSpan.FromSeconds(durationSeconds);
        }

        Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, _volume / 100.0);

        _dspHandle = Bass.ChannelSetDSP(_bassStream, _dspProcedure!, IntPtr.Zero, 0);
        _endSyncHandle = Bass.ChannelSetSync(_bassStream, SyncFlags.End, 0, _endSyncProcedure!, IntPtr.Zero);

        Bass.ChannelPlay(_bassStream);
    }

    public void Pause()
    {
        if (_bassStream != 0)
        {
            Bass.ChannelPause(_bassStream);
        }
    }

    public void Stop()
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

    public void Resume()
    {
        if (_bassStream != 0)
        {
            Bass.ChannelPlay(_bassStream);
        }
    }

    public void Seek(double positionMs)
    {
        if (_bassStream != 0)
        {
            Bass.ChannelSetPosition(_bassStream, Bass.ChannelSeconds2Bytes(_bassStream, positionMs / 1000.0));
        }
    }

    public double GetPositionMs()
    {
        if (_bassStream == 0) return -1;
        var pos = Bass.ChannelGetPosition(_bassStream);
        return TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(_bassStream, pos)).TotalMilliseconds;
    }

    public void SetVolume(double volume)
    {
        _volume = volume;
        if (_bassStream != 0)
        {
            Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, volume / 100.0);
        }
    }

    public void InitializeEqBands(double[] gains, float[] centerFrequencies)
    {
        if (!IsAvailable || _bassStream == 0) return;

        if (OperatingSystem.IsWindows())
        {
            for (int i = 0; i < Constants.EqBandCount; i++)
            {
                if (gains.Length > i && centerFrequencies.Length > i)
                {
                    _eqFxHandles[i] = Bass.ChannelSetFX(_bassStream, EffectType.DXParamEQ, 0);
                    var p = new DXParamEQParameters
                    {
                        fBandwidth = 18f,
                        fCenter = centerFrequencies[i],
                        fGain = (float)gains[i]
                    };
                    Bass.FXSetParameters(_eqFxHandles[i], p);
                }
            }
        }
        else
        {
            if (!_eqUnavailableWarned)
            {
                _eqUnavailableWarned = true;
                Debug.WriteLine($"[BASS Engine] EQ is not available on this platform ({Environment.OSVersion}).");
            }
        }
    }

    public void UpdateEqBand(int index, double gain, float centerFrequency)
    {
        if (!IsAvailable || _bassStream == 0 || index < 0 || index >= Constants.EqBandCount) return;

        if (OperatingSystem.IsWindows())
        {
            if (_eqFxHandles[index] == 0)
            {
                _eqFxHandles[index] = Bass.ChannelSetFX(_bassStream, EffectType.DXParamEQ, 0);
            }

            if (_eqFxHandles[index] != 0)
            {
                var p = new DXParamEQParameters
                {
                    fBandwidth = 18f,
                    fCenter = centerFrequency,
                    fGain = (float)gain
                };
                Bass.FXSetParameters(_eqFxHandles[index], p);
            }
        }
    }
    #endregion

    #region Private Methods
    private static void PreloadBassNative()
    {
        if (_bassPreloaded) return;
        _bassPreloaded = true;

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
            _bassNativeHandle = NativeLibrary.Load(fullPath, typeof(BassPlaybackEngine).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            if (_bassNativeHandle != IntPtr.Zero)
            {
                Debug.WriteLine($"[BASS Engine] Preloaded BASS native library from: {fullPath}");
                return;
            }
        }

        _bassNativeHandle = NativeLibrary.Load(fileName);
        if (_bassNativeHandle != IntPtr.Zero)
        {
            Debug.WriteLine($"[BASS Engine] Loaded BASS native library from OS search path: {fileName}");
        }
        else
        {
            Debug.WriteLine($"[BASS Engine] BASS native library not found. Looked in: {fullPath} and OS default search path.");
        }
    }

    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length > 0 && PcmDataAvailable != null)
        {
            int count = length / 2;
            short[] pcm = new short[count];
            Marshal.Copy(buffer, pcm, 0, count);
            PcmDataAvailable.Invoke(this, pcm);
        }
    }

    private void OnBassEndSync(int handle, int channel, int data, IntPtr user)
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }
    #endregion

    #region Dispose
    public void Dispose()
    {
        PcmDataAvailable = null;
        Stop();

        if (IsAvailable && _ownsBassContext)
        {
            Bass.Free();
        }

        IsAvailable = false;
    }
    #endregion
}

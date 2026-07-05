using Jukebox.Models;
using ManagedBass;
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

    // PeakEQ (BASS_FX) uses one FX handle per channel and multiplexes bands
    // via the lBand field in BASS_BFX_PEAKEQ. The previous DXParamEQ approach
    // created one FX handle per band; PeakEQ creates one handle for all bands.
    private int _eqFxHandle;

    private DSPProcedure? _dspProcedure;
    private SyncProcedure? _endSyncProcedure;
    private int _dspHandle;
    private int _endSyncHandle;
    private bool _ownsBassContext;
    private double _volume = 100;

    private static bool _bassPreloaded;
    private static IntPtr _bassNativeHandle;
    private static IntPtr _bassFxNativeHandle;

    // ── PeakEQ effect type and parameter struct ──
    //
    // Uses BASS_FX's PeakEQ (cross-platform pure-DSP effect).
    //
    // We define the effect type constant and parameter struct inline rather
    // than adding the ManagedBass.Fx NuGet package, because:
    //   1. ManagedBass.Fx (latest 3.0.1) may not be API-compatible with
    //      ManagedBass 4.0.2 used by this project.
    //   2. The core Bass.ChannelSetFX and Bass.FXSetParameters methods
    //      (in ManagedBass core) already support any effect type and any
    //      blittable parameter struct — no extra dependency needed.
    //   3. We only need one effect (PeakEQ), so defining it inline is
    //      simpler than pulling in a whole package.
    //
    // The native bass_fx library (bass_fx.dll / libbass_fx.so) must be
    // shipped in lib/ alongside bass.dll / libbass.so. It's preloaded
    // into the process in PreloadBassFxNative() so BASS can find it
    // when ChannelSetFX is called with the PeakEQ effect type.
    //
    // ManagedBass defines EffectType.PeakEQ (= 0x10004, matching the native
    // BASS_FX_BFX_PEAKEQ constant in bass_fx.h). We use the enum value directly.
    //
    // Must match the native BASS_BFX_PEAKEQ struct layout from bass_fx.h:
    //   typedef struct {
    //     int   lBand;        // band index (0-based)
    //     int   lChannel;     // BASS_BFX_CHANxxx flags (0 = all channels)
    //     float fCenter;      // center frequency in Hz
    //     float fGain;        // gain in dB (-30 to +30)
    //     float fBandwidth;   // bandwidth in octaves (0.1 to 4.0) [fQ can be used instead]
    //     float fQ;           // Q factor (0.1 to 10.0) [fBandwidth can be used instead]
    //   } BASS_BFX_PEAKEQ;
    // Must match the 24-byte native layout (2 ints + 4 floats). Passing less than 24 bytes
    // would result in random garbage for fQ. Setting fQ = 0 tells BASS to use fBandwidth instead.
    [StructLayout(LayoutKind.Sequential)]
    private struct PeakEqParams
    {
        public int lBand;
        public float fBandwidth;
        public float fQ;
        public float fCenter;
        public float fGain;
        public int lChannel;
    }

    // Bandwidth in octaves. The old DXParamEQ used 18 semitones (= 1.5 octaves).
    // PeakEQ's fBandwidth is directly in octaves, so 1.5f is the equivalent.
    private const float EqBandwidthOctaves = 1.5f;
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

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // On Linux (especially under PipeWire/PulseAudio ALSA emulation), BASS default buffers
                // can cause stuttering/underruns. Increase buffer to 1000ms and update period to 50ms.
                Bass.Configure(Configuration.PlaybackBufferLength, 1000);
                Bass.Configure(Configuration.UpdatePeriod, 50);
            }
            else
            {
                // On Windows, the default BASS update period is 100ms — the DSP callback
                // (which feeds PCM to the visualizer) fires only ~10 times/sec. At 60fps,
                // this means projectM gets audio data in bursts: 5-6 frames with no new data,
                // then 1 frame with a large burst. This causes visible flickering in
                // the visualization — it reacts strongly on the burst frame,
                // then is relatively static for the next 5 frames.
                //
                // Reducing the update period to 30ms gives ~33 DSP callbacks/sec,
                // much closer to the 60fps render rate. The visualizer now gets
                // fresh audio data almost every frame, eliminating the burst
                // pattern and the resulting flicker.
                //
                // The playback buffer stays at the default 300ms — only the
                // update frequency changes. Audio playback is unaffected.
                Bass.Configure(Configuration.UpdatePeriod, 30);
            }

            bool bassOk = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            if (bassOk || Bass.LastError == Errors.Already)
            {
                IsAvailable = true;
                _ownsBassContext = bassOk;
                Debug.WriteLine(bassOk
                    ? $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] ManagedBass initialized successfully in {sw.ElapsedMilliseconds}ms."
                    : $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Using shared ManagedBass initialization.");

                // Load AAC and Opus plugins if present in the lib folder
                string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
                LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bass_aac.dll" : "libbass_aac.so"));
                LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bassopus.dll" : "libbassopus.so"));
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

        Errors error = Errors.OK;
        try
        {
            string urlToPlay = track.FilePath;

            if (urlToPlay.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                urlToPlay.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Set User-Agent so BASS network stream requests aren't blocked
                Bass.NetAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                _bassStream = await Task.Run(() => {
                    int handle = Bass.CreateStream(urlToPlay, 0, BassFlags.Default, null, IntPtr.Zero);
                    if (handle == 0) error = Bass.LastError;
                    return handle;
                });
            }
            else
            {
                _bassStream = await Task.Run(() => {
                    int handle = Bass.CreateStream(urlToPlay, 0, 0, BassFlags.Default);
                    if (handle == 0) error = Bass.LastError;
                    return handle;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BASS Engine] PlayAsync failed: {ex.Message}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Audio Error",
                $"Could not open or resolve audio stream:\n{track.FilePath}\n\nReason: {ex.Message}");
            return;
        }

        if (_bassStream == 0)
        {
            Debug.WriteLine($"[BASS Engine] CreateStream failed for '{track.FilePath}'. Error: {error}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Audio Error",
                $"Could not open audio stream:\n{track.FilePath}\n\nReason: {error}");
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
            _eqFxHandle = 0;
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

    // Uses cross-platform BASS_FX PeakEQ instead of Windows-only DXParamEQ.
    // EQ works on Linux and macOS (requires bass_fx.dll / libbass_fx.so /
    // libbass_fx.dylib in the lib/ directory).
    public void InitializeEqBands(double[] gains, float[] centerFrequencies)
    {
        if (!IsAvailable || _bassStream == 0) return;

        // Remove any existing EQ FX before re-creating.
        if (_eqFxHandle != 0)
        {
            Bass.ChannelRemoveFX(_bassStream, _eqFxHandle);
            _eqFxHandle = 0;
        }

        _eqFxHandle = Bass.ChannelSetFX(_bassStream, EffectType.PeakEQ, 0);
        if (_eqFxHandle == 0)
        {
            Debug.WriteLine($"[BASS Engine] ChannelSetFX(PeakEQ) failed. Error: {Bass.LastError}. " +
                            "Ensure bass_fx.dll / libbass_fx.so is in the lib/ folder.");
            return;
        }
        Debug.WriteLine($"[BASS Engine] EQ FX created (handle={_eqFxHandle}).");

        // Set parameters for each band.
        for (int i = 0; i < Constants.EqBandCount; i++)
        {
            if (gains.Length > i && centerFrequencies.Length > i)
            {
                SetPeakEqParameters(i, centerFrequencies[i], (float)gains[i]);
            }
        }
    }

    // Updates a specific EQ band on all target channels.
    public void UpdateEqBand(int index, double gain, float centerFrequency)
    {
        if (!IsAvailable || _bassStream == 0 || index < 0 || index >= Constants.EqBandCount) return;

        if (_eqFxHandle == 0)
        {
            _eqFxHandle = Bass.ChannelSetFX(_bassStream, EffectType.PeakEQ, 0);
            if (_eqFxHandle == 0)
            {
                Debug.WriteLine($"[BASS Engine] UpdateEqBand: ChannelSetFX failed. Error: {Bass.LastError}.");
                return;
            }
        }

        SetPeakEqParameters(index, centerFrequency, (float)gain);
    }
    #endregion

    #region Private Methods
    /// <summary>
    /// Sets the PeakEQ parameters for a single band on the EQ FX handle.
    ///
    /// ManagedBass 4.0.2's <see cref="Bass.FXSetParameters(int, IntPtr)"/>
    /// overload takes a raw <see cref="IntPtr"/> to the parameter struct,
    /// not the struct itself (the <c>(int, IEffectParameter)</c> overload
    /// requires implementing the <c>IEffectParameter</c> interface, which
    /// is more ceremony than we need for one effect). We marshal the
    /// <see cref="PeakEqParams"/> struct to unmanaged memory, pass the
    /// pointer, then free.
    /// </summary>
    private void SetPeakEqParameters(int band, float centerFreq, float gainDb)
    {
        if (_eqFxHandle == 0) return;

        var p = new PeakEqParams
        {
            lBand = band,
            fBandwidth = EqBandwidthOctaves,
            fQ = 0.0f,
            fCenter = centerFreq,
            fGain = gainDb,
            lChannel = -1 // Apply to all channels (BASS_BFX_CHANALL)
        };

        // Use manual marshaling via IntPtr — ManagedBass's IEffectParameter
        // interface requires a FXType property we don't want to implement.
        // The IntPtr overload of FXSetParameters passes the raw struct bytes
        // directly to BASS_FXSetParameters.
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PeakEqParams>());
        try
        {
            Marshal.StructureToPtr(p, ptr, false);
            if (!Bass.FXSetParameters(_eqFxHandle, ptr))
            {
                Debug.WriteLine($"[BASS Engine] FXSetParameters failed for band {band} (freq={centerFreq}Hz, gain={gainDb}dB). Error: {Bass.LastError}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
    private static void PreloadBassNative()
    {
        if (_bassPreloaded) return;
        _bassPreloaded = true;

        // Register a DllImportResolver on the ManagedBass assembly so its
        // [DllImport("bass")] calls resolve to our lib/ folder. On Windows
        // this is redundant (NativeLibrary.Load is process-global), but on
        // Linux .NET scopes native handles per-assembly — so a library
        // loaded by the Jukebox assembly is invisible to ManagedBass.dll's
        // P/Invoke. The resolver makes it work on both platforms.
        NativeLibrary.SetDllImportResolver(typeof(Bass).Assembly, (name, assembly, path) =>
        {
            string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");

            if (name == "bass")
            {
                if (_bassNativeHandle != IntPtr.Zero)
                    return _bassNativeHandle;

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
                    if (NativeLibrary.TryLoad(fullPath, out _bassNativeHandle))
                    {
                        Debug.WriteLine($"[BASS Engine] Loaded BASS native library from: {fullPath}");
                        return _bassNativeHandle;
                    }
                }

                if (NativeLibrary.TryLoad(fileName, out _bassNativeHandle))
                {
                    Debug.WriteLine($"[BASS Engine] Loaded BASS native library from OS search path: {fileName}");
                    return _bassNativeHandle;
                }

                Debug.WriteLine($"[BASS Engine] BASS native library not found. Looked in: {fullPath} and OS default search path.");
            }

            if (name == "bass_fx")
            {
                if (_bassFxNativeHandle != IntPtr.Zero)
                    return _bassFxNativeHandle;
                string fxFile = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "bass_fx.dll"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        ? "libbass_fx.so"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                            ? "libbass_fx.dylib"
                            : "bass_fx";
                string fxFullPath = Path.Combine(libDir, fxFile);
                if (File.Exists(fxFullPath) && NativeLibrary.TryLoad(fxFullPath, out _bassFxNativeHandle))
                {
                    Debug.WriteLine($"[BASS Engine] Loaded BASS_FX from: {fxFullPath}");
                    return _bassFxNativeHandle;
                }
                if (NativeLibrary.TryLoad(fxFile, out _bassFxNativeHandle))
                {
                    Debug.WriteLine($"[BASS Engine] Loaded BASS_FX from OS search path: {fxFile}");
                    return _bassFxNativeHandle;
                }
                Debug.WriteLine($"[BASS Engine] BASS_FX NOT FOUND. EQ will not work. Looked in: {fxFullPath}");
            }

            return IntPtr.Zero;
        });

        // Preload bass_fx into the process so BASS can find it when
        // ChannelSetFX is called with the PeakEQ effect type. BASS loads
        // bass_fx dynamically via LoadLibrary/dlopen — if we preload it
        // here, BASS finds it already in the process address space.
        // Without this, bass_fx must be on the OS default search path
        // (which it isn't in our flat lib/ layout).
        PreloadBassFxNative();
    }

    private static void PreloadBassFxNative()
    {
        if (_bassFxNativeHandle != IntPtr.Zero) return;

        string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "bass_fx.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "libbass_fx.so"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "libbass_fx.dylib"
                    : "bass_fx";

        // 1) Try the lib/ drop-in folder.
        string fullPath = Path.Combine(libDir, fileName);
        if (File.Exists(fullPath))
        {
            if (NativeLibrary.TryLoad(fullPath, out _bassFxNativeHandle))
            {
                Debug.WriteLine($"[BASS Engine] Loaded BASS_FX native library from: {fullPath}");
                return;
            }
        }

        // 2) Fall back to OS default search path.
        if (NativeLibrary.TryLoad(fileName, out _bassFxNativeHandle))
        {
            Debug.WriteLine($"[BASS Engine] Loaded BASS_FX native library from OS search path: {fileName}");
            return;
        }

        Debug.WriteLine($"[BASS Engine] BASS_FX native library not found. Looked in: {fullPath} and OS default search path. " +
                        "EQ will not be available. Download from https://www.un4seen.com/ (bass_fx add-on).");
    }

    // Use the standard event-invocation pattern with a local copy to prevent
    // NullReferenceException during shutdown.
    //
    // OnDsp runs on BASS's internal audio thread. The dispose path on the
    // UI thread nulls PcmDataAvailable, then calls Bass.StreamFree. Without
    // the local copy, the following race can occur:
    //   1. BASS thread: reads PcmDataAvailable (non-null), passes the check
    //   2. UI thread:   sets PcmDataAvailable = null
    //   3. UI thread:   calls Bass.StreamFree (blocks waiting for BASS thread)
    //   4. BASS thread: reads PcmDataAvailable again for Invoke — now null → NRE
    //
    // A NRE on BASS's audio thread is unrecoverable — it tears down the
    // process. The local-copy pattern captures the delegate reference
    // atomically at entry, so even if the field is nulled mid-call, the
    // local copy is still valid.
    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;
        var handler = PcmDataAvailable;
        if (handler == null) return;

        int count = length / 2;
        short[] pcm = new short[count];
        Marshal.Copy(buffer, pcm, 0, count);
        handler.Invoke(this, pcm);
    }

    private void OnBassEndSync(int handle, int channel, int data, IntPtr user)
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void LoadPlugin(string path)
    {
        if (File.Exists(path))
        {
            int pluginHandle = Bass.PluginLoad(path);
            if (pluginHandle != 0)
            {
                Debug.WriteLine($"[BASS Engine] Loaded plugin successfully: {path}");
            }
            else
            {
                Debug.WriteLine($"[BASS Engine] Failed to load plugin: {path}. Error: {Bass.LastError}");
            }
        }
    }
    #endregion

    #region Dispose
    public void Dispose()
    {
        // Detach listeners first so any in-flight DSP callback sees null
        // and skips the Invoke (see OnDsp local-copy pattern).
        PcmDataAvailable = null;

        if (_bassStream != 0)
        {
            // Explicitly remove DSP and SYNC callbacks before freeing the
            // stream. Bass.StreamFree would auto-remove them, but explicit
            // removal guarantees any in-flight callback has been drained
            // before the stream memory is released. This is the belt-and-
            // suspenders companion to the OnDsp local-copy fix.
            if (_dspHandle != 0)
            {
                try { Bass.ChannelRemoveDSP(_bassStream, _dspHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveDSP failed: {ex.Message}"); }
                _dspHandle = 0;
            }
            if (_endSyncHandle != 0)
            {
                try { Bass.ChannelRemoveSync(_bassStream, _endSyncHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveSync failed: {ex.Message}"); }
                _endSyncHandle = 0;
            }

            try { Bass.StreamFree(_bassStream); }
            catch (Exception ex) { Debug.WriteLine($"[BASS Engine] StreamFree failed: {ex.Message}"); }
            _bassStream = 0;
            _eqFxHandle = 0;
        }

        if (IsAvailable && _ownsBassContext)
        {
            Bass.Free();
        }

        IsAvailable = false;
    }
    #endregion
}

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jukebox.Models;
using Jukebox.Native;
using ManagedBass;

namespace Jukebox.Services;

/// <summary>
/// VGM/VGZ playback engine implementing <see cref="IMediaPlayerEngine"/>.
///
/// Mirrors the architecture of <see cref="BassPlaybackEngine"/>:
///   - Parameterless constructor + <see cref="Initialize"/> method
///   - <see cref="IsAvailable"/> set during Initialize
///   - <see cref="PcmDataAvailable"/> event (NOT on the interface — concrete-class
///     extra, same as BassPlaybackEngine; JukeboxViewModel casts to subscribe)
///   - Volume is 0-100 scale (mapped to libvgm's 0x10000 fixed-point internally)
///   - Duration reported in milliseconds via <see cref="DurationChanged"/>
///   - Position reported in milliseconds via <see cref="GetPositionMs"/>
///
/// Playback pipeline:
///   1. Download the .vgz file via <see cref="VgmRipsCatalogueService.DownloadVgzAsync"/>
///   2. Gunzip in-memory to get the raw .vgm byte stream
///   3. Hand the bytes to libvgm via <c>VgmNative.LoadMemory</c>
///   4. Render PCM in a background thread (16-bit signed, stereo, 44100 Hz)
///   5. Push the PCM into a BASS push stream so playback goes through the
///      normal BASS pipeline — EQ, DSP, and the PCM tap that feeds the
///      visualizer (<see cref="PcmDataAvailable"/> event) all keep working
///
/// The BASS push stream is created via the CreateStream(int, int, BassFlags,
/// StreamProcedure, IntPtr) overload with a StreamProcedure that returns
/// StreamProcedureType.Push — this tells BASS to expect data via StreamPutData
/// rather than pulling it through the callback.
///
/// Native dependency: drop vgm-player.dll / libvgm-player.so / libvgm-player.dylib
/// into <c>&lt;appdir&gt;/lib/</c>. See <see cref="VgmNative"/> for the resolver
/// and the <c>libvgm/README.md</c> file in the root workspace for build instructions.
/// </summary>
public sealed class VgmPlaybackEngine : IMediaPlayerEngine
{
    #region Constants
    private const int SampleRate = 44100;
    private const int Channels = 2;
    // Use 16-bit output — matches what BassPlaybackEngine uses and what the
    // visualizer's PcmDataAvailable event expects (short[]). libvgm's 32-bit
    // mode outputs signed integers, NOT floats, so it's incompatible with
    // BassFlags.Float.
    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int BytesPerSecond = SampleRate * Channels * BytesPerSample;

    // Render 100ms of audio per pass — small enough for low latency,
    // large enough to avoid hammering libvgm with tiny render calls.
    private const int RenderChunkMs = 100;
    private const int RenderChunkBytes = BytesPerSecond / (1000 / RenderChunkMs);

    // libvgm's master volume is 16.16 fixed point: 0x10000 = 1.0 (full volume).
    private const uint FullVolume = 0x10000;

    // Bandwidth in octaves. The old DXParamEQ used 18 semitones (= 1.5 octaves).
    // PeakEQ's fBandwidth is directly in octaves, so 1.5f is the equivalent.
    private const float EqBandwidthOctaves = 1.5f;
    #endregion

    #region Fields
    private VgmNative.PlayerHandle? _player;
    private int _bassStream;
    private int _eqFxHandle;
    private int _endSyncHandle;
    private SyncProcedure? _endSyncProcedure;
    private Thread? _renderThread;
    private CancellationTokenSource? _renderCts;
    private double _volume = 100;
    private double _durationMs;
    private double _seekOffsetMs;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _loopInfinite = false;  // false = play 2 loops then end (triggers PlaybackEnded)
    private DSPProcedure? _dspProcedure;
    private readonly object _renderLock = new();

    // One-shot guard for PlaybackStarted — same pattern as BassPlaybackEngine.
    private int _playbackStartedFired;

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
    #endregion

    #region Public Properties (IMediaPlayerEngine)
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Whether to loop the track infinitely (default true — VGM is often short).
    /// Set this before calling <see cref="PlayAsync"/>.
    /// </summary>
    public bool LoopInfinite
    {
        get => _loopInfinite;
        set => _loopInfinite = value;
    }
    #endregion

    #region Public Events (IMediaPlayerEngine + concrete-class extra)
    public event EventHandler? PlaybackEnded;
    public event EventHandler? PlaybackStarted;
    public event EventHandler<double>? DurationChanged;
#pragma warning disable 0067
    public event EventHandler<string>? MetadataChanged;
#pragma warning restore 0067

    /// <summary>
    /// PCM tap for the visualizer. Mirrors <see cref="BassPlaybackEngine.PcmDataAvailable"/>:
    /// raised from the BASS DSP thread with a buffer of 16-bit stereo samples.
    /// NOT on the <see cref="IMediaPlayerEngine"/> interface — JukeboxViewModel
    /// casts to <c>VgmPlaybackEngine</c> to subscribe, exactly like it does for
    /// <c>BassPlaybackEngine</c>.
    /// </summary>
    public event EventHandler<short[]>? PcmDataAvailable;
    #endregion

    #region Constructor & Initialization
    public VgmPlaybackEngine()
    {
    }

    /// <summary>
    /// Initialize the engine. Called from <c>JukeboxViewModel.InitializeBackendAsync</c>
    /// via <c>Task.Run</c> — same pattern as <c>BassPlaybackEngine.Initialize</c>.
    /// </summary>
    public void Initialize()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VGM Engine] Initializing libvgm...");

        try
        {
            // Load the library and resolve all function pointers.
            // This tries both vgm-player.dll and vgm-player_Win64.dll in lib/.
            VgmNative.EnsureLoaded();
            IsAvailable = VgmNative.IsAvailable();

            if (IsAvailable)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VGM Engine] libvgm loaded successfully in {sw.ElapsedMilliseconds}ms.");
            }
            else
            {
                var libDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VGM Engine] vgm-player not loadable from {libDir}. " +
                                "VGM/VGZ playback will be unavailable. " +
                                "See Patches/BUILD-LIBVGM.md for build instructions.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VGM Engine] Init Exception: {ex.Message}");
            IsAvailable = false;
        }
    }
    #endregion

    #region IMediaPlayerEngine Methods
    public async Task PlayAsync(JukeboxTrack track)
    {
        if (!IsAvailable)
        {
            await Task.Run(() => Initialize()).ConfigureAwait(true);
        }

        if (!IsAvailable)
        {
            Debug.WriteLine("[VGM Engine] PlayAsync called but vgm-player is not available.");
            throw new InvalidOperationException("VGM/VGZ playback is unavailable. vgm-player was not found in the lib/ folder.\n\n" +
                "See Patches/BUILD-LIBVGM.md for build instructions.");
        }

        Stop();

        _seekOffsetMs = 0;

        var vgmUrl = track.FilePath;
        if (string.IsNullOrEmpty(vgmUrl))
        {
            Debug.WriteLine("[VGM Engine] PlayAsync: track.FilePath is empty.");
            return;
        }

        try
        {
            // 1. Read the file bytes locally and gunzip if needed.
            Debug.WriteLine($"[VGM Engine] Reading file bytes from {vgmUrl}...");
            byte[] vgzBytes;
            if (vgmUrl.Contains('|'))
            {
                vgzBytes = await Task.Run(() =>
                {
                    var parts = vgmUrl.Split('|', 2);
                    var zipPath = parts[0];
                    var entryName = parts[1];
                    using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
                    var entry = archive.GetEntry(entryName);
                    if (entry == null)
                        throw new FileNotFoundException($"Entry '{entryName}' not found in zip archive.");
                    using var entryStream = entry.Open();
                    using var memoryStream = new MemoryStream();
                    entryStream.CopyTo(memoryStream);
                    return memoryStream.ToArray();
                }).ConfigureAwait(true);
            }
            else
            {
                vgzBytes = await Task.Run(() => File.ReadAllBytes(vgmUrl)).ConfigureAwait(true);
            }
            var vgmBytes = Gunzip(vgzBytes);
            Debug.WriteLine($"[VGM Engine] Loaded {vgmBytes.Length} bytes of VGM data.");

            // 2. Create a new PlayerA handle (one per track — cheap to create/destroy).
            _player = VgmNative.CreatePlayer();
            if (_player.IsInvalid)
                throw new InvalidOperationException("vgm_player_create returned NULL.");

            // 3. Configure output: 44100 Hz, stereo, 16-bit signed PCM.
            if (VgmNative.SetOutput(_player, SampleRate, Channels, BitsPerSample) != 0)
                throw new InvalidOperationException("vgm_player_set_output failed.");

            // 4. Configure looping + volume.
            // Set loop count: 0 = infinite, 1 = play once, 2 = play with one loop, etc.
            // Default to 2 (play with one loop) so tracks end naturally and trigger
            // PlaybackEnded → next track auto-advances. Set LoopInfinite=true for
            // continuous looping (user must manually skip).
            VgmNative.SetLoopCount(_player, _loopInfinite ? 0u : 2u);
            VgmNative.SetVolume(_player, (uint)(_volume / 100.0 * FullVolume));

            // 5. Load the VGM data (already gunzipped from .vgz).
            if (VgmNative.LoadMemory(_player, vgmBytes) != 0)
                throw new InvalidOperationException("vgm_player_load_memory failed — VGM data may be corrupt or unsupported.");

            // 6. Compute duration (libvgm reports total samples for one play-through).
            var totalSamples = VgmNative.GetTotalSamples(_player);
            _durationMs = (double)totalSamples / SampleRate * 1000.0;
            DurationChanged?.Invoke(this, _durationMs);

            // Update the track's Length if it wasn't already set.
            if (track.Length == TimeSpan.Zero)
                track.Length = TimeSpan.FromMilliseconds(_durationMs);

            // 7. Create a BASS push stream — stereo 16-bit at 44100 Hz.
            //    ManagedBass has a dedicated overload for push streams:
            //      Bass.CreateStream(freq, chans, flags, StreamProcedureType.Push)
            //    No StreamProcedure delegate is needed — data is pushed via
            //    Bass.StreamPutData. 16-bit signed PCM matches libvgm's output
            //    and what the visualizer expects (short[]).
            _bassStream = Bass.CreateStream(
                SampleRate, Channels,
                BassFlags.Default,
                StreamProcedureType.Push);

            if (_bassStream == 0)
            {
                var err = Bass.LastError;
                throw new InvalidOperationException($"BASS push stream creation failed: {err}");
            }

            // 8. Wire up the PCM tap so the visualizer gets fed.
            //    Same pattern as BassPlaybackEngine.OnDsp — runs on BASS's thread.
            _dspProcedure = new DSPProcedure(OnBassPcmTap);
            Bass.ChannelSetDSP(_bassStream, _dspProcedure, IntPtr.Zero, 0);

            // Register the end sync callback so that PlaybackEnded is fired when all pushed audio is played.
            _endSyncProcedure = new SyncProcedure(OnBassEndSync);
            _endSyncHandle = Bass.ChannelSetSync(_bassStream, SyncFlags.End, 0, _endSyncProcedure, IntPtr.Zero);

            // 9. Start libvgm and the render thread that keeps the push buffer fed.
            if (VgmNative.Start(_player) != 0)
                throw new InvalidOperationException("vgm_player_start failed.");
            _isPlaying = true;
            _isPaused = false;

            _renderCts = new CancellationTokenSource();
            _renderThread = new Thread(() => RenderLoop(_renderCts.Token))
            {
                Name = "libvgm-render",
                IsBackground = true,
                // Normal priority — AboveNormal starves the UI thread and
                // the Windows compositor (DWM), causing "everything slows to
                // a crawl" symptoms. Audio rendering is not CPU-intensive
                // (libvgm renders faster than realtime), so Normal is plenty.
                Priority = ThreadPriority.Normal,
            };
            _renderThread.Start();

            // 10. Apply current volume + start BASS playback on the push stream.
            Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, _volume / 100.0);
            Bass.ChannelPlay(_bassStream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VGM Engine] PlayAsync failed: {ex.Message}");
            throw;
        }
    }

    public void Pause()
    {
        if (!_isPlaying) return;
        _isPaused = true;
        if (_player is not null) VgmNative.Pause(_player, paused: true);
        if (_bassStream != 0) Bass.ChannelPause(_bassStream);
    }

    public void Resume()
    {
        if (!_isPlaying || !_isPaused) return;
        _isPaused = false;
        if (_player is not null) VgmNative.Pause(_player, paused: false);
        if (_bassStream != 0) Bass.ChannelPlay(_bassStream);
    }

    public void Stop()
    {
        // Reset the one-shot PlaybackStarted guard so the next PlayAsync can
        // re-fire it when fresh PCM data arrives.
        Interlocked.Exchange(ref _playbackStartedFired, 0);

        _isPlaying = false;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;

        if (_renderThread is not null && _renderThread.IsAlive)
            _renderThread.Join(TimeSpan.FromMilliseconds(500));
        _renderThread = null;

        if (_player is not null)
        {
            try { VgmNative.Stop(_player); } catch { /* fine */ }
        }

        if (_bassStream != 0)
        {
            if (_endSyncHandle != 0)
            {
                Bass.ChannelRemoveSync(_bassStream, _endSyncHandle);
                _endSyncHandle = 0;
            }
            // StreamFree stops playback AND frees the stream — no separate
            // StreamStop needed (matches BassPlaybackEngine.Stop pattern).
            try { Bass.StreamFree(_bassStream); } catch { }
            _bassStream = 0;
            _eqFxHandle = 0;
        }

        // Release the delegate reference so GC can collect it. The DSP
        // procedure must stay alive while the stream is alive (BASS holds
        // raw pointers to it), but once StreamFree returns it's safe.
        _dspProcedure = null;
        _endSyncProcedure = null;

        // Dispose the player handle — we create a fresh one per track.
        _player?.Dispose();
        _player = null;
        _durationMs = 0;
        _seekOffsetMs = 0;
    }

    public void Seek(double positionMs)
    {
        if (_bassStream == 0 || _player == null) return;

        lock (_renderLock)
        {
            try
            {
                uint samplePosition = (uint)(positionMs / 1000.0 * SampleRate);
                VgmNative.Seek(_player, samplePosition);
                
                // Flush the BASS push stream (resets its position count to 0)
                Bass.ChannelSetPosition(_bassStream, 0);
                
                _seekOffsetMs = positionMs;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VGM Engine] Seek failed: {ex.Message}");
            }
        }
    }

    public double GetPositionMs()
    {
        if (_bassStream == 0) return -1;
        try
        {
            var pos = Bass.ChannelGetPosition(_bassStream);
            if (pos < 0) return -1;
            return _seekOffsetMs + (Bass.ChannelBytes2Seconds(_bassStream, pos) * 1000.0);
        }
        catch { return -1; }
    }

    public void SetVolume(double volume)
    {
        _volume = volume;
        // Apply to both libvgm master volume and the BASS stream — keeps
        // the two in sync. libvgm's volume is 0x10000 = 1.0.
        if (_player is not null)
            VgmNative.SetVolume(_player, (uint)(volume / 100.0 * FullVolume));
        if (_bassStream != 0)
            Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, volume / 100.0);
    }
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
            Debug.WriteLine($"[VGM Engine] ChannelSetFX(PeakEQ) failed. Error: {Bass.LastError}.");
            return;
        }
        Debug.WriteLine($"[VGM Engine] EQ FX created (handle={_eqFxHandle}).");

        // Set parameters for each band.
        for (int i = 0; i < Constants.EqBandCount; i++)
        {
            if (gains.Length > i && centerFrequencies.Length > i)
            {
                SetPeakEqParameters(i, centerFrequencies[i], (float)gains[i]);
            }
        }
    }

    public void UpdateEqBand(int index, double gain, float centerFrequency)
    {
        if (!IsAvailable || _bassStream == 0 || index < 0 || index >= Constants.EqBandCount) return;

        if (_eqFxHandle == 0)
        {
            _eqFxHandle = Bass.ChannelSetFX(_bassStream, EffectType.PeakEQ, 0);
            if (_eqFxHandle == 0)
            {
                Debug.WriteLine($"[VGM Engine] UpdateEqBand: ChannelSetFX failed. Error: {Bass.LastError}.");
                return;
            }
        }

        SetPeakEqParameters(index, centerFrequency, (float)gain);
    }

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

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PeakEqParams>());
        try
        {
            Marshal.StructureToPtr(p, ptr, false);
            if (!Bass.FXSetParameters(_eqFxHandle, ptr))
            {
                Debug.WriteLine($"[VGM Engine] FXSetParameters failed for band {band} (freq={centerFreq}Hz, gain={gainDb}dB). Error: {Bass.LastError}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
    #endregion

    #region IDisposable
    public void Dispose()
    {
        // Detach listeners first so any in-flight DSP callback sees null
        // and skips the Invoke (same pattern as BassPlaybackEngine.Dispose).
        PcmDataAvailable = null;
        Stop();
    }
    #endregion

    #region Private: Render Loop & PCM Tap
    /// <summary>
    /// Background render loop: keeps the BASS push buffer fed with freshly
    /// rendered PCM. Stops when the cancellation token fires or libvgm
    /// reports it's done playing.
    /// </summary>
    private void RenderLoop(CancellationToken ct)
    {
        // Allocate the render buffer once and reuse it — the shim's Render
        // function fills it in place. Pin it so BASS can read from it
        // without GC interference.
        var buffer = new byte[RenderChunkBytes];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var totalRendered = 0u;
        var iterations = 0;
        try
        {
            Debug.WriteLine($"[VGM Engine] Render loop started. Buffer: {buffer.Length} bytes ({RenderChunkMs}ms)");

            while (!ct.IsCancellationRequested && _isPlaying && _player is not null)
            {
                if (_isPaused)
                {
                    Thread.Sleep(50);
                    continue;
                }

                uint rendered = 0;
                int queued = 0;

                lock (_renderLock)
                {
                    if (!_isPlaying || _player is null || ct.IsCancellationRequested)
                        break;

                    rendered = VgmNative.Render(_player, handle.AddrOfPinnedObject(), (uint)buffer.Length);
                    
                    if (rendered > 0)
                    {
                        totalRendered += rendered;
                        queued = Bass.StreamPutData(_bassStream, handle.AddrOfPinnedObject(), (int)rendered);
                    }
                }

                iterations++;

                if (rendered == 0)
                {
                    Debug.WriteLine($"[VGM Engine] Render returned 0 (track ended). Total: {totalRendered} bytes, {iterations} iterations.");
                    _isPlaying = false;
                    
                    // Signal the end of the BASS push stream so that it plays out the remaining buffer and triggers SyncFlags.End
                    Bass.StreamPutData(_bassStream, IntPtr.Zero, unchecked((int)0x80000000));
                    break;
                }

                // Adaptive sleep to avoid CPU spin. libvgm renders much faster
                // than realtime, so if we don't sleep, the loop spins at 100%
                // CPU and starves the UI/compositor. Pace based on how much
                // data BASS has buffered:
                //   - queued > 500ms: plenty buffered, sleep long
                //   - queued > 200ms: comfortable, sleep briefly
                //   - queued > 0:     low, yield but don't sleep
                //   - queued <= 0:    error or empty, short sleep
                if (queued > BytesPerSecond / 2)       // > 500ms buffered
                    Thread.Sleep(RenderChunkMs);       // sleep 100ms
                else if (queued > BytesPerSecond / 5)  // > 200ms buffered
                    Thread.Sleep(RenderChunkMs / 2);   // sleep 50ms
                else if (queued > 0)                   // low buffer
                    Thread.Sleep(5);                   // sleep 5ms — don't spin
                else
                    Thread.Sleep(10);                  // error recovery
            }

            Debug.WriteLine($"[VGM Engine] Render loop exited. cancelled={ct.IsCancellationRequested}, isPlaying={_isPlaying}, iterations={iterations}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VGM Engine] Render loop crashed: {ex.Message}");
            _isPlaying = false;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>BASS DSP callback that taps PCM data for the visualizer.</summary>
    private void OnBassPcmTap(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;

        // First PCM buffer => raise PlaybackStarted (one-shot per stream).
        // Same Interlocked pattern as BassPlaybackEngine.OnDsp.
        if (Interlocked.CompareExchange(ref _playbackStartedFired, 1, 0) == 0)
        {
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }

        var handler = PcmDataAvailable;
        if (handler == null) return;

        // BASS gives us 16-bit signed samples (because we created the stream
        // without BassFlags.Float). Copy directly to short[] — same pattern
        // as BassPlaybackEngine.OnDsp.
        int count = length / 2;
        short[] pcm = new short[count];
        Marshal.Copy(buffer, pcm, 0, count);
        handler.Invoke(this, pcm);
    }

    /// <summary>Gunzip a .vgz byte array to get the raw .vgm bytes.</summary>
    private static byte[] Gunzip(byte[] vgz)
    {
        // vgz is just gzipped vgm. Detect the gzip magic (1f 8b) and decompress.
        if (vgz.Length < 2 || vgz[0] != 0x1f || vgz[1] != 0x8b)
        {
            // Not gzipped — assume it's already raw vgm.
            return vgz;
        }

        using var ms = new MemoryStream(vgz);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream(vgz.Length * 4);
        gz.CopyTo(outMs);
        return outMs.ToArray();
    }

    private void OnBassEndSync(int handle, int channel, int data, IntPtr user)
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    public static double GetVgmDurationMs(string filePath)
    {
        try
        {
            if (filePath.Contains('|'))
            {
                var parts = filePath.Split('|', 2);
                var zipPath = parts[0];
                var entryName = parts[1];
                using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry(entryName);
                if (entry == null) return 0;
                using var entryStream = entry.Open();
                using var memoryStream = new MemoryStream();
                entryStream.CopyTo(memoryStream);
                memoryStream.Position = 0;
                return GetVgmDurationMs(memoryStream);
            }
            else
            {
                using var fileStream = File.OpenRead(filePath);
                return GetVgmDurationMs(fileStream);
            }
        }
        catch
        {
            return 0;
        }
    }

    public static double GetVgmDurationMs(Stream fileStream)
    {
        try
        {
            byte[] magic = new byte[2];
            if (fileStream.Read(magic, 0, 2) != 2)
                return 0;

            fileStream.Position = 0;
            using Stream stream = (magic[0] == 0x1f && magic[1] == 0x8b)
                ? new GZipStream(fileStream, CompressionMode.Decompress)
                : fileStream;

            byte[] header = new byte[36];
            int bytesRead = 0;
            while (bytesRead < 36)
            {
                int read = stream.Read(header, bytesRead, 36 - bytesRead);
                if (read <= 0)
                    break;
                bytesRead += read;
            }

            if (bytesRead < 36)
                return 0;

            // Check VGM magic "Vgm " (0x56 0x67 0x6d 0x20)
            if (header[0] != 0x56 || header[1] != 0x67 || header[2] != 0x6d || header[3] != 0x20)
                return 0;

            uint totalSamples = BitConverter.ToUInt32(header, 24);
            uint loopOffset = BitConverter.ToUInt32(header, 28);
            uint loopSamples = BitConverter.ToUInt32(header, 32);

            // If the track loops, add the loop samples once (since player plays with loop count of 2)
            if (loopOffset != 0 && loopSamples > 0)
            {
                totalSamples += loopSamples;
            }

            return (double)totalSamples / 44100.0 * 1000.0;
        }
        catch
        {
            return 0;
        }
    }
    #endregion
}

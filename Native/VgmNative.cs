using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Jukebox.Native;

/// <summary>
/// P/Invoke wrapper for the libvgm C API shim (vgm_capi.cpp).
///
/// libvgm's public API is C++ classes (PlayerA, VGMPlayer) — not directly
/// P/Invoke-able. We ship a thin C++ shim (vgm_capi.cpp / vgm_capi.h) that
/// wraps PlayerA behind extern "C" flat functions operating on an opaque
/// handle. This class declares the C# side of that shim.
///
/// LOADING STRATEGY:
/// We do NOT use SetDllImportResolver because MpvNative already set one on
/// this assembly, and only one resolver is allowed per assembly. Instead,
/// we load the library manually via NativeLibrary.Load (trying both
/// "vgm-player.dll" and "vgm-player_Win64.dll" in the lib/ folder), then
/// resolve each function via NativeLibrary.GetExport and wrap it in a delegate.
///
/// This approach is completely self-contained and works regardless of the
/// DLL's filename — no renaming needed.
/// </summary>
internal static class VgmNative
{
    private const string LibName = "vgm-player";

    // The loaded library handle, set once by EnsureLoaded().
    private static IntPtr _libHandle = IntPtr.Zero;
    private static bool _loadAttempted = false;
    private static readonly object _loadLock = new();

    // ---- Delegate types (match the C function signatures) ----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr vgm_player_create_t();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void vgm_player_destroy_t(IntPtr player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int vgm_player_set_output_t(IntPtr player, uint sampleRate, uint channels, uint bits);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void vgm_player_set_volume_t(IntPtr player, uint volume);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void vgm_player_set_loop_count_t(IntPtr player, uint loopCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int vgm_player_load_file_t(IntPtr player, string filePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int vgm_player_load_memory_t(IntPtr player, byte[] data, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int vgm_player_start_t(IntPtr player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void vgm_player_stop_t(IntPtr player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void vgm_player_pause_t(IntPtr player, int paused);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void vgm_player_unload_t(IntPtr player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint vgm_player_render_t(IntPtr player, IntPtr buffer, uint byteCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint vgm_player_get_position_samples_t(IntPtr player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint vgm_player_get_total_samples_t(IntPtr player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int vgm_player_seek_t(IntPtr player, uint samplePosition);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint vgm_player_is_playing_t(IntPtr player);

    // ---- Delegate instances (resolved once at load time) ----

    private static vgm_player_create_t? _create;
    private static vgm_player_destroy_t? _destroy;
    private static vgm_player_set_output_t? _setOutput;
    private static vgm_player_set_volume_t? _setVolume;
    private static vgm_player_set_loop_count_t? _setLoopCount;
    private static vgm_player_load_file_t? _loadFile;
    private static vgm_player_load_memory_t? _loadMemory;
    private static vgm_player_start_t? _start;
    private static vgm_player_stop_t? _stop;
    private static vgm_player_pause_t? _pause;
    private static vgm_player_unload_t? _unload;
    private static vgm_player_render_t? _render;
    private static vgm_player_get_position_samples_t? _getPosition;
    private static vgm_player_get_total_samples_t? _getTotal;
    private static vgm_player_is_playing_t? _isPlaying;
    private static vgm_player_seek_t? _seek;

    /// <summary>
    /// Load the vgm-player library and resolve all function pointers.
    /// Called once at startup from VgmPlaybackEngine.Initialize().
    /// Thread-safe — uses a lock + flag to ensure it only runs once.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_loadAttempted) return;
        lock (_loadLock)
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            try
            {
                var libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
                var names = GetCandidateLibraryNames();

                foreach (var name in names)
                {
                    var fullPath = Path.Combine(libDir, name);
                    if (!File.Exists(fullPath))
                    {
                        Debug.WriteLine($"[VGM Native] Not found: {fullPath}");
                        continue;
                    }

                    Debug.WriteLine($"[VGM Native] Found: {fullPath}, loading...");

                    // Load with SafeDirectories | UseDllDirectoryForDependencies
                    // so Windows finds vgm-emu and vgm-utils (dependencies) in
                    // the same lib/ folder.
                    if (NativeLibrary.TryLoad(fullPath, typeof(VgmNative).Assembly,
                        DllImportSearchPath.SafeDirectories | DllImportSearchPath.UseDllDirectoryForDependencies,
                        out _libHandle))
                    {
                        Debug.WriteLine($"[VGM Native] Library loaded successfully. Resolving exports...");

                        // Resolve all function pointers.
                        _create = GetExport<vgm_player_create_t>("vgm_player_create");
                        _destroy = GetExport<vgm_player_destroy_t>("vgm_player_destroy");
                        _setOutput = GetExport<vgm_player_set_output_t>("vgm_player_set_output");
                        _setVolume = GetExport<vgm_player_set_volume_t>("vgm_player_set_volume");
                        _setLoopCount = GetExport<vgm_player_set_loop_count_t>("vgm_player_set_loop_count");
                        _loadFile = GetExport<vgm_player_load_file_t>("vgm_player_load_file");
                        _loadMemory = GetExport<vgm_player_load_memory_t>("vgm_player_load_memory");
                        _start = GetExport<vgm_player_start_t>("vgm_player_start");
                        _stop = GetExport<vgm_player_stop_t>("vgm_player_stop");
                        _pause = GetExport<vgm_player_pause_t>("vgm_player_pause");
                        _unload = GetExport<vgm_player_unload_t>("vgm_player_unload");
                        _render = GetExport<vgm_player_render_t>("vgm_player_render");
                        _getPosition = GetExport<vgm_player_get_position_samples_t>("vgm_player_get_position_samples");
                        _getTotal = GetExport<vgm_player_get_total_samples_t>("vgm_player_get_total_samples");
                        _isPlaying = GetExport<vgm_player_is_playing_t>("vgm_player_is_playing");
                        _seek = GetExport<vgm_player_seek_t>("vgm_player_seek");

                        Debug.WriteLine("[VGM Native] All exports resolved successfully.");
                        return;
                    }
                    else
                    {
                        Debug.WriteLine($"[VGM Native] TryLoad failed for {fullPath}");
                    }
                }

                // Last resort: try OS default search path.
                foreach (var name in names)
                {
                    if (NativeLibrary.TryLoad(name, out _libHandle))
                    {
                        Debug.WriteLine($"[VGM Native] Loaded from system path: {name}");
                        // Resolve exports (same as above — duplicate for simplicity).
                        _create = GetExport<vgm_player_create_t>("vgm_player_create");
                        _destroy = GetExport<vgm_player_destroy_t>("vgm_player_destroy");
                        _setOutput = GetExport<vgm_player_set_output_t>("vgm_player_set_output");
                        _setVolume = GetExport<vgm_player_set_volume_t>("vgm_player_set_volume");
                        _setLoopCount = GetExport<vgm_player_set_loop_count_t>("vgm_player_set_loop_count");
                        _loadFile = GetExport<vgm_player_load_file_t>("vgm_player_load_file");
                        _loadMemory = GetExport<vgm_player_load_memory_t>("vgm_player_load_memory");
                        _start = GetExport<vgm_player_start_t>("vgm_player_start");
                        _stop = GetExport<vgm_player_stop_t>("vgm_player_stop");
                        _pause = GetExport<vgm_player_pause_t>("vgm_player_pause");
                        _unload = GetExport<vgm_player_unload_t>("vgm_player_unload");
                        _render = GetExport<vgm_player_render_t>("vgm_player_render");
                        _getPosition = GetExport<vgm_player_get_position_samples_t>("vgm_player_get_position_samples");
                        _getTotal = GetExport<vgm_player_get_total_samples_t>("vgm_player_get_total_samples");
                        _isPlaying = GetExport<vgm_player_is_playing_t>("vgm_player_is_playing");
                        _seek = GetExport<vgm_player_seek_t>("vgm_player_seek");
                        return;
                    }
                }

                Debug.WriteLine("[VGM Native] Library not found in any location.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VGM Native] EnsureLoaded failed: {ex.Message}");
            }
        }
    }

    private static T GetExport<T>(string name) where T : Delegate
    {
        if (_libHandle == IntPtr.Zero)
            throw new InvalidOperationException("Library not loaded");
        var ptr = NativeLibrary.GetExport(_libHandle, name);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"Export not found: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static string[] GetCandidateLibraryNames() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ["vgm-player.dll", "vgm-player_Win64.dll"]
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? ["libvgm-player.so", "libvgm-player.so.1"]
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? ["libvgm-player.dylib"]
                    : ["vgm-player"];

    /// <summary>Probe whether vgm-player is loadable.</summary>
    public static bool IsAvailable()
    {
        EnsureLoaded();
        return _libHandle != IntPtr.Zero && _create != null;
    }

    // ---- Public wrapper API ----

    /// <summary>Wraps the opaque native handle. Always dispose when done.</summary>
    public sealed class PlayerHandle : SafeHandle
    {
        public PlayerHandle() : base(IntPtr.Zero, ownsHandle: true) { }

        internal void SetNativeHandle(IntPtr h) => SetHandle(h);

        public override bool IsInvalid => IsClosed || handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            if (!IsClosed && handle != IntPtr.Zero)
            {
                try { _destroy?.Invoke(handle); } catch { /* swallow */ }
            }
            return true;
        }
    }

    public static PlayerHandle CreatePlayer()
    {
        if (_create == null) throw new InvalidOperationException("VgmNative not loaded");
        var h = _create();
        var handle = new PlayerHandle();
        handle.SetNativeHandle(h);
        return handle;
    }

    public static int SetOutput(PlayerHandle player, uint sampleRate, uint channels, uint bits)
        => _setOutput!(player.DangerousGetHandle(), sampleRate, channels, bits);

    public static void SetVolume(PlayerHandle player, uint volume)
        => _setVolume!(player.DangerousGetHandle(), volume);

    public static void SetLoopCount(PlayerHandle player, uint loopCount)
        => _setLoopCount!(player.DangerousGetHandle(), loopCount);

    public static int LoadFile(PlayerHandle player, string filePath)
        => _loadFile!(player.DangerousGetHandle(), filePath);

    public static int LoadMemory(PlayerHandle player, byte[] data)
        => _loadMemory!(player.DangerousGetHandle(), data, (uint)data.Length);

    public static int Start(PlayerHandle player)
        => _start!(player.DangerousGetHandle());

    public static void Stop(PlayerHandle player)
        => _stop!(player.DangerousGetHandle());

    public static void Pause(PlayerHandle player, bool paused)
        => _pause!(player.DangerousGetHandle(), paused ? 1 : 0);

    public static void Unload(PlayerHandle player)
        => _unload!(player.DangerousGetHandle());

    public static uint Render(PlayerHandle player, IntPtr buffer, uint byteCount)
        => _render!(player.DangerousGetHandle(), buffer, byteCount);

    public static uint GetPositionSamples(PlayerHandle player)
        => _getPosition!(player.DangerousGetHandle());

    public static int Seek(PlayerHandle player, uint samplePosition)
        => _seek!(player.DangerousGetHandle(), samplePosition);

    public static uint GetTotalSamples(PlayerHandle player)
        => _getTotal!(player.DangerousGetHandle());

    public static bool IsPlaying(PlayerHandle player)
        => _isPlaying!(player.DangerousGetHandle()) != 0;
}

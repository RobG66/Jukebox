using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Jukebox.Mpv;

/// <summary>
/// P/Invoke declarations for libmpv (the C client API).
/// Native library: libmpv-2.dll (Windows), libmpv.so.2 (Linux), libmpv.2.dylib (macOS).
/// </summary>
/// <remarks>
/// Only the functions we actually use are declared. The full libmpv API
/// has ~100 functions — we need about 20.
///
/// The native binary is loaded from <c>&lt;appdir&gt;/lib/</c> first
/// (flat — Windows .dll and Linux .so coexist by extension). If not
/// found there, falls back to the OS default search path so the user
/// can also install libmpv system-wide on Linux if they prefer.
/// </remarks>
internal static class MpvNative
{
    private const string LibName = "libmpv-2";
    private static IntPtr _mpvHandle = IntPtr.Zero;
    private static readonly object _mpvLock = new();

    public static IntPtr ResolveMpv(Assembly assembly)
    {
        if (_mpvHandle != IntPtr.Zero) return _mpvHandle;
        lock (_mpvLock)
        {
            if (_mpvHandle != IntPtr.Zero) return _mpvHandle;

            string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
            string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "libmpv-2.dll"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "libmpv.so.2"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                        ? "libmpv.2.dylib"
                        : "libmpv";

            // 1) Try the lib/ drop-in folder (canonical location).
            string fullPath = Path.Combine(libDir, fileName);
            if (File.Exists(fullPath))
            {
                _mpvHandle = NativeLibrary.Load(fullPath, assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
                if (_mpvHandle != IntPtr.Zero) return _mpvHandle;
            }

            // 2) Fall back to OS default search path (lets Linux users
            //    use `apt install libmpv-dev` if they prefer).
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _mpvHandle = NativeLibrary.Load("libmpv-2.dll");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var h = NativeLibrary.Load("libmpv.so.2");
                if (h == IntPtr.Zero) h = NativeLibrary.Load("libmpv.so.1");
                if (h == IntPtr.Zero) h = NativeLibrary.Load("libmpv.so");
                _mpvHandle = h;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _mpvHandle = NativeLibrary.Load("libmpv.2.dylib");
            }

            return _mpvHandle;
        }
    }

    // ── Handle types ──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr MpvGetProcAddressDelegate(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name);

    // ── Core API ──
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr mpv);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr mpv);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create_client(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string name);

    // ── Command API ──
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr mpv, IntPtr[] args);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command_string(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string args);

    // ── Property API ──
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_error_string(int error);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string name, MpvFormat format, ref double data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string name, MpvFormat format, ref int data);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr mpv, [MarshalAs(UnmanagedType.LPStr)] string name, MpvFormat format, ref double data);

    // ── Property observation ──
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_observe_property(IntPtr mpv, ulong reply_userdata, [MarshalAs(UnmanagedType.LPStr)] string name, MpvFormat format);

    // ── Event API ──
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr mpv, double timeout);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_wakeup(IntPtr mpv);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_request_log_messages(
        IntPtr mpv,
        [MarshalAs(UnmanagedType.LPStr)] string min_level);

    // ── Render API ──
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, IntPtr params_ptr);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_render_context_set_update_callback(IntPtr ctx, IntPtr callback, IntPtr callback_ctx);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong mpv_render_context_update(IntPtr ctx);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_render_context_render(IntPtr ctx, IntPtr params_ptr);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_render_context_free(IntPtr ctx);

    // ── Render API constants ──
    internal const string MPV_RENDER_API_TYPE_OPENGL = "opengl";

    // mpv_render_param_type
    internal const int MPV_RENDER_PARAM_INVALID = 0;
    internal const int MPV_RENDER_PARAM_API_TYPE = 1;
    internal const int MPV_RENDER_PARAM_OPENGL_INIT_PARAMS = 2;
    internal const int MPV_RENDER_PARAM_OPENGL_FBO = 3;
    internal const int MPV_RENDER_PARAM_FLIP_Y = 4;
    internal const int MPV_RENDER_PARAM_DEPTH = 5;
    internal const int MPV_RENDER_PARAM_ICC_PROFILE = 6;
    internal const int MPV_RENDER_PARAM_AMBIENT_LIGHT = 7;
    internal const int MPV_RENDER_PARAM_X11_DISPLAY = 8;
    internal const int MPV_RENDER_PARAM_WL_DISPLAY = 9;
    internal const int MPV_RENDER_PARAM_ADVANCED_CONTROL = 10;

    // mpv_render_update_flag
    internal const ulong MPV_RENDER_UPDATE_FRAME = 1;
}

/// <summary>
/// libmpv property/event formats.
/// </summary>
public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

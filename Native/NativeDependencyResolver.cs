using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Jukebox.Native;
using Jukebox.Mpv;

namespace Jukebox;

internal static class NativeDependencyResolver
{
    private static bool _registered;
    private static readonly object _lock = new();

    public static void Register()
    {
        if (_registered) return;
        lock (_lock)
        {
            if (_registered) return;
            _registered = true;

            NativeLibrary.SetDllImportResolver(typeof(NativeDependencyResolver).Assembly, ResolveDll);
        }
    }

    private static IntPtr ResolveDll(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name == "bass")
        {
            BassNative.EnsureLoaded();
            return BassNative.BassHandle;
        }
        if (name == "bass_fx")
        {
            BassNative.EnsureLoaded();
            return BassNative.BassFxHandle;
        }
        if (name == "libmpv-2")
        {
            return MpvNative.ResolveMpv(assembly);
        }
        return IntPtr.Zero;
    }
}

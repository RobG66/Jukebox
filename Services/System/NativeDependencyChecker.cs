namespace Jukebox.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

/// <summary>
/// Checks the <c>lib/</c> folder at startup for required host
/// native runtime libraries. Produces a clear, user-facing report of
/// what's missing — same format every time, listing the missing files,
/// where to put them, and where to find download instructions.
/// </summary>
/// <remarks>
/// The check is filesystem-only — it just verifies the files exist in
/// <c>&lt;appdir&gt;/lib/</c>. It does NOT verify checksums or attempt
/// to load the libraries (that's the job of the individual loaders:
/// <c>MpvNative.cs</c> and <c>BassPlaybackEngine.cs::PreloadBassNative</c>).
///
/// Required libraries (bass, bass_fx, libmpv) — if missing, the app shows an
/// error dialog at startup and the user must address them before audio
/// or video playback will work.
/// </remarks>
public static class NativeDependencyChecker
{
    /// <summary>
    /// Run the check and return a formatted report. If everything is
    /// present, returns null (no report needed).
    /// </summary>
    /// <param name="pathProvider">Path provider (defaults to the singleton).</param>
    /// <returns>A user-facing error report string, or null if all required libs are present.</returns>
    public static string? CheckForMissingRequired(IPathProvider? pathProvider = null)
    {
        pathProvider ??= PathProvider.Current;
        var libDir = pathProvider.NativeLibDirectory;
        var missingRequired = new List<ExpectedLibrary>();

        foreach (var lib in GetExpectedLibraries())
        {
            string fullPath = Path.Combine(libDir, lib.FileName);
            if (File.Exists(fullPath)) continue;

            if (lib.IsRequired)
                missingRequired.Add(lib);
        }

        // If all required libs are present, no report.
        if (missingRequired.Count == 0) return null;

        return FormatReport(libDir, missingRequired);
    }

    /// <summary>
    /// The list of libraries the Jukebox expects to find in lib/,
    /// filtered to the current platform. Required libraries will block
    /// playback if missing; optional ones only affect the visualizer.
    /// </summary>
    private static IEnumerable<ExpectedLibrary> GetExpectedLibraries()
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // ── Required: BASS (audio) ──
        if (isWindows)
        {
            yield return new ExpectedLibrary(
                "bass.dll",
                IsRequired: true,
                "BASS audio library",
                "https://www.un4seen.com/ — download bass24.zip (64-bit)");
        }
        else if (isLinux)
        {
            yield return new ExpectedLibrary(
                "libbass.so",
                IsRequired: true,
                "BASS audio library",
                "https://www.un4seen.com/ — download bass24-linux.zip");
        }
        else if (isMac)
        {
            yield return new ExpectedLibrary(
                "libbass.dylib",
                IsRequired: true,
                "BASS audio library",
                "https://www.un4seen.com/ — download bass24-osx.zip");
        }

        // ── Required: BASS_FX (audio EQ effects) ──
        // NEW: BASS_FX is the cross-platform DSP effects add-on for BASS.
        // It provides the PeakEQ effect used by the 10-band equalizer.
        // Without it, EQ sliders have no audio effect on any platform.
        // Download from the same un4seen.com page as BASS (bass_fx add-on).
        if (isWindows)
        {
            yield return new ExpectedLibrary(
                "bass_fx.dll",
                IsRequired: true,
                "BASS_FX audio effects library (required for EQ)",
                "https://www.un4seen.com/ — download bassfx24-win.zip (64-bit)");
        }
        else if (isLinux)
        {
            yield return new ExpectedLibrary(
                "libbass_fx.so",
                IsRequired: true,
                "BASS_FX audio effects library (required for EQ)",
                "https://www.un4seen.com/ — download bassfx24-linux.zip");
        }
        else if (isMac)
        {
            yield return new ExpectedLibrary(
                "libbass_fx.dylib",
                IsRequired: true,
                "BASS_FX audio effects library (required for EQ)",
                "https://www.un4seen.com/ — download bassfx24-osx.zip");
        }

        // ── Required: libmpv (video) ──
        // Note: on Linux, libmpv.so.2 can also be found on the system
        // library path (apt install libmpv-dev). We still flag it as
        // missing from lib/ but the loader has a fallback — so this is
        // a "soft" required on Linux. To keep the report simple, we
        // treat it as required and let the user see the message; if
        // they've installed libmpv-dev, the loader will still work.
        if (isWindows)
        {
            yield return new ExpectedLibrary(
                "libmpv-2.dll",
                IsRequired: true,
                "libmpv video library",
                "https://sourceforge.net/projects/mpv-player-windows/files/libmpv/ — download mpv-dev-x86_64-*.7z, extract with 7-Zip, find libmpv-2.dll inside");
        }
        else if (isLinux)
        {
            // On Linux, libmpv may be installed system-wide via apt.
            // Only flag as missing if neither lib/ nor the system path has it.
            bool systemHasLibmpv = IsLibraryOnSystemPath("libmpv.so.2") ||
                                   IsLibraryOnSystemPath("libmpv.so.1") ||
                                   IsLibraryOnSystemPath("libmpv.so");
            if (!systemHasLibmpv)
            {
                yield return new ExpectedLibrary(
                    "libmpv.so.2",
                    IsRequired: true,
                    "libmpv video library",
                    "Either place libmpv.so.2 in lib/, OR run: sudo apt install libmpv-dev");
            }
        }
        else if (isMac)
        {
            yield return new ExpectedLibrary(
                "libmpv.2.dylib",
                IsRequired: true,
                "libmpv video library",
                "brew install mpv (or place libmpv.2.dylib in lib/)");
        }

    }

    /// <summary>
    /// Probe the system library path for a given library name. Used on
    /// Linux to check whether libmpv is installed system-wide (in which
    /// case we don't need to flag it as missing from lib/).
    /// </summary>
    private static bool IsLibraryOnSystemPath(string libraryName)
    {
        try
        {
            // NativeLibrary.Load with no assembly/path searches the OS
            // default search path. If it returns non-zero, the library
            // was found.
            IntPtr handle = NativeLibrary.Load(libraryName);
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // Library not found — that's fine, we're just probing.
        }
        return false;
    }

    /// <summary>
    /// Format the missing-required-libraries report. Single consistent
    /// format — same message structure every time.
    /// </summary>
    private static string FormatReport(string libDir, List<ExpectedLibrary> missing)
    {
        var lines = new List<string>();

        lines.Add("Required native libraries missing");
        lines.Add("");
        lines.Add($"The following required libraries were not found in the lib/ folder:");
        lines.Add("");
        lines.Add($"  {libDir}");
        lines.Add("");

        foreach (var m in missing)
        {
            lines.Add($"  • {m.FileName,-32}  {m.Description}");
            lines.Add($"      Get it from: {m.Source}");
            lines.Add("");
        }

        lines.Add("Audio and/or video playback will not work until these are placed");
        lines.Add("in the lib/ folder. See lib/README.md for full instructions.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Describes a library the Jukebox expects to find in lib/.
    /// Required libraries will block playback if missing; optional ones
    /// only affect the visualizer.
    /// </summary>
    private sealed record ExpectedLibrary(
        string FileName,
        bool IsRequired,
        string Description,
        string Source);
}

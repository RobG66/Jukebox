using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Jukebox.ViewModels;
using Jukebox.Views;
using System;
using System.Runtime.Versioning;

namespace Jukebox;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern bool AttachConsole(int dwProcessId);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Core.Initialize() has been moved to JukeboxViewModel to prevent GUI freeze.

            var vm = new JukeboxViewModel();
            var window = new JukeboxView { DataContext = vm };
            
            var storageService = new Jukebox.Services.StorageService(window);
            vm.StorageService = storageService;

            // Startup check: scan lib/ for required native libraries
            // Shows a clear error dialog listing what's missing and where
            // to get it. The app continues to start (so the user can see
            // the window and read the message), but audio/video playback
            // won't work until they address it.
            var missingReport = Jukebox.Services.NativeDependencyChecker.CheckForMissingRequired();
            if (missingReport != null)
            {
                // Defer showing the dialog until after the window is shown
                // so it appears on top of the main window.
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                        "Required libraries missing",
                        missingReport);
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }

            // Parse Command Line Arguments
            if (desktop.Args != null)
            {
                foreach (var argRaw in desktop.Args)
                {
                    string a = argRaw.ToLower();
                    if (a == "-?" || a == "-help" || a == "--help" || a == "?" || a == "/?" || a == "/help")
                    {
                        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                        {
                            AttachConsole(-1); // Attach to parent console
                        }

                        Console.WriteLine("\nJukebox Command Line Arguments:");
                        Console.WriteLine("-------------------------------");
                        Console.WriteLine(" -light / -dark          : Set the theme.");
                        Console.WriteLine(" -playlistlogo [file]    : Render an image logo above the playlist.");
                        Console.WriteLine(" -random                 : Enable random shuffle playback.");
                        Console.WriteLine(" -hidecontrols           : Hide the bottom control bar on startup.");
                        Console.WriteLine(" -nocontrols             : Disable ALL controls (strictly playback).");
                        Console.WriteLine(" -novisualizer           : Disable the ProjectM visualizer.");
                        Console.WriteLine(" -showplaying [timeout]  : Show current track OSD on track change.");
                        Console.WriteLine("                           If omitted, OSD is not shown.");
                        Console.WriteLine("                           Optional timeout in seconds (default 10).");
                        Console.WriteLine(" -randompreset [time]    : Enable visualizer preset randomization.");
                        Console.WriteLine("                           Optional interval 10-60 seconds (default 10).");
                        Console.WriteLine(" -volume [0-100]         : Set the initial volume.");
                        Console.WriteLine(" -stayontop              : Force window always-on-top.");
                        Console.WriteLine(" -fullscreen / -minimized: Set initial window state.");
                        Console.WriteLine(" -file [path]            : Auto-load file or directory.");
                        Console.WriteLine(" -loop                   : Loop playlist continuously.");
                        Console.WriteLine(" -title [text]           : Override the window title.\n");

                        Environment.Exit(0);
                    }
                }

                for (int i = 0; i < desktop.Args.Length; i++)
                {
                    string arg = desktop.Args[i].ToLower();

                    if (arg == "-light")
                        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                    else if (arg == "-dark")
                        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                    else if (arg == "-playlistlogo" && i + 1 < desktop.Args.Length)
                        vm.PlaylistLogo = desktop.Args[++i];
                    else if (arg == "-random")
                        vm.IsRandomPlayback = true;
                    else if (arg == "-hidecontrols")
                        vm.IsAutoHideEnabled = true;
                    else if (arg == "-nocontrols")
                        vm.IsControlsDisabled = true;
                    else if (arg == "-novisualizer")
                        vm.IsVisualizerDisabled = true;
                    else if (arg == "-showplaying")
                    {
                        vm.IsShowPlayingEnabled = true;
                        // Check if next arg is a number (the timeout)
                        if (i + 1 < desktop.Args.Length && int.TryParse(desktop.Args[i + 1], out int osdTimeout))
                        {
                            vm.ShowPlayingTimeout = osdTimeout;
                            i++; // consume the timeout
                        }
                    }
                    else if (arg == "-randompreset")
                    {
                        vm.VisualizerViewModel.IsVisualizerRandomizerEnabled = true;
                        // Check if next arg is a number (the interval)
                        if (i + 1 < desktop.Args.Length && int.TryParse(desktop.Args[i + 1], out int presetInterval))
                        {
                            // Clamp to 10-60 range
                            presetInterval = Math.Clamp(presetInterval, 10, 60);
                            vm.VisualizerViewModel.VisualizerRandomizerIntervalSeconds = presetInterval;
                            i++; // consume the interval
                        }
                    }
                    else if (arg == "-volume" && i + 1 < desktop.Args.Length && int.TryParse(desktop.Args[++i], out int vol))
                        vm.InitialVolume = vol;
                    else if (arg == "-stayontop")
                    {
                        vm.StayOnTop = true;
                        window.Topmost = true;
                    }
                    else if (arg == "-fullscreen")
                        vm.WindowState = Avalonia.Controls.WindowState.FullScreen;
                    else if (arg == "-file" && i + 1 < desktop.Args.Length)
                        vm.InitialFile = desktop.Args[++i];
                    else if (arg == "-loop")
                        vm.IsLoopEnabled = true;
                    else if (arg == "-minimized")
                        vm.WindowState = Avalonia.Controls.WindowState.Minimized;
                    else if (arg == "-title" && i + 1 < desktop.Args.Length)
                        window.Title = desktop.Args[++i];
                }
            }

            desktop.MainWindow = window;

            desktop.Exit += (sender, e) =>
            {
                vm?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

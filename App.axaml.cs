using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Jukebox.ViewModels;
using Jukebox.Views;
using System;
using LibVLCSharp.Shared;

namespace Jukebox;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
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
                        Console.WriteLine(" -volume [0-100]         : Set the initial volume.");
                        Console.WriteLine(" -stayontop              : Force window always-on-top.");
                        Console.WriteLine(" -fullscreen / -minimized: Set initial window state.");
                        Console.WriteLine(" -file [path]            : Auto-load file or directory.");
                        Console.WriteLine(" -forcevisualizer        : Force visualizer for non-audio media.");
                        Console.WriteLine(" -loop                   : Loop playlist continuously.");
                        Console.WriteLine(" -kiosk                  : Launch locked-down kiosk mode.");
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
                    else if (arg == "-volume" && i + 1 < desktop.Args.Length && int.TryParse(desktop.Args[++i], out int vol))
                        vm.InitialVolume = vol;
                    else if (arg == "-stayontop")
                        window.Topmost = true;
                    else if (arg == "-fullscreen")
                        window.WindowState = Avalonia.Controls.WindowState.FullScreen;
                    else if (arg == "-file" && i + 1 < desktop.Args.Length)
                        vm.InitialFile = desktop.Args[++i];
                    else if (arg == "-forcevisualizer")
                        vm.ForceVisualizer = true;
                    else if (arg == "-loop")
                        vm.IsLoopEnabled = true;
                    else if (arg == "-minimized")
                        window.WindowState = Avalonia.Controls.WindowState.Minimized;
                    else if (arg == "-kiosk")
                        vm.IsKioskMode = true;
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

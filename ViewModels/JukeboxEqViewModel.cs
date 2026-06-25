using CommunityToolkit.Mvvm.ComponentModel;
using Jukebox.Extensions;
using Jukebox.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxEqViewModel : ViewModelBase
{
    public ObservableCollection<EqSliderViewModel> EqBands { get; } = new();
    public ObservableCollection<string> EqPresets { get; } = new()
    {
        "Flat", "Acoustic", "Bass Boost", "Classical", "Electronic", "Pop", "Rock", "Custom"
    };

    [ObservableProperty] private string _selectedEqPreset = "Flat";

    public event EventHandler<EqSliderViewModel>? EqBandUpdated;

    private bool _isApplyingPreset = false;

    // REFACTOR: debounced save timer — instead of writing to disk on every
    // slider drag tick, we wait 300ms after the last change before persisting
    // (was smell §4.7 Warning: Synchronous JSON file IO in constructor and
    // SaveEqSettings). Load also moved out of constructor into LoadAsync.
    private readonly Avalonia.Threading.DispatcherTimer _saveDebounce;
    private readonly IPathProvider _pathProvider;

    public JukeboxEqViewModel() : this(PathProvider.Current)
    {
    }

    // Constructor added for testability.
    public JukeboxEqViewModel(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));

        _saveDebounce = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveEqSettingsAsync().SafeFireAndForget(nameof(SaveEqSettingsAsync));
        };

        SetupEqBands();
    }

    partial void OnSelectedEqPresetChanged(string value)
    {
        if (!_isApplyingPreset)
        {
            ApplyEqPreset(value);
        }
    }

    private void SetupEqBands()
    {
        float[] freqs = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        string[] labels = { "32", "64", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };

        for (int i = 0; i < Constants.EqBandCount; i++)
        {
            var band = new EqSliderViewModel { CenterFrequency = freqs[i], FrequencyLabel = labels[i], Gain = 0 };
            band.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(EqSliderViewModel.Gain))
                {
                    EqBandUpdated?.Invoke(this, band);

                    if (SelectedEqPreset != "Custom" && !_isApplyingPreset)
                    {
                        SelectedEqPreset = "Custom";
                    }
                    if (!_isApplyingPreset)
                    {
                        // REFACTOR: trigger debounced save instead of writing on every change.
                        ScheduleSave();
                    }
                }
            };
            EqBands.Add(band);
        }
    }

    /// <summary>
    /// Loads saved EQ settings from disk asynchronously. Should be called
    /// from the View's Loaded handler (was previously in the constructor).
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            var path = _pathProvider.EqSettingsFile;
            if (!await Task.Run(() => File.Exists(path))) return;

            var json = await Task.Run(() => File.ReadAllText(path));
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Preset", out var presetEl))
            {
                _isApplyingPreset = true;
                SelectedEqPreset = presetEl.GetString() ?? "Flat";
                _isApplyingPreset = false;
            }

            if (root.TryGetProperty("Gains", out var gainsEl))
            {
                _isApplyingPreset = true;
                int i = 0;
                foreach (var g in gainsEl.EnumerateArray())
                {
                    if (i < Constants.EqBandCount) EqBands[i].Gain = g.GetDouble();
                    i++;
                }
                _isApplyingPreset = false;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Eq Load Error: {ex.Message}"); }
    }

    private void ScheduleSave()
    {
        // Restart the debounce timer — only the final change in a 300ms window
        // will actually write to disk.
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private async Task SaveEqSettingsAsync()
    {
        try
        {
            var gains = new double[Constants.EqBandCount];
            for (int i = 0; i < Constants.EqBandCount; i++) gains[i] = EqBands[i].Gain;

            var settings = new { Preset = SelectedEqPreset, Gains = gains };
            var json = JsonSerializer.Serialize(settings);

            // REFACTOR: use IPathProvider + async write (was smell §4.7 Warning:
            // Synchronous JSON file IO + Direct Environment.SpecialFolder.ApplicationData coupling).
            var dir = _pathProvider.SettingsDirectory;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(_pathProvider.EqSettingsFile, json);
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Eq Save Error: {ex.Message}"); }
    }

    private void ApplyEqPreset(string preset)
    {
        _isApplyingPreset = true;
        double[]? gains = preset switch
        {
            "Flat" => new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            "Acoustic" => new double[] { 3, 3, 2, 0, -1, 1, 3, 3, 2, 1 },
            "Bass Boost" => new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
            "Classical" => new double[] { 3, 2, 1, 0, 0, 0, 1, 2, 3, 4 },
            "Electronic" => new double[] { 5, 4, 1, -1, -2, 0, 1, 3, 4, 5 },
            "Pop" => new double[] { -1, 1, 3, 4, 3, 1, 0, -1, -1, -1 },
            "Rock" => new double[] { 5, 4, 2, -1, -2, -1, 1, 3, 4, 5 },
            _ => null
        };

        if (gains != null)
        {
            for (int i = 0; i < Constants.EqBandCount; i++)
            {
                EqBands[i].Gain = gains[i];
            }
            ScheduleSave();
        }
        _isApplyingPreset = false;
    }
}

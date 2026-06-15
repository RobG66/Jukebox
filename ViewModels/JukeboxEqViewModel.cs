using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

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

    public JukeboxEqViewModel()
    {
        SetupEqBands();
        LoadEqSettings();
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

        for (int i = 0; i < 10; i++)
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
                        SaveEqSettings();
                    }
                }
            };
            EqBands.Add(band);
        }
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
            for (int i = 0; i < 10; i++)
            {
                EqBands[i].Gain = gains[i];
            }
            SaveEqSettings();
        }
        _isApplyingPreset = false;
    }

    private void SaveEqSettings()
    {
        try
        {
            var gains = new double[10];
            for (int i = 0; i < 10; i++) gains[i] = EqBands[i].Gain;
            
            var settings = new { Preset = SelectedEqPreset, Gains = gains };
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EqSettings.json"), json);
        }
        catch { }
    }

    private void LoadEqSettings()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EqSettings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
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
                        if (i < 10) EqBands[i].Gain = g.GetDouble();
                        i++;
                    }
                    _isApplyingPreset = false;
                }
            }
        }
        catch { }
    }
}

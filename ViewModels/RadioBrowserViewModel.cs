using Jukebox.Models;
using Jukebox.Services;
using Jukebox.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Collections;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class RadioBrowserViewModel : ViewModelBase
{
    #region Fields & Constants
    private readonly IRadioBrowserService _radioBrowserService;
    private readonly JukeboxViewModel _jukeboxViewModel;
    private readonly List<RadioStation> _allStations = new();
    private bool _isUpdatingFilters;
    #endregion

    #region Observable Properties
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedFilterType = "Name";
    [ObservableProperty] private bool _isSearching = false;
    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaySelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedCommand))]
    private RadioStation? _selectedStation;

    [ObservableProperty] private DataGridCollectionView? _stationsView;
    [ObservableProperty] private string? _selectedCountry;
    [ObservableProperty] private string? _selectedState;
    [ObservableProperty] private string? _selectedLanguage;
    #endregion

    #region Public Properties
    public ObservableCollection<string> FilterTypes { get; } = new() { "Name", "Tag", "Country", "Language" };
    public ObservableCollection<string> Countries { get; } = new();
    public ObservableCollection<string> States { get; } = new();
    public ObservableCollection<string> Languages { get; } = new();
    #endregion

    #region Property Change Callbacks
    partial void OnSelectedCountryChanged(string? value)
    {
        if (_isUpdatingFilters) return;
        UpdateStatesList();
        ApplyLocalFilter();
    }

    partial void OnSelectedStateChanged(string? value)
    {
        if (_isUpdatingFilters) return;
        ApplyLocalFilter();
    }

    partial void OnSelectedLanguageChanged(string? value)
    {
        if (_isUpdatingFilters) return;
        ApplyLocalFilter();
    }
    #endregion

    #region Private Properties
    private string CacheFilePath => Path.Combine(Jukebox.Services.PathProvider.Current.SettingsDirectory, "RadioStationsCache.json");
    #endregion

    #region Constructor
    public RadioBrowserViewModel(IRadioBrowserService radioBrowserService, JukeboxViewModel jukeboxViewModel)
    {
        _radioBrowserService = radioBrowserService;
        _jukeboxViewModel = jukeboxViewModel;
        StatusText = "Loading cached stations...";

        LoadCacheAsync().SafeFireAndForget(nameof(LoadCacheAsync));
    }
    #endregion

    #region Commands
    [RelayCommand]
    private void Search()
    {
        ApplyLocalFilter();
    }

    [RelayCommand]
    private void ResetFilters()
    {
        SearchQuery = string.Empty;
        SelectedCountry = "All Countries";
        SelectedLanguage = "All Languages";
        SelectedState = "All States";
        ApplyLocalFilter();
    }

    [RelayCommand]
    private async Task RefreshCacheAsync()
    {
        IsSearching = true;
        StatusText = "Fetching country list...";
        StationsView = null;

        try
        {
            var countries = await _radioBrowserService.GetCountriesAsync();
            if (countries == null || countries.Count == 0)
            {
                StatusText = "API returned no countries.";
                IsSearching = false;
                return;
            }

            var targetCountries = countries
                .Where(c => c.StationCount > 0 && !string.IsNullOrEmpty(c.Iso31661) && !string.IsNullOrEmpty(c.Name))
                .OrderBy(c => c.Name)
                .ToList();

            var allStations = new List<RadioStation>();
            int total = targetCountries.Count;
            int completed = 0;

            var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };

            await Parallel.ForEachAsync(targetCountries, options, async (country, cancellationToken) =>
            {
                try
                {
                    var stations = await _radioBrowserService.GetStationsByCountryCodeAsync(country.Iso31661);
                    if (stations != null && stations.Count > 0)
                    {
                        lock (allStations)
                        {
                            allStations.AddRange(stations);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RadioBrowser] Failed to fetch stations for {country.Name}: {ex.Message}");
                }

                int currentCompleted = System.Threading.Interlocked.Increment(ref completed);
                StatusText = $"Updating cache: {currentCompleted} of {total} countries completed...";
            });

            if (allStations.Count > 0)
            {
                _allStations.Clear();
                _allStations.AddRange(allStations);

                var json = JsonSerializer.Serialize(_allStations, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(CacheFilePath, json);

                PopulateFilterDropdowns();
                ApplyLocalFilter();
                StatusText = $"Cache refreshed. Loaded {_allStations.Count} stations.";
            }
            else
            {
                StatusText = "Failed to load any stations.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlayOrAddSelected))]
    private async Task PlaySelectedAsync()
    {
        if (SelectedStation == null) return;
        // Play Now uses a transient preview slot — the station appears in the
        // Radio playlist tab for easy resuming or promotion, but is not added
        // as a permanent entry and is not persisted to disk.
        var track = await _jukeboxViewModel.PlaylistViewModel.SetTransientRadioStationAsync(
            SelectedStation.Name, SelectedStation.UrlResolved, SelectedStation.Codec, SelectedStation.Bitrate, SelectedStation.Tags, SelectedStation.CountryCode);
        if (_jukeboxViewModel.PlayTrackCommand.CanExecute(track))
        {
            await _jukeboxViewModel.PlayTrackCommand.ExecuteAsync(track);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlayOrAddSelected))]
    private async Task AddSelectedAsync()
    {
        if (SelectedStation == null) return;
        await _jukeboxViewModel.PlaylistViewModel.AddRadioStationTrackAsync(
            SelectedStation.Name, SelectedStation.UrlResolved, SelectedStation.Codec, SelectedStation.Bitrate, SelectedStation.Tags, SelectedStation.CountryCode);
    }
    #endregion

    #region Private Methods
    private bool CanPlayOrAddSelected()
    {
        return SelectedStation != null;
    }

    private async Task LoadCacheAsync()
    {
        IsSearching = true;
        StatusText = "Loading cached stations...";
        try
        {
            if (File.Exists(CacheFilePath))
            {
                var json = await File.ReadAllTextAsync(CacheFilePath);
                var cached = JsonSerializer.Deserialize<List<RadioStation>>(json);
                if (cached != null && cached.Count > 0)
                {
                    _allStations.Clear();
                    _allStations.AddRange(cached);

                    PopulateFilterDropdowns();
                    ApplyLocalFilter();
                    StatusText = $"Loaded {_allStations.Count} stations from cache.";
                    return;
                }
            }

            await RefreshCacheAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading cache: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void PopulateFilterDropdowns()
    {
        _isUpdatingFilters = true;

        var prevCountry = SelectedCountry;
        var prevLang = SelectedLanguage;

        // 1. Countries
        Countries.Clear();
        Countries.Add("All Countries");
        var uniqueCountries = _allStations
            .Select(s => s.Country?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c);
        foreach (var c in uniqueCountries)
        {
            Countries.Add(c!);
        }

        if (prevCountry != null && Countries.Contains(prevCountry))
            SelectedCountry = prevCountry;
        else
            SelectedCountry = "All Countries";

        // 2. Languages
        Languages.Clear();
        Languages.Add("All Languages");
        var uniqueLanguages = _allStations
            .SelectMany(s => (s.LanguageCodes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l);
        foreach (var l in uniqueLanguages)
        {
            Languages.Add(l!);
        }

        if (prevLang != null && Languages.Contains(prevLang))
            SelectedLanguage = prevLang;
        else
            SelectedLanguage = "All Languages";

        _isUpdatingFilters = false;

        // 3. States (depends on SelectedCountry)
        UpdateStatesList();
    }

    private void UpdateStatesList()
    {
        bool wasUpdating = _isUpdatingFilters;
        _isUpdatingFilters = true;

        var prevState = SelectedState;

        States.Clear();
        States.Add("All States");

        IEnumerable<RadioStation> stations = _allStations;
        if (!string.IsNullOrEmpty(SelectedCountry) && SelectedCountry != "All Countries")
        {
            stations = stations.Where(s => string.Equals(s.Country, SelectedCountry, StringComparison.OrdinalIgnoreCase));
        }

        var uniqueStates = stations
            .Select(s => s.State?.Trim())
            .Where(st => !string.IsNullOrEmpty(st))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(st => st);

        foreach (var st in uniqueStates)
        {
            States.Add(st!);
        }

        if (prevState != null && States.Contains(prevState))
            SelectedState = prevState;
        else
            SelectedState = "All States";

        _isUpdatingFilters = wasUpdating;
    }

    private void ApplyLocalFilter()
    {
        var filtered = new List<RadioStation>();
        string query = SearchQuery?.Trim() ?? string.Empty;

        foreach (var station in _allStations)
        {
            // 1. Text Search Filter
            if (!string.IsNullOrWhiteSpace(query))
            {
                bool matchesText = SelectedFilterType.ToLower() switch
                {
                    "tag" => station.Tags?.Contains(query, StringComparison.OrdinalIgnoreCase) == true,
                    "country" => station.Country?.Contains(query, StringComparison.OrdinalIgnoreCase) == true,
                    "language" => station.Language?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                                  station.LanguageCodes?.Contains(query, StringComparison.OrdinalIgnoreCase) == true,
                    _ => station.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                };

                if (!matchesText) continue;
            }

            // 2. Dropdown Country Filter
            if (!string.IsNullOrEmpty(SelectedCountry) && SelectedCountry != "All Countries")
            {
                if (!string.Equals(station.Country, SelectedCountry, StringComparison.OrdinalIgnoreCase)) continue;
            }

            // 3. Dropdown State Filter
            if (!string.IsNullOrEmpty(SelectedState) && SelectedState != "All States")
            {
                if (!string.Equals(station.State, SelectedState, StringComparison.OrdinalIgnoreCase)) continue;
            }

            // 4. Dropdown Language Filter
            if (!string.IsNullOrEmpty(SelectedLanguage) && SelectedLanguage != "All Languages")
            {
                var stationLangs = (station.LanguageCodes ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!stationLangs.Any(l => string.Equals(l, SelectedLanguage, StringComparison.OrdinalIgnoreCase))) continue;
            }

            filtered.Add(station);
        }

        StationsView = new DataGridCollectionView(filtered);
        StatusText = $"Showing {filtered.Count} of {_allStations.Count} stations.";
    }
    #endregion
}

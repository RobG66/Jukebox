using Jukebox.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jukebox.Services;

public class RadioBrowserService : IRadioBrowserService
{
    private static readonly HttpClient _httpClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    private static readonly string[] FallbackHosts = {
        "de1.api.radio-browser.info",
        "fr1.api.radio-browser.info",
        "nl1.api.radio-browser.info",
        "at1.api.radio-browser.info"
    };

    private string? _resolvedBaseUrl;

    static RadioBrowserService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("JukeboxApp/1.0");
    }

    private async Task<string> GetBaseUrlAsync()
    {
        if (!string.IsNullOrEmpty(_resolvedBaseUrl))
            return _resolvedBaseUrl;

        try
        {
            var ips = await Dns.GetHostAddressesAsync("all.api.radio-browser.info");
            if (ips.Length > 0)
            {
                var hostEntry = await Dns.GetHostEntryAsync(ips[0]);
                if (!string.IsNullOrEmpty(hostEntry.HostName))
                {
                    _resolvedBaseUrl = $"https://{hostEntry.HostName}";
                    return _resolvedBaseUrl;
                }
            }
        }
        catch
        {
            // Ignore DNS errors and use fallback
        }

        // Fallback to random mirror from the list
        var random = new Random();
        _resolvedBaseUrl = $"https://{FallbackHosts[random.Next(FallbackHosts.Length)]}";
        return _resolvedBaseUrl;
    }

    public async Task<List<RadioStation>> SearchStationsAsync(string query, string filterType, int limit = 100)
    {
        try
        {
            string baseUrl = await GetBaseUrlAsync();
            string endpoint = filterType.ToLower() switch
            {
                "tag" => "bytag",
                "country" => "bycountry",
                "language" => "bylanguage",
                _ => "byname"
            };

            string url;
            if (string.IsNullOrWhiteSpace(query))
            {
                url = $"{baseUrl}/json/stations/topclick/{limit}?hidebroken=true";
            }
            else
            {
                url = $"{baseUrl}/json/stations/{endpoint}/{Uri.EscapeDataString(query)}?limit={limit}&hidebroken=true&order=clickcount&reverse=true";
            }

            var response = await _httpClient.GetStringAsync(url);
            var stations = JsonSerializer.Deserialize<List<RadioStation>>(response);
            return stations ?? new List<RadioStation>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RadioBrowser] Search failed: {ex.Message}");
            throw;
        }
    }

    public async Task<List<RadioCountry>> GetCountriesAsync()
    {
        try
        {
            string baseUrl = await GetBaseUrlAsync();
            string url = $"{baseUrl}/json/countries";
            var response = await _httpClient.GetStringAsync(url);
            var countries = JsonSerializer.Deserialize<List<RadioCountry>>(response);
            return countries ?? new List<RadioCountry>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RadioBrowser] Fetch countries failed: {ex.Message}");
            throw;
        }
    }

    public async Task<List<RadioStation>> GetStationsByCountryCodeAsync(string countryCode)
    {
        try
        {
            string baseUrl = await GetBaseUrlAsync();
            string url = $"{baseUrl}/json/stations/bycountrycodeexact/{Uri.EscapeDataString(countryCode)}?hidebroken=true";
            var response = await _httpClient.GetStringAsync(url);
            var stations = JsonSerializer.Deserialize<List<RadioStation>>(response);
            return stations ?? new List<RadioStation>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RadioBrowser] Fetch stations by country code failed: {ex.Message}");
            throw;
        }
    }

    public async Task<List<RadioStation>> GetTopStationsAsync(int limit)
    {
        try
        {
            string baseUrl = await GetBaseUrlAsync();
            string url = $"{baseUrl}/json/stations/topclick/{limit}?hidebroken=true";
            var response = await _httpClient.GetStringAsync(url);
            var stations = JsonSerializer.Deserialize<List<RadioStation>>(response);
            return stations ?? new List<RadioStation>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RadioBrowser] Fetch top stations failed: {ex.Message}");
            throw;
        }
    }
}

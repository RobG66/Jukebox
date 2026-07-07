using Jukebox.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jukebox.Services;

public interface IRadioBrowserService
{
    Task<List<RadioStation>> SearchStationsAsync(string query, string filterType, int limit = 100);
    Task<List<RadioCountry>> GetCountriesAsync();
    Task<List<RadioStation>> GetStationsByCountryCodeAsync(string countryCode);
    Task<List<RadioStation>> GetTopStationsAsync(int limit);
}

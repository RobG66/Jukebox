using System;
using System.Text.Json.Serialization;

namespace Jukebox.Models;

public class RadioStation
{
    [JsonPropertyName("stationuuid")]
    public string StationUuid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("url_resolved")]
    public string UrlResolved { get; set; } = string.Empty;

    [JsonPropertyName("favicon")]
    public string Favicon { get; set; } = string.Empty;

    private string _tags = string.Empty;

    [JsonPropertyName("tags")]
    public string Tags
    {
        get => _tags;
        set => _tags = NormalizeTags(value);
    }

    public static string NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return "—";

        var parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var normalizedParts = new System.Collections.Generic.List<string>();

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i];
                if (word.Length > 0)
                {
                    words[i] = char.ToUpperInvariant(word[0]) + word.Substring(1);
                }
            }

            normalizedParts.Add(string.Join(" ", words));
        }

        return normalizedParts.Count > 0 ? string.Join(", ", normalizedParts) : "—";
    }

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    private string _countryCode = string.Empty;

    [JsonPropertyName("countrycode")]
    public string CountryCode
    {
        get => _countryCode;
        set => _countryCode = value?.ToUpperInvariant() ?? string.Empty;
    }

    // Display-friendly short country name derived from CountryCode via the
    // ISO 3166-1 lookup in CountryNames. Use this for UI bindings instead of
    // Country — the API's "country" field is often bloated
    // ("The United Kingdom Of Great Britain And Northern Ireland",
    // "The Russian Federation", etc.) while CountryCode is always a clean
    // 2-letter alpha-2 code.
    //
    // Country (raw) is kept for serialization and the country dropdown filter,
    // which still uses the API's free-form names for grouping.
    [JsonIgnore]
    public string CountryDisplayName =>
        string.IsNullOrWhiteSpace(CountryCode)
            ? (string.IsNullOrWhiteSpace(Country) ? "—" : Country)
            : Jukebox.Helpers.CountryNames.GetShortName(CountryCode);

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    private string _languageCodes = string.Empty;

    [JsonPropertyName("languagecodes")]
    public string LanguageCodes
    {
        get => _languageCodes;
        set => _languageCodes = value?.ToUpperInvariant() ?? string.Empty;
    }

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = string.Empty;

    [JsonPropertyName("clickcount")]
    public int ClickCount { get; set; }
}

public class RadioCountry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("iso_3166_1")]
    public string Iso31661 { get; set; } = string.Empty;

    [JsonPropertyName("stationcount")]
    public int StationCount { get; set; }
}

using System;
using System.Collections.Generic;

namespace Jukebox.Helpers;

/// <summary>
/// Maps ISO 3166-1 alpha-2 country codes (e.g. "US", "GB", "RU") to short
/// English country names ("United States", "United Kingdom", "Russia").
/// </summary>
/// <remarks>
/// <para>
/// The radio-browser API returns both a free-form <c>country</c> string
/// (often bloated: "The United Kingdom Of Great Britain And Northern Ireland",
/// "The Russian Federation", etc.) and a normalized <c>countrycode</c> field
/// (always 2-letter ISO 3166-1 alpha-2). Every station in the cache has a
/// valid <c>countrycode</c>, so this lookup produces a clean, consistent
/// display name for 100% of stations.
/// </para>
/// <para>
/// The mapping is sourced from the official ISO 3166-1 country list. Short
/// names use the common English form (e.g. "Russia" rather than "Russian
/// Federation", "South Korea" rather than "Korea, Republic of"). ISO 3166-1
/// is stable — codes are only added (rarely) when a new country is recognized,
/// never removed or reassigned.
/// </para>
/// <para>
/// For codes not in the map (should never happen for valid stations, but
/// defensive against future codes), <see cref="GetShortName"/> returns the
/// raw 2-letter code itself — ugly but unambiguous, and clearly indicates
/// the lookup table needs updating.
/// </para>
/// </remarks>
public static class CountryNames
{
    /// <summary>
    /// ISO 3166-1 alpha-2 code → short English country name.
    /// 249 officially assigned codes, plus XK (Kosovo, user-assigned —
    /// widely used in radio station metadata).
    /// </summary>
    private static readonly Dictionary<string, string> _names =
        new(StringComparer.OrdinalIgnoreCase)
        {
            {"AD", "Andorra"},
            {"AE", "United Arab Emirates"},
            {"AF", "Afghanistan"},
            {"AG", "Antigua and Barbuda"},
            {"AI", "Anguilla"},
            {"AL", "Albania"},
            {"AM", "Armenia"},
            {"AO", "Angola"},
            {"AQ", "Antarctica"},
            {"AR", "Argentina"},
            {"AS", "American Samoa"},
            {"AT", "Austria"},
            {"AU", "Australia"},
            {"AW", "Aruba"},
            {"AX", "Åland Islands"},
            {"AZ", "Azerbaijan"},
            {"BA", "Bosnia and Herzegovina"},
            {"BB", "Barbados"},
            {"BD", "Bangladesh"},
            {"BE", "Belgium"},
            {"BF", "Burkina Faso"},
            {"BG", "Bulgaria"},
            {"BH", "Bahrain"},
            {"BI", "Burundi"},
            {"BJ", "Benin"},
            {"BL", "Saint Barthélemy"},
            {"BM", "Bermuda"},
            {"BN", "Brunei Darussalam"},
            {"BO", "Bolivia"},
            {"BQ", "Bonaire, Sint Eustatius and Saba"},
            {"BR", "Brazil"},
            {"BS", "Bahamas"},
            {"BT", "Bhutan"},
            {"BV", "Bouvet Island"},
            {"BW", "Botswana"},
            {"BY", "Belarus"},
            {"BZ", "Belize"},
            {"CA", "Canada"},
            {"CC", "Cocos (Keeling) Islands"},
            {"CD", "DR Congo"},
            {"CF", "Central African Republic"},
            {"CG", "Congo"},
            {"CH", "Switzerland"},
            {"CI", "Côte d'Ivoire"},
            {"CK", "Cook Islands"},
            {"CL", "Chile"},
            {"CM", "Cameroon"},
            {"CN", "China"},
            {"CO", "Colombia"},
            {"CR", "Costa Rica"},
            {"CU", "Cuba"},
            {"CV", "Cabo Verde"},
            {"CW", "Curaçao"},
            {"CX", "Christmas Island"},
            {"CY", "Cyprus"},
            {"CZ", "Czechia"},
            {"DE", "Germany"},
            {"DJ", "Djibouti"},
            {"DK", "Denmark"},
            {"DM", "Dominica"},
            {"DO", "Dominican Republic"},
            {"DZ", "Algeria"},
            {"EC", "Ecuador"},
            {"EE", "Estonia"},
            {"EG", "Egypt"},
            {"EH", "Western Sahara"},
            {"ER", "Eritrea"},
            {"ES", "Spain"},
            {"ET", "Ethiopia"},
            {"FI", "Finland"},
            {"FJ", "Fiji"},
            {"FK", "Falkland Islands"},
            {"FM", "Micronesia"},
            {"FO", "Faroe Islands"},
            {"FR", "France"},
            {"GA", "Gabon"},
            {"GB", "United Kingdom"},
            {"GD", "Grenada"},
            {"GE", "Georgia"},
            {"GF", "French Guiana"},
            {"GG", "Guernsey"},
            {"GH", "Ghana"},
            {"GI", "Gibraltar"},
            {"GL", "Greenland"},
            {"GM", "Gambia"},
            {"GN", "Guinea"},
            {"GP", "Guadeloupe"},
            {"GQ", "Equatorial Guinea"},
            {"GR", "Greece"},
            {"GS", "South Georgia and the South Sandwich Islands"},
            {"GT", "Guatemala"},
            {"GU", "Guam"},
            {"GW", "Guinea-Bissau"},
            {"GY", "Guyana"},
            {"HK", "Hong Kong"},
            {"HM", "Heard Island and McDonald Islands"},
            {"HN", "Honduras"},
            {"HR", "Croatia"},
            {"HT", "Haiti"},
            {"HU", "Hungary"},
            {"ID", "Indonesia"},
            {"IE", "Ireland"},
            {"IL", "Israel"},
            {"IM", "Isle of Man"},
            {"IN", "India"},
            {"IO", "British Indian Ocean Territory"},
            {"IQ", "Iraq"},
            {"IR", "Iran"},
            {"IS", "Iceland"},
            {"IT", "Italy"},
            {"JE", "Jersey"},
            {"JM", "Jamaica"},
            {"JO", "Jordan"},
            {"JP", "Japan"},
            {"KE", "Kenya"},
            {"KG", "Kyrgyzstan"},
            {"KH", "Cambodia"},
            {"KI", "Kiribati"},
            {"KM", "Comoros"},
            {"KN", "Saint Kitts and Nevis"},
            {"KP", "North Korea"},
            {"KR", "South Korea"},
            {"KW", "Kuwait"},
            {"KY", "Cayman Islands"},
            {"KZ", "Kazakhstan"},
            {"LA", "Laos"},
            {"LB", "Lebanon"},
            {"LC", "Saint Lucia"},
            {"LI", "Liechtenstein"},
            {"LK", "Sri Lanka"},
            {"LR", "Liberia"},
            {"LS", "Lesotho"},
            {"LT", "Lithuania"},
            {"LU", "Luxembourg"},
            {"LV", "Latvia"},
            {"LY", "Libya"},
            {"MA", "Morocco"},
            {"MC", "Monaco"},
            {"MD", "Moldova"},
            {"ME", "Montenegro"},
            {"MF", "Saint Martin (French part)"},
            {"MG", "Madagascar"},
            {"MH", "Marshall Islands"},
            {"MK", "North Macedonia"},
            {"ML", "Mali"},
            {"MM", "Myanmar"},
            {"MN", "Mongolia"},
            {"MO", "Macao"},
            {"MP", "Northern Mariana Islands"},
            {"MQ", "Martinique"},
            {"MR", "Mauritania"},
            {"MS", "Montserrat"},
            {"MT", "Malta"},
            {"MU", "Mauritius"},
            {"MV", "Maldives"},
            {"MW", "Malawi"},
            {"MX", "Mexico"},
            {"MY", "Malaysia"},
            {"MZ", "Mozambique"},
            {"NA", "Namibia"},
            {"NC", "New Caledonia"},
            {"NE", "Niger"},
            {"NF", "Norfolk Island"},
            {"NG", "Nigeria"},
            {"NI", "Nicaragua"},
            {"NL", "Netherlands"},
            {"NO", "Norway"},
            {"NP", "Nepal"},
            {"NR", "Nauru"},
            {"NU", "Niue"},
            {"NZ", "New Zealand"},
            {"OM", "Oman"},
            {"PA", "Panama"},
            {"PE", "Peru"},
            {"PF", "French Polynesia"},
            {"PG", "Papua New Guinea"},
            {"PH", "Philippines"},
            {"PK", "Pakistan"},
            {"PL", "Poland"},
            {"PM", "Saint Pierre and Miquelon"},
            {"PN", "Pitcairn"},
            {"PR", "Puerto Rico"},
            {"PS", "Palestine"},
            {"PT", "Portugal"},
            {"PW", "Palau"},
            {"PY", "Paraguay"},
            {"QA", "Qatar"},
            {"RE", "Réunion"},
            {"RO", "Romania"},
            {"RS", "Serbia"},
            {"RU", "Russia"},
            {"RW", "Rwanda"},
            {"SA", "Saudi Arabia"},
            {"SB", "Solomon Islands"},
            {"SC", "Seychelles"},
            {"SD", "Sudan"},
            {"SE", "Sweden"},
            {"SG", "Singapore"},
            {"SH", "Saint Helena"},
            {"SI", "Slovenia"},
            {"SJ", "Svalbard and Jan Mayen"},
            {"SK", "Slovakia"},
            {"SL", "Sierra Leone"},
            {"SM", "San Marino"},
            {"SN", "Senegal"},
            {"SO", "Somalia"},
            {"SR", "Suriname"},
            {"SS", "South Sudan"},
            {"ST", "Sao Tome and Principe"},
            {"SV", "El Salvador"},
            {"SX", "Sint Maarten (Dutch part)"},
            {"SY", "Syria"},
            {"SZ", "Eswatini"},
            {"TC", "Turks and Caicos Islands"},
            {"TD", "Chad"},
            {"TF", "French Southern Territories"},
            {"TG", "Togo"},
            {"TH", "Thailand"},
            {"TJ", "Tajikistan"},
            {"TK", "Tokelau"},
            {"TL", "Timor-Leste"},
            {"TM", "Turkmenistan"},
            {"TN", "Tunisia"},
            {"TO", "Tonga"},
            {"TR", "Turkey"},
            {"TT", "Trinidad and Tobago"},
            {"TV", "Tuvalu"},
            {"TW", "Taiwan"},
            {"TZ", "Tanzania"},
            {"UA", "Ukraine"},
            {"UG", "Uganda"},
            {"UM", "U.S. Minor Outlying Islands"},
            {"US", "United States"},
            {"UY", "Uruguay"},
            {"UZ", "Uzbekistan"},
            {"VA", "Holy See"},
            {"VC", "Saint Vincent and the Grenadines"},
            {"VE", "Venezuela"},
            {"VG", "British Virgin Islands"},
            {"VI", "U.S. Virgin Islands"},
            {"VN", "Vietnam"},
            {"VU", "Vanuatu"},
            {"WF", "Wallis and Futuna"},
            {"WS", "Samoa"},
            {"YE", "Yemen"},
            {"YT", "Mayotte"},
            {"ZA", "South Africa"},
            {"ZM", "Zambia"},
            {"ZW", "Zimbabwe"},
            // User-assigned code (not officially ISO 3166-1, but widely used
            // for Kosovo in radio station metadata).
            {"XK", "Kosovo"},
        };

    /// <summary>
    /// Returns the short English country name for the given ISO 3166-1
    /// alpha-2 country code. Lookup is case-insensitive ("us" works the
    /// same as "US").
    /// </summary>
    /// <param name="alpha2Code">
    /// The 2-letter ISO 3166-1 alpha-2 country code (e.g. "US", "GB").
    /// </param>
    /// <returns>
    /// The short country name (e.g. "United States", "United Kingdom").
    /// If the code is null, empty, or not in the lookup table, returns the
    /// raw code (or empty string if null/empty) — this signals a missing
    /// lookup entry without crashing.
    /// </returns>
    /// <example>
    /// <code>
    /// CountryNames.GetShortName("US")   // → "United States"
    /// CountryNames.GetShortName("GB")   // → "United Kingdom"
    /// CountryNames.GetShortName("RU")   // → "Russia"
    /// CountryNames.GetShortName("us")   // → "United States" (case-insensitive)
    /// CountryNames.GetShortName("XX")   // → "XX" (unknown code)
    /// CountryNames.GetShortName(null)   // → ""
    /// </code>
    /// </example>
    public static string GetShortName(string? alpha2Code)
    {
        if (string.IsNullOrWhiteSpace(alpha2Code))
            return string.Empty;

        return _names.TryGetValue(alpha2Code, out var name)
            ? name
            : alpha2Code.Trim().ToUpperInvariant();
    }
}

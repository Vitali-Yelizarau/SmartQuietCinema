
using System.Collections.Frozen;
using System.Globalization;

namespace SmartQuietCinema.Maui.Models;

/// <summary>
/// Provides the catalog of supported application languages.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>
    /// Gets the languages currently available in the application.
    /// </summary>
    public static IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
        Array.AsReadOnly<LanguageOption>(
        [
            new("en", "EN", "English"),
            new("be", "BY-Ў", "Беларуская (кірыліца)"),
            new("be-Latn", "BY-Ł", "Biełaruskaja łacinka"),
            new("uk", "UA", "Українська"),
            new("es", "ES", "Español"),
            new("ru", "RU", "Русский")
        ]);

    private static readonly FrozenSet<string> SupportedCultureCodes = AvailableLanguages.Select(language => language.CultureCode)
                                                                                        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether the specified language is supported.
    /// </summary>
    /// <param name="cultureCode">
    /// The language code to validate.
    /// </param>
    /// <returns>
    /// True if the language is supported; otherwise, false.
    /// </returns>
    public static bool IsSupported(string cultureCode)
    {
        return SupportedCultureCodes.Contains(cultureCode);
    }

    /// <summary>
    /// Gets the display code for the specified language.
    /// </summary>
    /// <param name="cultureCode">
    /// The language code used by the localization system.
    /// </param>
    /// <returns>
    /// The matching display code, or EN if no match exists.
    /// </returns>
    public static string GetDisplayCode(string cultureCode)
    {
        var dCode = AvailableLanguages.FirstOrDefault(language =>
        string.Equals(
            language.CultureCode,
            cultureCode,
            StringComparison.OrdinalIgnoreCase))?.DisplayCode;

        if (dCode is not null) return dCode;

        var neutralCode = CultureInfo.GetCultureInfo(cultureCode)
                                     .TwoLetterISOLanguageName;

        return AvailableLanguages.FirstOrDefault(language =>
            string.Equals(
                language.CultureCode,
                neutralCode,
                StringComparison.OrdinalIgnoreCase))?.DisplayCode ?? "EN";
    }
}

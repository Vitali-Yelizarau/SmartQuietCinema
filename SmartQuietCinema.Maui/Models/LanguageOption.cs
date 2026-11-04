namespace SmartQuietCinema.Maui.Models;

/// <summary>
/// Represents a language available for application localization.
/// </summary>
/// <param name="cultureCode">
/// The language code used by the localization system.
/// </param>
/// <param name="displayCode">
/// The language code displayed in the user interface.
/// </param>
/// <param name="nativeName">
/// The language name written in its native language.
/// </param>
public sealed partial class LanguageOption(string cultureCode, string displayCode, string nativeName)
{
    /// <summary>
    /// Gets the language code used for localization.
    /// </summary>
    public string CultureCode { get; } = cultureCode;

    /// <summary>
    /// Gets the language code displayed in the UI.
    /// </summary>
    public string DisplayCode { get; } = displayCode;

    /// <summary>
    /// Gets the native name of the language.
    /// </summary>
    public string NativeName { get; } = nativeName;
}

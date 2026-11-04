using System.ComponentModel;
using System.Globalization;
using SmartQuietCinema.Maui.Resources.Localization;

namespace SmartQuietCinema.Maui.Services
{
    /// <summary>
    /// Provides localized application strings and supports
    /// changing the interface language at runtime.
    /// </summary>
    public sealed partial class LocalizationService : INotifyPropertyChanged
    {
        /// <summary>
        /// Property name used to notify MAUI bindings
        /// that localized indexer values have changed.
        /// </summary>
        private const string IndexerPropertyName = "Item";

        private CultureInfo _culture = CultureInfo.CurrentUICulture;

        /// <summary>
        /// Occurs when a localized value or the selected
        /// culture changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Gets the currently selected UI culture.
        /// </summary>
        public CultureInfo Culture => _culture;

        /// <summary>
        /// Gets a localized string by its resource key.
        /// </summary>
        /// <param name="key">
        /// Resource key defined in AppResources.resx.
        /// </param>
        /// <returns>
        /// The localized string, or the resource key
        /// if the requested string cannot be found.
        /// </returns>
        public string this[string key]
        {
            get
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);

                return AppResources.ResourceManager.GetString(
                    key, _culture) ?? key;
            }
        }

        /// <summary>
        /// Changes the current application UI language
        /// and notifies registered data bindings.
        /// </summary>
        /// <param name="languageCode">
        /// Culture identifier, such as "en", "ru", or "de".
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the language code is null or empty,
        /// or does not represent a valid culture.
        /// </exception>
        public void SetLanguage(string languageCode)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

            CultureInfo newCulture =
                CultureInfo.GetCultureInfo(languageCode);

            if (_culture.Equals(newCulture))
                return;

            _culture = newCulture;

            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Culture)));

            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(IndexerPropertyName));
        }
    }
}

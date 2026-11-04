
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Maui.Controls;
using SmartQuietCinema.Maui.Resources.Localization;
using SmartQuietCinema.Maui.Services;
using SmartQuietCinema.Core.Interfaces;
using SmartQuietCinema.Core.Models;

namespace SmartQuietCinema.Maui.ViewModels
{
    /// <summary>
    /// Provides audio output device selection, capture controls,
    /// and diagnostic information for the main application page.
    /// </summary>
    public sealed partial class MainPageViewModel(IAudioOutputDeviceProvider deviceProvider, IAudioCaptureSessionFactory captureFactory, LocalizationService localization) 
        : INotifyPropertyChanged
    {
        /// <summary>
        /// Gets the localization service used to provide
        /// translated strings to the user interface.
        /// </summary>
        public LocalizationService Localization { get; } = localization ?? throw new ArgumentNullException(nameof(localization));
        private readonly IAudioOutputDeviceProvider _deviceProvider = deviceProvider ?? throw new ArgumentNullException(nameof(deviceProvider));
        private readonly IAudioCaptureSessionFactory _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));


        private bool _isLanguageMenuOpen;

        private Command? _toggleLanguageMenuCommand;
        private Command<string>? _changeLanguageCommand;

        /// <summary>
        /// Gets or sets whether the language selection menu is visible.
        /// </summary>
        public bool IsLanguageMenuOpen
        {
            get => _isLanguageMenuOpen;
            private set
            {
                if (_isLanguageMenuOpen == value)
                    return;

                _isLanguageMenuOpen = value;
                OnPropertyChanged(nameof(IsLanguageMenuOpen));
            }
        }

        /// <summary>
        /// Gets the flag associated with the currently selected
        /// or fallback application language.
        /// </summary>

        /// <summary>
        /// Gets the two-letter code of the currently selected
        /// application language.
        /// </summary>
        public string SelectedLanguageCode =>
            Localization.Culture.TwoLetterISOLanguageName.ToUpperInvariant();


        /// <summary>
        /// Gets the command used to toggle the language menu.
        /// </summary>
        public Command ToggleLanguageMenuCommand =>
            _toggleLanguageMenuCommand ??= new Command(
                () => IsLanguageMenuOpen = !IsLanguageMenuOpen);

        /// <summary>
        /// Gets the command used to change the application language.
        /// </summary>
        public Command<string> ChangeLanguageCommand =>
            _changeLanguageCommand ??= new Command<string>(
                ChangeLanguage);

        /// <summary>
        /// Changes the application language and closes the menu.
        /// </summary>
        /// <param name="languageCode">
        /// Language code supported by the current application resources.
        /// </param>
        private void ChangeLanguage(string? languageCode)
        {
            if (languageCode is not ("en" or "ru"))
                return;

            Localization.SetLanguage(languageCode);
            IsLanguageMenuOpen = false;
        }


        private IAudioCaptureSession? _captureSession;
        private AudioOutputDevice? _selectedOutputDevice;
        private Command? _startCaptureCommand;
        private Command? _stopCaptureCommand;

        private bool _isBusy;
        private long _capturedBytes;

        /// <summary>
        /// Indicates whether the ViewModel is subscribed
        /// to application language changes.
        /// </summary>
        private bool _isLocalizationSubscribed;

        /// <summary>
        /// Resource key identifying the current capture status.
        /// </summary>
        private string _captureStatusKey = nameof(AppResources.CaptureNotStarted);

        /// <summary>
        /// Arguments used to format the localized capture status.
        /// </summary>
        private object?[] _captureStatusArguments = [];

        /// <summary>
        /// Occurs when a ViewModel property changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Gets the available audio output devices.
        /// </summary>
        public ObservableCollection<AudioOutputDevice> OutputDevices { get; } = [];

        /// <summary>
        /// Gets or sets the selected audio output device.
        /// </summary>
        public AudioOutputDevice? SelectedOutputDevice
        {
            get => _selectedOutputDevice;
            set
            {
                if (Equals(_selectedOutputDevice, value))
                    return;

                _selectedOutputDevice = value;

                OnPropertyChanged(nameof(SelectedOutputDevice));
                UpdateCommandStates();
            }
        }


        /// <summary>
        /// Subscribes to application language changes
        /// and refreshes localized diagnostic properties.
        /// </summary>
        public void SubscribeToLanguageChanges()
        {
            if (_isLocalizationSubscribed)
                return;

            Localization.PropertyChanged += OnLocalizationChanged;
            _isLocalizationSubscribed = true;

            RefreshLocalizedDiagnostics();
        }

        /// <summary>
        /// Unsubscribes from application language changes
        /// to prevent unnecessary event handling and memory leaks.
        /// </summary>
        public void UnsubscribeFromLanguageChanges()
        {
            if (!_isLocalizationSubscribed)
                return;

            Localization.PropertyChanged -= OnLocalizationChanged;
            _isLocalizationSubscribed = false;
        }

        /// <summary>
        /// Handles changes to the selected application language.
        /// </summary>
        /// <param name="sender">
        /// The localization service that raised the event.
        /// </param>
        /// <param name="e">
        /// Information about the changed property.
        /// </param>

        private void OnLocalizationChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LocalizationService.Culture))
                return;

            OnPropertyChanged(nameof(CaptureStatus));
            OnPropertyChanged(nameof(CapturedBytesText));
            OnPropertyChanged(nameof(SelectedLanguageCode));
        }


        /// <summary>
        /// Notifies the UI that localized capture diagnostics
        /// must be evaluated again.
        /// </summary>
        private void RefreshLocalizedDiagnostics()
        {
            OnPropertyChanged(nameof(CaptureStatus));
            OnPropertyChanged(nameof(CapturedBytesText));
        }


        /// <summary>
        /// Gets the number of captured audio bytes.
        /// </summary>
        public long CapturedBytes
        {
            get => _capturedBytes;
            private set
            {
                if (_capturedBytes == value)
                    return;

                _capturedBytes = value;

                OnPropertyChanged(nameof(CapturedBytes));
                OnPropertyChanged(nameof(CapturedBytesText));
            }
        }


        /// <summary>
        /// Gets the captured byte count formatted
        /// according to the selected UI culture.
        /// </summary>
        public string CapturedBytesText =>
            string.Format(
                Localization.Culture,
                Localization[nameof(AppResources.CapturedBytes)],
                CapturedBytes);



        /// <summary>
        /// Gets the current capture status in the selected UI language.
        /// </summary>
        public string CaptureStatus =>
            string.Format(
                Localization.Culture,
                Localization[_captureStatusKey],
                _captureStatusArguments);


        /// <summary>
        /// Gets the command used to start audio capture.
        /// </summary>
        public Command StartCaptureCommand =>
            _startCaptureCommand ??= new Command(
                async () => await StartCaptureAsync(),
                CanStartCapture);

        /// <summary>
        /// Gets the command used to stop audio capture.
        /// </summary>
        public Command StopCaptureCommand =>
            _stopCaptureCommand ??= new Command(
                async () => await StopCaptureAsync(),
                CanStopCapture);


        /// <summary>
        /// Updates the current capture status using a localization
        /// resource key and optional formatting arguments.
        /// </summary>
        /// <param name="resourceKey">
        /// Resource key defined in AppResources.
        /// </param>
        /// <param name="arguments">
        /// Values inserted into the localized message.
        /// </param>
        private void SetCaptureStatus(
            string resourceKey,
            params object?[] arguments)
        {
            _captureStatusKey = resourceKey;
            _captureStatusArguments = arguments;

            OnPropertyChanged(nameof(CaptureStatus));
        }


        /// <summary>
        /// Refreshes the list of available audio output devices.
        /// Must be called from the UI thread.
        /// </summary>
        public void LoadOutputDevices()
        {
            var devices = _deviceProvider.GetActiveOutputDevices();

            OutputDevices.Clear();

            foreach (var device in devices)
            {
                OutputDevices.Add(device);
            }

            SelectedOutputDevice = null;
        }

        /// <summary>
        /// Starts a new audio capture session for the selected device.
        /// </summary>
        /// <returns>
        /// A task representing the capture startup operation.
        /// </returns>
        public async Task StartCaptureAsync()
        {
            if (!CanStartCapture())
                return;

            var selectedDevice = SelectedOutputDevice!;

            _isBusy = true;
            UpdateCommandStates();

            SetCaptureStatus(nameof(AppResources.CaptureStarting));
            CapturedBytes = 0;

            IAudioCaptureSession? session = null;

            try
            {
                session = _captureFactory.Create();
                session.Start(selectedDevice.Id);

                _captureSession = session;

                SetCaptureStatus(nameof(AppResources.CaptureStarted), session.SampleRate, session.Channels);
            }
            catch (Exception ex)
            {

                SetCaptureStatus(nameof(AppResources.CaptureStartError), ex.Message);

                if (session is not null)
                {
                    try
                    {
                        await session.DisposeAsync();
                    }
                    catch (Exception cleanupException)
                    {
                        SetCaptureStatus(nameof(AppResources.CaptureStartAndCleanupError), ex.Message, cleanupException.Message);
                    }
                }
            }
            finally
            {
                _isBusy = false;
                UpdateCommandStates();
            }
        }

        /// <summary>
        /// Stops the active capture session and releases its resources.
        /// The last captured byte count is preserved.
        /// </summary>
        /// <returns>
        /// A task representing the capture shutdown operation.
        /// </returns>
        public async Task StopCaptureAsync()
        {
            if (!CanStopCapture())
                return;

            _isBusy = true;
            UpdateCommandStates();

            var session = _captureSession!;
            _captureSession = null;

            SetCaptureStatus(nameof(AppResources.CaptureStopping));

            try
            {
                await session.DisposeAsync();

                CapturedBytes = session.CapturedBytes;
                SetCaptureStatus(nameof(AppResources.CaptureStopped));
            }
            catch (Exception ex)
            {
                SetCaptureStatus(nameof(AppResources.CaptureStopError), ex.Message);
            }
            finally
            {
                _isBusy = false;
                UpdateCommandStates();
            }
        }

        /// <summary>
        /// Updates the captured byte count from the active session.
        /// Must be called from the UI thread.
        /// </summary>
        public void RefreshCaptureStatistics()
        {
            if (_captureSession is not null)
            {
                CapturedBytes = _captureSession.CapturedBytes;
            }
        }

        /// <summary>
        /// Determines whether audio capture can be started.
        /// </summary>
        private bool CanStartCapture() =>
            !_isBusy &&
            _captureSession is null &&
            SelectedOutputDevice is not null;

        /// <summary>
        /// Determines whether the active capture can be stopped.
        /// </summary>
        private bool CanStopCapture() =>
            !_isBusy && _captureSession is not null;

        /// <summary>
        /// Refreshes the enabled state of capture commands.
        /// </summary>
        private void UpdateCommandStates()
        {
            _startCaptureCommand?.ChangeCanExecute();
            _stopCaptureCommand?.ChangeCanExecute();
        }

        /// <summary>
        /// Raises a property change notification.
        /// </summary>
        /// <param name="propertyName">
        /// Name of the property that has changed.
        /// </param>
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }
}

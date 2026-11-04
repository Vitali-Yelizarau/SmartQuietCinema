
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Maui.Controls;
using SmartQuietCinema.Core.Interfaces;
using SmartQuietCinema.Core.Models;

namespace SmartQuietCinema.Maui.ViewModels
{
    /// <summary>
    /// Provides audio output device selection, capture controls,
    /// and diagnostic information for the main application page.
    /// </summary>
    public sealed partial class MainPageViewModel(
        IAudioOutputDeviceProvider deviceProvider,
        IAudioCaptureSessionFactory captureFactory)
        : INotifyPropertyChanged
    {
        private readonly IAudioOutputDeviceProvider _deviceProvider =
            deviceProvider ?? throw new ArgumentNullException(nameof(deviceProvider));

        private readonly IAudioCaptureSessionFactory _captureFactory =
            captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));

        private IAudioCaptureSession? _captureSession;
        private AudioOutputDevice? _selectedOutputDevice;
        private Command? _startCaptureCommand;
        private Command? _stopCaptureCommand;

        private bool _isBusy;
        private long _capturedBytes;
        private string _captureStatus = "Захват не запущен";

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
        /// Gets the formatted capture statistics text.
        /// </summary>
        public string CapturedBytesText =>
            $"Получено данных: {CapturedBytes:N0} байт";

        /// <summary>
        /// Gets the current audio capture status.
        /// </summary>
        public string CaptureStatus
        {
            get => _captureStatus;
            private set
            {
                if (_captureStatus == value)
                    return;

                _captureStatus = value;
                OnPropertyChanged(nameof(CaptureStatus));
            }
        }

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

            CaptureStatus = "Запуск захвата...";
            CapturedBytes = 0;

            IAudioCaptureSession? session = null;

            try
            {
                session = _captureFactory.Create();
                session.Start(selectedDevice.Id);

                _captureSession = session;

                CaptureStatus =
                    $"Захват запущен. Частота: {session.SampleRate} Гц, " +
                    $"каналов: {session.Channels}";
            }
            catch (Exception ex)
            {
                CaptureStatus = $"Ошибка запуска: {ex.Message}";

                if (session is not null)
                {
                    try
                    {
                        await session.DisposeAsync();
                    }
                    catch (Exception cleanupException)
                    {
                        CaptureStatus +=
                            $" Ошибка освобождения ресурсов: {cleanupException.Message}";
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

            CaptureStatus = "Остановка захвата...";

            try
            {
                await session.DisposeAsync();

                CapturedBytes = session.CapturedBytes;
                CaptureStatus = "Захват остановлен";
            }
            catch (Exception ex)
            {
                CaptureStatus = $"Ошибка остановки: {ex.Message}";
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

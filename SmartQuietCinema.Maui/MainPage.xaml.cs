
using Microsoft.Maui.Dispatching;
using SmartQuietCinema.Maui.ViewModels;

namespace SmartQuietCinema.Maui
{
    /// <summary>
    /// Represents the main application page.
    /// </summary>
    public partial class MainPage : ContentPage
    {
        /// <summary>
        /// Interval between capture statistics updates, in milliseconds.
        /// This affects UI refresh only, not audio capture timing.
        /// </summary>
        private const int StatisticsRefreshIntervalMs = 250;

        private readonly MainPageViewModel _viewModel;
        private readonly IDispatcherTimer _statisticsTimer;

        /// <summary>
        /// Initializes the main page and its capture statistics timer.
        /// </summary>
        /// <param name="viewModel">
        /// ViewModel used for audio device and capture management.
        /// </param>
        public MainPage(MainPageViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel
                ?? throw new ArgumentNullException(nameof(viewModel));

            BindingContext = _viewModel;

            _statisticsTimer = Dispatcher.CreateTimer();

            _statisticsTimer.Interval =
                TimeSpan.FromMilliseconds(StatisticsRefreshIntervalMs);

            _statisticsTimer.IsRepeating = true;

            _statisticsTimer.Tick += OnStatisticsTimerTick;
        }

        /// <summary>
        /// Loads output devices and starts refreshing capture statistics.
        /// </summary>
        protected override void OnAppearing()
        {
            base.OnAppearing();

            _viewModel.SubscribeToLanguageChanges();
            _viewModel.LoadOutputDevices();
            _statisticsTimer.Start();
        }

        /// <summary>
        /// Stops the statistics timer and the active capture session
        /// when the page disappears.
        /// </summary>
        protected override async void OnDisappearing()
        {
            _viewModel.UnsubscribeFromLanguageChanges();
            _statisticsTimer.Stop();

            base.OnDisappearing();
            await _viewModel.StopCaptureAsync();
        }

        /// <summary>
        /// Refreshes audio capture statistics on the UI thread.
        /// </summary>
        /// <param name="sender">
        /// Timer that raised the event.
        /// </param>
        /// <param name="e">
        /// Event arguments.
        /// </param>
        private void OnStatisticsTimerTick(
            object? sender,
            EventArgs e)
        {
            _viewModel.RefreshCaptureStatistics();
        }


        /// <summary>
        /// Stops the current audio capture session when the
        /// selected audio output device changes.
        /// </summary>
        /// <param name="sender">
        /// The picker that raised the event.
        /// </param>
        /// <param name="e">
        /// The event arguments.
        /// </param>
        private async void OnAudioOutputDeviceChanged(
            object sender,
            EventArgs e)
        {
            if (BindingContext is not MainPageViewModel viewModel)
                return;

            await viewModel.StopCaptureOnDeviceChangeAsync();
        }
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace SmartQuietCinema.Maui
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "Smart Quiet Cinema"
            };


#if WINDOWS
            const double defaultWidth = 1000;
            const double defaultHeight = 750;

            const double minimumWidth = 760;
            const double maximumWidth = 1200;

            const double minimumHeight = 650;
            const double maximumHeight = 650;

            window.Width = defaultWidth;
            window.Height = defaultHeight;

            window.MinimumWidth = minimumWidth;
            window.MaximumWidth = maximumWidth;

            window.MinimumHeight = minimumHeight;
            window.MaximumHeight = maximumHeight;
#endif

            return window;
        }
    }
}
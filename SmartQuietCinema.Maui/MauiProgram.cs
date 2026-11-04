#if WINDOWS
using SmartQuietCinema.Core.Interfaces;
using SmartQuietCinema.Platform.Windows.Devices;
using SmartQuietCinema.Maui.ViewModels;
using SmartQuietCinema.Platform.Windows.Capture;
#endif

using Microsoft.Extensions.Logging;

namespace SmartQuietCinema.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();


#if WINDOWS
            // Register the Windows-specific audio output device provider.
            builder.Services.AddSingleton<
                IAudioOutputDeviceProvider,
                WindowsAudioDeviceEnumerator>();

            // Register the main page ViewModel.
            builder.Services.AddTransient<MainPageViewModel>();

            // Register the main application page.
            builder.Services.AddTransient<MainPage>();

            // Register the Windows audio capture session factory.
            builder.Services.AddSingleton<IAudioCaptureSessionFactory, WindowsAudioCaptureSessionFactory>();

#endif

            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

        #if DEBUG
    		builder.Logging.AddDebug();
        #endif

            return builder.Build();
        }
    }
}

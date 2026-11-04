using SmartQuietCinema.Core.Interfaces;

namespace SmartQuietCinema.Platform.Windows.Capture
{
    /// <summary>
    /// Creates WASAPI loopback capture sessions for Windows.
    /// </summary>
    public sealed class WindowsAudioCaptureSessionFactory : IAudioCaptureSessionFactory
    {
        /// <summary>
        /// Creates a new Windows audio capture session.
        /// </summary>
        /// <returns>
        /// A new WindowsAudioLoopbackCapture instance.
        /// </returns>
        public IAudioCaptureSession Create()
        {
            return new WindowsAudioLoopbackCapture();
        }
    }
}

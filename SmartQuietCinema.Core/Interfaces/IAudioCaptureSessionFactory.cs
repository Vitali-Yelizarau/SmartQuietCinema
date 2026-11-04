namespace SmartQuietCinema.Core.Interfaces
{
    /// <summary>
    /// Defines a platform-independent factory
    /// for creating audio capture sessions.
    /// </summary>
    public interface IAudioCaptureSessionFactory
    {
        /// <summary>
        /// Creates a new audio capture session.
        /// </summary>
        /// <returns>
        /// A new session instance that has not been started.
        /// The caller is responsible for disposing the session.
        /// </returns>
        IAudioCaptureSession Create();
    }
}

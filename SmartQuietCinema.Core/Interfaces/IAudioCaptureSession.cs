
namespace SmartQuietCinema.Core.Interfaces
{
    /// <summary>
    /// Defines a platform-independent contract for capturing
    /// audio rendered through an output device.
    /// </summary>
    /// <remarks>
    /// Each instance represents a single capture session.
    /// A new instance must be created for each new session.
    ///
    /// Disposing the session stops capture and releases
    /// the associated resources.
    /// </remarks>
    public interface IAudioCaptureSession : IAsyncDisposable
    {
        /// <summary>
        /// Gets the total number of captured audio bytes.
        /// </summary>
        long CapturedBytes { get; }

        /// <summary>
        /// Gets the audio sample rate in Hertz.
        /// Returns null before initialization.
        /// </summary>
        int? SampleRate { get; }

        /// <summary>
        /// Gets the number of audio channels.
        /// Returns null before initialization.
        /// </summary>
        int? Channels { get; }

        /// <summary>
        /// Starts capturing audio from the specified output device.
        /// </summary>
        /// <param name="deviceId">
        /// Platform-specific identifier of the audio output device.
        /// </param>
        void Start(string deviceId);
    }
}


namespace SmartQuietCinema.Core.Interfaces
{
    /// <summary>
    /// Defines a common contract for digital signal processing (DSP) components.
    /// Allows audio processing algorithms to operate independently
    /// of the underlying operating system and audio source.
    /// </summary>
    public interface IAudioProcessor
    {
        /// <summary>
        /// Processes an audio buffer in place.
        /// The processed samples overwrite the original data.
        /// </summary>
        /// <param name="samples">
        /// Audio samples represented as 32-bit floating-point values.
        /// Samples are stored in interleaved channel order.
        ///
        /// Example for stereo audio:
        /// [Left0, Right0, Left1, Right1, ...].
        ///
        /// Input values are typically normalized to [-1.0, 1.0].
        /// Output values may exceed this range if amplification is applied.
        /// 
        /// 
        /// </param>
        /// <param name="channels">
        /// Number of audio channels (e.g., 1 for mono, 2 for stereo,
        /// or 6 for 5.1 surround sound).
        ///
        /// Must be greater than zero.
        /// The buffer length must be divisible by this value.
        /// 
        /// 
        /// </param>
        /// <param name="sampleRate">
        /// Audio sample rate in Hertz (Hz).
        /// Common values include 44100, 48000, and 96000.
        /// Must be greater than zero.
        /// </param>
        /// <remarks>
        /// Implementations should be suitable for real-time audio processing.
        /// Avoid blocking operations, unnecessary memory allocations,
        /// and expensive computations that may interrupt playback.
        ///
        /// The method must preserve the number of samples
        /// and the original channel ordering.
        /// </remarks>
        void Process(Span<float> samples, int channels, int sampleRate);
    }
}

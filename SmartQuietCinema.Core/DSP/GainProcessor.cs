
using SmartQuietCinema.Core.Interfaces;

namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Adjusts the amplitude of an audio signal using
    /// a constant gain value expressed in decibels (dB).
    /// </summary>
    /// <remarks>
    /// This processor modifies audio samples in place.
    /// It does not apply dynamic range compression
    /// or prevent audio clipping.
    /// </remarks>
    public sealed class GainProcessor : IAudioProcessor
    {
        private readonly float _linearGain;

        /// <summary>
        /// Gets the configured gain in decibels.
        /// </summary>
        public double GainDb { get; }


        /// <summary>
        /// Base of the logarithmic decibel scale.
        /// </summary>
        private const double DecibelBase = 10.0;

        /// <summary>
        /// Conversion factor used for amplitude ratios in decibels.
        /// </summary>
        private const double AmplitudeDecibelFactor = 20.0;


        /// <summary>
        /// Initializes a new instance of the GainProcessor class.
        /// </summary>
        /// <param name="gainDb">
        /// Gain adjustment in decibels.
        /// Zero leaves the signal unchanged.
        /// Negative values attenuate the signal.
        /// Positive values amplify the signal.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the gain is not finite or cannot
        /// be represented as a finite floating-point multiplier.
        /// </exception>

        public GainProcessor(double gainDb)
        {
            if (!double.IsFinite(gainDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gainDb));
            }

            float linearGain = (float)Math.Pow(DecibelBase, gainDb / AmplitudeDecibelFactor);


            if (!float.IsFinite(linearGain))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gainDb));
            }

            GainDb = gainDb;
            _linearGain = linearGain;
        }


        /// <summary>
        /// Applies the configured gain to all audio samples.
        /// </summary>
        /// <param name="samples">
        /// Interleaved 32-bit floating-point audio samples.
        /// The buffer is modified in place.
        /// </param>
        /// <param name="channels">
        /// Number of audio channels.
        /// Must be greater than zero.
        /// </param>
        /// <param name="sampleRate">
        /// Audio sample rate in Hertz.
        /// Must be greater than zero.
        /// The gain calculation does not depend on this value.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when channels or sampleRate is not positive.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the buffer length is not divisible
        /// by the number of channels.
        /// </exception>
        public void Process(
            Span<float> samples,
            int channels,
            int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (samples.Length % channels != 0)
            {
                throw new ArgumentException(
                    "The buffer length must be divisible by the channel count.",
                    nameof(samples));
            }

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= _linearGain;
            }
        }
    }
}

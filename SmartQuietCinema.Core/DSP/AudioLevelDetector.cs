
namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Tracks the RMS envelope of an audio signal
    /// using configurable attack and release times.
    /// </summary>
    /// <remarks>
    /// The detector preserves its state between audio buffers.
    /// It does not modify the input samples.
    /// </remarks>
    public sealed class AudioLevelDetector
    {
        private readonly double _attackMilliseconds;
        private readonly double _releaseMilliseconds;

        private int _cachedSampleRate;
        private double _attackCoefficient;
        private double _releaseCoefficient;

        private readonly ActiveChannelRmsCalculator _activeChannels = new();

        /// <summary>
        /// Gets the current smoothed signal level
        /// expressed as a linear amplitude.
        /// </summary>
        public double Level { get; private set; }

        /// <summary>
        /// Initializes a new audio level detector.
        /// </summary>
        /// <param name="attackMilliseconds">
        /// Time constant used when the signal level rises.
        /// </param>
        /// <param name="releaseMilliseconds">
        /// Time constant used when the signal level falls.
        /// </param>
        public AudioLevelDetector(
            double attackMilliseconds,
            double releaseMilliseconds)
        {
            if (!double.IsFinite(attackMilliseconds) || attackMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attackMilliseconds));
            }

            if (!double.IsFinite(releaseMilliseconds) || releaseMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(releaseMilliseconds));
            }

            _attackMilliseconds = attackMilliseconds;
            _releaseMilliseconds = releaseMilliseconds;
        }

        /// <summary>
        /// Analyzes an interleaved audio buffer without
        /// modifying its samples.
        /// </summary>
        /// <param name="samples">
        /// Interleaved 32-bit floating-point samples.
        /// </param>
        /// <param name="channels">
        /// Number of audio channels.
        /// </param>
        /// <param name="sampleRate">
        /// Audio sample rate in Hertz.
        /// </param>
        public void Analyze(
            ReadOnlySpan<float> samples,
            int channels,
            int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (samples.Length % channels != 0)
            {
                throw new ArgumentException("The buffer length must be divisible by the channel count.", nameof(samples));
            }

            UpdateCoefficients(sampleRate);

            for (int i = 0; i < samples.Length; i += channels)
            {
                ProcessFrame(samples.Slice(i, channels));
            }
        }

        /// <summary>
        /// Analyzes one complete audio frame and returns
        /// the updated signal envelope.
        /// </summary>
        /// <param name="frame">
        /// Samples belonging to one audio frame,
        /// containing one sample per channel.
        /// </param>
        /// <param name="sampleRate">
        /// Audio sample rate in Hertz.
        /// </param>
        /// <returns>
        /// The updated smoothed signal level.
        /// </returns>
        public double AnalyzeFrame(
            ReadOnlySpan<float> frame,
            int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (frame.IsEmpty)
            {
                throw new ArgumentException("The audio frame must not be empty.", nameof(frame));
            }

            UpdateCoefficients(sampleRate);
            ProcessFrame(frame);

            return Level;
        }

        /// <summary>
        /// Updates attack and release coefficients
        /// when the audio sample rate changes.
        /// </summary>
        private void UpdateCoefficients(int sampleRate)
        {
            if (_cachedSampleRate == sampleRate)
                return;

            _attackCoefficient = Math.Exp(-1000.0 / (_attackMilliseconds * sampleRate));

            _releaseCoefficient = Math.Exp(-1000.0 / (_releaseMilliseconds * sampleRate));

            _cachedSampleRate = sampleRate;
        }

        /// <summary>
        /// Updates the signal envelope for one audio frame.
        /// </summary>
        private void ProcessFrame(ReadOnlySpan<float> frame)
        {
            double sumSquares = 0;

            for (int channel = 0; channel < frame.Length; channel++)
            {
                double sample = frame[channel];
                sumSquares += sample * sample;
            }

            double frameRms = _activeChannels.Calculate(frame, _cachedSampleRate);

            double coefficient = frameRms > Level ? _attackCoefficient : _releaseCoefficient;

            Level += (frameRms - Level) * (1.0 - coefficient);
        }

        /// <summary>
        /// Resets the detector to its initial state.
        /// </summary>
        public void Reset()
        {
            Level = 0;
            _activeChannels.Reset();
        }
    }
}

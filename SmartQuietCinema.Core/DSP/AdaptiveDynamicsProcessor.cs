
using SmartQuietCinema.Core.Interfaces;

namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Applies adaptive bidirectional dynamic range processing
    /// to interleaved floating-point audio samples.
    /// </summary>
    /// <remarks>
    /// Quiet signals are amplified, loud signals are attenuated,
    /// and signals within the neutral range remain unchanged
    /// after the gain has settled.
    ///
    /// Gain transitions are smoothed to avoid abrupt changes.
    /// The processor preserves its state between audio buffers.
    ///
    /// This implementation does not provide peak limiting
    /// or guarantee protection against clipping.
    /// </remarks>
    public sealed class AdaptiveDynamicsProcessor : IAudioProcessor
    {
        private const double DetectorAttackMs = 10.0;
        private const double DetectorReleaseMs = 100.0;

        private const double GainReductionAttackMs = 5.0;
        private const double GainRecoveryReleaseMs = 150.0;

        private const double DecibelFactor = 20.0;

        private readonly AudioLevelDetector _levelDetector;
        private readonly AdaptiveGainCurve _gainCurve;

        private double _currentLinearGain = 1.0;
        private readonly SamplePeakLimiter _peakLimiter = new();

        /// <summary>
        /// Gets the currently applied gain in decibels.
        /// </summary>
        public double CurrentGainDb => DecibelFactor * Math.Log10(_currentLinearGain);

        /// <summary>
        /// Initializes a new adaptive dynamics processor.
        /// </summary>
        public AdaptiveDynamicsProcessor()
        {
            _levelDetector = new AudioLevelDetector(DetectorAttackMs, DetectorReleaseMs);

            _gainCurve = new AdaptiveGainCurve();
        }

        /// <summary>
        /// Applies adaptive gain to interleaved audio samples.
        /// The input buffer is modified in place.
        /// </summary>
        /// <param name="samples">
        /// Interleaved 32-bit floating-point audio samples.
        /// </param>
        /// <param name="channels">
        /// Number of audio channels.
        /// </param>
        /// <param name="sampleRate">
        /// Sample rate in Hertz.
        /// </param>
        public void Process(
            Span<float> samples,
            int channels,
            int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (samples.Length % channels != 0)
            {
                throw new ArgumentException("The buffer length must be divisible by the channel count.", nameof(samples));
            }

            // Calculate smoothing coefficients once per buffer.
            double attackCoefficient = Math.Exp(-1000.0 / (GainReductionAttackMs * sampleRate));

            double releaseCoefficient = Math.Exp(-1000.0 / (GainRecoveryReleaseMs * sampleRate));

            // Process complete audio frames to preserve
            // channel synchronization.
            for (int i = 0; i < samples.Length; i += channels)
            {
                ReadOnlySpan<float> frame = samples.Slice(i, channels);

                // Analyze the original frame before modification.
                double detectedLevel = _levelDetector.AnalyzeFrame(frame, sampleRate);

                // Convert the measured linear amplitude to dBFS.
                double levelDb = detectedLevel > 0 ? DecibelFactor * Math.Log10(detectedLevel) : double.NegativeInfinity;

                // Calculate the desired gain adjustment.
                double targetGainDb = _gainCurve.CalculateTargetGainDb(levelDb);

                double targetLinearGain = Math.Pow(10.0, targetGainDb / DecibelFactor);

                // Reduce gain quickly, but recover more slowly.
                double coefficient = targetLinearGain < _currentLinearGain ? attackCoefficient : releaseCoefficient;

                _currentLinearGain = targetLinearGain + coefficient * (_currentLinearGain - targetLinearGain);

                // Apply the same gain to every channel in the frame.
                for (int channel = 0; channel < channels; channel++)
                {
                    samples[i + channel] = (float)(samples[i + channel] * _currentLinearGain);
                }

                // Protect the processed frame from sample-level overshoot.
                _peakLimiter.ProcessFrame(samples.Slice(i, channels), sampleRate);
            }
        }

        /// <summary>
        /// Resets the detector and gain smoothing state.
        /// </summary>
        public void Reset()
        {
            _levelDetector.Reset();
            _peakLimiter.Reset();
            _currentLinearGain = 1.0;
        }
    }
}

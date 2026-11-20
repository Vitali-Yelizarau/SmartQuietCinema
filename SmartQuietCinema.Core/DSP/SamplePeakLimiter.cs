
namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Limits sample peaks using channel-linked gain reduction
    /// and a smoothed release.
    /// </summary>
    /// <remarks>
    /// This is a zero-lookahead safety limiter.
    /// It prevents sample-level overshoot but does not
    /// guarantee transparent or true-peak limiting.
    /// </remarks>
    public sealed class SamplePeakLimiter
    {
        private const double Ceiling = 0.95;
        private const double ReleaseMilliseconds = 100.0;

        private double _currentGain = 1.0;
        private double _releaseCoefficient;
        private int _cachedSampleRate;

        /// <summary>
        /// Limits one complete interleaved audio frame.
        /// </summary>
        /// <param name="frame">
        /// One sample for each audio channel.
        /// </param>
        /// <param name="sampleRate">
        /// Audio sample rate in Hertz.
        /// </param>
        public void ProcessFrame(Span<float> frame, int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (frame.IsEmpty) throw new ArgumentException("The audio frame must not be empty.", nameof(frame));

            if (_cachedSampleRate != sampleRate)
            {
                const double MillisecondsPerSecond = 1000.0;
                _releaseCoefficient = Math.Exp(-MillisecondsPerSecond / (ReleaseMilliseconds * sampleRate));

                _cachedSampleRate = sampleRate;
            }

            double peak = 0.0;

            for (int i = 0; i < frame.Length; i++)
            {
                if (!float.IsFinite(frame[i]))
                {
                    frame[i] = 0.0f;
                    continue;
                }

                peak = Math.Max(peak, Math.Abs((double)frame[i]));
            }

            // Determine the gain required to respect the ceiling.
            double requiredGain = peak > Ceiling ? Ceiling / peak : 1.0;

            if (requiredGain < _currentGain)
            {
                // React immediately to a dangerous peak.
                _currentGain = requiredGain;
            }
            else
            {
                // Recover smoothly after the peak.
                _currentGain = requiredGain + _releaseCoefficient * (_currentGain - requiredGain);
            }

            // Apply identical gain to all channels.
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)(frame[i] * _currentGain);
            }
        }

        /// <summary>
        /// Restores the initial limiter state.
        /// </summary>
        public void Reset()
        {
            _currentGain = 1.0;
        }
    }
}

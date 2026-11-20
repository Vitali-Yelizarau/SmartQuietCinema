
namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Calculates frame RMS while excluding inactive audio channels.
    /// Channel activity is estimated using smoothed signal energy.
    /// </summary>
    public sealed class ActiveChannelRmsCalculator
    {
        // Initial experimental activity threshold: -80 dBFS.
        private const double ActivityThreshold = 0.0001;
        private const double ActivityThresholdSquared = ActivityThreshold * ActivityThreshold;

        private const double AttackMilliseconds = 5.0;
        private const double ReleaseMilliseconds = 250.0;

        private double[] _channelEnergy = Array.Empty<double>();

        private int _cachedSampleRate;
        private double _attackCoefficient;
        private double _releaseCoefficient;

        /// <summary>
        /// Calculates RMS using channels currently carrying signal.
        /// </summary>
        public double Calculate(
            ReadOnlySpan<float> frame,
            int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (frame.IsEmpty)
            {
                throw new ArgumentException("The audio frame must not be empty.", nameof(frame));
            }

            // Reinitialize activity tracking when channel count changes.
            if (_channelEnergy.Length != frame.Length)
            {
                _channelEnergy = new double[frame.Length];
            }

            // Cache time coefficients for the current sample rate.
            if (_cachedSampleRate != sampleRate)
            {
                _attackCoefficient = Math.Exp(-1000.0 / (AttackMilliseconds * sampleRate));

                _releaseCoefficient = Math.Exp(-1000.0 / (ReleaseMilliseconds * sampleRate));

                _cachedSampleRate = sampleRate;
            }

            double sumSquares = 0.0;
            int activeChannels = 0;

            for (int channel = 0; channel < frame.Length; channel++)
            {
                double sample = frame[channel];

                // Ignore non-finite values in activity estimation.
                if (!double.IsFinite(sample))
                {
                    sample = 0.0;
                }

                double power = sample * sample;
                double previousEnergy = _channelEnergy[channel];

                double coefficient = power > previousEnergy ? _attackCoefficient : _releaseCoefficient;

                double smoothedEnergy = power + coefficient * (previousEnergy - power);

                _channelEnergy[channel] = smoothedEnergy;

                // A channel becomes active immediately when its
                // signal exceeds the threshold. Smoothed energy
                // prevents it from becoming inactive too quickly.
                bool isActive = power >= ActivityThresholdSquared || smoothedEnergy >= ActivityThresholdSquared;

                if (!isActive)
                {
                    continue;
                }

                sumSquares += power;
                activeChannels++;
            }

            return activeChannels > 0 ? Math.Sqrt(sumSquares / activeChannels) : 0.0;
        }

        /// <summary>
        /// Clears channel activity history.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_channelEnergy);
        }
    }
}

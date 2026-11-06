
using SmartQuietCinema.Core.Interfaces;

namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Attenuates sudden audio peaks using lookahead and
    /// an adaptive reference envelope.
    /// The same gain is applied to all channels.
    /// </summary>
    public sealed class LookaheadTransientController : IAudioProcessor
    {
        public const int LookaheadMilliseconds = 12;

        // Transient detection uses the difference between
        // the current peak and the established peak level.
        private const double TriggerExcessPeak = 0.10;
        private const double FullReductionExcessPeak = 0.35;
        private const double MaximumReductionDb = 12.0;

        private const double AttackMilliseconds = 2.0;
        private const double ReleaseMilliseconds = 160.0;
        private const double PeakReleaseMilliseconds = 100.0;

        // The reference envelope follows established peak levels
        // without reacting immediately to sudden transients.
        private const double ReferenceAttackMilliseconds = 80.0;
        private const double ReferenceReleaseMilliseconds = 300.0;

        private float[] _delayBuffer = Array.Empty<float>();

        private int _sampleRate;
        private int _channels;
        private int _writeFrame;

        private double _attackCoefficient;
        private double _releaseCoefficient;
        private double _peakReleaseCoefficient;
        private double _referenceAttackCoefficient;
        private double _referenceReleaseCoefficient;

        private double _peakEnvelope;
        private double _referenceEnvelope;
        private double _currentGain = 1.0;

        /// <summary>
        /// Gets the processing latency in audio frames.
        /// </summary>
        public int LatencyFrames { get; private set; }

        /// <summary>
        /// Gets the current transient attenuation in decibels.
        /// </summary>
        public double CurrentReductionDb =>
            20.0 * Math.Log10(_currentGain);

        /// <summary>
        /// Processes interleaved floating-point audio samples.
        /// </summary>
        public void Process(
            Span<float> samples,
            int channels,
            int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                channels);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                sampleRate);

            if (samples.Length % channels != 0)
            {
                throw new ArgumentException(
                    "The buffer must contain complete audio frames.",
                    nameof(samples));
            }

            if (_sampleRate != sampleRate ||
                _channels != channels)
            {
                Configure(channels, sampleRate);
            }

            for (int offset = 0;
                 offset < samples.Length;
                 offset += channels)
            {
                double framePeak = 0.0;

                // Find the peak across all channels.
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = samples[offset + channel];

                    if (!float.IsFinite(sample))
                    {
                        sample = 0.0f;
                        samples[offset + channel] = sample;
                    }

                    framePeak = Math.Max(
                        framePeak,
                        Math.Abs((double)sample));
                }

                // Track recent peaks with gradual decay.
                _peakEnvelope = Math.Max(
                    framePeak,
                    _peakEnvelope * _peakReleaseCoefficient);

                // Track the established peak level more slowly.
                double referenceCoefficient =
                    _peakEnvelope > _referenceEnvelope
                        ? _referenceAttackCoefficient
                        : _referenceReleaseCoefficient;

                _referenceEnvelope =
                    _peakEnvelope +
                    referenceCoefficient *
                    (_referenceEnvelope - _peakEnvelope);

                // Only the excess above the established level
                // is considered a transient.
                double transientExcess = Math.Max(
                    0.0,
                    _peakEnvelope - _referenceEnvelope);

                double normalizedTransient = Math.Clamp(
                    (transientExcess - TriggerExcessPeak) /
                    (FullReductionExcessPeak - TriggerExcessPeak),
                    0.0,
                    1.0);

                double targetGainDb =
                    -MaximumReductionDb * normalizedTransient;

                double targetGain = Math.Pow(
                    10.0,
                    targetGainDb / 20.0);

                // Reduce gain quickly and restore it smoothly.
                double coefficient = targetGain < _currentGain
                    ? _attackCoefficient
                    : _releaseCoefficient;

                _currentGain =
                    targetGain +
                    coefficient * (_currentGain - targetGain);

                int delayOffset = _writeFrame * channels;

                // Play delayed samples while storing incoming
                // frames for future output.
                for (int channel = 0; channel < channels; channel++)
                {
                    float delayedSample =
                        _delayBuffer[delayOffset + channel];

                    _delayBuffer[delayOffset + channel] =
                        samples[offset + channel];

                    samples[offset + channel] =
                        (float)(delayedSample * _currentGain);
                }

                _writeFrame++;

                if (_writeFrame == LatencyFrames)
                {
                    _writeFrame = 0;
                }
            }
        }

        /// <summary>
        /// Clears the delay line and envelope history.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_delayBuffer);

            _writeFrame = 0;
            _peakEnvelope = 0.0;
            _referenceEnvelope = 0.0;
            _currentGain = 1.0;
        }

        private void Configure(int channels, int sampleRate)
        {
            _channels = channels;
            _sampleRate = sampleRate;

            LatencyFrames = Math.Max(
                1,
                (int)Math.Ceiling(
                    sampleRate *
                    LookaheadMilliseconds / 1000.0));

            _delayBuffer = new float[
                checked(LatencyFrames * channels)];

            _attackCoefficient = CalculateCoefficient(
                AttackMilliseconds, sampleRate);

            _releaseCoefficient = CalculateCoefficient(
                ReleaseMilliseconds, sampleRate);

            _peakReleaseCoefficient = CalculateCoefficient(
                PeakReleaseMilliseconds, sampleRate);

            _referenceAttackCoefficient = CalculateCoefficient(
                ReferenceAttackMilliseconds, sampleRate);

            _referenceReleaseCoefficient = CalculateCoefficient(
                ReferenceReleaseMilliseconds, sampleRate);

            Reset();
        }

        private static double CalculateCoefficient(
            double milliseconds,
            int sampleRate)
        {
            return Math.Exp(
                -1000.0 / (milliseconds * sampleRate));
        }
    }
}

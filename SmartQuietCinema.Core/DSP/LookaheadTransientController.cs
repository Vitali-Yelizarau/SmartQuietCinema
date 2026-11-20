
using SmartQuietCinema.Core.Interfaces;

namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Uses 12 ms of lookahead and an adaptive reference envelope to attenuate sudden peaks from their first frames.
    /// Applies the same gain to all channels to preserve their balance.
    /// </summary>
    /// <remarks>
    /// A new transient starts or restarts the 150 ms hold at detection, before the delayed peak is output.
    /// In addition to fast onsets, envelope excess above 0.14 starts a hold once until the excess falls to 0.10 or below.
    /// Holds started only by the envelope trigger weight attenuation by the recent peak level, up to the same 12 dB limit.
    /// During the hold, a stronger peak can deepen attenuation without stacking reductions.
    /// After the hold, gain recovers toward the current adaptive target with a 300 ms time constant.
    /// With original frames supplied, the same delay also anticipates where supplemental quiet gain must reach unity.
    /// </remarks>
    public sealed class LookaheadTransientController : IAudioProcessor
    {
        public const int LookaheadMilliseconds = 12;

        // Transient detection uses the difference between
        // the current peak and the established peak level.
        private const double TriggerExcessPeak = 0.10;
        private const double EnvelopeHoldTriggerExcess = 0.14;
        private const double FullReductionExcessPeak = 0.35;
        private const double MaximumReductionDb = 12.0;

        private const double AttackMilliseconds = 2.0;
        private const double HoldMilliseconds = 150.0;
        private const double ReleaseMilliseconds = 300.0;
        private const double PeakReleaseMilliseconds = 100.0;

        // The reference envelope follows established peak levels
        // without reacting immediately to sudden transients.
        private const double ReferenceAttackMilliseconds = 80.0;
        private const double ReferenceReleaseMilliseconds = 300.0;

        private const double MillisecondsPerSecond = 1000.0;
        private const double DecibelBase = 10.0;
        private const double AmplitudeDecibelFactor = 20.0;

        private const double QuietLevelAttackMilliseconds = 3.0;
        private const double QuietLevelReleaseMilliseconds = 20.0;
        private const double MaximumQuietBoostDb = 3.0;
        private const double QuietPeakGuard = 0.063; // Allow some crest factor above the curve's -30 dBFS quiet boundary.
        private const double QuietNoiseFloorDb = -65.0;
        private const double QuietNoiseFadeEndDb = -55.0;
        private const double QuietPresenceReleaseMilliseconds = 3.0;
        private const double QuietGainRiseMilliseconds = 25.0;
        private const double PeakCeiling = 0.95;
        private static readonly double QuietPresenceFloor = Math.Pow(DecibelBase, QuietNoiseFloorDb / AmplitudeDecibelFactor);
        private static readonly double MaximumQuietBoost = Math.Pow(DecibelBase, MaximumQuietBoostDb / AmplitudeDecibelFactor) - 1.0;

        private float[] _delayBuffer = Array.Empty<float>();
        private readonly AudioLevelDetector _quietLevel = new(QuietLevelAttackMilliseconds, QuietLevelReleaseMilliseconds);
        private readonly AdaptiveGainCurve _quietGainCurve = new();
        private (long Frame, double Boost)[] _quietLimits = [];
        private int _quietLimitHead;
        private int _quietLimitCount;
        private long _quietFrame;
        private double _recentOriginalPeak;
        private double _quietPresenceRelease;
        private double _quietBoost;
        private double _quietRiseStep;
        private double _quietFallStep;

        private int _sampleRate;
        private int _channels;
        private int _writeFrame;
        private int _holdFrames;
        private int _holdFramesRemaining;
        private bool _envelopeHoldArmed = true;
        private bool _scaleEnvelopeHoldStrength;

        private double _attackCoefficient;
        private double _releaseCoefficient;
        private double _peakReleaseCoefficient;
        private double _referenceAttackCoefficient;
        private double _referenceReleaseCoefficient;

        private double _peakEnvelope;
        private double _referenceEnvelope;
        private double _heldGain = 1.0;
        private double _currentGain = 1.0;

        /// <summary>
        /// Gets the processing latency in audio frames.
        /// </summary>
        public int LatencyFrames { get; private set; }

        /// <summary>
        /// Gets the current transient attenuation in decibels.
        /// </summary>
        public double CurrentReductionDb => AmplitudeDecibelFactor * Math.Log10(_currentGain);

        /// <summary>
        /// Replaces interleaved input samples with delayed, transient-controlled output.
        /// Weights the reduction by recent peak level during a hold started by the envelope trigger alone.
        /// Preserves the delay, envelope, hold, trigger hysteresis, and gain state between buffers of the same format.
        /// </summary>
        /// <param name="samples">The in-place buffer containing complete interleaved audio frames.</param>
        /// <param name="channels">The number of channels in each audio frame.</param>
        /// <param name="sampleRate">The sample rate in Hertz, used to convert timing settings to frames and coefficients.</param>
        public void Process(Span<float> samples, int channels, int sampleRate)
            => ProcessCore(samples, default, channels, sampleRate);

        /// <summary>
        /// Applies standard transient protection and uses the aligned original frames to bound supplemental quiet gain.
        /// The processed input must be the output of the main dynamics stage, including its sample peak limiter.
        /// </summary>
        /// <param name="samples">Interleaved output from the main dynamics stage, replaced in place.</param>
        /// <param name="originalSamples">Separate, unchanged input samples covering the same frames.</param>
        /// <param name="channels">The number of channels in each audio frame.</param>
        /// <param name="sampleRate">The sample rate in Hertz.</param>
        public void Process(Span<float> samples, ReadOnlySpan<float> originalSamples, int channels, int sampleRate)
        {
            if (samples.Length != originalSamples.Length || ((ReadOnlySpan<float>)samples).Overlaps(originalSamples))
                throw new ArgumentException("The original samples must cover the same frames in a separate buffer.", nameof(originalSamples));

            ProcessCore(samples, originalSamples, channels, sampleRate);
        }

        /// <summary>
        /// Advances the existing transient state and delay, applying any permitted quiet gain only at output.
        /// </summary>
        /// <param name="samples">The complete interleaved frames to process in place.</param>
        /// <param name="originalSamples">Matching original frames, or an empty span for transient protection alone.</param>
        /// <param name="channels">The number of channels in each audio frame.</param>
        /// <param name="sampleRate">The sample rate in Hertz.</param>
        private void ProcessCore(Span<float> samples, ReadOnlySpan<float> originalSamples, int channels, int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (samples.Length % channels != 0) throw new ArgumentException("The buffer must contain complete audio frames.", nameof(samples));

            if (_sampleRate != sampleRate || _channels != channels) Configure(channels, sampleRate);

            bool enhanceQuiet = !originalSamples.IsEmpty;
            if (enhanceQuiet && _quietLimits.Length != LatencyFrames + 1) _quietLimits = new (long, double)[LatencyFrames + 1];
            if (!enhanceQuiet && !samples.IsEmpty && _quietFrame != 0) ResetQuietGain();

            for (int offset = 0; offset < samples.Length; offset += channels)
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

                    framePeak = Math.Max(framePeak, Math.Abs((double)sample));
                }

                // Track recent peaks with gradual decay.
                double decayedPeak = _peakEnvelope * _peakReleaseCoefficient;
                _peakEnvelope = Math.Max(framePeak, decayedPeak);

                // Track the established peak level more slowly.
                double referenceCoefficient = _peakEnvelope > _referenceEnvelope ? _referenceAttackCoefficient : _referenceReleaseCoefficient;

                _referenceEnvelope = _peakEnvelope + referenceCoefficient * (_referenceEnvelope - _peakEnvelope);

                // Only the excess above the established level
                // is considered a transient.
                double transientExcess = Math.Max(0.0, _peakEnvelope - _referenceEnvelope);

                double normalizedTransient = Math.Clamp((transientExcess - TriggerExcessPeak) / (FullReductionExcessPeak - TriggerExcessPeak), 0.0, 1.0);

                if (transientExcess <= TriggerExcessPeak) _envelopeHoldArmed = true;

                bool envelopeHoldTriggered = _envelopeHoldArmed && transientExcess > EnvelopeHoldTriggerExcess;
                if (envelopeHoldTriggered) _envelopeHoldArmed = false;

                bool fastHoldTriggered = framePeak - Math.Max(decayedPeak, _referenceEnvelope) > TriggerExcessPeak;
                if (fastHoldTriggered || envelopeHoldTriggered)
                {
                    _holdFramesRemaining = _holdFrames;
                    _scaleEnvelopeHoldStrength = !fastHoldTriggered;
                }

                if (_holdFramesRemaining > 0 && _scaleEnvelopeHoldStrength)
                {
                    // Weight the existing reduction so a low peak cannot add a fixed attenuation floor.
                    normalizedTransient = Math.Min(1.0, normalizedTransient * (1.0 + Math.Min(_peakEnvelope, 1.0)));
                }

                double targetGainDb = -MaximumReductionDb * normalizedTransient;
                double targetGain = Math.Pow(DecibelBase, targetGainDb / AmplitudeDecibelFactor);

                if (_holdFramesRemaining > 0)
                {
                    targetGain = Math.Min(targetGain, _heldGain);
                    _holdFramesRemaining--;
                    _heldGain = _holdFramesRemaining > 0 ? targetGain : 1.0;
                }

                // Reduce gain quickly and restore it smoothly.
                double coefficient = targetGain < _currentGain ? _attackCoefficient : _releaseCoefficient;

                _currentGain = targetGain + coefficient * (_currentGain - targetGain);

                double outputGain = _currentGain;
                if (enhanceQuiet)
                {
                    double limit = CalculateQuietLimit(originalSamples.Slice(offset, channels), sampleRate, framePeak, fastHoldTriggered || envelopeHoldTriggered);
                    double boost = AdvanceQuietGain(limit);
                    if (boost > 0.0) outputGain *= 1.0 + boost;
                }

                int delayOffset = _writeFrame * channels;

                // Play delayed samples while storing incoming
                // frames for future output.
                for (int channel = 0; channel < channels; channel++)
                {
                    float delayedSample = _delayBuffer[delayOffset + channel];

                    _delayBuffer[delayOffset + channel] = samples[offset + channel];

                    samples[offset + channel] = (float)(delayedSample * outputGain);
                }

                _writeFrame++;

                if (_writeFrame == LatencyFrames) _writeFrame = 0;
            }
        }

        /// <summary>
        /// Bounds additional gain for one original frame by its level, existing gain, and peak headroom.
        /// </summary>
        /// <param name="originalFrame">One original sample per channel before any dynamics processing.</param>
        /// <param name="sampleRate">The rate used by the short original-level envelope.</param>
        /// <param name="processedPeak">The linked peak after main dynamics and its limiter, before transient control.</param>
        /// <param name="transientTriggered">Whether the existing controller detected a new transient on this frame.</param>
        /// <returns>The allowed linear gain increment above unity, or zero for a protected frame.</returns>
        private double CalculateQuietLimit(ReadOnlySpan<float> originalFrame, int sampleRate, double processedPeak, bool transientTriggered)
        {
            double level = _quietLevel.AnalyzeFrame(originalFrame, sampleRate);
            double originalPeak = 0.0;
            foreach (float sample in originalFrame)
            {
                if (!float.IsFinite(sample)) return 0.0;
                originalPeak = Math.Max(originalPeak, Math.Abs((double)sample));
            }

            // Require recent quiet activity so a decaying loud envelope cannot boost near-silence on its own.
            _recentOriginalPeak = Math.Max(originalPeak, _recentOriginalPeak * _quietPresenceRelease);
            if (transientTriggered || originalPeak >= QuietPeakGuard || processedPeak >= PeakCeiling ||
                _recentOriginalPeak <= QuietPresenceFloor || level <= QuietPresenceFloor)
                return 0.0;

            double levelDb = AmplitudeDecibelFactor * Math.Log10(level);
            double quietGainDb = _quietGainCurve.CalculateTargetGainDb(levelDb);
            if (quietGainDb <= 0.0 || levelDb <= QuietNoiseFloorDb) return 0.0;

            double referenceGain = Math.Pow(DecibelBase, quietGainDb / AmplitudeDecibelFactor);
            double noiseWeight = Math.Min(1.0, (levelDb - QuietNoiseFloorDb) / (QuietNoiseFadeEndDb - QuietNoiseFloorDb));
            double allowedBoost = Math.Min(referenceGain - 1.0, MaximumQuietBoost) * noiseWeight;
            if (originalPeak == 0.0 || processedPeak == 0.0) return allowedBoost;

            // Account only for gain already applied upstream. Keep existing transient attenuation on the final signal.
            double deficit = Math.Max(0.0, referenceGain * originalPeak / processedPeak - 1.0);
            return Math.Min(Math.Min(allowedBoost, deficit), PeakCeiling / processedPeak - 1.0);
        }

        /// <summary>
        /// Limits the delayed quiet gain against future frames so it reaches every protective limit on time.
        /// </summary>
        /// <param name="limit">The gain increment permitted by the newly received frame.</param>
        /// <returns>The additional gain increment for the frame leaving the audio delay.</returns>
        private double AdvanceQuietGain(double limit)
        {
            long frame = _quietFrame++;
            long outputFrame = frame - LatencyFrames;
            if (_quietLimitCount > 0 && _quietLimits[_quietLimitHead].Frame < outputFrame)
            {
                _quietLimitHead = (_quietLimitHead + 1) % _quietLimits.Length;
                _quietLimitCount--;
            }

            // Keep the lowest future limits after adding the amount of gain we can shed before each one.
            // A new limit cannot require an immediate jump: the full boost can be removed in one delay.
            while (_quietLimitCount > 0)
            {
                var tail = _quietLimits[(_quietLimitHead + _quietLimitCount - 1) % _quietLimits.Length];
                if (tail.Boost - (frame - tail.Frame) * _quietFallStep < limit) break;
                _quietLimitCount--;
            }
            _quietLimits[(_quietLimitHead + _quietLimitCount) % _quietLimits.Length] = (frame, limit);
            _quietLimitCount++;

            if (outputFrame < 0) return 0.0;

            var nextLimit = _quietLimits[_quietLimitHead];
            double outputLimit = nextLimit.Boost + (nextLimit.Frame - outputFrame) * _quietFallStep;
            _quietBoost = Math.Min(Math.Min(_quietBoost + _quietRiseStep, outputLimit), MaximumQuietBoost);
            return _quietBoost;
        }

        /// <summary>
        /// Clears the delay, transient and quiet envelopes, and restores the armed trigger and unity gain for a new stream.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_delayBuffer);

            _writeFrame = 0;
            _holdFramesRemaining = 0;
            _envelopeHoldArmed = true;
            _scaleEnvelopeHoldStrength = false;
            _peakEnvelope = 0.0;
            _referenceEnvelope = 0.0;
            _heldGain = 1.0;
            _currentGain = 1.0;
            ResetQuietGain();
        }

        /// <summary>
        /// Clears only supplemental gain state while leaving the original transient controller state intact.
        /// </summary>
        private void ResetQuietGain()
        {
            _quietLevel.Reset();
            _quietFrame = 0;
            _recentOriginalPeak = 0.0;
            _quietLimitHead = 0;
            _quietLimitCount = 0;
            _quietBoost = 0.0;
        }

        /// <summary>
        /// Reconfigures the delay and time constants for the incoming audio format and resets previous stream state.
        /// </summary>
        /// <param name="channels">The number of channels per interleaved frame.</param>
        /// <param name="sampleRate">The number of frames per second.</param>
        private void Configure(int channels, int sampleRate)
        {
            _channels = channels;
            _sampleRate = sampleRate;

            const int MinimumLatencyFrames = 1;
            LatencyFrames = Math.Max(MinimumLatencyFrames, (int)Math.Ceiling(sampleRate * LookaheadMilliseconds / MillisecondsPerSecond));
            _holdFrames = (int)Math.Ceiling(sampleRate * HoldMilliseconds / MillisecondsPerSecond);
            _quietPresenceRelease = CalculateCoefficient(QuietPresenceReleaseMilliseconds, sampleRate);
            const double MinimumQuietRiseFrames = 1.0;
            _quietRiseStep = MaximumQuietBoost / Math.Max(MinimumQuietRiseFrames, Math.Ceiling(sampleRate * QuietGainRiseMilliseconds / MillisecondsPerSecond));
            _quietFallStep = MaximumQuietBoost / LatencyFrames;

            _delayBuffer = new float[checked(LatencyFrames * channels)];

            _attackCoefficient = CalculateCoefficient(AttackMilliseconds, sampleRate);

            _releaseCoefficient = CalculateCoefficient(ReleaseMilliseconds, sampleRate);

            _peakReleaseCoefficient = CalculateCoefficient(PeakReleaseMilliseconds, sampleRate);

            _referenceAttackCoefficient = CalculateCoefficient(ReferenceAttackMilliseconds, sampleRate);

            _referenceReleaseCoefficient = CalculateCoefficient(ReferenceReleaseMilliseconds, sampleRate);

            Reset();
        }

        private static double CalculateCoefficient(double milliseconds, int sampleRate)
        {
            return Math.Exp(-MillisecondsPerSecond / (milliseconds * sampleRate));
        }
    }
}

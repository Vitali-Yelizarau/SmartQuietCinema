using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    public sealed class LookaheadTransientControllerTests
    {
        private const float QuietLevel = 0.01f;
        private const int EnvelopeTestSampleRate = 48000;
        private const int FramesPerMillisecond = EnvelopeTestSampleRate / 1000;
        private const int EnvelopeStart = 100 * FramesPerMillisecond;

        /// <summary>
        /// Verifies that a stereo impulse after quiet audio is attenuated on its first delayed frame,
        /// preventing the leading peak from escaping lookahead or changing the channel ratio.
        /// </summary>
        [Fact]
        public void Process_SuddenPeak_AttenuatesFirstPeak()
        {
            var processor = new LookaheadTransientController();

            const int sampleRate = 48000;
            const int channels = 2;
            const int transientFrame = 4800;

            float[] samples = new float[sampleRate * channels];

            Array.Fill(samples, 0.01f);

            // A sudden stereo transient follows quiet audio.
            samples[transientFrame * channels] = 0.8f;
            samples[transientFrame * channels + 1] = 0.4f;

            processor.Process(samples, channels, sampleRate);

            int outputFrame = transientFrame + processor.LatencyFrames;

            float left = samples[outputFrame * channels];
            float right = samples[outputFrame * channels + 1];

            // Even the first transient frame must be attenuated.
            Assert.InRange(left, 0.19f, 0.3f);

            // Linked processing must preserve channel proportions.
            Assert.InRange(right, 0.0f, 0.15f);
            Assert.Equal(left * 0.5f, right, 5);
        }

        /// <summary>
        /// Checks that a sharp peak too small to pass the envelope threshold still starts the original fast hold.
        /// </summary>
        [Fact]
        public void Process_SharpPeakBelowEnvelopeThreshold_StillStartsFastHold()
        {
            float[] samples = CreateQuietSignal(EnvelopeTestSampleRate);
            samples[EnvelopeStart] = 0.13f;
            int lastHeldFrame = EnvelopeStart + 150 * FramesPerMillisecond - 1;

            new LookaheadTransientController().Process(samples, 1, EnvelopeTestSampleRate);

            Assert.InRange(samples[lastHeldFrame] / QuietLevel, 0.87f, 0.90f);
            Assert.Equal(samples[lastHeldFrame - 1], samples[lastHeldFrame]);
            Assert.True(samples[lastHeldFrame + 1] > samples[lastHeldFrame]);
        }

        /// <summary>
        /// Checks that an 80 ms ramp gains stronger envelope attenuation without a fast onset or a gain discontinuity.
        /// </summary>
        /// <param name="sampleRate">The rate in Hertz used to verify the same envelope timing and strength.</param>
        [Theory]
        [InlineData(44100)]
        [InlineData(48000)]
        [InlineData(96000)]
        public void Process_GradualRise_HoldsAttenuationAndRecoversSmoothly(int sampleRate)
        {
            float[] input = CreateQuietSignal(sampleRate);
            int rampStart = sampleRate / 10;
            int rampLength = sampleRate * 80 / 1000;
            WriteRamp(input.AsSpan(rampStart, rampLength), QuietLevel, 0.28f);
            input.AsSpan(rampStart + rampLength).Fill(0.28f);
            float[] output = (float[])input.Clone();

            var processor = new LookaheadTransientController();
            processor.Process(output, 1, sampleRate);

            float held = output[rampStart + sampleRate * 200 / 1000];
            Assert.InRange(held / 0.28f, 0.59f, 0.62f);
            Assert.Equal(held, output[rampStart + sampleRate * 180 / 1000]);
            Assert.True(output[rampStart + sampleRate * 240 / 1000] > held + 0.008f);

            double previousGain = 1.0;
            for (int frame = processor.LatencyFrames; frame < output.Length; frame++)
            {
                double gain = output[frame] / (double)input[frame - processor.LatencyFrames];
                Assert.InRange(gain - previousGain, -0.005, 0.0001);
                previousGain = gain;
            }
        }

        /// <summary>
        /// Compares equal envelope rises from different settled levels to verify that a higher peak receives more attenuation.
        /// </summary>
        [Fact]
        public void Process_EqualGradualRises_AttenuatesTheHigherAbsolutePeakMore()
        {
            float[] levels = [0.01f, 0.21f];
            var gains = new float[levels.Length];
            for (int index = 0; index < levels.Length; index++)
            {
                int rampStart = EnvelopeTestSampleRate;
                int rampLength = 80 * FramesPerMillisecond;
                float peak = levels[index] + 0.27f;
                var samples = new float[2 * EnvelopeTestSampleRate];
                samples.AsSpan(0, rampStart).Fill(levels[index]);
                WriteRamp(samples.AsSpan(rampStart, rampLength), levels[index], peak);
                samples.AsSpan(rampStart + rampLength).Fill(peak);

                new LookaheadTransientController().Process(samples, 1, EnvelopeTestSampleRate);

                gains[index] = samples[rampStart + 180 * FramesPerMillisecond] / peak;
            }

            Assert.InRange(gains[0], 0.59f, 0.62f);
            Assert.InRange(gains[1], 0.55f, 0.57f);
            Assert.True(gains[1] < gains[0] - 0.03f);
        }

        /// <summary>
        /// Verifies that a quiet impulse stays unchanged and a low peak that barely qualifies does not get a fixed gain cut.
        /// </summary>
        /// <param name="peak">The maximum input amplitude.</param>
        /// <param name="rampMilliseconds">The duration of the rising edge, or zero for an impulse.</param>
        /// <param name="minimumGain">The lowest acceptable gain for this event.</param>
        /// <param name="maximumGain">The highest acceptable gain for this event.</param>
        [Theory]
        [InlineData(0.075f, 0, 1.0f, 1.0f)]
        [InlineData(0.153f, 8, 0.75f, 0.76f)]
        public void Process_LowPeakEvents_DoNotAcquireAnAttenuationFloor(float peak, int rampMilliseconds, float minimumGain, float maximumGain)
        {
            const float background = 0.002f;
            var samples = new float[EnvelopeTestSampleRate];
            Array.Fill(samples, background);
            int rampLength = rampMilliseconds * FramesPerMillisecond;
            if (rampLength == 0)
            {
                samples[EnvelopeStart] = peak;
            }
            else
            {
                WriteRamp(samples.AsSpan(EnvelopeStart, rampLength), background, peak);
                samples.AsSpan(EnvelopeStart + rampLength).Fill(peak);
            }

            var processor = new LookaheadTransientController();
            processor.Process(samples, 1, EnvelopeTestSampleRate);

            int probe = rampLength == 0 ? EnvelopeStart + processor.LatencyFrames : EnvelopeStart + 100 * FramesPerMillisecond;
            Assert.InRange(samples[probe] / peak, minimumGain, maximumGain);
        }

        /// <summary>
        /// Checks that a loud rise held above the threshold recovers without repeating the hold or sustaining the extra attenuation.
        /// </summary>
        [Fact]
        public void Process_SustainedHighExcess_RecoversWithoutRepeatedEnvelopeHold()
        {
            float[] samples = CreateQuietSignal(EnvelopeTestSampleRate);
            int rampLength = 240 * FramesPerMillisecond;
            WriteRamp(samples.AsSpan(EnvelopeStart, rampLength), QuietLevel, 0.65f);
            samples.AsSpan(EnvelopeStart + rampLength).Fill(0.65f);

            new LookaheadTransientController().Process(samples, 1, EnvelopeTestSampleRate);

            float earlyGain = samples[EnvelopeStart + 255 * FramesPerMillisecond] / 0.65f;
            float laterGain = samples[EnvelopeStart + 315 * FramesPerMillisecond] / 0.65f;
            Assert.InRange(earlyGain, 0.40f, 0.43f);
            Assert.True(laterGain > earlyGain + 0.045f);
        }

        /// <summary>
        /// Checks that renewed excess can deepen attenuation but extends the hold only if the preceding dip rearmed the trigger.
        /// </summary>
        /// <param name="plateauMilliseconds">Time between the ramps, controlling how far the envelope excess can fall.</param>
        /// <param name="expectRearm">Whether the first plateau allows the excess to reach the lower threshold.</param>
        [Theory]
        [InlineData(30, false)]
        [InlineData(60, true)]
        public void Process_SecondGradualRise_ExtendsHoldOnlyAfterRearming(int plateauMilliseconds, bool expectRearm)
        {
            float[] samples = CreateTwoGradualRises(plateauMilliseconds);

            new LookaheadTransientController().Process(samples, 1, EnvelopeTestSampleRate);

            float firstHold = samples[EnvelopeStart + 200 * FramesPerMillisecond] / 0.48f;
            float laterGain = samples[EnvelopeStart + 260 * FramesPerMillisecond] / 0.48f;
            float recovered = samples[EnvelopeStart + 340 * FramesPerMillisecond] / 0.48f;
            Assert.InRange(firstHold, 0.34f, 0.43f);
            if (expectRearm)
                Assert.InRange(Math.Abs(firstHold - laterGain), 0.0f, 0.00001f);
            else
                Assert.True(laterGain > firstHold + 0.04f);
            Assert.True(recovered > laterGain + 0.035f);
        }

        /// <summary>
        /// Checks speech-like syllables in isolation and after an impact to catch new attenuation of moderate or quiet speech.
        /// </summary>
        /// <param name="hasImpact">Whether a sharp impact precedes the quieter speech-like signal.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Process_SpeechLikeModulation_DoesNotStartNewAttenuation(bool hasImpact)
        {
            const int speechStart = EnvelopeTestSampleRate / 2;
            float[] input = CreateQuietSignal(EnvelopeTestSampleRate * 3 / 2);
            if (hasImpact)
                input[EnvelopeStart] = 0.8f;
            for (int frame = speechStart; frame < input.Length; frame++)
            {
                double seconds = (double)(frame - speechStart) / EnvelopeTestSampleRate;
                double syllable = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 4.0 * seconds);
                double carrier = 0.65 * Math.Sin(2.0 * Math.PI * 170.0 * seconds) + 0.35 * Math.Sin(2.0 * Math.PI * 510.0 * seconds + 0.5);
                input[frame] = (float)((hasImpact ? 0.035 : 0.165) * (0.2 + 0.8 * syllable * syllable) * carrier);
            }

            float[] output = (float[])input.Clone();
            var processor = new LookaheadTransientController();
            processor.Process(output, 1, EnvelopeTestSampleRate);

            double previousGain = 0.0;
            for (int frame = speechStart + processor.LatencyFrames; frame < output.Length; frame++)
            {
                float original = input[frame - processor.LatencyFrames];
                if (!hasImpact)
                {
                    Assert.Equal(original, output[frame]);
                }
                else if (Math.Abs(original) > 0.00001f)
                {
                    double gain = output[frame] / (double)original;
                    Assert.InRange(gain, previousGain - 0.000001, 1.0);
                    previousGain = gain;
                }
            }

            if (hasImpact)
                Assert.InRange(previousGain, 0.97, 1.0);
        }

        /// <summary>
        /// Checks that a rising chord with repeating accents recovers without further gain dips once the music-like pattern settles.
        /// </summary>
        [Fact]
        public void Process_RepeatingChord_RecoversWithoutPumpingAfterFadeIn()
        {
            var input = new float[3 * EnvelopeTestSampleRate];
            for (int frame = 0; frame < input.Length; frame++)
            {
                double seconds = (double)frame / EnvelopeTestSampleRate;
                double phase = 2.0 * Math.PI * seconds;
                double chord = 0.5 * Math.Cos(110.0 * phase) + 0.3 * Math.Cos(165.0 * phase) + 0.2 * Math.Cos(220.0 * phase);
                double accent = 0.85 + 0.15 * Math.Cos(2.0 * phase);
                input[frame] = (float)(0.55 * Math.Min(1.0, 2.0 * seconds) * accent * chord);
            }

            float[] output = (float[])input.Clone();
            var processor = new LookaheadTransientController();
            processor.Process(output, 1, EnvelopeTestSampleRate);

            double previousGain = 0.0;
            for (int frame = EnvelopeTestSampleRate + processor.LatencyFrames; frame < output.Length; frame++)
            {
                float original = input[frame - processor.LatencyFrames];
                if (Math.Abs(original) <= 0.00001f)
                    continue;

                double gain = output[frame] / (double)original;
                Assert.InRange(gain, previousGain - 0.000001, 1.0);
                previousGain = gain;
            }

            Assert.InRange(previousGain, 0.99, 1.0);
        }

        /// <summary>
        /// Verifies that impacts during hold and recovery all receive bounded first-frame protection, regardless of event order.
        /// </summary>
        /// <param name="sampleRate">The sample rate in Hertz at which to verify linked timing and peak protection.</param>
        [Theory]
        [InlineData(44100)]
        [InlineData(48000)]
        [InlineData(96000)]
        public void Process_ImpactsAtUnevenIntervals_RetainFastOnsetProtection(int sampleRate)
        {
            int[] times = [100, 135, 207, 500];
            float[] samples = CreateQuietSignal(sampleRate);
            foreach (int time in times)
                samples[sampleRate * time / 1000] = 0.8f;

            var processor = new LookaheadTransientController();
            processor.Process(samples, 1, sampleRate);

            foreach (int time in times)
            {
                float outputPeak = samples[sampleRate * time / 1000 + processor.LatencyFrames];
                Assert.InRange(outputPeak / 0.8f, 0.25f, 0.26f);
            }
        }

        /// <summary>
        /// Verifies that splitting gradual events across buffers preserves trigger state and linked gain on every channel.
        /// </summary>
        /// <param name="channels">The interleaved channel count to test.</param>
        [Theory]
        [InlineData(2)]
        [InlineData(6)]
        public void Process_GradualEvents_PreserveBufferContinuityAndChannelBalance(int channels)
        {
            float[] mono = CreateTwoGradualRises(60);
            float[] continuous = new float[mono.Length * channels];
            for (int frame = 0; frame < mono.Length; frame++)
            {
                int offset = frame * channels;
                continuous[offset] = mono[frame];
                for (int channel = 1; channel < channels; channel++)
                    continuous[offset + channel] = mono[frame] * (channel % 2 == 0 ? 0.25f : -0.5f);
            }

            float[] split = (float[])continuous.Clone();
            var continuousProcessor = new LookaheadTransientController();
            var splitProcessor = new LookaheadTransientController();
            continuousProcessor.Process(continuous, channels, EnvelopeTestSampleRate);

            int[] boundaries = [57, 65, 123, 155, 157, 208, 306, 308, 900];
            int start = 0;
            foreach (int boundary in boundaries)
            {
                int end = (EnvelopeStart + boundary * FramesPerMillisecond) * channels;
                splitProcessor.Process(split.AsSpan(start, end - start), channels, EnvelopeTestSampleRate);
                start = end;
            }

            Assert.Equal(continuous, split);
            Assert.Equal(continuousProcessor.CurrentReductionDb, splitProcessor.CurrentReductionDb);
            for (int offset = 0; offset < split.Length; offset += channels)
            {
                for (int channel = 1; channel < channels; channel++)
                    Assert.Equal(split[offset] * (channel % 2 == 0 ? 0.25f : -0.5f), split[offset + channel]);
            }
        }

        /// <summary>
        /// Verifies that an isolated impulse holds attenuation for 150 ms from detection,
        /// preventing recovery from being scheduled relative to the later, delayed output peak.
        /// </summary>
        /// <param name="sampleRate">The sample rate in Hertz at which to verify frame timing.</param>
        [Theory]
        [InlineData(44100)]
        [InlineData(48000)]
        public void Process_IsolatedTransient_HoldsFor150Milliseconds(int sampleRate)
        {
            int transientFrame = sampleRate / 10;
            int holdFrames = sampleRate * 150 / 1000;
            int lookaheadFrames = (int)Math.Ceiling(sampleRate * 12 / 1000.0);
            float[] samples = CreateQuietSignal(sampleRate);
            samples[transientFrame] = 0.8f;

            var processor = new LookaheadTransientController();
            processor.Process(samples, 1, sampleRate);
            Assert.Equal(lookaheadFrames, processor.LatencyFrames);

            int transientOutputFrame = transientFrame + processor.LatencyFrames;
            int recoveryOutputFrame = transientOutputFrame + (holdFrames - lookaheadFrames);

            Assert.InRange(samples[transientOutputFrame] / 0.8f, 0.25f, 0.27f);
            Assert.InRange(samples[recoveryOutputFrame - 1] / QuietLevel, 0.25f, 0.253f);
            Assert.Equal(samples[recoveryOutputFrame - 2], samples[recoveryOutputFrame - 1]);
            Assert.True(samples[recoveryOutputFrame] > samples[recoveryOutputFrame - 1]);
        }

        /// <summary>
        /// Verifies that gain rises smoothly on a quiet background after an isolated impulse,
        /// preventing abrupt recovery steps, overshoot, or a change to the 300 ms time constant.
        /// </summary>
        [Fact]
        public void Process_AfterHold_RecoversSmoothlyWith300MillisecondTimeConstant()
        {
            const int sampleRate = 48000;
            const int transientFrame = 4800;
            const int lastHoldFrame = transientFrame + 7200 - 1;
            const int recoveryFrames = 14400;
            float[] samples = CreateQuietSignal(sampleRate);
            samples[transientFrame] = 0.8f;

            new LookaheadTransientController().Process(samples, 1, sampleRate);

            for (int frame = lastHoldFrame + 1; frame <= lastHoldFrame + recoveryFrames; frame++)
            {
                Assert.InRange((samples[frame] - samples[frame - 1]) / QuietLevel, 0.0f, 0.00006f);
                Assert.True(samples[frame] <= QuietLevel);
            }

            float initialDifference = QuietLevel - samples[lastHoldFrame];
            float remainingDifference = QuietLevel - samples[lastHoldFrame + recoveryFrames];
            Assert.InRange(remainingDifference / initialDifference, 0.367f, 0.369f);
        }

        /// <summary>
        /// Verifies that a peak followed by sustained loud audio recovers after the hold more slowly than
        /// it would toward unity, preventing recovery from ignoring the still-attenuating adaptive target.
        /// </summary>
        [Fact]
        public void Process_AfterHold_RecoversTowardAdaptiveTargetWhileSoundRemainsLoud()
        {
            const int sampleRate = 48000;
            const int transientFrame = sampleRate / 10;
            const int holdFrames = sampleRate * 150 / 1000;
            const int probeFrames = sampleRate * 10 / 1000;
            const double recoveryMilliseconds = 300.0;
            const float sustainedLevel = 0.9f;
            float[] samples = CreateQuietSignal(sampleRate);
            samples[transientFrame] = 1.0f;
            samples.AsSpan(transientFrame + 1).Fill(sustainedLevel);

            var processor = new LookaheadTransientController();
            processor.Process(samples, 1, sampleRate);

            int transientOutputFrame = transientFrame + processor.LatencyFrames;
            int lastHoldOutputFrame = transientOutputFrame + (holdFrames - processor.LatencyFrames) - 1;
            double heldGain = samples[lastHoldOutputFrame] / sustainedLevel;
            double recoveringGain = samples[lastHoldOutputFrame + probeFrames] / sustainedLevel;
            double unityRecoveryGain = 1.0 - (1.0 - heldGain) * Math.Exp(-1000.0 * probeFrames / (sampleRate * recoveryMilliseconds));

            Assert.InRange(samples[transientOutputFrame], 0.25f, 0.27f);
            Assert.InRange(heldGain, 0.25, 0.253);
            Assert.InRange(recoveringGain, heldGain + 0.01, unityRecoveryGain - 0.002);
        }

        /// <summary>
        /// Verifies that steady quiet audio emerges unchanged after lookahead, preventing spurious
        /// attenuation of audio that never triggers the hold.
        /// </summary>
        [Fact]
        public void Process_QuietAudio_RemainsUnchangedAfterLatency()
        {
            var processor = new LookaheadTransientController();

            const int sampleRate = 48000;

            float[] samples = new float[sampleRate];

            Array.Fill(samples, 0.01f);

            processor.Process(samples, 1, sampleRate);

            // The delay line must initially output silence.
            Assert.Equal(0.0f, samples[0]);

            // Quiet audio must retain its level after the delay.
            int firstOutputFrame = processor.LatencyFrames;

            Assert.InRange(
                samples[firstOutputFrame],
                0.009999f,
                0.010001f);

            Assert.InRange(
                samples[^1],
                0.009999f,
                0.010001f);
            Assert.Equal(0.0, processor.CurrentReductionDb);
        }


        /// <summary>
        /// Verifies that a loud tone with a sudden onset is initially attenuated but then recovers,
        /// preventing subsequent cycles of the steady tone from repeatedly triggering the hold.
        /// </summary>
        [Fact]
        public void Process_SustainedTone_DoesNotRemainTransientAttenuated()
        {
            var processor = new LookaheadTransientController();

            const int sampleRate = 48000;
            const int frequency = 1000;
            const float amplitude = 0.8f;

            float[] samples = new float[sampleRate * 2];

            // Generate two seconds of a steady sine wave.
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = amplitude * (float)Math.Cos(2.0 * Math.PI * frequency * i / sampleRate);
            }

            processor.Process(samples, 1, sampleRate);

            Assert.InRange(samples[processor.LatencyFrames] / amplitude, 0.25f, 0.27f);
            Assert.InRange(samples[processor.LatencyFrames + sampleRate * 3 / 10] / amplitude, 0.5f, 0.65f);

            // Measure the final second after the initial onset.
            double sumSquares = 0.0;

            for (int i = sampleRate; i < samples.Length; i++)
            {
                sumSquares += (double)samples[i] * samples[i];
            }

            double outputRms = Math.Sqrt(sumSquares / sampleRate);

            double expectedRms = amplitude / Math.Sqrt(2.0);

            // A steady signal should not remain strongly attenuated
            // by the transient controller.
            double ratio = outputRms / expectedRms;

            Assert.InRange(ratio, 0.95, 1.05);
        }

        /// <summary>
        /// Verifies that a stronger impact during an active hold deepens and extends attenuation,
        /// preventing either a missed retrigger or compounding of the two gain reductions.
        /// </summary>
        [Fact]
        public void Process_StrongerTransientDuringHold_DeepensAndExtendsHoldWithoutStacking()
        {
            const int sampleRate = 48000;
            const int firstTransient = 4800;
            const int secondTransient = firstTransient + 3360;
            const int holdFrames = 7200;
            float[] samples = CreateQuietSignal(sampleRate);
            samples[firstTransient] = 0.24f;
            samples[secondTransient] = 0.8f;

            var processor = new LookaheadTransientController();
            processor.Process(samples, 1, sampleRate);

            Assert.InRange(samples[firstTransient + processor.LatencyFrames] / 0.24f, 0.47f, 0.50f);
            Assert.InRange(samples[secondTransient + processor.LatencyFrames] / 0.8f, 0.25f, 0.26f);
            Assert.InRange(samples[firstTransient + holdFrames + 600] / QuietLevel, 0.25f, 0.253f);
            Assert.True(samples[secondTransient + holdFrames] > samples[secondTransient + holdFrames - 1]);
        }

        /// <summary>
        /// Verifies that an impact during gain recovery is attenuated from its first delayed frame
        /// and starts a full new hold, preventing recovery from masking a retrigger.
        /// </summary>
        [Fact]
        public void Process_SecondTransientDuringRecovery_AttenuatesFirstFrameAndStartsNewHold()
        {
            const int sampleRate = 48000;
            const int firstTransient = 4800;
            const int secondTransient = firstTransient + 14400;
            const int holdFrames = 7200;
            float[] samples = CreateQuietSignal(sampleRate);
            samples[firstTransient] = 0.8f;
            samples[secondTransient] = 0.8f;

            var processor = new LookaheadTransientController();
            processor.Process(samples, 1, sampleRate);

            Assert.InRange(samples[secondTransient - 1] / QuietLevel, 0.54f, 0.55f);
            Assert.InRange(samples[secondTransient + processor.LatencyFrames] / 0.8f, 0.25f, 0.26f);
            Assert.InRange(samples[secondTransient + holdFrames - 1] / QuietLevel, 0.25f, 0.253f);
            Assert.True(samples[secondTransient + holdFrames] > samples[secondTransient + holdFrames - 1]);
        }

        /// <summary>
        /// Verifies that a transient sequence split across onset, hold, and recovery boundaries matches
        /// continuous input exactly, preventing state resets or discontinuities between buffers.
        /// </summary>
        [Fact]
        public void Process_SplitBuffers_MatchContinuousOutput()
        {
            const int sampleRate = 48000;
            float[] continuous = CreateQuietSignal(sampleRate);
            continuous[4800] = 0.5f;
            continuous[8100] = 0.8f;
            continuous[20000] = -0.7f;
            float[] split = (float[])continuous.Clone();
            var continuousProcessor = new LookaheadTransientController();
            var splitProcessor = new LookaheadTransientController();

            continuousProcessor.Process(continuous, 1, sampleRate);
            int start = 0;
            int[] boundaries = [4800, 4801, 8100, 8677, 15299, 15301, 20001, 27201, sampleRate];
            foreach (int end in boundaries)
            {
                splitProcessor.Process(split.AsSpan(start, end - start), 1, sampleRate);
                start = end;
            }

            Assert.Equal(continuous, split);
            Assert.Equal(continuousProcessor.CurrentReductionDb, splitProcessor.CurrentReductionDb);
        }

        /// <summary>
        /// Verifies that a stereo transient and its background retain their signed channel ratio through
        /// hold and recovery, preventing independent gain changes from skewing the stereo balance.
        /// </summary>
        [Fact]
        public void Process_Stereo_PreservesChannelBalanceThroughHoldAndRecovery()
        {
            const int sampleRate = 48000;
            const int channels = 2;
            const int transientFrame = 4800;
            float[] samples = new float[sampleRate * channels];
            for (int frame = 0; frame < sampleRate; frame++)
            {
                float left = frame == transientFrame ? 0.8f : QuietLevel;
                samples[frame * channels] = left;
                samples[frame * channels + 1] = -0.5f * left;
            }

            var processor = new LookaheadTransientController();
            processor.Process(samples, channels, sampleRate);

            for (int frame = processor.LatencyFrames; frame < sampleRate; frame++)
            {
                Assert.Equal(-0.5f * samples[frame * channels], samples[frame * channels + 1]);
            }
        }

        /// <summary>
        /// Builds two slow rises with a variable pause to test upper-threshold crossings with and without rearming.
        /// </summary>
        /// <param name="plateauMilliseconds">The time at constant amplitude between the two ramps.</param>
        /// <returns>A mono signal with a quiet lead-in and no sample step large enough to trigger fast hold.</returns>
        private static float[] CreateTwoGradualRises(int plateauMilliseconds)
        {
            float[] samples = CreateQuietSignal(EnvelopeTestSampleRate);
            int firstEnd = EnvelopeStart + 80 * FramesPerMillisecond;
            int secondStart = firstEnd + plateauMilliseconds * FramesPerMillisecond;
            int secondLength = 40 * FramesPerMillisecond;
            WriteRamp(samples.AsSpan(EnvelopeStart, firstEnd - EnvelopeStart), QuietLevel, 0.28f);
            samples.AsSpan(firstEnd, secondStart - firstEnd).Fill(0.28f);
            WriteRamp(samples.AsSpan(secondStart, secondLength), 0.28f, 0.48f);
            samples.AsSpan(secondStart + secondLength).Fill(0.48f);
            return samples;
        }

        /// <summary>
        /// Writes a linear amplitude transition so a test fixture can accumulate envelope excess without a sample jump.
        /// </summary>
        /// <param name="samples">The ramp frames to populate.</param>
        /// <param name="start">The amplitude just before the first ramp frame.</param>
        /// <param name="end">The amplitude reached by the last ramp frame.</param>
        private static void WriteRamp(Span<float> samples, float start, float end)
        {
            for (int frame = 0; frame < samples.Length; frame++)
                samples[frame] = start + (end - start) * (frame + 1) / samples.Length;
        }

        /// <summary>
        /// Creates a nonzero quiet background so tests can measure applied gain after an impulse.
        /// </summary>
        /// <param name="frameCount">The number of mono frames to generate.</param>
        /// <returns>A mono buffer filled with the quiet reference amplitude.</returns>
        private static float[] CreateQuietSignal(int frameCount)
        {
            float[] samples = new float[frameCount];
            Array.Fill(samples, QuietLevel);
            return samples;
        }
    }
}

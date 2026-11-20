using System.Runtime.InteropServices;
using SmartQuietCinema.Core.DSP;
using Xunit.Abstractions;

namespace SmartQuietCinema.Tests
{
    /// <summary>
    /// Compares the quiet enhancement against the unchanged dynamics and transient path on identical input frames.
    /// </summary>
    public sealed class LookaheadQuietEnhancementTests
    {
        private const int DefaultRate = 48000;
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Captures timing-window measurements in the test log for independent DSP review.
        /// </summary>
        /// <param name="output">The xUnit test log for the current scenario.</param>
        public LookaheadQuietEnhancementTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Replays the repeated-impact sequence at different quiet-gap lengths. The protected peak and neighboring
        /// envelope must preserve the baseline and catch the previous 2.27 dB peak regression.
        /// </summary>
        /// <param name="quietMilliseconds">Time between the compressed tone and the isolated impact.</param>
        [Theory]
        [InlineData(50)]
        [InlineData(100)]
        [InlineData(200)]
        [InlineData(300)]
        public void Process_ImpactAfterQuiet_LeavesProtectedPeakBitIdentical(int quietMilliseconds)
        {
            float[] input = Transition(DefaultRate, 0.32f, 0.010f);
            int impact = DefaultRate + Frames(quietMilliseconds, DefaultRate);
            input.AsSpan(impact).Clear();
            input[impact] = 0.8f;
            float[] baseline = ProcessAligned(input, 1, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, 1, DefaultRate);

            Assert.Equal(BitConverter.SingleToInt32Bits(baseline[impact]), BitConverter.SingleToInt32Bits(candidate[impact]));
            if (quietMilliseconds == 200)
            {
                Assert.InRange(baseline[impact], 0.152956f, 0.152958f);
                const double rejectedPeak = 0.198747;
                Assert.InRange(20 * Math.Log10(rejectedPeak / baseline[impact]), 2.27, 2.28);
                Assert.True(candidate[impact] < rejectedPeak);
            }

            int neighborFrames = Frames(24, DefaultRate);
            Assert.Equal(Peak(baseline.AsSpan(impact - neighborFrames, 2 * neighborFrames)), Peak(candidate.AsSpan(impact - neighborFrames, 2 * neighborFrames)));
            AssertSmoothCorrection(baseline, candidate, impact - neighborFrames, impact, Frames(12, DefaultRate));
            _output.WriteLine($"Impact after {quietMilliseconds} ms: baseline {baseline[impact]:F6}, candidate {candidate[impact]:F6}");
        }

        /// <summary>
        /// Starts an impact in a different channel while quiet enhancement is active. The additional gain must ramp
        /// down within the real lookahead and reach exactly unity at the protected cross-buffer frame.
        /// </summary>
        [Fact]
        public void Process_UpcomingImpactInAnotherChannel_FadesBeforeDelayedOutput()
        {
            const int channels = 2;
            int impact = DefaultRate + Frames(82, DefaultRate);
            float[] mono = Transition(DefaultRate, 0.0f, 0.010f);
            var input = new float[mono.Length * channels];
            for (int frame = 0; frame < mono.Length; frame++)
                input[frame * channels] = 0.010f * Math.Sign(mono[frame]);
            input[impact * channels + 1] = -0.85f;

            float[] baseline = ProcessAligned(input, channels, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, channels, DefaultRate, blockFrames: impact + 1);
            int latency = Frames(12, DefaultRate);
            int start = impact - latency;
            Assert.True(candidate[start * channels] / baseline[start * channels] > 1.06);
            Assert.True(candidate[(impact - 1) * channels] / baseline[(impact - 1) * channels] < 1.002);
            Assert.True(BitwiseEqual(baseline.AsSpan(impact * channels, channels), candidate.AsSpan(impact * channels, channels)));
            for (int frame = start + 1; frame <= impact; frame++)
            {
                double previous = candidate[(frame - 1) * channels] / baseline[(frame - 1) * channels];
                double gain = candidate[frame * channels] / baseline[frame * channels];
                Assert.InRange(gain - previous, -0.00073, 0.00073);
            }
        }

        /// <summary>
        /// Measures quiet beginnings after different histories. The enhancer must improve an early window, respect the
        /// supplemental gain limit, and avoid applying a second full boost to the settled signal.
        /// </summary>
        /// <param name="history">The signal history before the quiet onset.</param>
        /// <param name="noisePeak">The peak scale of optional seeded background noise.</param>
        [Theory]
        [InlineData("silence", 0.0f)]
        [InlineData("sustained loud", 0.0f)]
        [InlineData("sharp impact", 0.0f)]
        [InlineData("sustained loud", 0.0002f)]
        public void Process_QuietOnset_ImprovesEarlyWindowsWithoutDoublingGain(string history, float noisePeak)
        {
            float[] input = Transition(DefaultRate, history == "sustained loud" ? 0.32f : 0.0f, 0.010f, noisePeak);
            if (history == "sharp impact")
                input[DefaultRate - 1] = 0.8f;

            float[] baseline = ProcessAligned(input, 1, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, 1, DefaultRate);
            double[] changes = new double[4];
            (int Start, int End)[] windows = [(1000, 1050), (1000, 1100), (1000, 1200), (1500, 1850)];
            for (int i = 0; i < windows.Length; i++)
            {
                var (start, end) = windows[i];
                double oldDb = RmsChange(input, baseline, start, end);
                double newDb = RmsChange(input, candidate, start, end);
                changes[i] = newDb - oldDb;
                _output.WriteLine($"{history}, noise peak {noisePeak:F5}, {start - 1000}-{end - 1000} ms: {oldDb:+0.00;-0.00} to {newDb:+0.00;-0.00} dB; delta {changes[i]:+0.00;-0.00} dB");
                Assert.InRange(changes[i], 0.0, 3.001);
            }
            Assert.True(changes[2] > 1.0);
            Assert.InRange(changes[3], 0.0, 1.25);
            Assert.InRange(RmsChange(input, candidate, 1500, 1850), 0.0, 12.001);
            if (history == "sharp impact")
                Assert.Equal(baseline[DefaultRate - 1], candidate[DefaultRate - 1]);
        }

        /// <summary>
        /// Holds a constant quiet level until both envelopes reach the same curve target. The supplemental path must
        /// stop boosting once the main stage alone supplies the requested gain.
        /// </summary>
        /// <param name="amplitude">A level inside the positive part of the gain curve.</param>
        [Theory]
        [InlineData(0.003f)]
        [InlineData(0.010f)]
        [InlineData(0.020f)]
        public void Process_SettledQuietAudio_DoesNotApplyGainTwice(float amplitude)
        {
            var input = new float[3 * DefaultRate];
            Array.Fill(input, amplitude);
            float[] baseline = ProcessAligned(input, 1, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, 1, DefaultRate);
            Assert.InRange(RmsChange(baseline, candidate, 0, 100), 0.5, 3.001);
            Assert.True(BitwiseEqual(baseline.AsSpan(Frames(2500, DefaultRate)), candidate.AsSpan(Frames(2500, DefaultRate))));
        }

        /// <summary>
        /// Measures background-only input around the eligibility floor. Very low noise must receive no extra gain,
        /// and noise within the same level range as quiet content must remain subject to the supplemental gain bound.
        /// </summary>
        /// <param name="noisePeak">The peak scale of seeded white noise.</param>
        /// <param name="eligible">Whether this noise falls inside the quiet enhancement range.</param>
        /// <param name="afterLoud">Whether noise follows established loud compression.</param>
        [Theory]
        [InlineData(0.00024f, false, false)]
        [InlineData(0.00024f, false, true)]
        [InlineData(0.008f, true, false)]
        public void Process_BackgroundAlone_ReportsWhenItSharesQuietSignalGain(float noisePeak, bool eligible, bool afterLoud)
        {
            float[] input = Transition(DefaultRate, afterLoud ? 0.32f : 0.0f, 0.0f, noisePeak);
            float[] baseline = ProcessAligned(input, 1, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, 1, DefaultRate);
            double first200 = RmsChange(baseline, candidate, 1000, 1200);
            _output.WriteLine($"Background alone, peak {noisePeak:F5}, after loud = {afterLoud}: {first200:+0.00;-0.00} dB candidate vs baseline in 200 ms.");
            if (eligible)
                Assert.InRange(first200, 0.25, 3.001);
            else
                Assert.True(BitwiseEqual(baseline.AsSpan(DefaultRate), candidate.AsSpan(DefaultRate)));
        }

        /// <summary>
        /// Repeats quiet-to-loud transitions and compares the entire settled moderate and loud intervals. The new state
        /// must not accumulate across passages or boost sustained program material.
        /// </summary>
        [Fact]
        public void Process_RepeatedTransitions_ProtectsModerateAndSustainedLoudAudio()
        {
            float[] cycle = Transition(DefaultRate, 0.32f, 0.010f);
            for (int i = 0; i < Frames(250, DefaultRate); i++)
                cycle[i] = (float)(0.06 * Math.Sin(2 * Math.PI * 173 * i / DefaultRate));
            float[] input = [.. cycle, .. cycle, .. cycle];
            float[] baseline = ProcessAligned(input, 1, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, 1, DefaultRate, blockFrames: 173);
            for (int repetition = 0; repetition < 3; repetition++)
            {
                int start = repetition * cycle.Length;
                int moderateStart = start + Frames(60, DefaultRate);
                int moderateCount = Frames(150, DefaultRate);
                Assert.True(BitwiseEqual(baseline.AsSpan(moderateStart, moderateCount), candidate.AsSpan(moderateStart, moderateCount)));
                int loudStart = start + Frames(250, DefaultRate);
                int loudCount = Frames(660, DefaultRate);
                Assert.True(BitwiseEqual(baseline.AsSpan(loudStart, loudCount), candidate.AsSpan(loudStart, loudCount)));
                Assert.InRange(RmsChange(baseline, candidate, 2000 * repetition + 1000, 2000 * repetition + 1200), 1.0, 3.001);
            }
        }

        /// <summary>
        /// Passes a gradual rise that stays below the instantaneous peak guard but crosses the sustained level
        /// boundary. The envelope must prevent gain over the moderate plateau even without a sharp peak.
        /// </summary>
        [Fact]
        public void Process_GraduallyRisingSignal_UsesLevelAsWellAsPeakProtection()
        {
            float[] input = Transition(DefaultRate, 0.0f, 0.010f);
            int start = DefaultRate + Frames(100, DefaultRate);
            for (int i = start; i < input.Length; i++)
            {
                double fraction = Math.Min(1.0, (i - start) / (double)Frames(60, DefaultRate));
                input[i] = (float)((0.013 + 0.046 * fraction) * Math.Sin(2 * Math.PI * 173 * (i - start) / DefaultRate));
            }
            float[] baseline = ProcessAligned(input, 1, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, 1, DefaultRate);
            Assert.InRange(RmsChange(baseline, candidate, 1000, 1080), 1.0, 3.001);
            Assert.True(BitwiseEqual(baseline.AsSpan(Frames(1180, DefaultRate)), candidate.AsSpan(Frames(1180, DefaultRate))));
            AssertSmoothCorrection(baseline, candidate, start, start + Frames(130, DefaultRate), Frames(12, DefaultRate));
        }

        /// <summary>
        /// Splits stereo and surround processing at nonaligned block lengths across sample rates. This guards delayed
        /// metadata alignment, channel gain ratios, inactive channels, and exact state continuity.
        /// </summary>
        /// <param name="sampleRate">The rate at which the physical lookahead is kept constant.</param>
        /// <param name="channels">The channel count, including layouts with silent channels.</param>
        [Theory]
        [InlineData(44100, 2)]
        [InlineData(48000, 6)]
        [InlineData(96000, 6)]
        public void Process_InterleavedFrames_PreserveChannelRatiosAndSplitBufferContinuity(int sampleRate, int channels)
        {
            float[] mono = Transition(sampleRate, 0.32f, 0.010f);
            var input = new float[mono.Length * channels];
            int active = channels == 6 ? 2 : 0;
            for (int offset = 0; offset < input.Length; offset += channels)
            {
                input[offset + active] = mono[offset / channels];
                input[offset + channels - 1] = -0.5f * input[offset + active];
            }

            float[] baseline = ProcessAligned(input, channels, sampleRate, enhance: false);
            float[] continuous = ProcessAligned(input, channels, sampleRate);
            float[] split = ProcessAligned(input, channels, sampleRate, blockFrames: 157);
            Assert.True(BitwiseEqual(continuous, split));
            Assert.InRange(RmsChange(baseline, split, 1000, 1200, sampleRate, channels), 1.0, 3.001);
            for (int offset = 0; offset < split.Length; offset += channels)
            {
                Assert.Equal(-0.5f * split[offset + active], split[offset + channels - 1]);
                if (channels == 6)
                    Assert.Equal(0.0f, split[offset]);
            }
        }

        /// <summary>
        /// Introduces out-of-range and nonfinite samples after quiet gain has begun. The complete chain must retain
        /// the baseline peak protection and produce finite output no greater than the existing sample ceiling.
        /// </summary>
        [Fact]
        public void Process_NonfiniteSamplesAndLargeImpact_KeepTheSampleCeiling()
        {
            float[] input = Transition(DefaultRate, 0.0f, 0.010f);
            int impact = DefaultRate + Frames(100, DefaultRate);
            input[impact - 2] = float.PositiveInfinity;
            input[impact - 1] = float.NaN;
            input[impact] = 1.5f;
            input[impact + 1] = -float.MaxValue;
            float[] baseline = ProcessAligned(input, 1, DefaultRate, enhance: false);
            float[] candidate = ProcessAligned(input, 1, DefaultRate);
            Assert.True(BitwiseEqual(baseline.AsSpan(impact - 2, 4), candidate.AsSpan(impact - 2, 4)));
            Assert.All(candidate, sample => Assert.True(float.IsFinite(sample) && Math.Abs(sample) <= 0.95f));
        }

        /// <summary>
        /// Fills the lookahead window, then measures managed allocation while processing more quiet blocks.
        /// This guards against per-callback or per-frame allocations in the supplemental gain path.
        /// </summary>
        [Fact]
        public void Process_PreparedFormat_DoesNotAllocatePerBlock()
        {
            const int channels = 2;
            float[] original = new float[512];
            Array.Fill(original, 0.010f);
            float[] processed = (float[])original.Clone();
            var dynamics = new AdaptiveDynamicsProcessor();
            var transient = new LookaheadTransientController();
            for (int i = 0; i < 16; i++)
            {
                original.CopyTo(processed, 0);
                dynamics.Process(processed, channels, DefaultRate);
                transient.Process(processed, original, channels, DefaultRate);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++)
            {
                original.CopyTo(processed, 0);
                dynamics.Process(processed, channels, DefaultRate);
                transient.Process(processed, original, channels, DefaultRate);
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        /// <summary>
        /// Generates an unprocessed tone or silence followed by quiet modulated audio and optional independent noise.
        /// The tone and quiet mixture reproduce the timing of the previous repeated-impact regression fixture.
        /// </summary>
        /// <param name="sampleRate">Audio frame rate in Hertz.</param>
        /// <param name="loudPeak">The preceding tone peak, or zero for silence.</param>
        /// <param name="quietPeak">Peak scale of the quiet modulated mixture.</param>
        /// <param name="noisePeak">The optional independent noise peak.</param>
        /// <returns>Two seconds of unprocessed mono samples.</returns>
        private static float[] Transition(int sampleRate, float loudPeak, float quietPeak, float noisePeak = 0.0f)
        {
            var samples = new float[2 * sampleRate];
            for (int i = 0; i < sampleRate; i++)
                samples[i] = loudPeak * (float)Math.Sin(2 * Math.PI * 173 * i / sampleRate);
            var noise = new Random(4271);
            for (int i = sampleRate; i < samples.Length; i++)
            {
                double t = (i - sampleRate) / (double)sampleRate;
                double envelope = (1.0 - Math.Exp(-t / 0.003)) * (0.68 + 0.32 * Math.Sin(2 * Math.PI * 7 * t));
                double mixture = 0.55 * Math.Sin(2 * Math.PI * 137 * t) + 0.30 * Math.Sin(2 * Math.PI * 251 * t) + 0.15 * Math.Sin(2 * Math.PI * 491 * t);
                samples[i] = (float)(quietPeak * envelope * mixture) + (float)(noisePeak * (2 * noise.NextDouble() - 1));
            }
            return samples;
        }

        /// <summary>
        /// Runs fresh dynamics and lookahead state from the same original signal and removes the delay for comparison.
        /// </summary>
        /// <param name="original">The unchanged original samples.</param>
        /// <param name="channels">Interleaved channels per frame.</param>
        /// <param name="sampleRate">Frame rate in Hertz.</param>
        /// <param name="enhance">Whether to supply original samples for supplemental gain.</param>
        /// <param name="blockFrames">The block size in frames, or zero to process one contiguous buffer.</param>
        /// <returns>Output on the original timeline with a silent tail.</returns>
        private static float[] ProcessAligned(float[] original, int channels, int sampleRate, bool enhance = true, int blockFrames = 0)
        {
            float[] output = (float[])original.Clone();
            var dynamics = new AdaptiveDynamicsProcessor();
            var transient = new LookaheadTransientController();
            int blockSamples = blockFrames == 0 ? output.Length : blockFrames * channels;
            for (int offset = 0; offset < output.Length; offset += blockSamples)
            {
                Span<float> block = output.AsSpan(offset, Math.Min(blockSamples, output.Length - offset));
                dynamics.Process(block, channels, sampleRate);
                if (enhance)
                    transient.Process(block, original.AsSpan(offset, block.Length), channels, sampleRate);
                else
                    transient.Process(block, channels, sampleRate);
            }
            Assert.Equal(Math.Max(1, (int)Math.Ceiling(sampleRate * 0.012)), transient.LatencyFrames);
            int latencySamples = transient.LatencyFrames * channels;
            output.AsSpan(latencySamples).CopyTo(output);
            output.AsSpan(output.Length - latencySamples).Clear();
            return output;
        }

        /// <summary>
        /// Checks the correction slope on nonzero frames, accounting for skipped waveform zero crossings.
        /// </summary>
        /// <param name="baseline">Original-timeline baseline output.</param>
        /// <param name="candidate">Aligned output with supplemental gain.</param>
        /// <param name="start">First frame to inspect.</param>
        /// <param name="end">Last frame to inspect.</param>
        /// <param name="latency">Number of frames available to remove the entire allowed correction.</param>
        private static void AssertSmoothCorrection(float[] baseline, float[] candidate, int start, int end, int latency)
        {
            int previousFrame = -1;
            double previous = 1.0;
            for (int frame = start; frame <= end; frame++)
            {
                if (Math.Abs(baseline[frame]) < 0.00001f)
                    continue;
                double gain = candidate[frame] / (double)baseline[frame];
                Assert.InRange(gain, 0.999999, 1.413);
                if (previousFrame >= 0)
                    Assert.InRange(Math.Abs(gain - previous), 0.0, (frame - previousFrame) * 0.413 / latency + 0.000001);
                previous = gain;
                previousFrame = frame;
            }
        }

        /// <summary>
        /// Compares RMS energy over matching timeline windows, preserving short-onset timing in gain measurements.
        /// </summary>
        /// <param name="reference">Reference audio used as the denominator.</param>
        /// <param name="output">Output audio aligned to the same time.</param>
        /// <param name="startMs">Inclusive start in milliseconds.</param>
        /// <param name="endMs">Exclusive end in milliseconds.</param>
        /// <param name="sampleRate">Frame rate in Hertz.</param>
        /// <param name="channels">Number of channels contributing to RMS.</param>
        /// <returns>RMS difference in decibels.</returns>
        private static double RmsChange(float[] reference, float[] output, int startMs, int endMs, int sampleRate = DefaultRate, int channels = 1)
        {
            double referenceEnergy = 0.0, outputEnergy = 0.0;
            int end = Frames(endMs, sampleRate) * channels;
            for (int i = Frames(startMs, sampleRate) * channels; i < end; i++)
            {
                referenceEnergy += (double)reference[i] * reference[i];
                outputEnergy += (double)output[i] * output[i];
            }
            return 10.0 * Math.Log10(outputEnergy / referenceEnergy);
        }

        /// <summary>
        /// Finds the sample peak to compare an entire impact window, including nearby frames.
        /// </summary>
        /// <param name="samples">The samples in the impact window.</param>
        /// <returns>The largest absolute sample amplitude.</returns>
        private static float Peak(ReadOnlySpan<float> samples)
        {
            float peak = 0.0f;
            foreach (float sample in samples)
                peak = Math.Max(peak, Math.Abs(sample));
            return peak;
        }

        /// <summary>
        /// Checks exact float representations so protected outputs cannot hide changes through approximate comparison.
        /// </summary>
        /// <param name="left">The baseline span.</param>
        /// <param name="right">The corresponding candidate span.</param>
        /// <returns>True when the spans contain exactly the same bytes.</returns>
        private static bool BitwiseEqual(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
            => MemoryMarshal.AsBytes(left).SequenceEqual(MemoryMarshal.AsBytes(right));

        /// <summary>
        /// Converts exact fixture timings to frame indices at the selected sample rate.
        /// </summary>
        /// <param name="milliseconds">The original-timeline duration in milliseconds.</param>
        /// <param name="sampleRate">Audio frame rate in Hertz.</param>
        /// <returns>The integer number of complete frames.</returns>
        private static int Frames(int milliseconds, int sampleRate) => milliseconds * sampleRate / 1000;
    }
}

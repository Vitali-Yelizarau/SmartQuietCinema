using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    public sealed class AdaptiveDynamicsProcessorTests
    {
        [Fact]
        public void Process_Silence_RemainsSilent()
        {
            var processor = new AdaptiveDynamicsProcessor();

            float[] samples = new float[48000];

            processor.Process(samples, 1, 48000);

            Assert.All(samples, sample =>
                Assert.Equal(0f, sample));

            Assert.Equal(0.0, processor.CurrentGainDb, 6);
        }

        /// <summary>
        /// Checks that a steady -40 dBFS signal still settles near +6 dB after lowering the compression threshold.
        /// </summary>
        [Fact]
        public void Process_QuietSignal_IsAmplified()
        {
            var processor = new AdaptiveDynamicsProcessor();

            float[] samples = new float[48000];
            Array.Fill(samples, 0.01f);

            processor.Process(samples, 1, 48000);

            // After settling, a -40 dBFS signal
            // should receive approximately +6 dB.
            Assert.InRange(samples[^1], 0.015f, 0.021f);

            Assert.InRange(processor.CurrentGainDb, 5.95, 6.05);
        }

        /// <summary>
        /// Verifies that noise peaking below -70 dBFS remains unchanged, guarding against boost below the noise floor.
        /// </summary>
        [Fact]
        public void Process_NoiseBelowFloor_IsNotAmplified()
        {
            var processor = new AdaptiveDynamicsProcessor();
            var random = new Random(42);
            float[] input = new float[48000];
            for (int index = 0; index < input.Length; index++)
                input[index] = (float)((2.0 * random.NextDouble() - 1.0) * 0.0003);
            float[] processed = (float[])input.Clone();

            processor.Process(processed, 1, 48000);

            Assert.Equal(input, processed);
            Assert.Equal(0.0, processor.CurrentGainDb);
        }

        /// <summary>
        /// Checks that a loud step settles near the compressed level without a gain jump on its first frame.
        /// </summary>
        /// <param name="sampleRate">The sample rate in Hertz used to verify time-based behavior.</param>
        [Theory]
        [InlineData(44100)]
        [InlineData(48000)]
        [InlineData(96000)]
        public void Process_LoudSignal_IsAttenuated(int sampleRate)
        {
            var processor = new AdaptiveDynamicsProcessor();
            int onset = sampleRate / 2;
            float[] samples = new float[sampleRate];
            samples.AsSpan(0, onset).Fill(0.075f);
            samples.AsSpan(onset).Fill(0.5f);

            processor.Process(samples, 1, sampleRate);

            Assert.InRange(samples[onset], 0.499f, 0.501f);
            Assert.InRange(samples[onset + sampleRate / 25], 0.110f, 0.120f);
            Assert.InRange(samples[^1], 0.108f, 0.111f);
        }

        /// <summary>
        /// Checks that a gradual rise and repeated loud peaks stay compressed while linked gain and buffer continuity are preserved.
        /// </summary>
        /// <param name="channels">The number of active interleaved audio channels.</param>
        [Theory]
        [InlineData(2)]
        [InlineData(6)]
        public void Process_GradualAndFluctuatingLoudPassage_StaysCompressedAcrossBuffers(int channels)
        {
            const int sampleRate = 48000;
            float[] input = new float[2 * sampleRate * channels];
            for (int frame = 0; frame < input.Length / channels; frame++)
            {
                float level = frame < sampleRate / 4 ? 0.075f
                    : frame < 3 * sampleRate / 4 ? 0.075f + 0.345f * (frame - sampleRate / 4) / (sampleRate / 2)
                    : frame % 3840 < 1920 ? 0.34f : 0.62f;
                for (int channel = 0; channel < channels; channel++)
                    input[frame * channels + channel] = level * (channel % 2 == 0 ? 1.0f : -0.5f);
            }

            float[] continuous = (float[])input.Clone();
            float[] split = (float[])input.Clone();
            new AdaptiveDynamicsProcessor().Process(continuous, channels, sampleRate);
            var splitProcessor = new AdaptiveDynamicsProcessor();
            int boundary = (3 * sampleRate / 4 + 137) * channels;
            splitProcessor.Process(split.AsSpan(0, boundary), channels, sampleRate);
            splitProcessor.Process(split.AsSpan(boundary), channels, sampleRate);

            Assert.Equal(continuous, split);
            Assert.InRange(continuous[(sampleRate / 5) * channels], 0.0749f, 0.0751f);
            int loudStart = sampleRate * 6 / 5 * channels;
            int loudLength = sampleRate * 3 / 5 * channels;
            Assert.InRange(MeasureRms(continuous.AsSpan(loudStart, loudLength)) / MeasureRms(input.AsSpan(loudStart, loudLength)), 0.22, 0.24);
            for (int offset = 0; offset < continuous.Length; offset += channels)
            {
                for (int channel = 1; channel < channels; channel++)
                    Assert.Equal(continuous[offset] * (channel % 2 == 0 ? 1.0f : -0.5f), continuous[offset + channel]);
            }
        }

        /// <summary>
        /// Guards a moderate speech-like pattern from inadvertent attenuation caused by changes to the loud branch.
        /// </summary>
        [Fact]
        public void Process_ModerateSpeechLikeSignal_PreservesSettledLevel()
        {
            const int sampleRate = 48000;
            float[] input = new float[sampleRate];
            FillSpeechLike(input, sampleRate, 0.1f);
            float[] output = (float[])input.Clone();

            new AdaptiveDynamicsProcessor().Process(output, 1, sampleRate);

            int start = 2 * sampleRate / 5;
            Assert.InRange(MeasureRms(output.AsSpan(start)) / MeasureRms(input.AsSpan(start)), 0.999, 1.001);
        }

        /// <summary>
        /// Verifies that a short whisper-like phrase after silence still receives useful boost without changing the silent lead-in.
        /// </summary>
        [Fact]
        public void Process_ShortWhisperAfterSilence_StillReceivesBoost()
        {
            const int sampleRate = 48000;
            int start = sampleRate / 2;
            int phraseLength = sampleRate * 160 / 1000;
            float[] input = new float[start + phraseLength];
            FillSpeechLike(input.AsSpan(start), sampleRate, 0.012f);
            float[] output = (float[])input.Clone();

            new AdaptiveDynamicsProcessor().Process(output, 1, sampleRate);

            Assert.True(output.AsSpan(0, start).SequenceEqual(input.AsSpan(0, start)));
            int latePhrase = input.Length - sampleRate / 20;
            Assert.InRange(MeasureRms(output.AsSpan(latePhrase)) / MeasureRms(input.AsSpan(latePhrase)), 1.85, 2.0);
        }

        /// <summary>
        /// Checks that a quiet speech-like signal following a loud plateau recovers to boost instead of remaining suppressed.
        /// </summary>
        [Fact]
        public void Process_QuietSpeechAfterLoudPassage_RecoversFromCompression()
        {
            const int sampleRate = 48000;
            int start = sampleRate / 2;
            float[] input = new float[start + sampleRate];
            input.AsSpan(0, start).Fill(0.5f);
            FillSpeechLike(input.AsSpan(start), sampleRate, 0.012f);
            float[] output = (float[])input.Clone();

            new AdaptiveDynamicsProcessor().Process(output, 1, sampleRate);

            int firstWindow = sampleRate * 80 / 1000;
            double openingGain = MeasureRms(output.AsSpan(start, firstWindow)) / MeasureRms(input.AsSpan(start, firstWindow));
            int lastWindow = input.Length - sampleRate * 150 / 1000;
            double recoveredGain = MeasureRms(output.AsSpan(lastWindow)) / MeasureRms(input.AsSpan(lastWindow));
            Assert.InRange(openingGain, 0.22, 0.30);
            Assert.InRange(recoveredGain, 2.35, 2.6);
        }

        [Fact]
        public void Process_NormalSignal_RemainsNearOriginalLevel()
        {
            var processor = new AdaptiveDynamicsProcessor();

            float[] samples = new float[48000];
            Array.Fill(samples, 0.1f);

            processor.Process(samples, 1, 48000);

            Assert.InRange(samples[^1], 0.095f, 0.105f);

            Assert.InRange(processor.CurrentGainDb, -0.5, 0.5);
        }

        [Fact]
        public void Process_SplitBuffers_ProducesIdenticalOutput()
        {
            var fullProcessor = new AdaptiveDynamicsProcessor();
            var splitProcessor = new AdaptiveDynamicsProcessor();

            float[] original = new float[4000];

            for (int i = 0; i < original.Length; i++)
            {
                original[i] = i % 12 < 6 ? 0.01f : 0.5f;
            }

            float[] fullBuffer = (float[])original.Clone();
            float[] splitBuffer = (float[])original.Clone();

            fullProcessor.Process(fullBuffer, 2, 48000);

            splitProcessor.Process(
                splitBuffer.AsSpan(0, 1200),
                2,
                48000);

            splitProcessor.Process(
                splitBuffer.AsSpan(1200),
                2,
                48000);

            for (int i = 0; i < original.Length; i++)
            {
                Assert.Equal(fullBuffer[i], splitBuffer[i]);
            }
        }

        /// <summary>
        /// Checks that compression preserves signed channel balance and keeps inactive channels silent in stereo and 5.1 buffers.
        /// </summary>
        /// <param name="channels">The number of interleaved channels, only the first two of which carry audio.</param>
        [Theory]
        [InlineData(2)]
        [InlineData(6)]
        public void Process_Compression_PreservesChannelBalance(int channels)
        {
            const int sampleRate = 48000;
            var processor = new AdaptiveDynamicsProcessor();
            float[] samples = new float[4800 * channels];
            for (int offset = 0; offset < samples.Length; offset += channels)
            {
                samples[offset] = 0.5f;
                samples[offset + 1] = -0.25f;
            }

            processor.Process(samples, channels, sampleRate);

            Assert.True(processor.CurrentGainDb < 0.0);
            for (int offset = 0; offset < samples.Length; offset += channels)
            {
                Assert.Equal(-0.5f * samples[offset], samples[offset + 1]);
                for (int channel = 2; channel < channels; channel++)
                    Assert.Equal(0.0f, samples[offset + channel]);
            }
        }

        /// <summary>
        /// Checks that a quiet signal following a loud passage recovers monotonically without a sudden gain jump.
        /// </summary>
        [Fact]
        public void Process_LoudToQuiet_RecoversGradually()
        {
            var processor = new AdaptiveDynamicsProcessor();

            float[] loudSamples = new float[48000];
            Array.Fill(loudSamples, 0.5f);

            processor.Process(loudSamples, 1, 48000);

            Assert.True(processor.CurrentGainDb < 0);

            // A sudden quiet frame must not receive
            // the full positive gain immediately.
            float[] firstQuietFrame = { 0.01f };

            processor.Process(firstQuietFrame, 1, 48000);

            Assert.True(firstQuietFrame[0] < 0.01f);

            // After sustained quiet input,
            // positive gain should be restored.
            float[] quietSamples = new float[48000];
            Array.Fill(quietSamples, 0.01f);

            processor.Process(quietSamples, 1, 48000);

            float previousSample = firstQuietFrame[0];
            foreach (float sample in quietSamples)
            {
                Assert.InRange(sample - previousSample, 0.0f, 0.0000025f);
                previousSample = sample;
            }

            Assert.True(quietSamples[^1] > 0.015f);
            Assert.True(processor.CurrentGainDb > 0);
        }


        /// <summary>
        /// Verifies that a full-scale impulse during quiet boost still hits the limiter ceiling instead of clipping.
        /// </summary>
        [Fact]
        public void Process_SuddenLoudPeak_DoesNotExceedMaximumAmplitude()
        {
            var processor = new AdaptiveDynamicsProcessor();

            // Simulate a sustained quiet signal.
            float[] quietSamples = new float[48000];
            Array.Fill(quietSamples, 0.01f);

            processor.Process(quietSamples, 1, 48000);

            // Verify that the processor has increased the gain.
            Assert.True(processor.CurrentGainDb > 0);

            // Simulate a sudden full-scale audio peak.
            float[] loudPeak = [1.0f];

            processor.Process(loudPeak, 1, 48000);

            Assert.Equal(0.95f, loudPeak[0]);
        }

        /// <summary>
        /// Writes a harmonic carrier with syllable-rate modulation as an unprocessed speech-like test signal.
        /// </summary>
        /// <param name="samples">The mono frames to populate.</param>
        /// <param name="sampleRate">The input rate for converting frame indices to time.</param>
        /// <param name="amplitude">The scale for quiet or conversational test levels.</param>
        private static void FillSpeechLike(Span<float> samples, int sampleRate, float amplitude)
        {
            for (int frame = 0; frame < samples.Length; frame++)
            {
                double phase = 2.0 * Math.PI * frame / sampleRate;
                double syllable = 0.75 + 0.25 * Math.Sin(4.0 * phase);
                double carrier = 0.75 * Math.Sin(150.0 * phase) + 0.25 * Math.Sin(450.0 * phase);
                samples[frame] = (float)(amplitude * syllable * carrier);
            }
        }

        /// <summary>
        /// Measures a sample window so tests can verify gain on the waveform rather than read processor state.
        /// </summary>
        /// <param name="samples">The nonempty window of mono or interleaved samples.</param>
        /// <returns>The linear RMS amplitude across the window.</returns>
        private static double MeasureRms(ReadOnlySpan<float> samples)
        {
            double sum = 0.0;
            foreach (float sample in samples) sum += (double)sample * sample;
            return Math.Sqrt(sum / samples.Length);
        }
    }
}

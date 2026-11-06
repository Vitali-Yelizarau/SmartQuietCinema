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

            Assert.True(processor.CurrentGainDb > 0);
        }

        [Fact]
        public void Process_LoudSignal_IsAttenuated()
        {
            var processor = new AdaptiveDynamicsProcessor();

            float[] samples = new float[48000];
            Array.Fill(samples, 0.5f);

            processor.Process(samples, 1, 48000);

            Assert.InRange(samples[^1], 0.1f, 0.3f);

            Assert.True(processor.CurrentGainDb < 0);
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

            Assert.True(quietSamples[^1] > 0.015f);
            Assert.True(processor.CurrentGainDb > 0);
        }


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

            // The processed signal must not exceed
            // the normalized audio amplitude range.
            Assert.InRange(loudPeak[0], -1.0f, 1.0f);
        }

    }
}

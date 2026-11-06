using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    public sealed class LookaheadTransientControllerTests
    {
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
            Assert.InRange(left, 0.0f, 0.3f);

            // Linked processing must preserve channel proportions.
            Assert.InRange(right, 0.0f, 0.15f);
            Assert.Equal(left * 0.5f, right, 5);
        }

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
        }


        [Fact]
        public void Process_SustainedTone_DoesNotRemainTransientAttenuated()
        {
            var processor = new LookaheadTransientController();

            const int sampleRate = 48000;
            const int frequency = 1000;
            const float amplitude = 0.25f;

            float[] samples = new float[sampleRate * 2];

            // Generate two seconds of a steady sine wave.
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = amplitude * (float)Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);
            }

            processor.Process(samples, 1, sampleRate);

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

    }
}

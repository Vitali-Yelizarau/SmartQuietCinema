using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    public sealed class AudioLevelDetectorTests
    {
        [Fact]
        public void Analyze_Silence_KeepsLevelAtZero()
        {
            var detector = new AudioLevelDetector(10, 100);

            float[] samples = new float[100];

            detector.Analyze(samples, 2, 1000);

            Assert.Equal(0, detector.Level);
        }

        [Fact]
        public void Analyze_LoudSignalThenSilence_UsesAttackAndRelease()
        {
            var detector = new AudioLevelDetector(10, 100);

            float[] loudSamples = new float[20];
            Array.Fill(loudSamples, 0.5f);

            detector.Analyze(loudSamples, 1, 1000);

            double levelAfterAttack = detector.Level;

            Assert.InRange(levelAfterAttack, 0.43, 0.435);

            detector.Analyze(new float[20], 1, 1000);

            Assert.True(detector.Level < levelAfterAttack);
            Assert.InRange(detector.Level, 0.35, 0.36);
        }

        [Fact]
        public void Analyze_SplitBuffers_PreservesDetectorState()
        {
            var singleBufferDetector = new AudioLevelDetector(10, 100);

            var splitBufferDetector = new AudioLevelDetector(10, 100);

            float[] samples = { 0.2f, -0.2f, 0.4f, -0.4f, 0.6f, -0.6f, 0.3f, -0.3f };

            singleBufferDetector.Analyze(samples, 2, 1000);

            splitBufferDetector.Analyze(samples.AsSpan(0, 4), 2, 1000);

            splitBufferDetector.Analyze(samples.AsSpan(4), 2, 1000);

            Assert.Equal(singleBufferDetector.Level, splitBufferDetector.Level, 10);
        }


        [Fact]
        public void AnalyzeFrame_ProducesSameResultAsBufferAnalysis()
        {
            var bufferDetector = new AudioLevelDetector(10, 100);
            var frameDetector = new AudioLevelDetector(10, 100);

            float[] samples = { 0.2f, -0.2f, 0.4f, -0.4f, 0.6f, -0.6f, 0.3f, -0.3f };

            bufferDetector.Analyze(samples, 2, 1000);

            for (int i = 0; i < samples.Length; i += 2)
            {
                frameDetector.AnalyzeFrame(samples.AsSpan(i, 2), 1000);
            }

            Assert.Equal(bufferDetector.Level, frameDetector.Level, 10);
        }

        [Fact]
        public void AnalyzeFrame_SampleRateChange_UpdatesCoefficients()
        {
            var detector = new AudioLevelDetector(10, 100);

            float[] frame = { 1.0f };

            for (int i = 0; i < 10; i++)
            {
                detector.AnalyzeFrame(frame, 1000);
            }

            for (int i = 0; i < 10; i++)
            {
                detector.AnalyzeFrame(frame, 2000);
            }

            Assert.InRange(detector.Level, 0.77, 0.78);
        }


        [Fact]
        public void AnalyzeFrame_InactiveChannels_DoNotReduceDetectedLevel()
        {
            var stereoDetector = new AudioLevelDetector(10, 100);
            var surroundDetector = new AudioLevelDetector(10, 100);

            float[] stereoFrame = { 0.1f, 0.1f };

            float[] surroundFrame = [0.1f, 0.1f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f];

            // Process one second of identical stereo content
            // represented in two different channel layouts.
            for (int i = 0; i < 48000; i++)
            {
                stereoDetector.AnalyzeFrame(stereoFrame, 48000);
                surroundDetector.AnalyzeFrame(surroundFrame, 48000);
            }

            // Inactive channels must not artificially
            // decrease the detected signal level.
            Assert.InRange(stereoDetector.Level, 0.099, 0.101);

            Assert.InRange(surroundDetector.Level, 0.099, 0.101);
        }

    }
}

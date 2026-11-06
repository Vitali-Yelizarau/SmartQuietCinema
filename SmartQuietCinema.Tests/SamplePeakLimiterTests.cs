using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    public sealed class SamplePeakLimiterTests
    {
        [Fact]
        public void ProcessFrame_StereoPeak_LimitsBothChannelsEqually()
        {
            var limiter = new SamplePeakLimiter();

            float[] frame = { 2.0f, 0.5f };

            limiter.ProcessFrame(frame, 48000);

            // The louder channel reaches the safety ceiling.
            Assert.InRange(frame[0], 0.9499f, 0.9501f);

            // The quieter channel receives the same gain factor.
            Assert.InRange(frame[1], 0.2374f, 0.2376f);
        }


        [Fact]
        public void ProcessFrame_AfterPeak_RecoversGradually()
        {
            var limiter = new SamplePeakLimiter();

            // Trigger gain reduction with a strong peak.
            float[] peakFrame = { 2.0f };

            limiter.ProcessFrame(peakFrame, 48000);

            Assert.InRange(peakFrame[0], 0.9499f, 0.9501f);

            // The next quiet frame should remain attenuated.
            float[] quietFrame = { 0.5f };

            limiter.ProcessFrame(quietFrame, 48000);

            float initialRecoveryOutput = quietFrame[0];

            Assert.True(initialRecoveryOutput < 0.5f);

            // Continue processing for 100 milliseconds.
            for (int i = 0; i < 4800; i++)
            {
                quietFrame[0] = 0.5f;
                limiter.ProcessFrame(quietFrame, 48000);
            }

            // Gain must recover without instantly jumping to unity.
            Assert.True(quietFrame[0] > initialRecoveryOutput);
            Assert.True(quietFrame[0] < 0.5f);
        }

        [Fact]
        public void ProcessFrame_NegativePeak_PreservesPolarity()
        {
            var limiter = new SamplePeakLimiter();

            float[] frame = { -2.0f, 0.5f };

            limiter.ProcessFrame(frame, 48000);

            // Negative peak must respect the same ceiling.
            Assert.InRange(frame[0], -0.9501f, -0.9499f);

            // Both channels must receive identical gain.
            Assert.InRange(frame[1], 0.2374f, 0.2376f);
        }

    }
}

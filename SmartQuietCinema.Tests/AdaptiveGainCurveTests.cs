using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    public sealed class AdaptiveGainCurveTests
    {
        [Fact]
        public void Silence_DoesNotReceiveAmplification()
        {
            var curve = new AdaptiveGainCurve();

            Assert.Equal(
                0.0,
                curve.CalculateTargetGainDb(
                    double.NegativeInfinity));
        }

        /// <summary>
        /// Checks both sides of the quiet boost peak so changing compression does not alter quiet amplification.
        /// </summary>
        /// <param name="levelDb">A detected quiet level in dBFS.</param>
        /// <param name="expectedGainDb">The unchanged boost in decibels.</param>
        [Theory]
        [InlineData(-60.0, 6.0)]
        [InlineData(-50.0, 12.0)]
        [InlineData(-40.0, 6.0)]
        public void QuietSignal_ReceivesPositiveGain(double levelDb, double expectedGainDb)
        {
            var curve = new AdaptiveGainCurve();

            double gain = curve.CalculateTargetGainDb(levelDb);

            Assert.Equal(expectedGainDb, gain, 6);
        }

        /// <summary>
        /// Checks moderate levels through the new threshold so the lower knee does not attenuate the neutral range.
        /// </summary>
        /// <param name="levelDb">A detected level in the neutral range, in dBFS.</param>
        [Theory]
        [InlineData(-26.0)]
        [InlineData(-24.0)]
        [InlineData(-23.0)]
        [InlineData(-20.0)]
        public void NormalSignal_RemainsUnchanged(double levelDb)
        {
            var curve = new AdaptiveGainCurve();

            double gain = curve.CalculateTargetGainDb(levelDb);

            Assert.Equal(0.0, gain);
        }

        /// <summary>
        /// Checks that the loud branch increases compression with level without moving the existing knee.
        /// </summary>
        /// <param name="levelDb">A detected level above the compression threshold, in dBFS.</param>
        /// <param name="expectedGainDb">The expected negative gain in decibels.</param>
        [Theory]
        [InlineData(-19.0, -0.8)]
        [InlineData(-18.0, -1.666666667)]
        [InlineData(-12.0, -7.333333333)]
        [InlineData(-8.0, -11.25)]
        [InlineData(0.0, -18.0)]
        public void LoudSignal_ReceivesNegativeGain(double levelDb, double expectedGainDb)
        {
            var curve = new AdaptiveGainCurve();

            double gain = curve.CalculateTargetGainDb(levelDb);

            Assert.Equal(expectedGainDb, gain, 6);
        }

        /// <summary>
        /// Checks continuity and the local 4:1 slope at the knee so the new curve does not introduce a gain step.
        /// </summary>
        [Fact]
        public void LoudThreshold_IsContinuousAcrossKnee()
        {
            var curve = new AdaptiveGainCurve();

            Assert.Equal(0.0, curve.CalculateTargetGainDb(-20.001));
            Assert.Equal(0.0, curve.CalculateTargetGainDb(-20.0));
            Assert.InRange(curve.CalculateTargetGainDb(-19.999), -0.000751, -0.00075);
        }

        /// <summary>
        /// Samples the loud branch through the attenuation cap to guard against a louder input producing a lower settled output level.
        /// </summary>
        [Fact]
        public void LoudCurve_OutputLevelDoesNotReverseThroughAttenuationCap()
        {
            var curve = new AdaptiveGainCurve();
            double previousOutputDb = -20.0;
            for (double levelDb = -19.875; levelDb <= 6.0; levelDb += 0.125)
            {
                double gainDb = curve.CalculateTargetGainDb(levelDb);
                double outputDb = levelDb + gainDb;
                Assert.InRange(gainDb, -18.0, 0.0);
                Assert.True(outputDb >= previousOutputDb);
                previousOutputDb = outputDb;
            }
        }

        [Fact]
        public void VeryLoudSignal_AttenuationIsLimited()
        {
            var curve = new AdaptiveGainCurve();

            double gain = curve.CalculateTargetGainDb(20.0);

            Assert.Equal(-18.0, gain);
        }
    }
}

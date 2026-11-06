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

        [Fact]
        public void QuietSignal_ReceivesPositiveGain()
        {
            var curve = new AdaptiveGainCurve();

            double gain = curve.CalculateTargetGainDb(-40.0);

            Assert.Equal(6.0, gain, 6);
        }

        [Fact]
        public void NormalSignal_RemainsUnchanged()
        {
            var curve = new AdaptiveGainCurve();

            double gain = curve.CalculateTargetGainDb(-24.0);

            Assert.Equal(0.0, gain);
        }

        [Fact]
        public void LoudSignal_ReceivesNegativeGain()
        {
            var curve = new AdaptiveGainCurve();

            double gain = curve.CalculateTargetGainDb(-8.0);

            Assert.Equal(-7.5, gain, 6);
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

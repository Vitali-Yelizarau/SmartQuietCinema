using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    /// <summary>
    /// Contains unit tests for the GainProcessor class.
    /// Verifies gain calculations and input validation.
    /// </summary>
    public sealed class GainProcessorTests
    {
        private const int StereoChannels = 2;
        private const int SampleRateHz = 48000;
        private const float Tolerance = 0.000001f;

        /// <summary>
        /// Verifies that zero gain leaves audio samples unchanged.
        /// </summary>
        [Fact]
        public void Process_ZeroDb_PreservesSamples()
        {
            float[] samples = [0.5f, -0.5f, 0.25f, -0.25f];
            float[] expected = (float[])samples.Clone();

            var processor = new GainProcessor(0.0);

            processor.Process(samples, StereoChannels, SampleRateHz);

            Assert.Equal(expected, samples);
        }

        /// <summary>
        /// Verifies that -20 dB reduces signal amplitude
        /// by a factor of ten.
        /// </summary>
        [Fact]
        public void Process_Minus20Db_AttenuatesSamples()
        {
            const double GainDb = -20.0;
            const float ExpectedAmplitude = 0.05f;

            float[] samples = [0.5f, -0.5f];

            var processor = new GainProcessor(GainDb);

            processor.Process(samples, StereoChannels, SampleRateHz);

            Assert.InRange(MathF.Abs(samples[0] - ExpectedAmplitude), 0f, Tolerance);

            Assert.InRange(MathF.Abs(samples[1] + ExpectedAmplitude), 0f, Tolerance);
        }

        /// <summary>
        /// Verifies that non-finite gain values are rejected.
        /// </summary>
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Constructor_InvalidGain_ThrowsException(double gainDb)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GainProcessor(gainDb));
        }

        /// <summary>
        /// Verifies that an invalid channel count is rejected.
        /// </summary>
        [Fact]
        public void Process_ZeroChannels_ThrowsException()
        {
            float[] samples = [0.5f, -0.5f];

            var processor = new GainProcessor(0.0);

            Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(samples, 0, SampleRateHz));
        }

        /// <summary>
        /// Verifies that an incomplete audio frame is rejected.
        /// </summary>
        [Fact]
        public void Process_IncompleteFrame_ThrowsException()
        {
            float[] samples = [0.5f, -0.5f, 0.25f];

            var processor = new GainProcessor(0.0);

            Assert.Throws<ArgumentException>(() => processor.Process(samples, StereoChannels, SampleRateHz));
        }
    }
}

using System;
using SmartQuietCinema.Core.DSP;
using Xunit;

namespace SmartQuietCinema.Tests
{
    public sealed class AudioDiagnosticsAccumulatorTests
    {
        [Fact]
        public void TakeSnapshot_AggregatesMultipleBuffers()
        {
            var diagnostics = new AudioDiagnosticsAccumulator();

            diagnostics.Add([0.2f, -0.4f], [0.1f, -0.2f], -3.0);

            diagnostics.Add([0.8f, -0.6f], [0.4f, -0.3f], 2.0);

            var result = diagnostics.TakeSnapshot();

            Assert.Equal(4, result.SampleCount);
            Assert.Equal(0.8, result.InputPeak, 5);
            Assert.Equal(0.4, result.OutputPeak, 5);
            Assert.Equal(Math.Sqrt(0.3), result.InputRms, 5);
            Assert.Equal(Math.Sqrt(0.075), result.OutputRms, 5);
            Assert.Equal(-3.0, result.MinGainDb);
            Assert.Equal(2.0, result.MaxGainDb);
        }

        [Fact]
        public void TakeSnapshot_CapturesShortPeak()
        {
            var diagnostics = new AudioDiagnosticsAccumulator();

            diagnostics.Add([0.1f, 1.0f, 0.1f], [0.1f, 0.5f, 0.1f], -6.0);

            var result = diagnostics.TakeSnapshot();

            Assert.Equal(1.0, result.InputPeak);
            Assert.Equal(0.5, result.OutputPeak);
        }

        [Fact]
        public void TakeSnapshot_ResetsMeasurements()
        {
            var diagnostics = new AudioDiagnosticsAccumulator();

            diagnostics.Add([0.5f], [0.25f], -6.0);

            diagnostics.TakeSnapshot();

            var empty = diagnostics.TakeSnapshot();

            Assert.Equal(0, empty.SampleCount);
            Assert.Equal(0, empty.InputPeak);
            Assert.Equal(0, empty.OutputPeak);
            Assert.Equal(0, empty.InputRms);
            Assert.Equal(0, empty.OutputRms);
        }
    }
}

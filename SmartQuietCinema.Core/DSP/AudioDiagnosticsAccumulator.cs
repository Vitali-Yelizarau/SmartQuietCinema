using System;

namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Aggregated measurements of processed audio samples.
    /// </summary>
    public readonly record struct AudioDiagnosticsSnapshot(
        long SampleCount,
        double InputPeak,
        double OutputPeak,
        double InputRms,
        double OutputRms,
        double MinGainDb,
        double MaxGainDb);

    /// <summary>
    /// Accumulates audio levels across multiple buffers.
    /// Intended for use on a single capture callback thread.
    /// </summary>
    public sealed class AudioDiagnosticsAccumulator
    {
        private long _sampleCount;
        private double _inputSumSquares;
        private double _outputSumSquares;
        private double _inputPeak;
        private double _outputPeak;
        private double _minGainDb = double.PositiveInfinity;
        private double _maxGainDb = double.NegativeInfinity;

        /// <summary>
        /// Adds input and processed samples to the current interval.
        /// </summary>
        public void Add(
            ReadOnlySpan<float> input,
            ReadOnlySpan<float> output,
            double gainDb)
        {
            if (input.Length != output.Length)
            {
                throw new ArgumentException(
                    "Input and output lengths must match.",
                    nameof(output));
            }

            if (!double.IsFinite(gainDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(gainDb));
            }

            bool hasValidSamples = false;

            for (int i = 0; i < input.Length; i++)
            {
                if (!float.IsFinite(input[i]) ||
                    !float.IsFinite(output[i]))
                {
                    continue;
                }

                double inputAmplitude = Math.Abs((double)input[i]);
                double outputAmplitude = Math.Abs((double)output[i]);

                _inputPeak = Math.Max(_inputPeak, inputAmplitude);
                _outputPeak = Math.Max(_outputPeak, outputAmplitude);

                _inputSumSquares += inputAmplitude * inputAmplitude;
                _outputSumSquares += outputAmplitude * outputAmplitude;

                _sampleCount++;
                hasValidSamples = true;
            }

            if (hasValidSamples)
            {
                _minGainDb = Math.Min(_minGainDb, gainDb);
                _maxGainDb = Math.Max(_maxGainDb, gainDb);
            }
        }

        /// <summary>
        /// Returns accumulated measurements and starts a new interval.
        /// </summary>
        public AudioDiagnosticsSnapshot TakeSnapshot()
        {
            var snapshot = _sampleCount == 0
                ? new AudioDiagnosticsSnapshot()
                : new AudioDiagnosticsSnapshot(
                    _sampleCount,
                    _inputPeak,
                    _outputPeak,
                    Math.Sqrt(_inputSumSquares / _sampleCount),
                    Math.Sqrt(_outputSumSquares / _sampleCount),
                    _minGainDb,
                    _maxGainDb);

            Reset();

            return snapshot;
        }

        /// <summary>
        /// Clears all accumulated measurements.
        /// </summary>
        public void Reset()
        {
            _sampleCount = 0;
            _inputSumSquares = 0;
            _outputSumSquares = 0;
            _inputPeak = 0;
            _outputPeak = 0;
            _minGainDb = double.PositiveInfinity;
            _maxGainDb = double.NegativeInfinity;
        }
    }
}

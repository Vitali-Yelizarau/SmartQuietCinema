namespace SmartQuietCinema.Tools.AudioTestBench
{
    /// <summary>
    /// Names a half-open interval on the original WAV timeline for repeatable comparisons.
    /// </summary>
    /// <param name="Name">The label shown in the diagnostic report.</param>
    /// <param name="StartSeconds">The inclusive start time in seconds.</param>
    /// <param name="EndSeconds">The exclusive end time in seconds.</param>
    public readonly record struct TimeInterval(string Name, decimal StartSeconds, decimal EndSeconds);

    /// <summary>
    /// Reports input and output levels over the same frames, excluding artificial padding.
    /// </summary>
    /// <param name="Interval">The original request, before rounding or truncation.</param>
    /// <param name="StartFrame">The first compared frame, or the range boundary if there are no comparable frames.</param>
    /// <param name="EndFrame">The exclusive end frame of the comparison.</param>
    /// <param name="IsTruncated">Whether the request extends past the end of the non-padded output.</param>
    /// <param name="InputRms">Linear input RMS, or null when no finite input samples were measured.</param>
    /// <param name="InputPeak">Linear input sample peak, or null when no finite input samples were measured.</param>
    /// <param name="OutputRms">Linear output RMS, or null when no finite output samples were measured.</param>
    /// <param name="OutputPeak">Linear output sample peak, or null when no finite output samples were measured.</param>
    public readonly record struct IntervalMeasurement(TimeInterval Interval, long StartFrame, long EndFrame, bool IsTruncated,
        double? InputRms, double? InputPeak, double? OutputRms, double? OutputPeak)
    {
        private const double AmplitudeDecibelFactor = 20.0;

        /// <summary>
        /// Gets the output-minus-input RMS in decibels, or null when a finite ratio is unavailable.
        /// </summary>
        public double? RmsDifferenceDb => DifferenceDb(InputRms, OutputRms);

        /// <summary>
        /// Gets the output-minus-input sample peak in decibels, or null when a finite ratio is unavailable.
        /// </summary>
        public double? PeakDifferenceDb => DifferenceDb(InputPeak, OutputPeak);

        /// <summary>
        /// Converts an amplitude ratio to decibels without assigning a finite change to silence.
        /// </summary>
        /// <param name="input">The input amplitude, if measured.</param>
        /// <param name="output">The output amplitude, if measured.</param>
        /// <returns>A signed gain in decibels, or null if either level is zero or unavailable.</returns>
        private static double? DifferenceDb(double? input, double? output)
            => input is > 0 && output is > 0 && double.IsFinite(input.Value) && double.IsFinite(output.Value)
                ? AmplitudeDecibelFactor * (Math.Log10(output.Value) - Math.Log10(input.Value)) : null;
    }
}

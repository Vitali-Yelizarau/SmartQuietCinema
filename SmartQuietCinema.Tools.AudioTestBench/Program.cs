using System.Globalization;

namespace SmartQuietCinema.Tools.AudioTestBench
{
    /// <summary>
    /// Runs the offline WAV processor and reports signal levels for repeatable comparisons.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Writes a comparison WAV and reports whole-file and optional interval levels, or explains a failure.
        /// </summary>
        /// <param name="args">The input and output paths followed by optional repeated interval arguments.</param>
        /// <returns>Zero on success, one on a processing error, or two for incorrect usage.</returns>
        private static int Main(string[] args)
        {
            const int RequiredPathArguments = 2;
            const int IntervalArgumentPairSize = 2;
            if (args.Length < RequiredPathArguments)
            {
                Console.Error.WriteLine("Usage: SmartQuietCinema.Tools.AudioTestBench <input.wav> <output.wav> [--interval \"name:start:end\"]...");
                return 2;
            }

            try
            {
                var intervals = new List<TimeInterval>();
                for (int index = RequiredPathArguments; index < args.Length; index += IntervalArgumentPairSize)
                {
                    if (args[index] != "--interval" || index + 1 >= args.Length)
                        throw new FormatException($"Expected --interval \"name:start:end\", found '{args[index]}'.");

                    intervals.Add(ParseInterval(args[index + 1]));
                }

                var (result, measurements) = WaveFileProcessor.Process(args[0], args[1], intervals);
                Console.WriteLine(FormattableString.Invariant($"{result.SampleRate} Hz | {result.Channels} channels | {result.FrameCount} frames"));
                Console.WriteLine(FormattableString.Invariant($"Input:  RMS={result.InputRms:F6}, Peak={result.InputPeak:F6}"));
                Console.WriteLine(FormattableString.Invariant($"Output: RMS={result.OutputRms:F6}, Peak={result.OutputPeak:F6}"));
                foreach (IntervalMeasurement measurement in measurements)
                    PrintInterval(measurement, result.SampleRate);
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidDataException or NotSupportedException or FormatException)
            {
                Console.Error.WriteLine($"Audio test bench: {exception.Message}");
                return 1;
            }
        }

        /// <summary>
        /// Parses a command-line interval with culture-independent decimal seconds for reproducible frame boundaries.
        /// </summary>
        /// <param name="argument">A quoted or unquoted name:start:end argument.</param>
        /// <returns>The named time range before validation against the WAV duration.</returns>
        private static TimeInterval ParseInterval(string argument)
        {
            string[] parts = argument.Split(':', StringSplitOptions.TrimEntries);
            const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            if (parts.Length != 3 || parts[0].Length == 0
                || !decimal.TryParse(parts[1], styles, CultureInfo.InvariantCulture, out decimal start)
                || !decimal.TryParse(parts[2], styles, CultureInfo.InvariantCulture, out decimal end))
                throw new FormatException($"Invalid interval '{argument}'. Use name:start:end with decimal seconds, for example whisper:1.25:2.5.");

            return new TimeInterval(parts[0], start, end);
        }

        /// <summary>
        /// Prints both requested and compared bounds so clipping to non-padded audio is visible.
        /// </summary>
        /// <param name="measurement">The aligned interval measurement to display.</param>
        /// <param name="sampleRate">The WAV sample rate used to express compared frames in seconds.</param>
        private static void PrintInterval(IntervalMeasurement measurement, int sampleRate)
        {
            const string decibelFormat = "+0.00;-0.00;0.00";
            TimeInterval interval = measurement.Interval;
            Console.WriteLine(FormattableString.Invariant($"Interval {interval.Name}: [{interval.StartSeconds}, {interval.EndSeconds}) s"));
            string truncated = measurement.IsTruncated ? " [truncated: excludes padded tail]" : "";
            long frames = measurement.EndFrame - measurement.StartFrame;
            decimal start = (decimal)measurement.StartFrame / sampleRate;
            decimal end = (decimal)measurement.EndFrame / sampleRate;
            Console.WriteLine(FormattableString.Invariant($"  Compared: [{start:F6}, {end:F6}) s ({frames} frames){truncated}"));
            Console.WriteLine($"  Input:  RMS={FormatLevel(measurement.InputRms)}, Peak={FormatLevel(measurement.InputPeak)}");
            Console.WriteLine($"  Output: RMS={FormatLevel(measurement.OutputRms)}, Peak={FormatLevel(measurement.OutputPeak)}");
            Console.WriteLine($"  Change (dB): RMS={FormatLevel(measurement.RmsDifferenceDb, decibelFormat)}, Peak={FormatLevel(measurement.PeakDifferenceDb, decibelFormat)}");
        }

        /// <summary>
        /// Formats an interval level without confusing missing levels or undefined ratios with zero.
        /// </summary>
        /// <param name="value">The measured level or decibel difference.</param>
        /// <param name="format">The numeric format to use for an available value.</param>
        /// <returns>The formatted value or N/A when no value is defined.</returns>
        private static string FormatLevel(double? value, string format = "F6")
            => value?.ToString(format, CultureInfo.InvariantCulture) ?? "N/A";
    }
}

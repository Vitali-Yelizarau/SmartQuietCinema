using System.Runtime.InteropServices;
using NAudio.Wave;
using SmartQuietCinema.Core.DSP;
using SmartQuietCinema.Tools.AudioTestBench;

namespace SmartQuietCinema.Tests
{
    /// <summary>
    /// Exercises offline processing against real WAV files to catch format, alignment, and file safety regressions.
    /// </summary>
    public sealed class WaveFileProcessorTests : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("SmartQuietCinema-");

        /// <summary>
        /// Checks standard and extensible float files, including empty and short clips, so export preserves format and duration.
        /// </summary>
        /// <param name="sampleRate">The source rate to preserve, in Hertz.</param>
        /// <param name="channels">The source channel count to preserve.</param>
        /// <param name="frames">The source frame count, including cases shorter than lookahead.</param>
        /// <param name="extensible">Whether the WAV uses an extensible float format header.</param>
        [Theory]
        [InlineData(44100, 2, 1601, false)]
        [InlineData(48000, 6, 1000, true)]
        [InlineData(48000, 2, 5, false)]
        [InlineData(48000, 2, 0, false)]
        public void Process_FloatWave_PreservesFormatAndFrameCount(int sampleRate, int channels, int frames, bool extensible)
        {
            WaveFormat format = extensible
                ? new WaveFormatExtensible(sampleRate, 32, channels, AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT, 32, 0)
                : WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            float[] samples = new float[frames * channels];
            Array.Fill(samples, 0.2f);
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, format, MemoryMarshal.AsBytes(samples.AsSpan()));

            WaveProcessingResult result = WaveFileProcessor.Process(inputPath, outputPath, bufferFrames: 127);

            using var output = new WaveFileReader(outputPath);
            Assert.Equal(format.Encoding, output.WaveFormat.Encoding);
            Assert.Equal(32, output.WaveFormat.BitsPerSample);
            Assert.Equal(sampleRate, output.WaveFormat.SampleRate);
            Assert.Equal(channels, output.WaveFormat.Channels);
            Assert.Equal((long)samples.Length * sizeof(float), output.Length);
            Assert.Equal(frames, result.FrameCount);
            if (frames <= 5)
                Assert.All(ReadSamples(output), sample => Assert.Equal(0.0f, sample));
        }

        /// <summary>
        /// Compares a stereo transient clip with the capture-order DSP result to catch mismatched original-frame
        /// handoff for quiet gain, frame shifts, or loss of duration from compensating for lookahead.
        /// </summary>
        [Fact]
        public void Process_StereoTransients_MatchesCaptureDspWithAlignedOutput()
        {
            const int sampleRate = 44100;
            const int channels = 2;
            float[] input = CreateStimulus();
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels), MemoryMarshal.AsBytes(input.AsSpan()));

            WaveProcessingResult result = WaveFileProcessor.Process(inputPath, outputPath, bufferFrames: 73);

            float[] delayed = (float[])input.Clone();
            new AdaptiveDynamicsProcessor().Process(delayed, channels, sampleRate);
            new LookaheadTransientController().Process(delayed, input, channels, sampleRate);
            int delaySamples = (int)Math.Ceiling(sampleRate * 12 / 1000.0) * channels;
            float[] expected = new float[input.Length];
            delayed.AsSpan(delaySamples).CopyTo(expected);

            using var output = new WaveFileReader(outputPath);
            float[] actual = ReadSamples(output);
            Assert.Equal(expected, actual);
            Assert.NotEqual(0.0f, actual[0]);
            Assert.All(actual[^delaySamples..], sample => Assert.Equal(0.0f, sample));
            Assert.Equal((double)input.Max(Math.Abs), result.InputPeak);
            Assert.Equal((double)expected.Max(Math.Abs), result.OutputPeak);
            Assert.Equal(Math.Sqrt(input.Average(sample => (double)sample * sample)), result.InputRms, 12);
            Assert.Equal(Math.Sqrt(expected.Average(sample => (double)sample * sample)), result.OutputRms, 12);
        }

        /// <summary>
        /// Processes one clip in both a single block and small blocks, expecting byte-identical WAVs and statistics.
        /// This prevents resets of lookahead, transient hold, or dynamics at buffer boundaries.
        /// </summary>
        [Fact]
        public void Process_SmallBlocks_MatchOneBlock()
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string continuousPath = Path.Combine(_directory.FullName, "continuous.wav");
            string splitPath = Path.Combine(_directory.FullName, "split.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), MemoryMarshal.AsBytes(CreateStimulus().AsSpan()));

            WaveProcessingResult continuous = WaveFileProcessor.Process(inputPath, continuousPath, bufferFrames: 32000);
            WaveProcessingResult split = WaveFileProcessor.Process(inputPath, splitPath, bufferFrames: 128);

            Assert.Equal(continuous, split);
            Assert.Equal(File.ReadAllBytes(continuousPath), File.ReadAllBytes(splitPath));
        }

        /// <summary>
        /// Runs the same input and default settings twice to catch state leaking between independent experiments.
        /// </summary>
        [Fact]
        public void Process_IdenticalRuns_ProduceIdenticalFilesAndStatistics()
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string firstPath = Path.Combine(_directory.FullName, "first.wav");
            string secondPath = Path.Combine(_directory.FullName, "second.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), MemoryMarshal.AsBytes(CreateStimulus().AsSpan()));

            Assert.Equal(WaveFileProcessor.Process(inputPath, firstPath), WaveFileProcessor.Process(inputPath, secondPath));
            Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
        }

        /// <summary>
        /// Checks that 32-bit integer PCM is rejected before creating a destination, preventing PCM bits from being decoded as floats.
        /// </summary>
        /// <param name="extensible">Whether the PCM fixture has an extensible header with an integer subtype.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Process_IntegerPcm_RejectsInputWithoutCreatingOutput(bool extensible)
        {
            string inputPath = Path.Combine(_directory.FullName, "integer.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WaveFormat format = extensible ? new WaveFormatExtensible(48000, 32, 2, AudioMediaSubtypes.MEDIASUBTYPE_PCM, 32, 0) : new WaveFormat(48000, 32, 2);
            WriteWave(inputPath, format, new byte[8]);

            Assert.Throws<NotSupportedException>(() => WaveFileProcessor.Process(inputPath, outputPath));
            Assert.False(File.Exists(outputPath));
        }

        /// <summary>
        /// Checks that missing input and destination directories fail without creating files or changing the input.
        /// </summary>
        [Fact]
        public void Process_InvalidPaths_FailsWithoutCreatingOutput()
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            Assert.Throws<FileNotFoundException>(() => WaveFileProcessor.Process(inputPath, outputPath));
            Assert.False(File.Exists(outputPath));

            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), new byte[4]);
            byte[] original = File.ReadAllBytes(inputPath);
            string missingDirectory = Path.Combine(_directory.FullName, "missing");
            Assert.Throws<DirectoryNotFoundException>(() => WaveFileProcessor.Process(inputPath, Path.Combine(missingDirectory, "output.wav")));
            Assert.False(Directory.Exists(missingDirectory));
            Assert.Equal(original, File.ReadAllBytes(inputPath));
        }

        /// <summary>
        /// Verifies that the processor rejects both an alias of the input path and an existing destination without truncating either file.
        /// </summary>
        [Fact]
        public void Process_ExistingFiles_LeavesInputAndDestinationUntouched()
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string inputAlias = Path.Combine(_directory.FullName, ".", "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), new byte[4]);
            byte[] original = File.ReadAllBytes(inputPath);
            byte[] existing = [1, 2, 3];
            File.WriteAllBytes(outputPath, existing);

            Assert.ThrowsAny<IOException>(() => WaveFileProcessor.Process(inputPath, inputAlias));
            Assert.ThrowsAny<IOException>(() => WaveFileProcessor.Process(inputPath, outputPath));
            Assert.Equal(original, File.ReadAllBytes(inputPath));
            Assert.Equal(existing, File.ReadAllBytes(outputPath));
        }

        /// <summary>
        /// Checks settled quiet and loud signals against their known DSP gain to catch incorrect RMS, peak, or dB reporting.
        /// </summary>
        /// <param name="level">The constant amplitude in the input WAV.</param>
        /// <param name="expectedChangeDb">The expected settled change, with a negative value for attenuation.</param>
        [Theory]
        [InlineData(0.01f, 6.0)]
        [InlineData(0.5f, -13.201877)]
        public void Process_IntervalWithKnownLevel_ReportsExpectedRmsPeakAndChanges(float level, double expectedChangeDb)
        {
            const int sampleRate = 1000;
            float[] samples = new float[6000];
            Array.Fill(samples, level);
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1), MemoryMarshal.AsBytes(samples.AsSpan()));

            var (_, intervals) = WaveFileProcessor.Process(inputPath, outputPath, [new("steady", 4m, 5m)], bufferFrames: 37);

            IntervalMeasurement interval = Assert.Single(intervals);
            Assert.False(interval.IsTruncated);
            Assert.Equal((double)level, interval.InputRms!.Value, 9);
            Assert.Equal((double)level, interval.InputPeak);
            double expectedOutput = level * Math.Pow(10.0, expectedChangeDb / 20.0);
            Assert.Equal(expectedOutput, interval.OutputRms!.Value, 5);
            Assert.Equal(expectedOutput, interval.OutputPeak!.Value, 5);
            Assert.InRange(interval.RmsDifferenceDb!.Value, expectedChangeDb - 0.001, expectedChangeDb + 0.001);
            Assert.InRange(interval.PeakDifferenceDb!.Value, expectedChangeDb - 0.001, expectedChangeDb + 0.001);
        }

        /// <summary>
        /// Checks exact and fractional boundaries across blocks to prevent neighboring frames from changing interval statistics.
        /// </summary>
        [Fact]
        public void Process_IntervalBoundaries_SelectFramesWithTimestampsInHalfOpenRange()
        {
            float[] samples = new float[2000];
            Array.Fill(samples, 0.07f);
            samples[1000] = 0.04f;
            samples[1001] = -0.08f;
            samples[1002] = 0.12f;
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(1000, 1), MemoryMarshal.AsBytes(samples.AsSpan()));
            TimeInterval[] requested = [new("fractional", 0.9995m, 1.0015m), new("adjacent", 1.002m, 1.003m), new("subframe", 1.0031m, 1.0032m)];

            var (_, intervals) = WaveFileProcessor.Process(inputPath, outputPath, requested, bufferFrames: 7);

            Assert.Equal(requested, intervals.Select(interval => interval.Interval));
            Assert.Equal((1000L, 1002L), (intervals[0].StartFrame, intervals[0].EndFrame));
            double expectedRms = Math.Sqrt(((double)samples[1000] * samples[1000] + (double)samples[1001] * samples[1001]) / 2.0);
            Assert.Equal(expectedRms, intervals[0].InputRms);
            Assert.Equal(expectedRms, intervals[0].OutputRms);
            Assert.Equal((double)0.08f, intervals[0].InputPeak);
            Assert.Equal((double)0.08f, intervals[0].OutputPeak);
            Assert.Equal((1002L, 1003L), (intervals[1].StartFrame, intervals[1].EndFrame));
            Assert.Equal((double)0.12f, intervals[1].InputPeak);
            Assert.Equal((1004L, 1004L), (intervals[2].StartFrame, intervals[2].EndFrame));
            Assert.False(intervals[2].IsTruncated);
            Assert.Null(intervals[2].InputRms);
            Assert.Null(intervals[2].OutputRms);
        }

        /// <summary>
        /// Compares a stereo impact on its original frame and clips both sides at the alignment cutoff,
        /// preventing delayed samples or synthetic tail silence from biasing the report.
        /// </summary>
        [Fact]
        public void Process_IntervalsWithLookahead_AlignsStereoAndExcludesPaddedTail()
        {
            const int sampleRate = 44100;
            const int channels = 2;
            const int frameCount = 1400;
            const int impact = 441;
            const int comparableFrames = frameCount - 530;
            float[] samples = new float[frameCount * channels];
            samples[impact * channels] = 0.8f;
            samples[impact * channels + 1] = -0.5f;
            samples[comparableFrames * channels] = 0.95f;
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels), MemoryMarshal.AsBytes(samples.AsSpan()));
            TimeInterval[] requested = [new("impact", 0.01m, 0.01001m), new("crosses tail", 0.01m, 1m), new("only tail", 0.025m, 0.03m)];

            var (_, intervals) = WaveFileProcessor.Process(inputPath, outputPath, requested, bufferFrames: 17);

            using var reader = new WaveFileReader(outputPath);
            float[] processed = ReadSamples(reader);
            double left = processed[impact * channels];
            double right = processed[impact * channels + 1];
            Assert.InRange(left, 0.01, 0.79);
            Assert.Equal((impact, impact + 1L), (intervals[0].StartFrame, intervals[0].EndFrame));
            Assert.False(intervals[0].IsTruncated);
            Assert.Equal((double)0.8f, intervals[0].InputPeak);
            Assert.Equal(left, intervals[0].OutputPeak);
            Assert.Equal(Math.Sqrt((left * left + right * right) / channels), intervals[0].OutputRms);

            Assert.True(intervals[1].IsTruncated);
            Assert.Equal((impact, (long)comparableFrames), (intervals[1].StartFrame, intervals[1].EndFrame));
            Assert.Equal((double)0.8f, intervals[1].InputPeak);
            Assert.Equal(left, intervals[1].OutputPeak);
            double comparedSamples = (comparableFrames - impact) * channels;
            Assert.Equal(intervals[0].OutputRms!.Value * Math.Sqrt(channels / comparedSamples), intervals[1].OutputRms!.Value, 12);
            Assert.Equal(intervals[0].InputRms!.Value * Math.Sqrt(channels / comparedSamples), intervals[1].InputRms!.Value, 12);

            Assert.True(intervals[2].IsTruncated);
            Assert.Equal(((long)comparableFrames, (long)comparableFrames), (intervals[2].StartFrame, intervals[2].EndFrame));
            Assert.Null(intervals[2].InputPeak);
            Assert.Null(intervals[2].OutputPeak);
            Assert.Null(intervals[2].RmsDifferenceDb);
        }

        /// <summary>
        /// Checks an interval of real silence, which must show zero levels but no finite dB ratio.
        /// </summary>
        [Fact]
        public void Process_SilentInterval_ReportsZerosAndUnavailableChanges()
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(1000, 1), new byte[1000 * sizeof(float)]);

            var (_, intervals) = WaveFileProcessor.Process(inputPath, outputPath, [new("silence", 0m, 0.5m)]);

            IntervalMeasurement interval = Assert.Single(intervals);
            Assert.False(interval.IsTruncated);
            Assert.Equal(0.0, interval.InputRms);
            Assert.Equal(0.0, interval.InputPeak);
            Assert.Equal(0.0, interval.OutputRms);
            Assert.Equal(0.0, interval.OutputPeak);
            Assert.Null(interval.RmsDifferenceDb);
            Assert.Null(interval.PeakDifferenceDb);
        }

        /// <summary>
        /// Checks that a zero level on either side prevents a misleading infinite change in the report.
        /// </summary>
        /// <param name="input">The measured input amplitude.</param>
        /// <param name="output">The measured output amplitude.</param>
        [Theory]
        [InlineData(0.0, 0.1)]
        [InlineData(0.1, 0.0)]
        public void IntervalMeasurement_OneSilentSide_DoesNotReportDecibelRatio(double input, double output)
        {
            var measurement = new IntervalMeasurement(new TimeInterval("silence", 0m, 1m), 0, 1, false, input, input, output, output);

            Assert.Null(measurement.RmsDifferenceDb);
            Assert.Null(measurement.PeakDifferenceDb);
        }

        /// <summary>
        /// Checks that malformed range requests identify the problem and never create a destination.
        /// </summary>
        /// <param name="name">The requested label.</param>
        /// <param name="start">The requested start in seconds.</param>
        /// <param name="end">The requested end in seconds.</param>
        /// <param name="message">The expected diagnostic text.</param>
        [Theory]
        [InlineData("", 0, 1, "nonempty name")]
        [InlineData("negative", -1, 1, "0 <= start < end")]
        [InlineData("equal", 0, 0, "0 <= start < end")]
        [InlineData("reversed", 1, 0, "0 <= start < end")]
        [InlineData("too late", 1, 2, "end of the input WAV")]
        public void Process_InvalidInterval_ReportsProblemWithoutCreatingOutput(string name, int start, int end, string message)
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(1000, 1), new byte[1000 * sizeof(float)]);

            ArgumentException error = Assert.Throws<ArgumentException>(() => WaveFileProcessor.Process(inputPath, outputPath, [new(name, start, end)]));

            Assert.Contains(message, error.Message);
            Assert.False(File.Exists(outputPath));
        }

        /// <summary>
        /// Rejects labels that differ only by case so reports cannot contain ambiguous names.
        /// </summary>
        [Fact]
        public void Process_DuplicateIntervalNames_RejectsAmbiguousReports()
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string outputPath = Path.Combine(_directory.FullName, "output.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(1000, 1), new byte[1000 * sizeof(float)]);
            TimeInterval[] requested = [new("Speech", 0m, 0.4m), new("speech", 0.1m, 0.5m)];

            ArgumentException error = Assert.Throws<ArgumentException>(() => WaveFileProcessor.Process(inputPath, outputPath, requested));

            Assert.Contains("used more than once", error.Message);
            Assert.False(File.Exists(outputPath));
        }

        /// <summary>
        /// Runs the same clip with and without overlapping diagnostics to catch changes to export bytes or whole-file statistics.
        /// </summary>
        [Fact]
        public void Process_WithIntervals_ProducesIdenticalOutputAndWholeFileStatistics()
        {
            string inputPath = Path.Combine(_directory.FullName, "input.wav");
            string plainPath = Path.Combine(_directory.FullName, "plain.wav");
            string measuredPath = Path.Combine(_directory.FullName, "measured.wav");
            WriteWave(inputPath, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), MemoryMarshal.AsBytes(CreateStimulus().AsSpan()));
            TimeInterval[] requested = [new("first", 0m, 0.05m), new("second", 0.01m, 0.3m), new("tail", 0.5m, 0.6m)];

            WaveProcessingResult plain = WaveFileProcessor.Process(inputPath, plainPath, bufferFrames: 127);
            var (measured, intervals) = WaveFileProcessor.Process(inputPath, measuredPath, requested, bufferFrames: 127);

            Assert.Equal(requested.Length, intervals.Count);
            Assert.Equal(plain, measured);
            Assert.Equal(File.ReadAllBytes(plainPath), File.ReadAllBytes(measuredPath));
        }

        /// <summary>
        /// Removes the isolated fixture directory after xUnit finishes the test.
        /// </summary>
        public void Dispose() => _directory.Delete(recursive: true);

        /// <summary>
        /// Builds quiet stereo audio with differently weighted impacts to exercise linked gain and limiter state.
        /// </summary>
        /// <returns>Interleaved stereo samples with impacts near block boundaries and during recovery.</returns>
        private static float[] CreateStimulus()
        {
            float[] samples = new float[28000 * 2];
            Array.Fill(samples, 0.01f);
            samples[127 * 2] = 1.4f;
            samples[127 * 2 + 1] = -0.5f;
            samples[3200 * 2 + 1] = -1.1f;
            samples[19510 * 2] = 0.8f;
            return samples;
        }

        /// <summary>
        /// Writes raw fixture samples without scaling so test inputs exercise the intended amplitudes and format tags.
        /// </summary>
        /// <param name="path">The fixture destination.</param>
        /// <param name="format">The format to record in the WAV header.</param>
        /// <param name="sampleBytes">The raw interleaved payload in the specified format.</param>
        private static void WriteWave(string path, WaveFormat format, ReadOnlySpan<byte> sampleBytes)
        {
            using var writer = new WaveFileWriter(path, format);
            writer.Write(sampleBytes);
        }

        /// <summary>
        /// Reads the complete float payload for exact sample comparisons rather than converting or resampling it.
        /// </summary>
        /// <param name="reader">The WAV reader positioned at the start of the payload.</param>
        /// <returns>The decoded interleaved samples in file order.</returns>
        private static float[] ReadSamples(WaveFileReader reader)
        {
            float[] samples = new float[checked((int)(reader.Length / sizeof(float)))];
            reader.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
            return samples;
        }
    }
}

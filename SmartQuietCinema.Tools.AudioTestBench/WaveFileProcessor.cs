using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SmartQuietCinema.Core.DSP;

namespace SmartQuietCinema.Tools.AudioTestBench
{
    /// <summary>
    /// Describes the matched WAV formats, duration, and linear signal levels for an offline comparison.
    /// </summary>
    /// <param name="SampleRate">The sample rate shared by both files, in Hertz.</param>
    /// <param name="Channels">The number of interleaved channels in each frame.</param>
    /// <param name="FrameCount">The number of frames in each file, including output tail padding.</param>
    /// <param name="InputRms">The RMS amplitude across finite input samples.</param>
    /// <param name="InputPeak">The maximum absolute amplitude across finite input samples.</param>
    /// <param name="OutputRms">The RMS amplitude of the aligned output, including tail padding.</param>
    /// <param name="OutputPeak">The maximum absolute amplitude of the aligned output.</param>
    public readonly record struct WaveProcessingResult(int SampleRate, int Channels, long FrameCount,
        double InputRms, double InputPeak, double OutputRms, double OutputPeak);

    /// <summary>
    /// Applies the Windows capture DSP order to float WAV files without requiring audio hardware.
    /// </summary>
    public static class WaveFileProcessor
    {
        /// <summary>
        /// Processes complete WAV frames with one instance of each DSP stage and writes an aligned comparison file.
        /// </summary>
        /// <remarks>
        /// Discards the transient controller's leading delay and replaces the unavailable tail with silence.
        /// Files no longer than the delay are entirely silent but keep their original duration.
        /// </remarks>
        /// <param name="inputPath">The existing 32-bit IEEE float WAV file.</param>
        /// <param name="outputPath">The new file path. Existing files, including the input, are never overwritten.</param>
        /// <param name="bufferFrames">The processing block size in frames; changing it must not change the result.</param>
        /// <returns>Format, frame count, and signal levels for the input and exported output.</returns>
        public static WaveProcessingResult Process(string inputPath, string outputPath, int bufferFrames = 4096)
            => Process(inputPath, outputPath, [], bufferFrames).Wave;

        /// <summary>
        /// Processes a WAV using the original frames for lookahead quiet gain and optionally measures aligned intervals.
        /// </summary>
        /// <param name="inputPath">The existing 32-bit IEEE float WAV file.</param>
        /// <param name="outputPath">The destination path, which must not already exist.</param>
        /// <param name="intervals">The requested half-open time intervals, in reporting order.</param>
        /// <param name="bufferFrames">The number of frames to process per block.</param>
        /// <returns>Whole-file statistics and interval comparisons limited to non-padded frames.</returns>
        public static (WaveProcessingResult Wave, IReadOnlyList<IntervalMeasurement> Intervals) Process(
            string inputPath, string outputPath, IReadOnlyList<TimeInterval> intervals, int bufferFrames = 4096)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ArgumentNullException.ThrowIfNull(intervals);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferFrames);
            if (!BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("Raw WAV processing requires a little-endian runtime.");

            using var inputStream = File.OpenRead(inputPath);
            using var reader = new WaveFileReader(inputStream);
            WaveFormat format = reader.WaveFormat;
            int channels = format.Channels;
            int sampleRate = format.SampleRate;
            if (!IsFloat32(format) || sampleRate <= 0 || channels <= 0 || format.BlockAlign != channels * sizeof(float))
                throw new NotSupportedException("Input must be a 32-bit IEEE float WAV with complete interleaved frames.");
            if (reader.Length % format.BlockAlign != 0)
                throw new InvalidDataException("The WAV data ends in an incomplete audio frame.");

            var dynamics = new AdaptiveDynamicsProcessor();
            var transientController = new LookaheadTransientController();
            transientController.Process(Span<float>.Empty, channels, sampleRate);

            long totalSamples = reader.Length / sizeof(float);
            long remainingSamples = totalSamples;
            int samplesToSkip = checked(transientController.LatencyFrames * channels);
            int tailSamples = (int)Math.Min(totalSamples, samplesToSkip);
            long totalFrames = totalSamples / channels;
            long comparableFrames = totalFrames - tailSamples / channels;
            IntervalLevels[] intervalLevels = CreateIntervalLevels(intervals, channels, sampleRate, totalFrames, comparableFrames);
            float[] buffer = new float[checked(bufferFrames * channels)];
            float[] originalBuffer = new float[buffer.Length];
            var inputLevels = new SignalLevels();
            var outputLevels = new SignalLevels();
            long alignedSamplesWritten = 0;

            using var outputStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new WaveFileWriter(outputStream, format);
            while (remainingSamples > 0)
            {
                Span<float> samples = buffer.AsSpan(0, (int)Math.Min(remainingSamples, buffer.Length));
                reader.ReadExactly(MemoryMarshal.AsBytes(samples));
                foreach (IntervalLevels interval in intervalLevels)
                    interval.Add(samples, totalSamples - remainingSamples, isOutput: false);
                remainingSamples -= samples.Length;
                inputLevels.Add(samples);

                Span<float> original = originalBuffer.AsSpan(0, samples.Length);
                samples.CopyTo(original);
                dynamics.Process(samples, channels, sampleRate);
                transientController.Process(samples, original, channels, sampleRate);

                int skipped = Math.Min(samplesToSkip, samples.Length);
                samplesToSkip -= skipped;
                Span<float> aligned = samples[skipped..];
                writer.Write(MemoryMarshal.AsBytes(aligned));
                outputLevels.Add(aligned);
                // After skipping lookahead, the first written sample is on frame zero of the original timeline.
                foreach (IntervalLevels interval in intervalLevels)
                    interval.Add(aligned, alignedSamplesWritten, isOutput: true);
                alignedSamplesWritten += aligned.Length;
            }

            buffer.AsSpan(0, Math.Min(tailSamples, buffer.Length)).Clear();
            while (tailSamples > 0)
            {
                Span<float> padding = buffer.AsSpan(0, Math.Min(tailSamples, buffer.Length));
                writer.Write(MemoryMarshal.AsBytes(padding));
                outputLevels.Add(padding);
                tailSamples -= padding.Length;
            }

            var wave = new WaveProcessingResult(sampleRate, channels, totalFrames, inputLevels.Rms, inputLevels.Peak, outputLevels.Rms, outputLevels.Peak);
            return (wave, Array.ConvertAll(intervalLevels, static interval => interval.ToMeasurement()));
        }

        /// <summary>
        /// Validates requested ranges before creating the output and maps them to comparable frame indices.
        /// </summary>
        /// <param name="intervals">The named requests in user-specified order.</param>
        /// <param name="channels">The number of samples per frame.</param>
        /// <param name="sampleRate">The number of frames per second.</param>
        /// <param name="totalFrames">The number of original input frames.</param>
        /// <param name="comparableFrames">The number of output frames backed by actual input, excluding padding.</param>
        /// <returns>Interval accumulators with both sides bounded to the same timeline.</returns>
        private static IntervalLevels[] CreateIntervalLevels(IReadOnlyList<TimeInterval> intervals, int channels, int sampleRate,
            long totalFrames, long comparableFrames)
        {
            if (intervals.Count == 0)
                return [];

            decimal inputDuration = (decimal)totalFrames / sampleRate;
            decimal comparableDuration = (decimal)comparableFrames / sampleRate;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new IntervalLevels[intervals.Count];
            for (int index = 0; index < intervals.Count; index++)
            {
                TimeInterval interval = intervals[index];
                if (string.IsNullOrWhiteSpace(interval.Name))
                    throw new ArgumentException("Every interval must have a nonempty name.");
                if (!names.Add(interval.Name))
                    throw new ArgumentException($"Interval name '{interval.Name}' is used more than once.");
                if (interval.StartSeconds < 0 || interval.EndSeconds <= interval.StartSeconds)
                    throw new ArgumentException($"Interval '{interval.Name}' must satisfy 0 <= start < end.");
                if (interval.StartSeconds >= inputDuration)
                    throw new ArgumentException($"Interval '{interval.Name}' starts at or beyond the end of the input WAV.");

                // Frame timestamps in [start, end) correspond to rounding both boundaries up.
                long startFrame = interval.StartSeconds >= comparableDuration
                    ? comparableFrames : Math.Min(comparableFrames, (long)decimal.Ceiling(interval.StartSeconds * sampleRate));
                bool truncated = interval.EndSeconds > comparableDuration;
                long endFrame = truncated ? comparableFrames : Math.Min(comparableFrames, (long)decimal.Ceiling(interval.EndSeconds * sampleRate));
                result[index] = new IntervalLevels(interval, startFrame, endFrame, channels, truncated);
            }

            return result;
        }

        /// <summary>
        /// Accumulates overlapping portions of input and aligned output blocks for one requested interval.
        /// </summary>
        /// <param name="interval">The interval as requested, for reporting.</param>
        /// <param name="startFrame">The first included frame after clipping to comparable audio.</param>
        /// <param name="endFrame">The exclusive end frame after clipping to comparable audio.</param>
        /// <param name="channels">The number of interleaved samples per frame.</param>
        /// <param name="truncated">Whether part of the requested interval falls beyond comparable audio.</param>
        private sealed class IntervalLevels(TimeInterval interval, long startFrame, long endFrame, int channels, bool truncated)
        {
            private readonly long _startSample = startFrame * channels;
            private readonly long _endSample = endFrame * channels;
            private SignalLevels _input;
            private SignalLevels _output;

            /// <summary>
            /// Adds the intersection with a block without retaining or modifying its samples.
            /// </summary>
            /// <param name="samples">The original or aligned block.</param>
            /// <param name="blockStart">The first sample index on the original timeline, across all channels.</param>
            /// <param name="isOutput">Whether these samples belong to the aligned export.</param>
            public void Add(ReadOnlySpan<float> samples, long blockStart, bool isOutput)
            {
                long first = Math.Max(blockStart, _startSample);
                long end = Math.Min(blockStart + samples.Length, _endSample);
                if (end <= first)
                    return;

                ReadOnlySpan<float> overlap = samples.Slice((int)(first - blockStart), (int)(end - first));
                if (isOutput)
                    _output.Add(overlap);
                else
                    _input.Add(overlap);
            }

            /// <summary>
            /// Reports the measured bounds and distinguishes unavailable levels from measured silence.
            /// </summary>
            /// <returns>The original request, comparable frame range, and paired linear levels.</returns>
            public IntervalMeasurement ToMeasurement() => new(interval, startFrame, endFrame, truncated,
                _input.HasSamples ? _input.Rms : null, _input.HasSamples ? _input.Peak : null,
                _output.HasSamples ? _output.Rms : null, _output.HasSamples ? _output.Peak : null);
        }

        /// <summary>
        /// Checks both float WAV header forms to prevent interpreting integer bytes as audio floats.
        /// </summary>
        /// <param name="format">The decoded WAV format, which may expose extensible data as raw bytes.</param>
        /// <returns>Whether the payload stores 32-bit IEEE float samples.</returns>
        private static bool IsFloat32(WaveFormat format)
        {
            if (format.BitsPerSample != 32)
                return false;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                return true;
            if (format.Encoding != WaveFormatEncoding.Extensible)
                return false;

            return format switch
            {
                WaveFormatExtensible extensible => extensible.ValidBitsPerSample == 32 && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT,
                WaveFormatExtraData extra when extra.ExtraData.Length >= 22 => BinaryPrimitives.ReadUInt16LittleEndian(extra.ExtraData) == 32
                    && new Guid(extra.ExtraData.AsSpan(6, 16)) == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT,
                _ => false
            };
        }

        /// <summary>
        /// Measures input and exported samples independently so alignment and nonfinite input do not bias output levels.
        /// </summary>
        private struct SignalLevels
        {
            private long _sampleCount;
            private double _sumSquares;

            public bool HasSamples => _sampleCount > 0;
            public double Peak { get; private set; }
            public double Rms => _sampleCount == 0 ? 0.0 : Math.Sqrt(_sumSquares / _sampleCount);

            /// <summary>
            /// Accumulates finite sample amplitudes to compute file-wide peak and RMS without storing the waveform.
            /// </summary>
            /// <param name="samples">A block of interleaved samples from one file.</param>
            public void Add(ReadOnlySpan<float> samples)
            {
                foreach (float sample in samples)
                {
                    if (!float.IsFinite(sample))
                        continue;

                    double amplitude = Math.Abs((double)sample);
                    Peak = Math.Max(Peak, amplitude);
                    _sumSquares += amplitude * amplitude;
                    _sampleCount++;
                }
            }
        }
    }
}

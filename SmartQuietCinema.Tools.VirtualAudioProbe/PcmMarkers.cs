using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>
/// Generates and recognizes two distinct stereo markers to distinguish real mixed PCM from a sample driver's tone.
/// </summary>
internal static class PcmMarkers
{
    private const int SysVadLoopbackToneHz = 3000;
    internal static readonly int[] Frequencies = [688, 1104, 1424, 1936, SysVadLoopbackToneHz];
    private const int StereoChannels = 2;
    private const double MinimumProjectionAmplitude = 0.0002;
    private const double Amplitude = 0.08;
    private const int RequiredWindows = 4;

    /// <summary>
    /// Generates one interleaved sample with phase determined by frame number, independently of render packet sizes.
    /// </summary>
    /// <param name="marker">Zero for the first producer, one for the second.</param>
    /// <param name="channel">Zero for left, one for right.</param>
    /// <param name="frame">The frame number within the producer's stream.</param>
    /// <param name="sampleRate">The producer's declared frames per second.</param>
    /// <returns>The original unprocessed marker sample.</returns>
    public static float Sample(int marker, int channel, long frame, int sampleRate)
        => (float)(Amplitude * Math.Sin(2 * Math.PI * Frequencies[marker * StereoChannels + channel] * (frame % sampleRate) / sampleRate));

    /// <summary>
    /// Uses phase-independent spectral projections to demand both producers in the same consecutive time windows,
    /// with each marker stronger in its intended channel than in the opposite channel.
    /// </summary>
    /// <param name="samples">Decoded interleaved stereo capture, with silence represented by zeros.</param>
    /// <param name="sampleRate">The actual capture rate, which can differ from the producers' rates.</param>
    /// <returns>Peak, individual and joint window counts, plus the longest uninterrupted joint detection.</returns>
    public static Result Analyze(ReadOnlySpan<float> samples, int sampleRate)
    {
        const int WindowsPerSecond = 8;
        const double StockToneRmsFraction = 0.10;

        int windowFrames = sampleRate / WindowsPerSecond;
        int frames = samples.Length / StereoChannels;
        int first = 0, second = 0, both = 0, streak = 0, longest = 0, stockTone = 0;
        double peak = 0;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));

        var cosine = new double[Frequencies.Length, windowFrames];
        var sine = new double[Frequencies.Length, windowFrames];
        double weightSum = 0;
        for (int i = 0; i < windowFrames; i++)
        {
            double weight = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / windowFrames);
            weightSum += weight;
            for (int frequency = 0; frequency < Frequencies.Length; frequency++)
            {
                double phase = 2 * Math.PI * Frequencies[frequency] * i / sampleRate;
                cosine[frequency, i] = weight * Math.Cos(phase);
                sine[frequency, i] = weight * Math.Sin(phase);
            }
        }

        // Each of the five frequencies uses consecutive left and right slots; keep the fixed stack storage in sync with that layout.
        Span<double> levels = stackalloc double[10];
        for (int start = 0; start + windowFrames <= frames; start += windowFrames)
        {
            double leftEnergy = 0, rightEnergy = 0;
            for (int i = 0; i < windowFrames; i++)
            {
                double left = samples[StereoChannels * (start + i)], right = samples[StereoChannels * (start + i) + 1];
                leftEnergy += left * left;
                rightEnergy += right * right;
            }
            double leftRms = Math.Sqrt(leftEnergy / windowFrames), rightRms = Math.Sqrt(rightEnergy / windowFrames);
            for (int frequency = 0; frequency < Frequencies.Length; frequency++)
            {
                double leftCos = 0, leftSin = 0, rightCos = 0, rightSin = 0;
                for (int i = 0; i < windowFrames; i++)
                {
                    double left = samples[StereoChannels * (start + i)], right = samples[StereoChannels * (start + i) + 1];
                    leftCos += left * cosine[frequency, i];
                    leftSin += left * sine[frequency, i];
                    rightCos += right * cosine[frequency, i];
                    rightSin += right * sine[frequency, i];
                }
                levels[StereoChannels * frequency] = 2 * Math.Sqrt(leftCos * leftCos + leftSin * leftSin) / weightSum;
                levels[StereoChannels * frequency + 1] = 2 * Math.Sqrt(rightCos * rightCos + rightSin * rightSin) / weightSum;
            }

            // Slots are left/right for source A's two markers, source B's two markers, then the stock SysVAD tone.
            bool foundFirst = Matches(levels[0], levels[1], leftRms) && Matches(levels[3], levels[2], rightRms);
            bool foundSecond = Matches(levels[4], levels[5], leftRms) && Matches(levels[7], levels[6], rightRms);
            if (levels[8] >= Math.Max(MinimumProjectionAmplitude, leftRms * StockToneRmsFraction) || levels[9] >= Math.Max(MinimumProjectionAmplitude, rightRms * StockToneRmsFraction)) stockTone++;
            if (foundFirst) first++;
            if (foundSecond) second++;
            if (foundFirst && foundSecond) both++;
            streak = foundFirst && foundSecond ? streak + 1 : 0;
            longest = Math.Max(longest, streak);
        }
        return new(peak, first, second, both, longest, stockTone);
    }

    /// <summary>Rejects weak projections and channel leakage while allowing the engine to change stream gain.</summary>
    private static bool Matches(double intended, double opposite, double rms)
    {
        const double MinimumMarkerRmsFraction = 0.30;
        const int MinimumChannelSeparation = 3;
        return intended >= Math.Max(MinimumProjectionAmplitude, Math.Max(rms * MinimumMarkerRmsFraction, opposite * MinimumChannelSeparation));
    }

    /// <summary>
    /// Stores marker evidence on the actual capture timeline. Joint windows prevent sequential signals from passing.
    /// </summary>
    internal readonly record struct Result(double Peak, int FirstWindows, int SecondWindows, int JointWindows, int LongestJointRun, int StockToneWindows)
    {
        private const double SilencePeakFloor = 0.0001;

        public bool IsSilent => Peak < SilencePeakFloor;
        public bool Passed => LongestJointRun >= RequiredWindows && StockToneWindows == 0;
    }

    /// <summary>Provides one continuous deterministic stereo stream for an independent WASAPI render client.</summary>
    /// <param name="marker">The marker pair to render.</param>
    /// <param name="sampleRate">The source rate used for this render client.</param>
    internal sealed class Source(int marker, int sampleRate) : WaveProvider32(sampleRate, StereoChannels)
    {
        private long _samplePosition;

        /// <summary>
        /// Fills a render request with the marker and preserves its phase across requests.
        /// </summary>
        /// <param name="buffer">The requested span of interleaved destination samples.</param>
        /// <returns>The number of samples written.</returns>
        public override int Read(Span<float> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = Sample(marker, (int)(_samplePosition % StereoChannels), _samplePosition / StereoChannels, WaveFormat.SampleRate);
                _samplePosition++;
            }
            return buffer.Length;
        }

    }
}

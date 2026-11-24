namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Creates two deterministic reference signals and recovers source offsets from PCM independently of device timestamps.</summary>
internal static class SequenceAlignment
{
    internal const int Channels = 2;
    private const int ChipsPerSecond = 3500;
    private const float Amplitude = 0.07f;
    private const int CoarseStep = 4;
    private const int SampleStride = 8;
    private const int MinimumWindowFrames = 320;
    private const double MinimumCorrelation = 0.86;
    private const double MinimumRms = 0.0005;
    internal const int OffsetToleranceFrames = 3;

    /// <summary>Returns a smooth, aperiodic code sample with independent seeds for the left and right references.</summary>
    internal static float Sample(long frame, int rate, int channel)
    {
        long time = frame * ChipsPerSecond;
        uint chip = unchecked((uint)(time / rate));
        float phase = (float)(time % rate) / rate;
        float ease = phase * phase * (3 - 2 * phase);
        uint seed = channel == 0 ? 0xA4E74B93 : 0x66D7267B;
        float first = Chip(chip ^ seed);
        float next = Chip(unchecked(chip + 1) ^ seed);
        return Amplitude * (first + (next - first) * ease);
    }

    /// <summary>Hashes an absolute chip index so there is no repeating short waveform or dependence on render buffer boundaries.</summary>
    private static float Chip(uint value)
    {
        const uint Mix = 0x45D9F3B;
        value = unchecked(((value >> 16) ^ value) * Mix);
        value = unchecked(((value >> 16) ^ value) * Mix);
        const float SignedByteScale = 127.5f;
        return (byte)(value ^ (value >> 16)) / SignedByteScale - 1;
    }

    /// <summary>Generates a complete reference for one channel at the capture rate for independent offline alignment.</summary>
    internal static float[] Reference(int rate, int frames, int channel)
    {
        var result = new float[frames];
        for (int i = 0; i < result.Length; i++) result[i] = Sample(i, rate, channel);
        return result;
    }

    /// <summary>Finds a source offset via normalized correlation, tolerating gain and DC offset changes and allowing a coarse-to-fine search.</summary>
    /// <returns>A null frame when the window is short, silent, or has insufficient match confidence.</returns>
    internal static Match Find(ReadOnlySpan<float> interleaved, int channel, ReadOnlySpan<float> reference, int minimum, int maximum)
    {
        int frames = interleaved.Length / Channels;
        minimum = Math.Max(0, minimum);
        maximum = Math.Min(reference.Length - frames, maximum);
        if (frames < MinimumWindowFrames || minimum > maximum) return default;

        double sum = 0, energy = 0;
        int count = 0;
        for (int i = 0; i < frames; i += SampleStride)
        {
            float x = interleaved[Channels * i + channel];
            sum += x;
            energy += x * x;
            count++;
        }
        double centered = energy - sum * sum / count;
        if (centered < count * MinimumRms * MinimumRms) return default;

        int best = minimum;
        double score = -1;
        for (int i = minimum; i <= maximum; i += CoarseStep)
        {
            double candidate = Score(interleaved, channel, reference[i..], sum, centered, count);
            if (candidate > score) (score, best) = (candidate, i);
        }
        int start = Math.Max(minimum, best - CoarseStep + 1), end = Math.Min(maximum, best + CoarseStep - 1);
        for (int i = start; i <= end; i++)
        {
            double candidate = Score(interleaved, channel, reference[i..], sum, centered, count);
            if (candidate > score) (score, best) = (candidate, i);
        }
        return new(score >= MinimumCorrelation ? best : null, score);
    }

    /// <summary>Calculates DC-removed correlation for an offset without allocating or assuming equal gains.</summary>
    private static double Score(ReadOnlySpan<float> input, int channel, ReadOnlySpan<float> reference, double sum, double centered, int count)
    {
        double refSum = 0, refEnergy = 0, dot = 0;
        for (int i = 0; i < input.Length / Channels; i += SampleStride)
        {
            float value = reference[i];
            refSum += value;
            refEnergy += value * value;
            dot += input[Channels * i + channel] * value;
        }
        double variance = refEnergy - refSum * refSum / count;
        return variance > 0 ? (dot - sum * refSum / count) / Math.Sqrt(centered * variance) : 0;
    }

    /// <summary>Keeps content continuity separate from device-position continuity, using only two independently aligned PCM blocks.</summary>
    internal static string Classify(int? previous, int previousFrames, int? current, Int128? positionDelta)
    {
        if (previous is not int last || current is not int now) return "unresolved";
        int delta = now - last - previousFrames;
        if (Math.Abs(now - last) <= OffsetToleranceFrames) return "repeated-pcm-block";
        if (delta > OffsetToleranceFrames) return "forward-source-gap";
        if (delta < -OffsetToleranceFrames) return "overlapping-source-interval";
        return positionDelta is Int128 value && value != 0 ? "consecutive-pcm-position-mismatch" : "consecutive-pcm";
    }

    /// <summary>Combines channel-local transitions without assuming the two independently started render clients share an initial offset.</summary>
    internal static string Combine(string left, string right)
    {
        if (left == right) return left;
        if (left == "unresolved" || right == "unresolved") return IsAnomaly(left) || IsAnomaly(right) ? "single-channel-content-anomaly" : "unresolved";
        return "channel-disagreement";
    }

    /// <summary>Distinguishes an observed content anomaly from incomplete evidence and metadata-only disagreement.</summary>
    private static bool IsAnomaly(string value) => value is "repeated-pcm-block" or "forward-source-gap" or "overlapping-source-interval";

    /// <summary>Reports the best candidate and peak correlation in the requested search window.</summary>
    internal readonly record struct Match(int? Frame, double Correlation);
}

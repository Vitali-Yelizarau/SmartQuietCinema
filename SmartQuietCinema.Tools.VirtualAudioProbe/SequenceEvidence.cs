using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Writes source references and a position-independent alignment report for nonperiodic PCM.</summary>
internal static class SequenceEvidence
{
    /// <summary>Exports the two exact source voices and analyzes each captured packet against the capture-rate reference model.</summary>
    internal static bool Write(string prefix, int sourceRate, int captureRate, int seconds, float[] samples, ReadOnlySpan<PacketContinuity.Packet> packets)
    {
        WriteReference(prefix + "-source-left.wav", sourceRate, seconds, 0);
        WriteReference(prefix + "-source-right.wav", sourceRate, seconds, 1);
        float[] left = SequenceAlignment.Reference(captureRate, captureRate * seconds, 0);
        float[] right = SequenceAlignment.Reference(captureRate, captureRate * seconds, 1);
        using var writer = new StreamWriter(new FileStream(prefix + "-alignment.csv", FileMode.CreateNew));
        writer.WriteLine("packet_index,captured_start_frame,frames,position_frames,position_delta_frames,left_source_frame,right_source_frame,left_correlation,right_correlation,left_classification,right_classification,classification");
        int start = 0, previousFrames = 0, matched = 0, anomalies = 0, inconclusive = 0;
        int? previousLeft = null, previousRight = null;
        int searchRadius = captureRate / 4;
        for (int i = 0; i < packets.Length; i++)
        {
            var packet = packets[i];
            if (packet.Frames == 0) continue;
            if (packet.Frames > samples.Length / SequenceAlignment.Channels - start) break;
            var block = samples.AsSpan(start * SequenceAlignment.Channels, packet.Frames * SequenceAlignment.Channels);
            int? leftAnchor = previousLeft + previousFrames, rightAnchor = previousRight + previousFrames;
            var first = SequenceAlignment.Find(block, 0, left, leftAnchor - searchRadius ?? 0, leftAnchor + searchRadius ?? left.Length);
            var second = SequenceAlignment.Find(block, 1, right, rightAnchor - searchRadius ?? 0, rightAnchor + searchRadius ?? right.Length);
            string leftStatus = SequenceAlignment.Classify(previousLeft, previousFrames, first.Frame, packet.Delta);
            string rightStatus = SequenceAlignment.Classify(previousRight, previousFrames, second.Frame, packet.Delta);
            string classification = SequenceAlignment.Combine(leftStatus, rightStatus);
            if (classification is "consecutive-pcm" or "consecutive-pcm-position-mismatch") matched++;
            else if (classification == "unresolved") inconclusive++;
            else anomalies++;
            writer.WriteLine(FormattableString.Invariant($"{i},{start},{packet.Frames},{packet.Position},{packet.Delta},{first.Frame},{second.Frame},{first.Correlation:F6},{second.Correlation:F6},{leftStatus},{rightStatus},{classification}"));
            previousLeft = first.Frame;
            previousRight = second.Frame;
            previousFrames = packet.Frames;
            start += packet.Frames;
        }
        const int MinimumMatchedPairs = 4;
        const double MinimumMatchedFraction = 0.75;
        Console.WriteLine($"Sequence evidence: consecutive pairs={matched}, content anomalies={anomalies}, unresolved pairs={inconclusive}. See the alignment CSV and raw WAV files.");
        return matched >= MinimumMatchedPairs && matched >= MinimumMatchedFraction * (matched + inconclusive) && anomalies == 0;
    }

    /// <summary>Recreates one original stereo source voice with the unused channel silent.</summary>
    private static void WriteReference(string path, int rate, int seconds, int channel)
    {
        using var stream = new FileStream(path, FileMode.CreateNew);
        using var writer = new WaveFileWriter(stream, WaveFormat.CreateIeeeFloatWaveFormat(rate, SequenceAlignment.Channels));
        float[] samples = new float[rate * seconds * SequenceAlignment.Channels];
        _ = new Source(channel, rate).Read(samples);
        writer.WriteSamples(samples, 0, samples.Length);
    }

    /// <summary>Renders an independent aperiodic voice on one stereo channel for source-offset correlation.</summary>
    internal sealed class Source(int channel, int sampleRate) : WaveProvider32(sampleRate, SequenceAlignment.Channels)
    {
        private long _sample;

        /// <summary>Generates samples using absolute source frames even across odd-length render requests.</summary>
        public override int Read(Span<float> buffer)
        {
            int rate = WaveFormat.SampleRate;
            for (int i = 0; i < buffer.Length; i++, _sample++)
                buffer[i] = _sample % SequenceAlignment.Channels == channel ? SequenceAlignment.Sample(_sample / SequenceAlignment.Channels, rate, channel) : 0;
            return buffer.Length;
        }
    }
}

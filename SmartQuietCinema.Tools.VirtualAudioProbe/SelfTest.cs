using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Checks the PCM evidence rules offline without opening COM endpoints or audio streams.</summary>
internal static class SelfTest
{
    /// <summary>Runs deterministic positive and negative controls for the spectral detector and capture decoder.</summary>
    /// <returns>Zero when every synthetic case behaves as expected.</returns>
    public static int Run()
    {
        var cases = new (string Name, Func<bool> Check)[]
        {
            ("WASAPI marker sources preserve interleaved PCM at 44.1 kHz", () => ChecksRenderSources(44100)),
            ("WASAPI marker sources preserve interleaved PCM at 48 kHz", () => ChecksRenderSources(48000)),
            ("Stereo mixture at 44.1 kHz with gain and phase offsets", () => Mixed(44100).Passed),
            ("Stereo mixture at 48 kHz with gain and phase offsets", () => Mixed(48000).Passed),
            ("Only source A is rejected", () => !Mixed(48000, second: false).Passed),
            ("Only source B is rejected", () => !Mixed(48000, first: false).Passed),
            ("Sequential rather than simultaneous sources are rejected", () => !Mixed(48000, sequential: true).Passed),
            ("Swapped channels are rejected", () => !Mixed(48000, swap: true).Passed),
            ("Unrelated 3 kHz tone is rejected", () => RejectTone(3000)),
            ("Fixed tone coinciding with one marker is rejected", () => RejectTone(PcmMarkers.Frequencies[0])),
            ("A 3 kHz tone contaminating both valid markers is rejected", () => Mixed(48000, addStockTone: true) is { JointWindows: > 0, StockToneWindows: > 0, Passed: false }),
            ("Captured silence is recognized and rejected", () => PcmMarkers.Analyze(new float[96000], 48000) is { IsSilent: true, Passed: false }),
            ("PCM16 sign and scaling", () => Decodes(new WaveFormat(48000, 16, 2), [0, 0x40, 0, 0xC0])),
            ("PCM24 sign and scaling", () => Decodes(new WaveFormat(48000, 24, 2), [0, 0, 0x40, 0, 0, 0xC0])),
            ("PCM32 sign and scaling", () => Decodes(new WaveFormat(48000, 32, 2), [0, 0, 0, 0x40, 0, 0, 0, 0xC0])),
            ("IEEE float sign and scaling", () => Decodes(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), MemoryMarshal.AsBytes<float>([0.5f, -0.5f]))),
            ("Silence, flow flags, and packet positions are counted", TrackPackets)
        };

        int passed = 0;
        foreach (var (name, check) in cases)
        {
            bool success = check();
            if (success) passed++;
            Console.WriteLine($"{(success ? "PASS" : "FAIL")}: {name}");
        }
        Console.WriteLine($"Synthetic tests: {passed} passed, {cases.Length - passed} failed, 0 skipped.");
        return passed == cases.Length ? 0 : 1;
    }

    /// <summary>
    /// Reads both production render providers at the requested rate and checks stereo format, phase across odd-sized reads,
    /// and joint marker detection to catch generator and capture analyzer regressions without an audio device.
    /// </summary>
    /// <param name="rate">The source and synthetic capture sample rate in Hertz.</param>
    /// <returns>Whether the providers produce the same continuous PCM and the expected two distinct markers.</returns>
    private static bool ChecksRenderSources(int rate)
    {
        const int FirstRequestSamples = 257;
        const int SecondRequestSamples = 1031;
        var mixture = new float[rate * 2];
        for (int marker = 0; marker < 2; marker++)
        {
            var source = new PcmMarkers.Source(marker, rate);
            if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || source.WaveFormat.SampleRate != rate ||
                source.WaveFormat.Channels != 2 || source.WaveFormat.BitsPerSample != 32) return false;

            var split = new float[mixture.Length];
            int read = source.Read(split.AsSpan(0, FirstRequestSamples));
            read += source.Read(split.AsSpan(FirstRequestSamples, SecondRequestSamples));
            read += source.Read(split.AsSpan(FirstRequestSamples + SecondRequestSamples));
            var whole = new float[mixture.Length];
            if (read != split.Length || new PcmMarkers.Source(marker, rate).Read(whole) != whole.Length || !split.AsSpan().SequenceEqual(whole)) return false;

            var single = PcmMarkers.Analyze(split, rate);
            if (single.StockToneWindows != 0 || single.JointWindows != 0 || (marker == 0
                ? single.FirstWindows == 0 || single.SecondWindows != 0
                : single.SecondWindows == 0 || single.FirstWindows != 0)) return false;

            for (int i = 0; i < split.Length; i++) mixture[i] += split[i];
        }
        return PcmMarkers.Analyze(mixture, rate).Passed;
    }

    /// <summary>Simulates a captured mixture with independent gains and phases to verify overlap and channel separation.</summary>
    private static PcmMarkers.Result Mixed(int rate, bool first = true, bool second = true, bool sequential = false, bool swap = false, bool addStockTone = false)
    {
        var samples = new float[rate * 4];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                if (first && (!sequential || frame < rate))
                    samples[frame * 2 + channel] = 0.5f * PcmMarkers.Sample(0, swap ? 1 - channel : channel, frame + 55, rate);
                if (second && (!sequential || frame >= rate))
                    samples[frame * 2 + channel] += 0.8f * PcmMarkers.Sample(1, swap ? 1 - channel : channel, frame + 173, rate);
                if (addStockTone) samples[frame * 2 + channel] += (float)(0.025 * Math.Sin(2 * Math.PI * 3000 * frame / rate));
            }
        }
        return PcmMarkers.Analyze(samples, rate);
    }

    /// <summary>Checks that a stereo fixed tone fails recognition despite producing a clearly nonzero capture.</summary>
    private static bool RejectTone(int frequency)
    {
        var samples = new float[96000];
        for (int frame = 0; frame < samples.Length / 2; frame++)
            samples[2 * frame] = samples[2 * frame + 1] = (float)(0.07 * Math.Sin(2 * Math.PI * frequency * frame / 48000));
        return PcmMarkers.Analyze(samples, 48000) is { IsSilent: false, Passed: false };
    }

    /// <summary>Verifies that both positive and negative channel samples survive format decoding and frame accounting.</summary>
    private static bool Decodes(WaveFormat format, ReadOnlySpan<byte> packet)
    {
        var capture = new CaptureBuffer(format, maximumSeconds: 1);
        capture.Append(packet, AudioClientBufferFlags.None, 0);
        return capture.Frames == 1 && capture.Decode(out int invalid).AsSpan().SequenceEqual([0.5f, -0.5f]) && invalid == 0;
    }

    /// <summary>Verifies that silent payload bytes are ignored and post-start discontinuities are surfaced.</summary>
    private static bool TrackPackets()
    {
        var capture = new CaptureBuffer(new WaveFormat(48000, 16, 2), maximumSeconds: 1);
        capture.Append([0, 0x40, 0, 0xC0], AudioClientBufferFlags.DataDiscontinuity | AudioClientBufferFlags.Silent, 20);
        capture.Append([0, 0x40, 0, 0xC0], AudioClientBufferFlags.DataDiscontinuity, 22);
        capture.Append([0, 0, 0, 0], AudioClientBufferFlags.TimestampError, 1000);
        return capture is { Frames: 3, Packets: 3, SilentFrames: 1, Discontinuities: 1, PositionGaps: 1, TimestampErrors: 1 } &&
            capture.Decode(out _).AsSpan(0, 2).SequenceEqual([0f, 0f]);
    }
}

using System.Diagnostics;
using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Records render-provider calls in memory so source delivery can be compared with capture wakes and ETW after shutdown.</summary>
internal sealed class RenderTimingSource(WaveProvider32 source, long origin) : WaveProvider32(source.WaveFormat.SampleRate, source.WaveFormat.Channels)
{
    private const int MaximumCalls = 8192;
    private readonly Call[] _calls = new Call[MaximumCalls];
    private int _count;
    private long _samples;
    public bool Overflow { get; private set; }

    /// <summary>Serves the original source samples and records the request, supplied count and timing without write I/O.</summary>
    public override int Read(Span<float> buffer)
    {
        long start = Stopwatch.GetTimestamp() - origin;
        int read = source.Read(buffer);
        if (_count < _calls.Length) _calls[_count++] = new(start, Stopwatch.GetTimestamp() - origin, _samples, buffer.Length, read);
        else Overflow = true;
        _samples += read;
        return read;
    }

    /// <summary>Writes source offsets in interleaved samples and timing in the probe's shared Stopwatch coordinate system.</summary>
    internal void WriteCsv(string path)
    {
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew));
        writer.WriteLine("call_index,start_ticks,end_ticks,source_start_sample,requested_samples,written_samples");
        for (int i = 0; i < _count; i++)
        {
            Call call = _calls[i];
            writer.WriteLine(FormattableString.Invariant($"{i},{call.Start},{call.End},{call.SourceSample},{call.Requested},{call.Written}"));
        }
    }

    /// <summary>Stores render-call evidence without retaining the caller's audio span.</summary>
    private readonly record struct Call(long Start, long End, long SourceSample, int Requested, int Written);
}

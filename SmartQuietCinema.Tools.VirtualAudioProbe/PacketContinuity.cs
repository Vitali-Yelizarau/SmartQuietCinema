using System.Diagnostics;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Tracks WASAPI frame positions and optionally records a bounded callback trace for post-capture analysis.</summary>
internal sealed class PacketContinuity
{
    internal const uint DataDiscontinuity = 0x1;
    internal const uint Silent = 0x2;
    internal const uint TimestampError = 0x4;
    private readonly Packet[] _trace;
    private ulong? _previousPosition;
    private ulong? _expectedPosition;
    private ulong? _previousQpc;
    private int _traceCount;

    public int Packets { get; private set; }
    public int EmptyReads { get; private set; }
    public int Discontinuities { get; private set; }
    public int TimestampErrors { get; private set; }
    public int QpcRegressions { get; private set; }
    public int RepeatedQpc { get; private set; }
    public int InvalidPositions { get; private set; }
    public int ForwardGaps { get; private set; }
    public int Overlaps { get; private set; }
    public int PositionGaps => ForwardGaps + Overlaps;
    public UInt128 ForwardDeltaFrames { get; private set; }
    public UInt128 OverlapDeltaFrames { get; private set; }
    public bool TraceOverflow { get; private set; }
    public ReadOnlySpan<Packet> Trace => _trace.AsSpan(0, _traceCount);

    /// <summary>Reserves trace storage before callbacks begin. Zero capacity disables tracing while retaining validation counters.</summary>
    public PacketContinuity(int traceCapacity = 0) => _trace = traceCapacity == 0 ? [] : new Packet[traceCapacity];

    /// <summary>Compares nonempty packets and counts empty callbacks without using their undefined position, timestamp or flags.</summary>
    /// <param name="frames">Packet length in interleaved audio frames.</param>
    /// <param name="flags">Unmodified AUDCLNT_BUFFERFLAGS value.</param>
    /// <param name="position">Raw stream-relative position of the first frame.</param>
    /// <param name="qpc">Native QPC timestamp already converted to 100-nanosecond units by WASAPI.</param>
    /// <param name="arrivalTicks">Callback arrival relative to the run origin, in Stopwatch ticks.</param>
    public void Append(int frames, uint flags, ulong position, ulong qpc, long arrivalTicks)
    {
        if (frames == 0)
        {
            EmptyReads++;
            Record(new(frames, flags, null, position, null, null, qpc, null, arrivalTicks));
            return;
        }

        bool discontinuous = (flags & DataDiscontinuity) != 0;
        if (Packets++ > 0 && discontinuous) Discontinuities++;
        bool timestampError = (flags & TimestampError) != 0;
        if (timestampError) TimestampErrors++;

        ulong? expected = _expectedPosition;
        Int128? delta = null;
        Int128? qpcDelta = null;
        if (!timestampError)
        {
            if (_previousQpc is ulong previousQpc)
            {
                qpcDelta = (Int128)qpc - previousQpc;
                if (qpc < previousQpc) QpcRegressions++;
                if (qpc == previousQpc) RepeatedQpc++;
            }
            if (expected is ulong next)
            {
                Int128 signedDelta = (Int128)position - next;
                delta = signedDelta;
                if (signedDelta > 0) { ForwardGaps++; ForwardDeltaFrames += (UInt128)signedDelta; }
                if (signedDelta < 0) { Overlaps++; OverlapDeltaFrames += (UInt128)(-signedDelta); }
            }

            if (position > ulong.MaxValue - (uint)frames)
            {
                InvalidPositions++;
                _expectedPosition = null;
            }
            else _expectedPosition = position + (uint)frames;
        }
        else _expectedPosition = null;

        Record(new(frames, flags, _previousPosition, position, expected, delta, qpc, qpcDelta, arrivalTicks));
        _previousPosition = position;
        _previousQpc = timestampError ? null : qpc;
    }

    /// <summary>Stores diagnostic rows without allocating during capture and makes trace overflow visible.</summary>
    private void Record(Packet packet)
    {
        if (_trace.Length == 0) return;
        if (_traceCount < _trace.Length) _trace[_traceCount++] = packet;
        else TraceOverflow = true;
    }

    /// <summary>Writes all retained packet metadata after capture, preserving exact positions, flags and timestamps.</summary>
    public void WriteCsv(TextWriter writer)
    {
        const double MicrosecondsPerSecond = 1_000_000;
        writer.WriteLine("packet_index,frames,raw_flags_hex,previous_position_frames,position_frames,expected_position_frames,position_delta_frames,qpc_100ns,qpc_delta_100ns,arrival_stopwatch_ticks,arrival_us,arrival_delta_us");
        ReadOnlySpan<Packet> trace = Trace;
        for (int i = 0; i < trace.Length; i++)
        {
            Packet packet = trace[i];
            double arrivalUs = packet.ArrivalTicks * MicrosecondsPerSecond / Stopwatch.Frequency;
            double? arrivalDeltaUs = i > 0 ? (packet.ArrivalTicks - trace[i - 1].ArrivalTicks) * MicrosecondsPerSecond / Stopwatch.Frequency : null;
            writer.WriteLine(FormattableString.Invariant($"{i},{packet.Frames},0x{packet.Flags:X8},{packet.PreviousPosition},{packet.Position},{packet.ExpectedPosition},{packet.Delta},{packet.Qpc},{packet.QpcDelta},{packet.ArrivalTicks},{arrivalUs:F3},{arrivalDeltaUs:F3}"));
        }
    }

    /// <summary>Stores the raw packet and the continuity result without retaining any native buffer pointer.</summary>
    internal readonly record struct Packet(int Frames, uint Flags, ulong? PreviousPosition, ulong Position, ulong? ExpectedPosition, Int128? Delta, ulong Qpc, Int128? QpcDelta, long ArrivalTicks);
}

using System.Globalization;
using SmartQuietCinema.Tools.VirtualAudioProbe;

namespace SmartQuietCinema.Tests;

/// <summary>Checks packet continuity directly from the WASAPI frame and timestamp contracts, without starting an audio client.</summary>
public sealed class PacketContinuityTests
{
    /// <summary>Uses variable frame counts and a silent packet to catch regressions that confuse frames with bytes, samples or clock ticks.</summary>
    [Fact]
    public void VariableSizedAndSilentPacketsRemainContinuous()
    {
        var timing = new PacketContinuity(3);
        timing.Append(240, 0, 9000, 12_000_000, 10);
        timing.Append(480, PacketContinuity.Silent, 9240, 12_050_000, 20);
        timing.Append(96, 0, 9720, 12_150_000, 30);

        Assert.Equal(3, timing.Packets);
        Assert.Equal(0, timing.PositionGaps);
        Assert.Equal((ulong)9240, timing.Trace[1].ExpectedPosition);
        Assert.Equal((Int128)0, timing.Trace[2].Delta);
    }

    /// <summary>Confirms that forward gaps and backward or overlapping positions both fail continuity and retain their distinct signed frame deltas.</summary>
    [Fact]
    public void SeparatesForwardAndBackwardDeltasWithoutCancellingThem()
    {
        var timing = new PacketContinuity(4);
        timing.Append(240, 0, 1000, 0, 100);
        timing.Append(200, PacketContinuity.DataDiscontinuity, 1250, 0, 200);
        timing.Append(90, 0, 1430, 0, 300);
        timing.Append(120, 0, 700, 0, 400);

        Assert.Equal(3, timing.PositionGaps);
        Assert.Equal(1, timing.ForwardGaps);
        Assert.Equal(2, timing.Overlaps);
        Assert.Equal((UInt128)10, timing.ForwardDeltaFrames);
        Assert.Equal((UInt128)840, timing.OverlapDeltaFrames);
        Assert.Equal(1, timing.Discontinuities);
        Assert.Equal((Int128)10, timing.Trace[1].Delta);
        Assert.Equal((Int128)(-20), timing.Trace[2].Delta);
        Assert.Equal((Int128)(-820), timing.Trace[3].Delta);
        Assert.Equal((ulong)1430, timing.Trace[3].PreviousPosition);
    }

    /// <summary>Preserves the startup flag and all raw timestamps while preventing an unreliable timestamp from seeding a false comparison.</summary>
    [Fact]
    public void TimestampErrorBreaksOnlyPositionComparisonAndStillFailsTransport()
    {
        var timing = new PacketContinuity(4);
        timing.Append(100, PacketContinuity.DataDiscontinuity, 100, 12345, 1);
        timing.Append(100, PacketContinuity.TimestampError | PacketContinuity.DataDiscontinuity, 0, 0, 2);
        timing.Append(100, 0, 10000, 56789, 3);
        timing.Append(100, 0, 10100, 67890, 4);

        Assert.Equal(1, timing.Discontinuities);
        Assert.Equal(PacketContinuity.DataDiscontinuity, timing.Trace[0].Flags);
        Assert.Equal(1, timing.TimestampErrors);
        Assert.Equal(0, timing.PositionGaps);
        Assert.Null(timing.Trace[1].Delta);
        Assert.Null(timing.Trace[2].ExpectedPosition);
        Assert.Equal((ulong)56789, timing.Trace[2].Qpc);
        Assert.Equal((Int128)0, timing.Trace[3].Delta);
    }

    /// <summary>Checks the native UINT64 domain and signed subtraction so large positions cannot wrap into a false contiguous sequence.</summary>
    [Fact]
    public void RejectsUnrepresentableEndsAndPreservesLargeSignedDeltas()
    {
        var timing = new PacketContinuity(4);
        timing.Append(8, 0, 0, 1, 0);
        timing.Append(8, 0, ulong.MaxValue - 4, 2, 0);
        timing.Append(1, 0, ulong.MaxValue - 1, 3, 0);
        timing.Append(1, 0, 0, 4, 0);

        Assert.Equal(1, timing.InvalidPositions);
        Assert.Equal((Int128)ulong.MaxValue - 12, timing.Trace[1].Delta);
        Assert.Null(timing.Trace[2].Delta);
        Assert.Equal(-(Int128)ulong.MaxValue, timing.Trace[3].Delta);
    }

    /// <summary>Ensures filling the diagnostic buffer is visible and does not stop transport validation on later packets.</summary>
    [Fact]
    public void ReportsTraceOverflowWhileContinuingToValidatePackets()
    {
        var timing = new PacketContinuity(1);
        timing.Append(25, 0, 0, 0, 0);
        timing.Append(25, PacketContinuity.DataDiscontinuity, 30, 0, 1);

        Assert.True(timing.TraceOverflow);
        Assert.Equal(1, timing.Trace.Length);
        Assert.Equal(2, timing.Packets);
        Assert.Equal(1, timing.PositionGaps);
    }

    /// <summary>Checks that post-capture CSV preserves raw flags, signed position and QPC deltas with culture-independent numeric fields.</summary>
    [Fact]
    public void CsvPreservesIndependentClockAndFrameInformation()
    {
        var timing = new PacketContinuity(2);
        timing.Append(20, 0, 100, 10000, 0);
        timing.Append(20, PacketContinuity.DataDiscontinuity, 117, 20000, 10);
        using var output = new StringWriter(CultureInfo.GetCultureInfo("de-DE"));
        timing.WriteCsv(output);

        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("qpc_delta_100ns", lines[0]);
        Assert.StartsWith("1,20,0x00000001,100,117,120,-3,20000,10000,10,", lines[2]);
        Assert.Equal(12, lines[2].Split(',').Length);
    }
}

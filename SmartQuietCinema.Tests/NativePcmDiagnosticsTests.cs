using SmartQuietCinema.Tools.VirtualAudioProbe;

namespace SmartQuietCinema.Tests;

/// <summary>Exercises WASAPI lease handling and nonperiodic block comparisons without touching a live audio endpoint.</summary>
public sealed class NativePcmDiagnosticsTests
{
    private const int CaptureRate = 48000;
    private const int BlockFrames = 480;

    /// <summary>Empty success and native failures retain their HRESULT but cannot consume a buffer or seed frame and QPC continuity.</summary>
    [Theory]
    [InlineData(NativePacketReader.BufferEmpty)]
    [InlineData(unchecked((int)0x88890004))]
    public void UnavailableNativePacketsAreNotReleased(int status)
    {
        var packet = default(NativePacketReader.Packet);
        var read = NativePacketReader.Read(new NativeCall(status, 0).Get, _ => throw new InvalidOperationException("No packet was acquired."), result => packet = result);
        Assert.Equal(status, read.Result);
        Assert.Null(read.Release);
        Assert.Equal(0ul, packet.Position);
        Assert.Equal(0ul, packet.Qpc);
        Assert.Equal(0u, packet.Flags);
    }

    /// <summary>Releases the full frame count after copying the packet and preserves the raw status and unsigned position in the result.</summary>
    [Fact]
    public void AcquiredPacketIsConsumedBeforeRelease()
    {
        var operations = new List<string>();
        var read = NativePacketReader.Read(new NativeCall(0, BlockFrames).Get, frames =>
        {
            operations.Add($"release:{frames}");
            return unchecked((int)0x88890007);
        }, packet =>
        {
            Assert.Equal(ulong.MaxValue, packet.Position);
            Assert.Equal(123u, packet.Flags);
            operations.Add("consume");
        });
        Assert.Equal(new[] { "consume", "release:480" }, operations);
        Assert.Equal(unchecked((int)0x88890007), read.Release);
    }

    /// <summary>Ensures a failed consumer cannot leave an outstanding WASAPI lease when copying or validating a packet throws.</summary>
    [Fact]
    public void ConsumerFailureStillReleasesThePacket()
    {
        uint released = 0;
        Assert.Throws<InvalidOperationException>(() => NativePacketReader.Read(new NativeCall(0, BlockFrames).Get, frames => { released = frames; return 0; },
            _ => throw new InvalidOperationException("The consumer failed.")));
        Assert.Equal((uint)BlockFrames, released);
    }

    /// <summary>Identifies real repeated blocks, missing blocks and position-only repeats from distinct PCM matches under gain and DC changes.</summary>
    [Theory]
    [InlineData(0, -480, "repeated-pcm-block")]
    [InlineData(960, 480, "forward-source-gap")]
    [InlineData(480, -480, "consecutive-pcm-position-mismatch")]
    [InlineData(480, 0, "consecutive-pcm")]
    public void MatchesContentIndependentlyOfReportedDevicePosition(int step, int positionDelta, string classification)
    {
        const int InitialFrame = 1347;
        float[] reference = SequenceAlignment.Reference(CaptureRate, 5000, 0);
        int? previous = Recognize(reference, InitialFrame, CaptureRate);
        int? current = Recognize(reference, InitialFrame + step, 44100);
        Assert.Equal(InitialFrame, previous);
        Assert.InRange(current!.Value, InitialFrame + step - 1, InitialFrame + step + 1);
        Assert.Equal(classification, SequenceAlignment.Classify(previous, BlockFrames, current, positionDelta));
    }

    /// <summary>Rejects silence and a different channel seed so missing data does not masquerade as a confident source offset.</summary>
    [Fact]
    public void AbsentReferenceDoesNotProduceAnAlignment()
    {
        float[] reference = SequenceAlignment.Reference(CaptureRate, 4000, 0);
        Assert.Null(SequenceAlignment.Find(new float[BlockFrames * 2], 0, reference, 0, 3500).Frame);
        Assert.Null(SequenceAlignment.Find(Mixed(700, CaptureRate, 1), 0, reference, 0, 3500).Frame);
    }

    /// <summary>Independent voice startup offsets may differ while both channels still demonstrate uninterrupted source progress.</summary>
    [Fact]
    public void IndependentChannelStartsDoNotLookLikeMissingPcm()
    {
        string left = SequenceAlignment.Classify(1100, BlockFrames, 1580, -480);
        string right = SequenceAlignment.Classify(1580, BlockFrames, 2060, -480);
        Assert.Equal("consecutive-pcm-position-mismatch", SequenceAlignment.Combine(left, right));
        Assert.Equal("channel-disagreement", SequenceAlignment.Combine(left, "repeated-pcm-block"));
    }

    /// <summary>Finds a source offset using the capture-rate model after a simulated source-rate conversion.</summary>
    private static int? Recognize(float[] reference, int offset, int sourceRate) => SequenceAlignment.Find(Mixed(offset, sourceRate, 0), 0, reference, 0, 4500).Frame;

    /// <summary>Simulates linear rate conversion, gain and DC change, plus low-level interference from the other voice.</summary>
    private static float[] Mixed(int offset, int sourceRate, int channel)
    {
        float[] samples = new float[BlockFrames * 2];
        for (int i = 0; i < BlockFrames; i++)
        {
            double sourceFrame = (offset + i) * (double)sourceRate / CaptureRate;
            int first = (int)sourceFrame;
            float a = SequenceAlignment.Sample(first, sourceRate, channel);
            float b = SequenceAlignment.Sample(first + 1, sourceRate, channel);
            samples[i * 2] = 0.48f * (a + (b - a) * (float)(sourceFrame - first)) + 0.013f + 0.04f * SequenceAlignment.Sample(offset + i, CaptureRate, 1 - channel);
        }
        return samples;
    }

    /// <summary>Supplies a test double with intentionally undefined metadata when no frames were acquired.</summary>
    private sealed class NativeCall(int result, uint count)
    {
        /// <summary>Returns exact mock native arguments without dereferencing the test pointer.</summary>
        public int Get(out nint data, out uint frames, out uint flags, out ulong position, out ulong qpc)
        {
            (data, frames, flags, position, qpc) = (123, count, 123, ulong.MaxValue, 456);
            return result;
        }
    }
}

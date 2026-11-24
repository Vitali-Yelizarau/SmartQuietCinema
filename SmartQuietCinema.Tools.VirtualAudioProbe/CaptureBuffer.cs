using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>
/// Saves a bounded sequence of loopback packets in memory so analysis stays off the capture thread.
/// </summary>
internal sealed class CaptureBuffer
{
    private const int BitsPerByte = 8;
    private const int StereoChannels = 2;
    private const int Pcm16BitsPerSample = 16;
    private const int Pcm24BitsPerSample = 24;
    private const int Pcm32BitsPerSample = 32;
    private const int FloatBitsPerSample = 32;

    private readonly int _blockAlign;
    private readonly byte[] _bytes;
    private readonly WaveFormat _format;
    private readonly bool _float;
    private int _length;
    private readonly long? _traceOrigin;
    public PacketContinuity Continuity { get; }
    public long Frames { get; private set; }
    public long SilentFrames { get; private set; }
    public int Packets => Continuity.Packets;
    public int Discontinuities => Continuity.Discontinuities;
    public int PositionGaps => Continuity.PositionGaps;
    public int TimestampErrors => Continuity.TimestampErrors;
    public bool InvalidPacket { get; private set; }
    public bool Overflow { get; private set; }

    /// <summary>
    /// Validates stereo uncompressed capture and preallocates storage before the recorder starts.
    /// </summary>
    /// <param name="format">The recorder's negotiated capture format.</param>
    /// <param name="maximumSeconds">Upper bound on stored audio, including startup time.</param>
    /// <param name="traceOrigin">Run start in Stopwatch ticks, or null to collect counters without a packet trace.</param>
    public CaptureBuffer(WaveFormat format, int maximumSeconds, long? traceOrigin = null)
    {
        const int MinimumSampleRateHz = 8000;
        const int MaximumSampleRateHz = 192000;
        const int MaximumTracePackets = 32768;

        bool pcm = format.Encoding == WaveFormatEncoding.Pcm;
        _float = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format is WaveFormatExtensible extensible)
        {
            pcm = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
            _float = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT && extensible.ValidBitsPerSample == FloatBitsPerSample;
        }
        if (format.Channels != StereoChannels || format.SampleRate is < MinimumSampleRateHz or > MaximumSampleRateHz ||
            format.BlockAlign != StereoChannels * (format.BitsPerSample / BitsPerByte) ||
            !(pcm && format.BitsPerSample is Pcm16BitsPerSample or Pcm24BitsPerSample or Pcm32BitsPerSample || _float && format.BitsPerSample == FloatBitsPerSample))
            throw new NotSupportedException($"Unsupported capture format: {format}. Expected stereo PCM16/24/32 or IEEE float32.");

        _format = format;
        _blockAlign = format.BlockAlign;
        _bytes = new byte[checked(format.SampleRate * maximumSeconds * _blockAlign)];
        _traceOrigin = traceOrigin;
        Continuity = new(traceOrigin.HasValue ? MaximumTracePackets : 0);
    }

    /// <summary>
    /// Copies a nonempty WASAPI packet and keeps an empty read out of PCM and continuity accounting.
    /// A first-packet discontinuity remains in the trace and is excluded from the post-start counter.
    /// </summary>
    /// <param name="packet">The recorder-owned packet, valid only during this callback.</param>
    /// <param name="flags">WASAPI packet flags.</param>
    /// <param name="position">Device position of the packet's first frame.</param>
    /// <param name="qpc">Raw QPC value in 100-nanosecond units, as projected by NAudio.</param>
    public void Append(ReadOnlySpan<byte> packet, AudioClientBufferFlags flags, long position, long qpc = 0)
    {
        if (packet.Length % _blockAlign != 0)
        {
            InvalidPacket = true;
            return;
        }
        int frames = packet.Length / _blockAlign;
        // NAudio projects the native UINT64 fields as long. Keep the original bits for frame and time arithmetic.
        Continuity.Append(frames, (uint)flags, unchecked((ulong)position), unchecked((ulong)qpc), _traceOrigin is long origin ? Stopwatch.GetTimestamp() - origin : 0);
        if (frames == 0) return;
        Frames += frames;

        bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
        if (silent) SilentFrames += frames;
        if (packet.Length > _bytes.Length - _length)
        {
            Overflow = true;
            return;
        }
        if (!silent) packet.CopyTo(_bytes.AsSpan(_length));
        _length += packet.Length;
    }

    /// <summary>
    /// Converts captured samples using their negotiated container width and counts invalid float samples.
    /// </summary>
    /// <param name="invalidSamples">Number of nonfinite samples replaced by silence for analysis.</param>
    /// <returns>Interleaved normalized stereo samples.</returns>
    public float[] Decode(out int invalidSamples)
    {
        const int Pcm16BytesPerSample = 2;
        const int Pcm24BytesPerSample = 3;
        const float Pcm16Scale = 32768f;
        const float Pcm32Scale = 2147483648f;

        int width = _format.BitsPerSample / BitsPerByte;
        var samples = new float[_length / width];
        invalidSamples = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            ReadOnlySpan<byte> source = _bytes.AsSpan(i * width, width);
            // Shift packed PCM24 into a signed 32-bit value before applying the PCM32 full-scale divisor.
            float sample = width switch
            {
                Pcm16BytesPerSample => BinaryPrimitives.ReadInt16LittleEndian(source) / Pcm16Scale,
                Pcm24BytesPerSample => ((source[0] | (source[1] << 8) | (source[2] << 16)) << 8) / Pcm32Scale,
                _ => _float ? BinaryPrimitives.ReadSingleLittleEndian(source) : BinaryPrimitives.ReadInt32LittleEndian(source) / Pcm32Scale
            };
            if (float.IsFinite(sample)) samples[i] = sample;
            else invalidSamples++;
        }
        return samples;
    }
}

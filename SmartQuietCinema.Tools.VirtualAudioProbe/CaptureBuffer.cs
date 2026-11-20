using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>
/// Saves a bounded sequence of loopback packets in memory so analysis stays off the capture thread.
/// </summary>
internal sealed class CaptureBuffer
{
    private readonly int _blockAlign;
    private readonly byte[] _bytes;
    private readonly WaveFormat _format;
    private readonly bool _float;
    private int _length;
    private long? _nextPosition;
    public long Frames { get; private set; }
    public long SilentFrames { get; private set; }
    public int Packets { get; private set; }
    public int Discontinuities { get; private set; }
    public int PositionGaps { get; private set; }
    public int TimestampErrors { get; private set; }
    public bool InvalidPacket { get; private set; }
    public bool Overflow { get; private set; }

    /// <summary>
    /// Validates stereo uncompressed capture and preallocates storage before the recorder starts.
    /// </summary>
    /// <param name="format">The recorder's negotiated capture format.</param>
    /// <param name="maximumSeconds">Upper bound on stored audio, including startup time.</param>
    public CaptureBuffer(WaveFormat format, int maximumSeconds)
    {
        bool pcm = format.Encoding == WaveFormatEncoding.Pcm;
        _float = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format is WaveFormatExtensible extensible)
        {
            pcm = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
            _float = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT && extensible.ValidBitsPerSample == 32;
        }
        if (format.Channels != 2 || format.SampleRate is < 8000 or > 192000 ||
            format.BlockAlign != 2 * (format.BitsPerSample / 8) ||
            !(pcm && format.BitsPerSample is 16 or 24 or 32 || _float && format.BitsPerSample == 32))
            throw new NotSupportedException($"Unsupported capture format: {format}. Expected stereo PCM16/24/32 or IEEE float32.");

        _format = format;
        _blockAlign = format.BlockAlign;
        _bytes = new byte[checked(format.SampleRate * maximumSeconds * _blockAlign)];
    }

    /// <summary>
    /// Copies a reported WASAPI packet, preserves silent frames, and counts transport errors and position gaps.
    /// A first-packet discontinuity is treated as startup rather than loss inside the capture.
    /// </summary>
    /// <param name="packet">The recorder-owned packet, valid only during this callback.</param>
    /// <param name="flags">WASAPI packet flags.</param>
    /// <param name="position">Device position of the packet's first frame.</param>
    public void Append(ReadOnlySpan<byte> packet, AudioClientBufferFlags flags, long position)
    {
        if (packet.Length % _blockAlign != 0)
        {
            InvalidPacket = true;
            return;
        }
        int frames = packet.Length / _blockAlign;
        if (Packets > 0 && (flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Discontinuities++;
        Packets++;
        Frames += frames;
        if ((flags & AudioClientBufferFlags.TimestampError) != 0)
        {
            TimestampErrors++;
            _nextPosition = null;
        }
        else
        {
            if (_nextPosition is long expected && expected != position) PositionGaps++;
            _nextPosition = position + frames;
        }

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
        int width = _format.BitsPerSample / 8;
        var samples = new float[_length / width];
        invalidSamples = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            ReadOnlySpan<byte> source = _bytes.AsSpan(i * width, width);
            float sample = width switch
            {
                2 => BinaryPrimitives.ReadInt16LittleEndian(source) / 32768f,
                3 => ((source[0] | (source[1] << 8) | (source[2] << 16)) << 8) / 2147483648f,
                _ => _float ? BinaryPrimitives.ReadSingleLittleEndian(source) : BinaryPrimitives.ReadInt32LittleEndian(source) / 2147483648f
            };
            if (float.IsFinite(sample)) samples[i] = sample;
            else invalidSamples++;
        }
        return samples;
    }
}

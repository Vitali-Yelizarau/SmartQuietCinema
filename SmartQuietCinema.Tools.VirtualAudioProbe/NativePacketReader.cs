namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Implements the native capture-buffer ownership contract independently of COM activation.</summary>
internal static class NativePacketReader
{
    internal const int BufferEmpty = 0x08890001;
    internal const int Success = 0;

    /// <summary>Matches the WASAPI GetBuffer arguments, including unsigned positions and frame count.</summary>
    internal delegate int GetPacket(out nint bytes, out uint frames, out uint flags, out ulong position, out ulong qpc);

    /// <summary>Records the status and pointer during one read. Pointer lifetime ends before the next read.</summary>
    internal readonly record struct Packet(int Result, nint Bytes, uint Frames, uint Flags, ulong Position, ulong Qpc);

    /// <summary>Acquires a packet, copies its data through the consumer, and releases exactly the acquired frames even if consumption throws.</summary>
    /// <returns>The native read status, released frames and release status. Empty and failed reads have no release status.</returns>
    internal static (int Result, uint Frames, int? Release) Read(GetPacket get, Func<uint, int> release, Action<Packet> consume)
    {
        int result = get(out nint data, out uint frames, out uint flags, out ulong position, out ulong qpc);
        if (result < 0 || result == BufferEmpty || frames == 0)
        {
            consume(new(result, 0, frames, 0, 0, 0));
            return (result, frames, null);
        }

        int releaseResult;
        try { consume(new(result, data, frames, flags, position, qpc)); }
        finally { releaseResult = release(frames); }
        return (result, frames, releaseResult);
    }
}

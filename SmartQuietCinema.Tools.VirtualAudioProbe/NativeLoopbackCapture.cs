using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Captures an explicit endpoint using the Windows Core Audio COM ABI independently of NAudio capture interop.</summary>
internal sealed class NativeLoopbackCapture : IAsyncDisposable
{
    private const uint InProcessServer = 1;
    private const int SharedMode = 0;
    private const uint SharedLoopbackEvent = 0x88060000; // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY | EVENTCALLBACK | LOOPBACK
    private const int MaximumCalls = 65536;
    private readonly AutoResetEvent _event = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly NativeCall[] _calls = new NativeCall[MaximumCalls];
    private readonly IClient _client;
    private Thread? _thread;
    private int _callCount;
    public WaveFormat WaveFormat { get; }
    public double LatencyMilliseconds { get; }
    public uint EngineBufferFrames { get; }
    public long DefaultPeriod { get; }
    public long MinimumPeriod { get; }
    public uint WorkerThreadId { get; private set; }
    public bool TraceOverflow { get; private set; }

    /// <summary>Activates the endpoint through IMMDevice and configures the same event-driven shared mix-format stream as the original reader.</summary>
    public NativeLoopbackCapture(string id, int bufferMilliseconds)
    {
        object? enumerator = null, device = null, activated = null;
        nint format = 0;
        try
        {
            enumerator = new Enumerator();
            Check(((IEnumerator)enumerator).GetDevice(id, out IDevice selected));
            device = selected;
            Check(selected.Activate(typeof(IClient).GUID, InProcessServer, 0, out activated));
            _client = (IClient)activated;
            Check(_client.GetMixFormat(out format));
            WaveFormat = WaveFormat.MarshalFromPtr(format);
            Check(_client.GetDevicePeriod(out long standard, out long minimum));
            DefaultPeriod = standard;
            MinimumPeriod = minimum;
            Check(_client.Initialize(SharedMode, SharedLoopbackEvent, bufferMilliseconds * TimeSpan.TicksPerMillisecond, 0, format, Guid.Empty));
            Check(_client.GetBufferSize(out uint size));
            EngineBufferFrames = size;
            LatencyMilliseconds = size * (double)TimeSpan.TicksPerSecond / WaveFormat.SampleRate / TimeSpan.TicksPerMillisecond;
            Check(_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()));
        }
        catch
        {
            Release(activated);
            _event.Dispose();
            _stop.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>Starts one worker which owns the capture service and all GetBuffer and ReleaseBuffer calls.</summary>
    public void Start(CaptureBuffer target, Action<Exception> failure, int waitMilliseconds, long origin)
    {
        _thread = new Thread(() => Capture(target, failure, waitMilliseconds, origin)) { Name = "Native WASAPI loopback", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Requests capture termination so the worker can release its current packet and service.</summary>
    public void Stop() => _stop.Set();

    /// <summary>Drains ready packets in chronological callback order and records HRESULTs and clock data in preallocated memory.</summary>
    private void Capture(CaptureBuffer target, Action<Exception> failure, int waitMilliseconds, long origin)
    {
        object? service = null;
        bool started = false;
        try
        {
            WorkerThreadId = GetCurrentThreadId();
            Check(_client.GetService(typeof(ICapture).GUID, out service));
            var capture = (ICapture)service;
            var get = new NativePacketReader.GetPacket(capture.GetBuffer);
            Func<uint, int> release = capture.ReleaseBuffer;
            NativePacketReader.Packet packet = default;
            Action<NativePacketReader.Packet> consume = result =>
            {
                packet = result;
                if (result.Result >= 0 && result.Result != NativePacketReader.BufferEmpty)
                    target.AppendNative(result.Bytes, checked((int)result.Frames), result.Flags, result.Position, result.Qpc);
                else if (result.Result == NativePacketReader.BufferEmpty) target.AppendNative(0, 0, 0, 0, 0);
            };
            WaitHandle[] signals = [_stop, _event];
            Check(_client.Start());
            started = true;
            int wakeIndex = 0, wake;
            while ((wake = WaitHandle.WaitAny(signals, waitMilliseconds)) != 0)
            {
                long wakeTicks = Stopwatch.GetTimestamp() - origin;
                wakeIndex++;
                while (!_stop.WaitOne(0))
                {
                    int next = capture.GetNextPacketSize(out uint offered);
                    long ticks = Stopwatch.GetTimestamp() - origin;
                    if (next < 0 || offered == 0)
                    {
                        Record(new(wakeIndex, wake, wakeTicks, ticks, next, offered, null, default, null));
                        Check(next);
                        break;
                    }

                    var read = NativePacketReader.Read(get, release, consume);
                    Record(new(wakeIndex, wake, wakeTicks, ticks, next, offered, read.Result, packet with { Bytes = 0 }, read.Release));
                    Check(read.Result);
                    if (read.Release is int releaseResult) Check(releaseResult);
                    if (read.Result == NativePacketReader.BufferEmpty || read.Frames == 0) break;
                }
            }
        }
        catch (Exception exception) { failure(exception); }
        finally
        {
            if (started)
            {
                int stop = _client.Stop();
                if (stop < 0) failure(Marshal.GetExceptionForHR(stop) ?? new COMException("WASAPI Stop failed.", stop));
            }
            Release(service);
        }
    }

    /// <summary>Retains one native query without allocating and exposes overflow so diagnostics cannot silently truncate.</summary>
    private void Record(NativeCall call)
    {
        if (_callCount == _calls.Length) TraceOverflow = true;
        else _calls[_callCount++] = call;
    }

    /// <summary>Writes native statuses and packet metadata after the capture worker has stopped.</summary>
    public void WriteCsv(TextWriter writer)
    {
        writer.WriteLine("query_index,wake_index,wake_result,wake_stopwatch_ticks,query_stopwatch_ticks,next_hresult,offered_frames,get_hresult,frames,raw_flags_hex,position_frames,qpc_100ns,release_hresult");
        for (int i = 0; i < _callCount; i++)
        {
            NativeCall call = _calls[i];
            NativePacketReader.Packet packet = call.Packet;
            bool read = call.Get.HasValue;
            bool acquired = call.Release.HasValue;
            writer.WriteLine(FormattableString.Invariant($"{i},{call.WakeIndex},{call.Wake},{call.WakeTicks},{call.Ticks},0x{call.Next:X8},{call.Offered},{Hex(call.Get)},{(read && call.Get >= 0 ? packet.Frames : (uint?)null)},{Hex(acquired ? (int)packet.Flags : null)},{(acquired ? packet.Position : (ulong?)null)},{(acquired ? packet.Qpc : (ulong?)null)},{Hex(call.Release)}"));
        }
    }

    /// <summary>Formats a native status without replacing an absent native call with a fabricated success.</summary>
    private static string Hex(int? value) => value is int code ? $"0x{code:X8}" : "";

    /// <summary>Joins the worker before releasing the audio client and its event handles.</summary>
    public async ValueTask DisposeAsync()
    {
        await JoinAsync();
        Release(_client);
        _event.Dispose();
        _stop.Dispose();
    }

    /// <summary>Stops and joins capture before the caller formats or writes the trace.</summary>
    public async Task JoinAsync()
    {
        Stop();
        if (_thread is not null) await Task.Run(_thread.Join);
    }

    /// <summary>Preserves native HRESULT failure propagation while leaving positive success codes available for the trace.</summary>
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    /// <summary>Releases each private COM wrapper after its last use.</summary>
    private static void Release(object? value)
    {
        if (value is not null) Marshal.ReleaseComObject(value);
    }

    /// <summary>Stores query results without retaining a native audio pointer beyond its lease.</summary>
    private readonly record struct NativeCall(int WakeIndex, int Wake, long WakeTicks, long Ticks, int Next, uint Offered, int? Get, NativePacketReader.Packet Packet, int? Release);

    /// <summary>Identifies the native capture worker in the guest's scheduler trace.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    /// <summary>Activates the documented MMDeviceEnumerator coclass.</summary>
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class Enumerator { }

    /// <summary>Preserves IMMDeviceEnumerator vtable order through GetDevice.</summary>
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumerator
    {
        /// <summary>Reserves the first IMMDeviceEnumerator slot for endpoint enumeration.</summary>
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out nint collection);
        /// <summary>Reserves the default-endpoint slot. This reader uses explicit IDs.</summary>
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out nint device);
        /// <summary>Resolves the already verified opaque endpoint ID.</summary>
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    }

    /// <summary>Declares IMMDevice activation for IAudioClient.</summary>
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        /// <summary>Activates the requested audio interface without altering endpoint policy.</summary>
        [PreserveSig] int Activate(in Guid id, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    /// <summary>Declares IAudioClient in SDK vtable order, retaining HRESULT and native argument widths.</summary>
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClient
    {
        /// <summary>Configures the shared loopback stream.</summary>
        [PreserveSig] int Initialize(int share, uint flags, long duration, long period, nint format, in Guid session);
        /// <summary>Returns endpoint buffer capacity in frames.</summary>
        [PreserveSig] int GetBufferSize(out uint frames);
        /// <summary>Retains the stream-latency slot in the native ABI.</summary>
        [PreserveSig] int GetStreamLatency(out long latency);
        /// <summary>Retains the current-padding slot in the native ABI.</summary>
        [PreserveSig] int GetCurrentPadding(out uint frames);
        /// <summary>Retains the format-support slot in the native ABI.</summary>
        [PreserveSig] int IsFormatSupported(int share, nint format, out nint closest);
        /// <summary>Returns a CoTaskMem-allocated shared mode mix format.</summary>
        [PreserveSig] int GetMixFormat(out nint format);
        /// <summary>Retains the device-period slot in the native ABI.</summary>
        [PreserveSig] int GetDevicePeriod(out long standard, out long minimum);
        /// <summary>Starts the capture stream on its worker.</summary>
        [PreserveSig] int Start();
        /// <summary>Stops the capture stream after the final lease is released.</summary>
        [PreserveSig] int Stop();
        /// <summary>Retains the reset slot in the native ABI.</summary>
        [PreserveSig] int Reset();
        /// <summary>Supplies the event used by event-driven capture.</summary>
        [PreserveSig] int SetEventHandle(nint handle);
        /// <summary>Obtains the native capture service.</summary>
        [PreserveSig] int GetService(in Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    /// <summary>Preserves all IAudioCaptureClient return values and unsigned packet metadata.</summary>
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICapture
    {
        /// <summary>Acquires one available native packet.</summary>
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong position, out ulong qpc);
        /// <summary>Releases the full packet on the acquisition thread.</summary>
        [PreserveSig] int ReleaseBuffer(uint frames);
        /// <summary>Returns the next available packet's frame count.</summary>
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

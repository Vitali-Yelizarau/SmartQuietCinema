using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>Runs the same WASAPI loopback stream with the packet lease released before the next size query.</summary>
internal sealed class OrderedLoopbackCapture : IAsyncDisposable
{
    private readonly AudioClient _client;
    private readonly AutoResetEvent _ready = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _worker;

    public WaveFormat WaveFormat { get; }
    public double LatencyMilliseconds { get; }

    /// <summary>Opens a shared event-driven stream using the same endpoint, mix format and requested duration as WasapiRecorder.</summary>
    public OrderedLoopbackCapture(MMDevice endpoint, int bufferMilliseconds)
    {
        AudioClient? client = null;
        try
        {
            client = endpoint.CreateAudioClient();
            _client = client;
            WaveFormat = _client.MixFormat;
            const AudioClientStreamFlags Flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            _client.Initialize(AudioClientShareMode.Shared, Flags, bufferMilliseconds * TimeSpan.TicksPerMillisecond, 0, WaveFormat, Guid.Empty);
            _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            LatencyMilliseconds = (double)_client.BufferSize * TimeSpan.TicksPerSecond / WaveFormat.SampleRate / TimeSpan.TicksPerMillisecond;
        }
        catch
        {
            client?.Dispose();
            _ready.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    /// <summary>Starts a dedicated worker so GetNextPacketSize, GetBuffer and ReleaseBuffer run on the same thread.</summary>
    public void Start(CaptureDataAvailableHandler onPacket, Action<Exception> onFailure, int waitMilliseconds)
    {
        _worker = new Thread(() => Capture(onPacket, onFailure, waitMilliseconds)) { Name = "Ordered WASAPI loopback", IsBackground = true };
        _worker.Start();
    }

    /// <summary>Signals the worker to leave the wait or finish its current packet before shutdown.</summary>
    public void Stop() => _stop.Set();

    /// <summary>Drains each available packet in order and returns after an empty read, stop request or stream failure.</summary>
    private void Capture(CaptureDataAvailableHandler onPacket, Action<Exception> onFailure, int waitMilliseconds)
    {
        try
        {
            AudioCaptureClient capture = _client.AudioCaptureClient;
            WaitHandle[] events = [_stop, _ready];
            _client.Start();
            try
            {
                while (WaitHandle.WaitAny(events, waitMilliseconds) != 0)
                {
                    while (!_stop.WaitOne(0) && capture.GetNextPacketSize() > 0)
                    {
                        using var lease = capture.GetBufferLease(WaveFormat.BlockAlign);
                        onPacket(lease.Buffer, lease.Flags, lease.DevicePosition, lease.QPCPosition);
                        if (lease.FrameCount == 0) break;
                        // The lease ends at this iteration, before the next GetNextPacketSize.
                    }
                }
            }
            finally { _client.Stop(); }
        }
        catch (Exception exception) { onFailure(exception); }
    }

    /// <summary>Joins the capture thread before freeing the WASAPI client and signal handles.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_worker is not null) await Task.Run(_worker.Join);
        _client.Dispose();
        _ready.Dispose();
        _stop.Dispose();
    }
}

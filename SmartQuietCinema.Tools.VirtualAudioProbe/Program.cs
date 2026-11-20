using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SmartQuietCinema.Tools.VirtualAudioProbe;

/// <summary>
/// Runs only read-only discovery, synthetic self-tests, or an explicitly targeted virtual endpoint experiment.
/// </summary>
internal static class Program
{
    /// <summary>Routes commands and turns missing, invalid, or unsuitable endpoints into diagnostic exit codes.</summary>
    /// <param name="args">The requested command and its endpoint ID and source rate, if applicable.</param>
    /// <returns>Zero for success, one for a failed validation, or two for incorrect usage.</returns>
    private static async Task<int> Main(string[] args)
    {
        const int DefaultSourceRateHz = 48000;
        const int AlternateSourceRateHz = 44100;

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This probe requires Windows.");
            return 1;
        }
        try
        {
            if (args is ["self-test"]) return SelfTest.Run();
            if (args is ["list"]) return ListRenderEndpoints();
            if (args is ["defaults"]) return ReadDefaultEndpoints();
            if (args.Length is 2 or 3 && args[0] == "probe" && !string.IsNullOrWhiteSpace(args[1]))
            {
                int rate = DefaultSourceRateHz;
                if (args.Length == 2 || int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out rate) && rate is AlternateSourceRateHz or DefaultSourceRateHz)
                    return await ProbeAsync(args[1], rate);
            }
            Console.Error.WriteLine("Usage: VirtualAudioProbe list | defaults | self-test | probe <endpoint-id> [44100|48000]");
            return 2;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"Probe failed (0x{exception.HResult:X8}): {exception.Message}");
            return 1;
        }
    }

    /// <summary>Reads all playback roles for laboratory before/after snapshots without opening any audio client.</summary>
    /// <returns>A successful read-only snapshot status.</returns>
    private static int ReadDefaultEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (Role role in Enum.GetValues<Role>())
        {
            enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, role, out MMDevice? endpoint);
            using (endpoint)
                Console.WriteLine($"{role}\t{endpoint?.ID ?? "<none>"}");
        }
        return 0;
    }

    /// <summary>Lists active render endpoints and marks devices with the experimental hardware identity.</summary>
    /// <returns>A successful read-only enumeration status.</returns>
    private static int ListRenderEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < endpoints.Count; i++)
        {
            using var endpoint = endpoints[i];
            string kind = EndpointIdentity.IsExpected(endpoint) ? "SmartQuietCinema experiment" : "other render endpoint";
            Console.WriteLine($"{endpoint.ID}\t{endpoint.FriendlyName}\t[{kind}]");
        }
        if (endpoints.Count == 0) Console.WriteLine("No active render endpoints.");
        return 0;
    }

    /// <summary>
    /// Opens capture and two independent render clients against the same verified ID without changing endpoint policy.
    /// </summary>
    /// <param name="endpointId">The selected experimental MMDevice render ID.</param>
    /// <param name="sourceRate">The original marker sample rate in Hertz.</param>
    /// <returns>Whether the observed capture contains a valid stereo mix with no reported transport failure.</returns>
    private static async Task<int> ProbeAsync(string endpointId, int sourceRate)
    {
        using var enumerator = new MMDeviceEnumerator();
        // Compare only opaque IDs from the active render list, before creating any audio client.
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < endpoints.Count; i++)
        {
            using var endpoint = endpoints[i];
            if (!string.Equals(endpoint.ID, endpointId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!EndpointIdentity.IsExpected(endpoint))
                throw new InvalidOperationException("The selected endpoint does not have the SmartQuietCinema experimental adapter identity.");

            return await CollectAsync(endpoint, sourceRate);
        }
        throw new InvalidOperationException("The requested endpoint is not an active render device. Use 'list' to obtain the experimental endpoint ID.");
    }

    /// <summary>
    /// Captures a bounded interval containing simultaneous deterministic markers and waits for capture shutdown before analysis.
    /// </summary>
    /// <param name="endpoint">The already verified experimental render device.</param>
    /// <param name="sourceRate">The negotiated-client input rate to request for both markers.</param>
    /// <returns>The validation exit code.</returns>
    private static async Task<int> CollectAsync(MMDevice endpoint, int sourceRate)
    {
        const int CaptureCapacitySeconds = 8;
        const int CaptureLeadInMilliseconds = 250;
        const int MarkerOverlapMilliseconds = 2500;

        var failures = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureBuffer capture;
        WaveFormat format;
        await using (var recorder = new WasapiRecorderBuilder().WithDevice(endpoint).WithLoopbackCapture().WithSharedMode().WithEventSync().Build())
        {
            format = recorder.WaveFormat;
            capture = new CaptureBuffer(format, maximumSeconds: CaptureCapacitySeconds);
            await using var first = new WasapiPlayerBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync().Build();
            await using var second = new WasapiPlayerBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync().Build();
            recorder.RecordingStopped += (_, eventArgs) => { if (eventArgs.Exception is not null) failures.TrySetResult(eventArgs.Exception); };
            first.PlaybackStopped += (_, eventArgs) => { if (eventArgs.Exception is not null) failures.TrySetResult(eventArgs.Exception); };
            second.PlaybackStopped += (_, eventArgs) => { if (eventArgs.Exception is not null) failures.TrySetResult(eventArgs.Exception); };
            recorder.DataAvailable += (packet, flags, position, _) => capture.Append(packet, flags, position);
            first.Init(new PcmMarkers.Source(0, sourceRate));
            second.Init(new PcmMarkers.Source(1, sourceRate));
            Console.WriteLine($"Endpoint: {endpoint.ID} | {endpoint.FriendlyName}");
            Console.WriteLine($"Sources: 2 × stereo IEEE float32 at {sourceRate} Hz");
            string subtype = format is WaveFormatExtensible extensible ? $", subtype={extensible.SubFormat}, valid bits={extensible.ValidBitsPerSample}" : "";
            Console.WriteLine($"Capture: {format.Encoding}, {format.SampleRate} Hz, {format.Channels} channels, {format.BitsPerSample} bits{subtype}");

            recorder.StartRecording();
            if (await Task.WhenAny(Task.Delay(CaptureLeadInMilliseconds), failures.Task) != failures.Task)
            {
                first.Play();
                second.Play();
                await Task.WhenAny(Task.Delay(MarkerOverlapMilliseconds), failures.Task);
            }
            first.Stop();
            second.Stop();
            recorder.StopRecording();
        }

        if (failures.Task.IsCompletedSuccessfully) throw new InvalidOperationException("WASAPI stream failed.", await failures.Task);
        float[] samples = capture.Decode(out int invalidSamples);
        var result = PcmMarkers.Analyze(samples, format.SampleRate);
        Console.WriteLine($"Captured frames={capture.Frames}, packets={capture.Packets}, silent frames={capture.SilentFrames}, peak={result.Peak:F6}");
        Console.WriteLine($"Discontinuities after startup={capture.Discontinuities}, position gaps={capture.PositionGaps}, timestamp errors={capture.TimestampErrors}");
        Console.WriteLine($"125 ms windows: source A={result.FirstWindows}, source B={result.SecondWindows}, both={result.JointWindows}, longest joint run={result.LongestJointRun}");
        Console.WriteLine($"Windows with the SysVAD default 3000 Hz tone: {result.StockToneWindows}");
        bool passed = result.Passed && !capture.InvalidPacket && !capture.Overflow && invalidSamples == 0 &&
            capture.Discontinuities == 0 && capture.PositionGaps == 0 && capture.TimestampErrors == 0;
        string message = capture.Frames == 0 ? "No captured frames." : result.IsSilent ? "Captured silence." :
            "Missing stereo markers, insufficient overlap, or a capture error.";
        Console.WriteLine(passed ? "PASS: both stereo markers arrived together." : $"FAIL: {message}");
        if (capture.InvalidPacket || capture.Overflow || invalidSamples != 0)
            Console.WriteLine($"Malformed packet={capture.InvalidPacket}, storage overflow={capture.Overflow}, invalid samples={invalidSamples}");
        return passed ? 0 : 1;
    }
}

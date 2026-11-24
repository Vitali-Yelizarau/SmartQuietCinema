using System.Globalization;
using System.Diagnostics;
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
            if (args is ["list"] or ["list", "--diagnostics"]) return ListRenderEndpoints(args.Length == 2);
            if (args is ["defaults"]) return ReadDefaultEndpoints();
            if (args.Length is 2 or 3 or 5 && args[0] is "probe" or "control" or "probe-ordered" or "control-ordered" or "probe-native" or "control-native" or "probe-sequence" or "control-sequence" && !string.IsNullOrWhiteSpace(args[1]))
            {
                int rate = DefaultSourceRateHz;
                bool sequence = args[0].EndsWith("-sequence", StringComparison.Ordinal);
                if ((!sequence || args.Length == 5) && (args.Length != 5 || args[3] == "--trace" && !string.IsNullOrWhiteSpace(args[4])) &&
                    (args.Length == 2 || int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out rate) && rate is AlternateSourceRateHz or DefaultSourceRateHz))
                    return await ProbeAsync(args[1], rate, args[0].StartsWith("control", StringComparison.Ordinal), args.Length == 5 ? Path.GetFullPath(args[4]) : null,
                        args[0].EndsWith("-ordered", StringComparison.Ordinal), sequence || args[0].EndsWith("-native", StringComparison.Ordinal), sequence);
            }
            Console.Error.WriteLine("Usage: VirtualAudioProbe list [--diagnostics] | defaults | self-test | {probe|control}[-ordered|-native|-sequence] <endpoint-id> [44100|48000 [--trace <new-csv>]] (-sequence requires --trace)");
            return 2;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException)
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

    /// <summary>Lists active render endpoints, labels hardware identity matches, and optionally reports rejection details.</summary>
    /// <param name="includeDiagnostics">Whether to report the native operation or identity check that rejected each endpoint.</param>
    /// <returns>A successful read-only enumeration status.</returns>
    private static int ListRenderEndpoints(bool includeDiagnostics)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < endpoints.Count; i++)
        {
            using var endpoint = endpoints[i];
            string id = endpoint.ID;
            bool expected = EndpointIdentity.IsExpected(id, out string diagnostic);
            string kind = expected ? "SmartQuietCinema experiment" : "other render endpoint";
            string friendlyName = endpoint.FriendlyName.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            Console.WriteLine($"{id}\t{friendlyName}\t[{kind}]");
            if (!expected && includeDiagnostics) Console.WriteLine($"  Identity {id}: {diagnostic}");
        }
        if (endpoints.Count == 0) Console.WriteLine("No active render endpoints.");
        return 0;
    }

    /// <summary>
    /// Opens capture and two independent render clients against the same verified ID without changing endpoint policy.
    /// </summary>
    /// <param name="endpointId">The explicitly selected MMDevice render ID.</param>
    /// <param name="sourceRate">The original marker sample rate in Hertz.</param>
    /// <param name="control">Whether the operator explicitly selected a separate nonexperimental control endpoint.</param>
    /// <param name="tracePath">New CSV path for post-capture diagnostics, or null to omit the trace.</param>
    /// <param name="ordered">Whether to test release-before-query instead of WasapiRecorder's packet scheduling.</param>
    /// <returns>Whether the observed capture contains a valid stereo mix with no reported transport failure.</returns>
    /// <param name="native">Whether to bypass NAudio capture interop.</param>
    /// <param name="sequence">Whether to render the aperiodic diagnostic voices instead of marker acceptance tones.</param>
    private static async Task<int> ProbeAsync(string endpointId, int sourceRate, bool control, string? tracePath, bool ordered, bool native, bool sequence)
    {
        using var enumerator = new MMDeviceEnumerator();
        // Compare only opaque IDs from the active render list, before creating any audio client.
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (int i = 0; i < endpoints.Count; i++)
        {
            using var endpoint = endpoints[i];
            if (!string.Equals(endpoint.ID, endpointId, StringComparison.OrdinalIgnoreCase)) continue;
            bool experimental = EndpointIdentity.IsExpected(endpoint.ID, out string diagnostic);
            if (control && experimental) throw new InvalidOperationException("Control requires a separate render endpoint selected by the operator.");
            if (!control && !experimental)
                throw new InvalidOperationException($"The selected endpoint does not have the SmartQuietCinema experimental adapter identity. {diagnostic}");

            if (tracePath is not null && (Path.Exists(tracePath) || !Directory.Exists(Path.GetDirectoryName(tracePath))))
                throw new IOException("Supply an unused trace path in an existing directory.");
            Console.WriteLine(control ? "Mode: explicitly selected control; not experimental acceptance." : "Mode: hardware-verified experimental endpoint.");
            return await CollectAsync(endpoint, sourceRate, tracePath, ordered, native, sequence);
        }
        throw new InvalidOperationException("The requested endpoint is not an active render device. Use 'list' to obtain the experimental endpoint ID.");
    }

    /// <summary>
    /// Captures a bounded interval containing simultaneous deterministic markers and waits for capture shutdown before analysis.
    /// </summary>
    /// <param name="endpoint">The verified experimental endpoint or explicitly selected control render device.</param>
    /// <param name="sourceRate">The negotiated-client input rate to request for both markers.</param>
    /// <param name="tracePath">A new file to write after the capture thread stops.</param>
    /// <param name="ordered">Whether to release each packet before querying the next packet size.</param>
    /// <returns>The validation exit code.</returns>
    /// <param name="native">Whether to use a separate native capture client.</param>
    /// <param name="sequence">Whether to retain evidence from the aperiodic test.</param>
    private static async Task<int> CollectAsync(MMDevice endpoint, int sourceRate, string? tracePath, bool ordered, bool native, bool sequence)
    {
        const int CaptureCapacitySeconds = 8;
        const int CaptureLeadInMilliseconds = 250;
        const int MarkerOverlapMilliseconds = 2500;
        const int CaptureBufferMilliseconds = 100;
        const int RenderBufferMilliseconds = 200;
        const int EventWaitBufferCount = 3;

        var failures = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureBuffer capture;
        WaveFormat format;
        long origin = Stopwatch.GetTimestamp();
        DateTimeOffset originUtc = DateTimeOffset.UtcNow;
        long captureStart, stopRequest;
        long? firstPlay = null, secondPlay = null;
        double captureLatency;
        RenderTimingSource? firstTiming = null, secondTiming = null;
        await using var nativeRecorder = native ? new NativeLoopbackCapture(endpoint.ID, CaptureBufferMilliseconds) : null;
        await using (var recorder = ordered || native ? null : new WasapiRecorderBuilder().WithDevice(endpoint).WithLoopbackCapture().WithSharedMode().WithEventSync().WithBufferLength(CaptureBufferMilliseconds).Build())
        await using (var orderedRecorder = ordered ? new OrderedLoopbackCapture(endpoint, CaptureBufferMilliseconds) : null)
        {
            format = recorder?.WaveFormat ?? orderedRecorder?.WaveFormat ?? nativeRecorder!.WaveFormat;
            capture = new CaptureBuffer(format, CaptureCapacitySeconds, tracePath is not null ? origin : null);
            await using var first = new WasapiPlayerBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync().WithLatency(RenderBufferMilliseconds).Build();
            await using var second = new WasapiPlayerBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync().WithLatency(RenderBufferMilliseconds).Build();
            if (recorder is not null)
            {
                recorder.RecordingStopped += (_, eventArgs) => { if (eventArgs.Exception is not null) failures.TrySetResult(eventArgs.Exception); };
                recorder.DataAvailable += capture.Append;
            }
            first.PlaybackStopped += (_, eventArgs) => { if (eventArgs.Exception is not null) failures.TrySetResult(eventArgs.Exception); };
            second.PlaybackStopped += (_, eventArgs) => { if (eventArgs.Exception is not null) failures.TrySetResult(eventArgs.Exception); };
            WaveProvider32 firstSource = sequence ? new SequenceEvidence.Source(0, sourceRate) : new PcmMarkers.Source(0, sourceRate);
            WaveProvider32 secondSource = sequence ? new SequenceEvidence.Source(1, sourceRate) : new PcmMarkers.Source(1, sourceRate);
            if (native && tracePath is not null)
            {
                firstTiming = new(firstSource, origin);
                secondTiming = new(secondSource, origin);
            }
            first.Init(firstTiming ?? firstSource);
            second.Init(secondTiming ?? secondSource);
            Console.WriteLine($"Endpoint: {endpoint.ID} | {endpoint.FriendlyName}");
            Console.WriteLine($"Sources: 2 × stereo IEEE float32 at {sourceRate} Hz");
            string subtype = format is WaveFormatExtensible extensible ? $", subtype={extensible.SubFormat}, valid bits={extensible.ValidBitsPerSample}" : "";
            Console.WriteLine($"Capture: {format.Encoding}, {format.SampleRate} Hz, {format.Channels} channels, {format.BitsPerSample} bits{subtype}");

            captureStart = Stopwatch.GetTimestamp() - origin;
            if (recorder is not null) recorder.StartRecording();
            else if (orderedRecorder is not null) orderedRecorder.Start(capture.Append, exception => failures.TrySetResult(exception), EventWaitBufferCount * CaptureBufferMilliseconds);
            else nativeRecorder!.Start(capture, exception => failures.TrySetResult(exception), EventWaitBufferCount * CaptureBufferMilliseconds, origin);
            captureLatency = recorder?.LatencyMilliseconds ?? orderedRecorder?.LatencyMilliseconds ?? nativeRecorder!.LatencyMilliseconds;
            if (await Task.WhenAny(Task.Delay(CaptureLeadInMilliseconds), failures.Task) != failures.Task)
            {
                firstPlay = Stopwatch.GetTimestamp() - origin;
                first.Play();
                secondPlay = Stopwatch.GetTimestamp() - origin;
                second.Play();
                await Task.WhenAny(Task.Delay(MarkerOverlapMilliseconds), failures.Task);
            }
            stopRequest = Stopwatch.GetTimestamp() - origin;
            first.Stop();
            second.Stop();
            recorder?.StopRecording();
            orderedRecorder?.Stop();
            nativeRecorder?.Stop();
        }

        if (nativeRecorder is not null) await nativeRecorder.JoinAsync();

        long captureJoined = Stopwatch.GetTimestamp() - origin;
        Console.WriteLine($"Capture reader: {(native ? "native Windows COM" : ordered ? "ordered drain over AudioCaptureClient" : "WasapiRecorder")}, stimulus={(sequence ? "aperiodic" : "stereo markers")}, UTC={DateTimeOffset.UtcNow:O}");
        if (nativeRecorder is not null) Console.WriteLine($"Native endpoint: buffer_frames={nativeRecorder.EngineBufferFrames}, default_period_100ns={nativeRecorder.DefaultPeriod}, minimum_period_100ns={nativeRecorder.MinimumPeriod}, pid={Environment.ProcessId}, capture_tid={nativeRecorder.WorkerThreadId}");
        Console.WriteLine($"Scheduling: event-driven shared mode, capture request={CaptureBufferMilliseconds} ms, render requests={RenderBufferMilliseconds} ms, reported capture latency={captureLatency:F3} ms, lead-in/overlap requests={CaptureLeadInMilliseconds}/{MarkerOverlapMilliseconds} ms");
        Console.WriteLine($"Stopwatch: frequency_hz={Stopwatch.Frequency}, origin_ticks={origin}, origin_utc={originUtc:O}, capture_start_ticks={captureStart}, first_play_ticks={firstPlay}, second_play_ticks={secondPlay}, stop_request_ticks={stopRequest}, joined_ticks={captureJoined}");
        if (tracePath is not null)
        {
            using var writer = new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.None));
            capture.Continuity.WriteCsv(writer);
            Console.WriteLine($"Packet trace: {tracePath}, rows={capture.Continuity.Trace.Length}, overflow={capture.Continuity.TraceOverflow}");
        }

        Exception? streamFailure = failures.Task.IsCompletedSuccessfully ? await failures.Task : null;
        float[] samples = capture.Decode(out int invalidSamples);
        if (nativeRecorder is not null && tracePath is not null)
        {
            using var writer = new StreamWriter(new FileStream(tracePath + ".native.csv", FileMode.CreateNew));
            nativeRecorder.WriteCsv(writer);
            capture.WriteWave(tracePath + ".capture.wav");
            firstTiming?.WriteCsv(tracePath + ".render-left.csv");
            secondTiming?.WriteCsv(tracePath + ".render-right.csv");
        }
        var result = sequence ? default : PcmMarkers.Analyze(samples, format.SampleRate);
        bool signalPassed = sequence ? SequenceEvidence.Write(tracePath!, sourceRate, format.SampleRate, CaptureCapacitySeconds, samples, capture.Continuity.Trace) : result.Passed;
        Console.WriteLine($"Captured frames={capture.Frames}, packets={capture.Packets}, empty reads={capture.Continuity.EmptyReads}, silent frames={capture.SilentFrames}");
        PacketContinuity timing = capture.Continuity;
        Console.WriteLine($"Transport: discontinuities after startup={capture.Discontinuities}, position mismatches={capture.PositionGaps}, native timestamp-error flags={capture.TimestampErrors}, invalid positions={timing.InvalidPositions}");
        Console.WriteLine($"QPC observations: regressions={timing.QpcRegressions}, repeated={timing.RepeatedQpc}");
        Console.WriteLine($"Position deltas: forward={timing.ForwardGaps} packets / +{timing.ForwardDeltaFrames} frames, overlap/backward={timing.Overlaps} packets / -{timing.OverlapDeltaFrames} frames");
        if (!sequence)
        {
            Console.WriteLine($"Peak={result.Peak:F6}; 125 ms windows: source A={result.FirstWindows}, source B={result.SecondWindows}, both={result.JointWindows}, longest joint run={result.LongestJointRun}");
            Console.WriteLine($"Windows with the SysVAD default 3000 Hz tone: {result.StockToneWindows}");
        }
        bool transportPassed = capture.Discontinuities == 0 && capture.PositionGaps == 0 && capture.TimestampErrors == 0 && timing.InvalidPositions == 0;
        bool auxiliaryTraceOverflow = nativeRecorder?.TraceOverflow == true || firstTiming?.Overflow == true || secondTiming?.Overflow == true;
        bool storagePassed = !capture.InvalidPacket && !capture.Overflow && !timing.TraceOverflow && !auxiliaryTraceOverflow && invalidSamples == 0 && streamFailure is null;
        Console.WriteLine($"{(sequence ? "Aperiodic content" : "Markers")}: {(signalPassed ? "PASS" : "FAIL")} (empty={capture.Frames == 0})");
        Console.WriteLine($"Transport: {(transportPassed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Capture: {(storagePassed ? "PASS" : "FAIL")} (malformed packet={capture.InvalidPacket}, storage overflow={capture.Overflow}, trace overflow={timing.TraceOverflow || auxiliaryTraceOverflow}, invalid samples={invalidSamples})");
        if (streamFailure is not null) Console.WriteLine($"WASAPI stream error (0x{streamFailure.HResult:X8}): {streamFailure.Message}");
        bool passed = signalPassed && transportPassed && storagePassed;
        Console.WriteLine(passed ? "PASS: signal and capture acceptance." : "FAIL: see separate signal, transport and capture results.");
        return passed ? 0 : 1;
    }
}

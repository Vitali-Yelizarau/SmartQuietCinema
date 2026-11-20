using NAudio.CoreAudioApi;
using NAudio.Wave;
using SmartQuietCinema.Core.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Buffers;
using SmartQuietCinema.Core.DSP;
using System.IO;
using System.Threading;

namespace SmartQuietCinema.Platform.Windows.Capture
{
    /// <summary>
    /// Captures audio rendered by a selected Windows output device
    /// using WASAPI loopback capture.
    /// </summary>
    /// <remarks>
    /// Captures a copy of the audio stream without modifying
    /// or interrupting normal audio playback.
    ///
    /// Each instance represents a single capture session.
    /// Create a new instance to start another session
    /// after the previous instance has been disposed.
    /// </remarks>
    public sealed class WindowsAudioLoopbackCapture : IAudioCaptureSession
    {
        private MMDevice? _device;
        private WasapiRecorder? _recorder;

        private long _capturedBytes;
        private bool _disposed;

        const int MAX_CAPTURE_DURATION_SECONDS = 60;

        /// <summary>
        /// Gets the total number of audio bytes received
        /// since the capture session started.
        /// </summary>
        public long CapturedBytes => Interlocked.Read(ref _capturedBytes);

        /// <summary>
        /// Gets the sample rate of the captured audio in Hertz.
        /// Returns null before capture initialization.
        /// </summary>
        public int? SampleRate => _recorder?.WaveFormat.SampleRate;

        /// <summary>
        /// Gets the number of channels in the captured audio.
        /// Returns null before capture initialization.
        /// </summary>
        public int? Channels => _recorder?.WaveFormat.Channels;

        /// <summary>
        /// Starts capturing audio from the specified output device.
        /// Uses the original float frames to guide lookahead quiet gain for processed diagnostics and A/B audio.
        /// </summary>
        /// 
        /// <param name="deviceId">
        /// The Windows audio endpoint identifier obtained
        /// from the audio output device provider.
        /// </param>
        /// 
        /// <exception cref="ArgumentException">
        /// Thrown when the device identifier is null or empty.
        /// </exception>
        /// 
        /// <exception cref="InvalidOperationException">
        /// Thrown when a capture session has already been started.
        /// </exception>
        /// 
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the capture instance has been disposed.
        /// </exception>

        public void Start(string deviceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recorder is not null)
            {
                throw new InvalidOperationException(
                    "The capture session has already been started.");
            }

            using var enumerator = new MMDeviceEnumerator();

            var device = enumerator.GetDevice(deviceId);
            WasapiRecorder? recorder = null;

            try
            {
                recorder = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithLoopbackCapture()
                    .Build();

                // Log the audio format used by the selected output device.
                var format = recorder.WaveFormat;

                // Verify that the capture format contains 32-bit IEEE Float samples.
                bool isFloat32 =
                    format.BitsPerSample == 32 &&
                    format.BlockAlign == format.Channels * sizeof(float) &&
                    (
                        format.Encoding == WaveFormatEncoding.IeeeFloat ||
                        (format is WaveFormatExtensible extensibleFormat &&
                         extensibleFormat.SubFormat ==
                             new Guid("00000003-0000-0010-8000-00aa00389b71") &&
                         extensibleFormat.ValidBitsPerSample == 32)
                    );

                // Create an adaptive processor for bidirectional dynamics control.
                var processor = new AdaptiveDynamicsProcessor();

                // Prepare lookahead protection for sudden loud events.
                var transientController = new LookaheadTransientController();

                // Accumulate measurements across all processed audio buffers.
                var diagnostics = new AudioDiagnosticsAccumulator();

                // Record a short A/B comparison using the captured audio format.
                AbComparisonCapture? abCapture = isFloat32
                    ? new AbComparisonCapture(format, MAX_CAPTURE_DURATION_SECONDS)
                    : null;


                // Accumulate input audio levels separately for each channel.
                var channelPeaks = new float[format.Channels];
                var channelSumSquares = new double[format.Channels];
                var channelSampleCounts = new long[format.Channels];

                Debug.WriteLine(
                    $"[SmartQuietCinema] Audio Format: " +
                    $"{format.SampleRate} Hz, " +
                    $"{format.Channels} channels, " +
                    $"{format.BitsPerSample} bits");


                long lastLevelLogTimestamp = Stopwatch.GetTimestamp();

                if (!isFloat32)
                {
                    Debug.WriteLine(
                        "[SmartQuietCinema] DSP test skipped: " +
                        "the audio format is not supported.");
                }

                // Process every captured audio buffer.
                recorder.DataAvailable += (buffer, _, _, _) =>
                {
                    Interlocked.Add(
                        ref _capturedBytes,
                        buffer.Length);

                    if (!isFloat32 ||
                        buffer.IsEmpty ||
                        buffer.Length % format.BlockAlign != 0)
                    {
                        return;
                    }

                    // Interpret the captured bytes as read-only float samples.
                    ReadOnlySpan<float> inputSamples =
                        MemoryMarshal.Cast<byte, float>(buffer);

                    // Rent a reusable buffer for processing.
                    float[] rentedBuffer =
                        ArrayPool<float>.Shared.Rent(inputSamples.Length);

                    try
                    {
                        Span<float> processedSamples =
                            rentedBuffer.AsSpan(0, inputSamples.Length);

                        // Preserve the original captured samples.
                        inputSamples.CopyTo(processedSamples);

                        // Apply DSP to every buffer.
                        processor.Process(
                            processedSamples,
                            format.Channels,
                            format.SampleRate);

                        // Share the captured frames with the lookahead controller so quiet gain can yield before loud events.
                        transientController.Process(processedSamples, inputSamples, format.Channels, format.SampleRate);

                        // Include every processed buffer in the diagnostics.
                        diagnostics.Add(
                            inputSamples,
                            processedSamples,
                            processor.CurrentGainDb);

                        // Preserve matching original and processed audio for A/B listening.
                        abCapture?.Add(
                            inputSamples,
                            processedSamples);

                        // Accumulate original audio levels for every channel.
                        for (int i = 0; i < inputSamples.Length; i++)
                        {
                            float sample = inputSamples[i];

                            if (!float.IsFinite(sample))
                            {
                                continue;
                            }

                            int channel = i % format.Channels;

                            float amplitude = Math.Abs(sample);

                            channelPeaks[channel] =
                                Math.Max(channelPeaks[channel], amplitude);

                            channelSumSquares[channel] +=
                                (double)sample * sample;

                            channelSampleCounts[channel]++;
                        }


                        // Limit diagnostic logging to approximately once per second.
                        if (Stopwatch.GetElapsedTime(lastLevelLogTimestamp)
                            < TimeSpan.FromSeconds(1))
                        {
                            return;
                        }

                        lastLevelLogTimestamp = Stopwatch.GetTimestamp();

                        // Get measurements accumulated across all buffers
                        // since the previous diagnostic snapshot.
                        AudioDiagnosticsSnapshot snapshot =
                            diagnostics.TakeSnapshot();

                        if (snapshot.SampleCount > 0)
                        {
                            Debug.WriteLine(
                                $"[SmartQuietCinema] Adaptive DSP | " +
                                $"Gain Range=[{snapshot.MinGainDb:F2}, " +
                                $"{snapshot.MaxGainDb:F2}] dB | " +
                                $"Input: Peak={snapshot.InputPeak:F4}, " +
                                $"RMS={snapshot.InputRms:F4} | " +
                                $"Output: Peak={snapshot.OutputPeak:F4}, " +
                                $"RMS={snapshot.OutputRms:F4} | " +
                                $"Samples={snapshot.SampleCount}");
                        }


                        // Log accumulated levels for individual audio channels.
                        string channelReport = "[SmartQuietCinema] Channels";

                        for (int channel = 0; channel < format.Channels; channel++)
                        {
                            double rms = channelSampleCounts[channel] > 0
                                ? Math.Sqrt(
                                    channelSumSquares[channel] /
                                    channelSampleCounts[channel])
                                : 0.0;

                            channelReport +=
                                $" | Ch{channel + 1}: " +
                                $"Peak={channelPeaks[channel]:F4}, " +
                                $"RMS={rms:F4}";
                        }

                        Debug.WriteLine(channelReport);

                        // Reset channel measurements for the next interval.
                        Array.Clear(channelPeaks);
                        Array.Clear(channelSumSquares);
                        Array.Clear(channelSampleCounts);

                    }
                    finally
                    {
                        // Return the buffer even if processing fails.
                        ArrayPool<float>.Shared.Return(rentedBuffer);
                    }
                };

                recorder.RecordingStopped += (_, args) =>
                {
                    if (args.Exception is not null)
                    {
                        Debug.WriteLine(
                            $"WASAPI capture error: {args.Exception}");
                    }
                };

                recorder.StartRecording();

                _device = device;
                _recorder = recorder;
            }
            catch
            {
                recorder?.Dispose();
                device.Dispose();
                throw;
            }
        }


        /// <summary>
        /// Stops audio capture and asynchronously releases
        /// the associated native audio resources.
        /// </summary>
        /// <returns>
        /// A task representing asynchronous resource cleanup.
        /// </returns>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_recorder is not null)
                {
                    await _recorder.DisposeAsync();
                }
            }
            finally
            {
                _device?.Dispose();

                _recorder = null;
                _device = null;
            }
        }


        private sealed class AbComparisonCapture
        {
            private const float SilentChannelThreshold = 0.00001f;

            private readonly WaveFormat _format;
            private readonly float[] _original;
            private readonly float[] _processed;
            private readonly object _sync = new();

            private int _writtenSamples;
            private bool _completed;

            public AbComparisonCapture(
                WaveFormat format,
                int durationSeconds)
            {
                ArgumentNullException.ThrowIfNull(format);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                    durationSeconds);

                _format = format;

                int sampleCount = checked(
                    format.SampleRate *
                    format.Channels *
                    durationSeconds);

                _original = new float[sampleCount];
                _processed = new float[sampleCount];
            }

            /// <summary>
            /// Copies paired audio samples without performing disk I/O.
            /// </summary>
            public void Add(
                ReadOnlySpan<float> original,
                ReadOnlySpan<float> processed)
            {
                if (original.Length != processed.Length ||
                    original.Length % _format.Channels != 0)
                {
                    throw new ArgumentException(
                        "Audio buffers must contain matching complete frames.");
                }

                bool shouldSave = false;

                lock (_sync)
                {
                    if (_completed)
                    {
                        return;
                    }

                    int count = Math.Min(
                        original.Length,
                        _original.Length - _writtenSamples);

                    original[..count].CopyTo(
                        _original.AsSpan(_writtenSamples, count));

                    processed[..count].CopyTo(
                        _processed.AsSpan(_writtenSamples, count));

                    _writtenSamples += count;

                    if (_writtenSamples == _original.Length)
                    {
                        _completed = true;
                        shouldSave = true;
                    }
                }

                if (shouldSave)
                {
                    // Perform WAV writing outside the capture callback.
                    _ = Task.Run(SaveFiles);
                }
            }

            /// <summary>
            /// Checks whether only the first two channels carry audio.
            /// </summary>
            private bool ContainsOnlyStereoAudio()
            {
                if (_format.Channels == 2)
                {
                    return true;
                }

                if (_format.Channels < 2)
                {
                    return false;
                }

                for (int i = 0; i < _original.Length;
                     i += _format.Channels)
                {
                    for (int channel = 2;
                         channel < _format.Channels;
                         channel++)
                    {
                        float sample = _original[i + channel];

                        if (!float.IsFinite(sample) ||
                            Math.Abs(sample) > SilentChannelThreshold)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            /// <summary>
            /// Saves both recordings in the same audio format in the user's local sample directory.
            /// </summary>
            private void SaveFiles()
            {
                try
                {
                    bool useStereo = ContainsOnlyStereoAudio();

                    WaveFormat outputFormat = useStereo
                        ? WaveFormat.CreateIeeeFloatWaveFormat(
                            _format.SampleRate, 2)
                        : _format;

                    string folder = WindowsSampleStorage.CreateCaptureDirectory(WindowsSampleStorage.GetDefaultRoot(), DateTime.Now);

                    WriteWav(
                        Path.Combine(folder, "Original.wav"),
                        _original,
                        outputFormat,
                        useStereo);


                    // Compensate for the lookahead latency in the A/B export.
                    float[] alignedProcessed = (float[])_processed.Clone();

                    int latencyFrames = Math.Max(
                        1,
                        (int)Math.Ceiling(
                            _format.SampleRate *
                            LookaheadTransientController.LookaheadMilliseconds /
                            1000.0));

                    int latencySamples = checked(
                        latencyFrames * _format.Channels);

                    // Shift the delayed audio back to its original timeline.
                    Array.Copy(
                        alignedProcessed,
                        latencySamples,
                        alignedProcessed,
                        0,
                        alignedProcessed.Length - latencySamples);

                    // The last frames have no corresponding captured future audio.
                    Array.Clear(
                        alignedProcessed,
                        alignedProcessed.Length - latencySamples,
                        latencySamples);

                    WriteWav(
                        Path.Combine(folder, "Processed.wav"),
                        alignedProcessed,
                        outputFormat,
                        useStereo);


                    Debug.WriteLine(
                        $"[SmartQuietCinema] A/B saved: {folder}");

                    Debug.WriteLine(
                        $"[SmartQuietCinema] A/B format: " +
                        $"{outputFormat.SampleRate} Hz, " +
                        $"{outputFormat.Channels} channels, " +
                        $"{outputFormat.BitsPerSample} bits");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[SmartQuietCinema] A/B saving failed: {ex}");
                }
            }

            /// <summary>
            /// Writes interleaved IEEE Float samples into a WAV file.
            /// </summary>
            private void WriteWav(
                string path,
                float[] samples,
                WaveFormat outputFormat,
                bool useStereo)
            {
                using var writer = new WaveFileWriter(
                    path, outputFormat);

                if (!useStereo)
                {
                    // Preserve the original multichannel format.
                    writer.Write(
                        MemoryMarshal.AsBytes(samples.AsSpan()));

                    return;
                }

                // Export the two active channels as stereo.
                const int chunkFrames = 4096;

                float[] stereoBuffer = new float[chunkFrames * 2];

                int totalFrames = samples.Length / _format.Channels;

                for (int offset = 0;
                     offset < totalFrames;
                     offset += chunkFrames)
                {
                    int frames = Math.Min(
                        chunkFrames,
                        totalFrames - offset);

                    for (int frame = 0; frame < frames; frame++)
                    {
                        int sourceIndex =
                            (offset + frame) * _format.Channels;

                        int destinationIndex = frame * 2;

                        stereoBuffer[destinationIndex] =
                            samples[sourceIndex];

                        stereoBuffer[destinationIndex + 1] =
                            samples[sourceIndex + 1];
                    }

                    writer.Write(
                        MemoryMarshal.AsBytes(
                            stereoBuffer.AsSpan(0, frames * 2)));
                }
            }
        }

    }
}

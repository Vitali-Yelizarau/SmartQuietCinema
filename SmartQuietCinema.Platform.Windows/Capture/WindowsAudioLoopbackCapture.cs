using NAudio.CoreAudioApi;
using NAudio.Wave;
using SmartQuietCinema.Core.Interfaces;
using System.Diagnostics;
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

                // Count captured bytes without copying audio buffers.
                recorder.DataAvailable += (buffer, _, _, _) =>
                {
                    Interlocked.Add(
                        ref _capturedBytes,
                        buffer.Length);
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
    }
}

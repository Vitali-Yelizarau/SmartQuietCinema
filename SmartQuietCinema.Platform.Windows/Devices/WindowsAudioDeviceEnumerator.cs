using NAudio.CoreAudioApi;
using SmartQuietCinema.Core.Interfaces;
using SmartQuietCinema.Core.Models;

namespace SmartQuietCinema.Platform.Windows.Devices
{
    /// <summary>
    /// Provides access to audio output devices
    /// available through the Windows Core Audio API.
    /// </summary>
    public sealed class WindowsAudioDeviceEnumerator : IAudioOutputDeviceProvider
    {
        /// <summary>
        /// Retrieves all currently active audio output devices.
        /// </summary>
        /// <returns>
        /// A read-only list containing device identifiers
        /// and human-readable device names.
        /// Returns an empty list if no active devices are found.
        /// </returns>
        /// <remarks>
        /// Only active playback endpoints are included.
        /// Disabled, disconnected, and inactive devices
        /// are excluded.
        ///
        /// Native audio resources are released after
        /// the device information has been retrieved.
        /// </remarks>
        public IReadOnlyList<AudioOutputDevice> GetActiveOutputDevices()
        {
            using var enumerator = new MMDeviceEnumerator();

            using var endpoints = enumerator.EnumerateAudioEndPoints(
                                                  DataFlow.Render,
                                                  DeviceState.Active);

            var devices = new List<AudioOutputDevice>(endpoints.Count);

            for (int i = 0; i < endpoints.Count; i++)
            {
                using var device = endpoints[i];

                devices.Add(new AudioOutputDevice(
                    device.ID,
                    device.FriendlyName));
            }

            return devices;
        }
    }
}

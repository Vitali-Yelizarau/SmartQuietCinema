using SmartQuietCinema.Core.Models;

namespace SmartQuietCinema.Core.Interfaces
{
    /// <summary>
    /// Defines a platform-independent contract
    /// for retrieving available audio output devices.
    /// </summary>
    public interface IAudioOutputDeviceProvider
    {
        /// <summary>
        /// Retrieves all currently active audio output devices.
        /// </summary>
        /// <returns>
        /// A read-only list of available audio output devices.
        /// Returns an empty list if no active devices are found.
        /// </returns>
        IReadOnlyList<AudioOutputDevice> GetActiveOutputDevices();
    }
}

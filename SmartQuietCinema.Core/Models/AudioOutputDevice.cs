
namespace SmartQuietCinema.Core.Models
{
    /// <summary>
    /// Represents an audio output device available
    /// on the current operating system.
    /// </summary>
    /// <param name="Id">
    /// An opaque identifier assigned to the audio endpoint
    /// by the operating system.
    /// </param>
    /// <param name="Name">
    /// A human-readable device name intended
    /// for display in the user interface.
    /// </param>
    public sealed record AudioOutputDevice(
        string Id,
        string Name);
}

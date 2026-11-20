namespace SmartQuietCinema.Platform.Windows.Capture
{
    /// <summary>
    /// Resolves the local sample directory shared by Windows A/B captures and offline automation.
    /// </summary>
    internal static class WindowsSampleStorage
    {
        /// <summary>
        /// Resolves the default sample root from the current Windows user's known folder without creating it.
        /// </summary>
        /// <returns>The shared SmartQuietCinema sample root.</returns>
        internal static string GetDefaultRoot()
            => ResolveRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

        /// <summary>
        /// Builds the shared sample path without falling back to Documents or to the working directory.
        /// </summary>
        /// <param name="localAppData">The current user's LocalAppData path.</param>
        /// <returns>The sample root under LocalAppData.</returns>
        internal static string ResolveRoot(string localAppData)
        {
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new DirectoryNotFoundException("The Windows LocalAppData directory could not be resolved for SmartQuietCinema samples.");

            return Path.Combine(localAppData, "SmartQuietCinema", "Samples");
        }

        /// <summary>
        /// Creates the timestamped capture directory and its missing parents before the A/B writer saves either recording.
        /// </summary>
        /// <param name="root">The resolved sample root.</param>
        /// <param name="timestamp">The existing local capture timestamp used for the folder name.</param>
        /// <returns>The path of the capture directory.</returns>
        internal static string CreateCaptureDirectory(string root, DateTime timestamp)
        {
            string folder = Path.Combine(root, timestamp.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

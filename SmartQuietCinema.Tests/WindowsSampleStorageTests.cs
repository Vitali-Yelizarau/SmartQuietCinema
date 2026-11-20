using System.Security.Cryptography;
using SmartQuietCinema.Platform.Windows.Capture;

namespace SmartQuietCinema.Tests
{
    /// <summary>
    /// Verifies Windows sample path and folder creation rules without touching a real capture directory.
    /// </summary>
    public sealed class WindowsSampleStorageTests
    {
        /// <summary>
        /// Checks that the sample root follows the supplied user profile rather than a hardcoded drive or user path.
        /// </summary>
        [Fact]
        public void ResolveRoot_UsesLocalAppData()
        {
            string localAppData = Path.Combine(Path.GetTempPath(), "Test Profile", "Local");

            Assert.Equal(Path.Combine(localAppData, "SmartQuietCinema", "Samples"), WindowsSampleStorage.ResolveRoot(localAppData));
        }

        /// <summary>
        /// Ensures an unavailable LocalAppData path fails clearly instead of writing relative to the working directory.
        /// </summary>
        /// <param name="localAppData">The unavailable directory value.</param>
        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public void ResolveRoot_RejectsUnresolvedLocalAppData(string localAppData)
        {
            var error = Assert.Throws<DirectoryNotFoundException>(() => WindowsSampleStorage.ResolveRoot(localAppData));

            Assert.Contains("LocalAppData", error.Message);
        }

        /// <summary>
        /// Checks that first use creates the existing timestamp layout and reopening the folder does not affect the original file.
        /// </summary>
        [Fact]
        public void CreateCaptureDirectory_CreatesTimestampFolderAndPreservesExistingFiles()
        {
            DirectoryInfo temporary = Directory.CreateTempSubdirectory("SmartQuietCinema-Storage-");
            try
            {
                string root = WindowsSampleStorage.ResolveRoot(temporary.FullName);
                var timestamp = new DateTime(2026, 11, 19, 20, 53, 46, 635);
                string expected = Path.Combine(root, timestamp.ToString("yyyyMMdd_HHmmss_fff"));

                Assert.False(Directory.Exists(root));
                Assert.Equal(expected, WindowsSampleStorage.CreateCaptureDirectory(root, timestamp));
                Assert.True(Directory.Exists(expected));

                string original = Path.Combine(expected, "Original.wav");
                File.WriteAllBytes(original, [1, 2, 3]);
                byte[] hash = SHA256.HashData(File.ReadAllBytes(original));

                Assert.Equal(expected, WindowsSampleStorage.CreateCaptureDirectory(root, timestamp));
                Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(original)));
            }
            finally
            {
                temporary.Delete(recursive: true);
            }
        }

        /// <summary>
        /// Verifies that a blocked sample root reports an I/O failure without modifying the existing obstacle.
        /// </summary>
        [Fact]
        public void CreateCaptureDirectory_WhenRootIsAFile_ReportsFailure()
        {
            DirectoryInfo temporary = Directory.CreateTempSubdirectory("SmartQuietCinema-Storage-");
            try
            {
                string root = WindowsSampleStorage.ResolveRoot(temporary.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                File.WriteAllText(root, "existing file");

                Assert.ThrowsAny<IOException>(() => WindowsSampleStorage.CreateCaptureDirectory(root, new DateTime(2026, 11, 19)));
                Assert.Equal("existing file", File.ReadAllText(root));
            }
            finally
            {
                temporary.Delete(recursive: true);
            }
        }
    }
}

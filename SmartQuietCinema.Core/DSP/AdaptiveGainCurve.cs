
namespace SmartQuietCinema.Core.DSP
{
    /// <summary>
    /// Calculates the target gain for adaptive
    /// bidirectional audio dynamics processing.
    /// </summary>
    /// <remarks>
    /// Quiet signals may be amplified, loud signals
    /// may be attenuated, and signals within the
    /// comfortable range remain unchanged.
    ///
    /// This class only calculates target gain.
    /// It does not modify audio samples or smooth
    /// gain transitions.
    /// </remarks>
    public sealed class AdaptiveGainCurve
    {
        // Initial experimental thresholds in dBFS.
        private const double NoiseFloorDb = -70.0;
        private const double MaximumBoostLevelDb = -50.0;
        private const double QuietNeutralLevelDb = -30.0;
        private const double LoudThresholdDb = -20.0;

        private const double MaximumBoostDb = 12.0;
        private const double MaximumAttenuationDb = 18.0;
        private const double CompressionRatio = 4.0;
        private const double LoudOutputHeadroomDb = 1.0;

        /// <summary>
        /// Calculates the desired gain adjustment
        /// based on the detected signal level.
        /// </summary>
        /// <remarks>
        /// Above the loud threshold, compression starts at 4:1 and increases smoothly as the compressed level approaches
        /// the experimental headroom above the threshold. The attenuation cap takes precedence for extreme input levels.
        /// </remarks>
        /// <param name="signalLevelDb">
        /// The detected signal level in dBFS.
        /// Negative infinity represents silence.
        /// </param>
        /// <returns>
        /// Positive gain for quiet signals,
        /// negative gain above the experimental -20 dBFS threshold,
        /// or zero when no adjustment is required.
        /// </returns>
        public double CalculateTargetGainDb(double signalLevelDb)
        {
            if (double.IsNegativeInfinity(signalLevelDb))
                return 0.0;

            if (!double.IsFinite(signalLevelDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(signalLevelDb));
            }

            // Do not amplify near-silence.
            if (signalLevelDb <= NoiseFloorDb)
                return 0.0;

            // Gradually introduce amplification
            // above the configured noise floor.
            if (signalLevelDb < MaximumBoostLevelDb)
            {
                double factor =
                    (signalLevelDb - NoiseFloorDb) /
                    (MaximumBoostLevelDb - NoiseFloorDb);

                return MaximumBoostDb * factor;
            }

            // Gradually reduce amplification
            // as the signal approaches the neutral range.
            if (signalLevelDb < QuietNeutralLevelDb)
            {
                double factor =
                    (QuietNeutralLevelDb - signalLevelDb) /
                    (QuietNeutralLevelDb - MaximumBoostLevelDb);

                return MaximumBoostDb * factor;
            }

            // Preserve signals within the neutral range.
            if (signalLevelDb <= LoudThresholdDb)
                return 0.0;

            // Bound the rise in output level without changing the slope at the knee or reversing the output curve.
            double excessDb = signalLevelDb - LoudThresholdDb;
            double compressedExcessDb = excessDb / (CompressionRatio + excessDb / LoudOutputHeadroomDb);
            return -Math.Min(excessDb - compressedExcessDb, MaximumAttenuationDb);
        }
    }
}

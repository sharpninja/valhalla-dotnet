// Faithful C# port of the predicted-speed constants from Valhalla baldr @ 3.7.0.
// Source: valhalla/baldr/predictedspeeds.h
//
// These compile-time constants drive the DCT-II / DCT-III speed compression and the
// base64 wire format for predicted speed profiles stored within a GraphTile.
//
// Public members are PascalCase per the TruckMate port convention; the underlying
// values are identical to the C++ constexpr originals.

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Predicted-speed compression constants ported from
/// <c>valhalla/baldr/predictedspeeds.h</c>.
/// </summary>
public static class PredictedSpeedConstants
{
    /// <summary>Size of a speed bucket in minutes. C++ <c>kSpeedBucketSizeMinutes</c>.</summary>
    public const uint SpeedBucketSizeMinutes = 5;

    /// <summary>Size of a speed bucket in seconds. C++ <c>kSpeedBucketSizeSeconds</c>.</summary>
    public const uint SpeedBucketSizeSeconds = SpeedBucketSizeMinutes * 60;

    /// <summary>
    /// Number of 5-minute buckets in a week: (7 * 24 * 60) / 5 = 2016.
    /// C++ <c>kBucketsPerWeek</c>.
    /// </summary>
    public const uint BucketsPerWeek = (7 * 24 * 60) / SpeedBucketSizeMinutes;

    /// <summary>
    /// Length of the transformed (DCT) speed coefficient array. C++ <c>kCoefficientCount</c>.
    /// </summary>
    public const uint CoefficientCount = 200;

    /// <summary>
    /// Expected size in bytes of the base64-decoded predicted-speed coefficients.
    /// Each int16 coefficient occupies two bytes. C++ <c>kDecodedSpeedSize</c>.
    /// </summary>
    public const uint DecodedSpeedSize = 2 * CoefficientCount;
}

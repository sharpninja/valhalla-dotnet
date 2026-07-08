// Faithful C# port of the routing-relevant subset of Valhalla midgard
// elevation_encoding.h @ 3.7.0.
// Source: valhalla/midgard/elevation_encoding.h
//
// PORT-NOTE: Only the helpers consumed by baldr EdgeInfo.encoded_elevation()
// (sampling_interval + encoded_elevation_count and their backing constants) are
// ported here. The encode_elevation / decode_elevation routines (used by the
// mjolnir tile builder, not by routing-time tile reads) are omitted as out of
// scope for the baldr edgeinfo slice.

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Elevation sampling helpers ported from <c>valhalla/midgard/elevation_encoding.h</c>.
/// </summary>
public static class ElevationEncoding
{
    /// <summary>Maximum sampling interval used when encoding elevation along an edge. Mirrors <c>kMaxEdgeElevationSampling</c>.</summary>
    public const uint MaxEdgeElevationSampling = 32;

    /// <summary>NO_DATA elevation value. Mirrors <c>ELEVATION_NO_DATA_VALUE</c>.</summary>
    public const int ElevationNoDataValue = -32768;

    /// <summary>Fixed precision (0.25 m). Mirrors <c>kElevationPrecision</c>.</summary>
    public const double ElevationPrecision = 0.25;

    /// <summary>Inverse of the fixed precision (4.0). Mirrors <c>kInvElevationPrecision</c>.</summary>
    public const double InvElevationPrecision = 4.0;

    /// <summary>
    /// Get the sampling interval to use along an edge of a given length. To allow consistent
    /// encoding forward and reverse along an edge we use a sampling interval that breaks the edge
    /// into an integral number of samples. Faithful port of C++ <c>sampling_interval</c>.
    /// </summary>
    /// <param name="length">Edge length.</param>
    /// <returns>The sampling interval in meters.</returns>
    public static double SamplingInterval(double length)
    {
        // C++: uint32_t sample_count = length / kMaxEdgeElevationSampling; (integer truncation)
        uint sampleCount = (uint)(length / MaxEdgeElevationSampling);
        return length / (sampleCount + 1);
    }

    /// <summary>
    /// Get the count of encoded elevations given the edge length. Computes the desired sampling
    /// interval (not to exceed <see cref="MaxEdgeElevationSampling"/>) and computes the number of
    /// vertices, excluding the first and last (these are not encoded). Faithful port of C++
    /// <c>encoded_elevation_count</c>.
    /// </summary>
    /// <param name="length">Edge length.</param>
    /// <returns>The number of encoded elevations.</returns>
    public static uint EncodedElevationCount(uint length)
        => (uint)System.Math.Round(length / SamplingInterval(length)) - 1;
}

// Faithful C# port of Valhalla baldr predicted speeds (DCT compression / decompression)
// @ 3.7.0.
// Sources:
//   valhalla/baldr/predictedspeeds.h
//   src/baldr/predictedspeeds.cc
//
// Speed profiles are stored per directed edge as a truncated DCT-II transform of the
// 2016 (= kBucketsPerWeek) five-minute speed buckets. Decoding applies the inverse
// (DCT-III) transform. The wire format packs each int16 coefficient big-endian and
// base64-encodes the result.
//
// FIDELITY NOTES
//   - All trigonometric / rounding math uses single precision (MathF.Cos, MathF.Round)
//     to match the C++ cosf/roundf calls exactly. The DCT-III accumulation order is
//     preserved (coefficient 0 scaled by 1/sqrt(2) first, then 1..199 accumulated).
//   - The BucketCosTable is a lazily-initialized singleton, matching the C++
//     Meyers-singleton (`static BucketCosTable instance;`). It precomputes
//     kCoefficientCount * kBucketsPerWeek = 200 * 2016 = 403,200 float cos values
//     (~1.6 MB), identical to the C++ table_.
//   - endian conversion + base64 reuse the already-ported midgard helpers
//     (Util.ToBigEndian / Util.ToLittleEndian / Util.Encode64 / Util.Decode64).
//
// PORT-NOTE: The C++ PredictedSpeeds class holds raw const pointers (offset_, profiles_)
// into the mmap'd GraphTile blob. C# cannot hold interior pointers safely, so the port
// holds the offset and profile arrays as ReadOnlyMemory<T> spans plus a base index, and
// Speed(idx, secondsOfWeek) slices profiles starting at offset[idx], reproducing the
// pointer arithmetic `profiles_ + offset_[idx]` exactly. The on-disk byte layout is
// unaffected (this class is a view over the tile, not part of its serialized layout).

using System;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Precomputed cos table, one row of <see cref="PredictedSpeedConstants.CoefficientCount"/>
/// values per bucket of the week. Faithful port of the C++ <c>BucketCosTable</c> singleton
/// in <c>src/baldr/predictedspeeds.cc</c>.
/// </summary>
internal sealed class BucketCosTable
{
    // DCT-III constants for speed decoding and normalization (src/baldr/predictedspeeds.cc).
    internal const float OneOverSqrt2 = 0.707106781f; // 1 / sqrt(2)
    internal const float PiBucketConstant = 3.14159265f / 2016.0f;
    internal const float SpeedNormalization = 0.031497039f; // sqrt(2.0f / 2016.0f)

    // Size of the cos table for the buckets: kCoefficientCount * kBucketsPerWeek.
    internal const uint CosBucketTableSize =
        PredictedSpeedConstants.CoefficientCount * PredictedSpeedConstants.BucketsPerWeek;

    private static readonly BucketCosTable s_instance = new();

    private readonly float[] _table;

    private BucketCosTable()
    {
        // Fill out the table in bucket order, matching the C++ construction loop exactly.
        _table = new float[CosBucketTableSize];
        uint t = 0;
        for (uint bucket = 0; bucket < PredictedSpeedConstants.BucketsPerWeek; ++bucket)
        {
            for (uint c = 0; c < PredictedSpeedConstants.CoefficientCount; ++c)
            {
                _table[t++] = MathF.Cos(PiBucketConstant * (bucket + 0.5f) * c);
            }
        }
    }

    /// <summary>Singleton accessor. C++ <c>BucketCosTable::GetInstance()</c>.</summary>
    public static BucketCosTable Instance => s_instance;

    /// <summary>
    /// Backing storage for the table. Used together with <see cref="RowOffset"/> to mirror
    /// the C++ <c>get(bucket)</c> pointer arithmetic.
    /// </summary>
    internal float[] Table => _table;

    /// <summary>
    /// Index of the first cos value for the given bucket within <see cref="Table"/>.
    /// Mirrors the pointer returned by C++ <c>get(bucket)</c> = <c>&amp;table_[bucket * kCoefficientCount]</c>.
    /// </summary>
    internal static uint RowOffset(uint bucket) => bucket * PredictedSpeedConstants.CoefficientCount;
}

/// <summary>
/// Free-function predicted-speed helpers ported from <c>src/baldr/predictedspeeds.cc</c>:
/// <c>compress_speed_buckets</c>, <c>decompress_speed_bucket</c>,
/// <c>encode_compressed_speeds</c>, <c>decode_compressed_speeds</c>.
/// </summary>
public static class PredictedSpeedCompression
{
    /// <summary>
    /// Compress speed buckets by truncating their DCT-II transform. Faithful port of
    /// <c>compress_speed_buckets</c>.
    /// </summary>
    /// <param name="speeds">Speed values for each bucket (must be 2016 values).</param>
    /// <returns>Transformed int16 coefficients (200 values).</returns>
    public static short[] CompressSpeedBuckets(ReadOnlySpan<float> speeds)
    {
        int count = (int)PredictedSpeedConstants.CoefficientCount;
        var coefficients = new float[count]; // zero-filled, matching coefficients.fill(0.f)

        float[] table = BucketCosTable.Instance.Table;

        // DCT-II with speed normalization
        for (uint bucket = 0; bucket < PredictedSpeedConstants.BucketsPerWeek; ++bucket)
        {
            uint row = BucketCosTable.RowOffset(bucket);
            float bucketSpeed = speeds[(int)bucket];
            for (int c = 0; c < count; ++c)
            {
                coefficients[c] += table[row + c] * bucketSpeed;
            }
        }

        coefficients[0] *= BucketCosTable.OneOverSqrt2;

        var result = new short[count];
        for (int i = 0; i < count; ++i)
        {
            result[i] = unchecked((short)MathF.Round(BucketCosTable.SpeedNormalization * coefficients[i],
                MidpointRounding.AwayFromZero));
        }

        return result;
    }

    /// <summary>
    /// Recover the speed value in a bucket by applying the DCT-III transform. Faithful port
    /// of <c>decompress_speed_bucket</c>.
    /// </summary>
    /// <param name="coefficients">Transformed speed buckets (200 values).</param>
    /// <param name="bucketIdx">Index of the bucket to recover.</param>
    /// <returns>Speed value (in KPH) for the bucket.</returns>
    public static float DecompressSpeedBucket(ReadOnlySpan<short> coefficients, uint bucketIdx)
    {
        float[] table = BucketCosTable.Instance.Table;
        uint row = BucketCosTable.RowOffset(bucketIdx);
        int count = (int)PredictedSpeedConstants.CoefficientCount;

        // DCT-III with speed normalization. Coefficient 0 is scaled by 1/sqrt(2), then the
        // remaining 1..199 are accumulated against the bucket's cos values (cos[0] is skipped,
        // exactly as the C++ pre-increments both pointers).
        float speed = coefficients[0] * BucketCosTable.OneOverSqrt2;
        for (int c = 1; c < count; ++c)
        {
            speed += coefficients[c] * table[row + c];
        }

        return speed * BucketCosTable.SpeedNormalization;
    }

    /// <summary>
    /// Pack transformed speed values (big-endian int16) into a base64-encoded string.
    /// Faithful port of <c>encode_compressed_speeds</c>.
    /// </summary>
    /// <param name="coefficients">Transformed speed buckets (200 values).</param>
    /// <returns>Base64-encoded string.</returns>
    public static string EncodeCompressedSpeeds(ReadOnlySpan<short> coefficients)
    {
        int count = (int)PredictedSpeedConstants.CoefficientCount;

        // Each coefficient is written big-endian as two bytes (matching the C++ which appends
        // the raw bytes of a big-endian uint16 into a std::string). We rebuild that exact byte
        // stream as a latin1 string so midgard Encode64 reproduces the C++ base64 output.
        var bytes = new byte[count * sizeof(ushort)];
        for (int i = 0; i < count; ++i)
        {
            ushort be = Util.ToBigEndian(unchecked((ushort)coefficients[i]));
            // C++ memory order of the big-endian value bytes (little-endian host):
            //   low byte first, then high byte.
            bytes[(i * 2) + 0] = (byte)(be & 0xFF);
            bytes[(i * 2) + 1] = (byte)((be >> 8) & 0xFF);
        }

        // midgard Encode64 treats each char as one octet (latin1), so feed it a latin1 string.
        var sb = new System.Text.StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append((char)b);
        }

        return Util.Encode64(sb.ToString());
    }

    /// <summary>
    /// Decode a base64 string and recover the transformed speed buckets. Faithful port of
    /// <c>decode_compressed_speeds</c>. Throws if the decoded byte count is unexpected.
    /// </summary>
    /// <param name="encoded">Base64-encoded string.</param>
    /// <returns>Transformed speed buckets (200 values).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the decoded size != <see cref="PredictedSpeedConstants.DecodedSpeedSize"/>.
    /// </exception>
    public static short[] DecodeCompressedSpeeds(string encoded)
    {
        string decodedStr = Util.Decode64(encoded);
        if (decodedStr.Length != PredictedSpeedConstants.DecodedSpeedSize)
        {
            throw new InvalidOperationException(
                "Decoded speed string size expected= " + PredictedSpeedConstants.DecodedSpeedSize +
                " actual=" + decodedStr.Length);
        }

        int count = (int)PredictedSpeedConstants.CoefficientCount;
        var coefficients = new short[count];

        // Each group of 2 bytes is a signed int16 in big-endian order; convert to host order.
        // ToLittleEndian byte-swaps the raw big-endian uint16 (formed low-byte/high-byte from
        // the latin1 chars) to produce the signed int16 value.
        for (int i = 0, idx = 0; i < count; ++i, idx += 2)
        {
            ushort raw = (ushort)((byte)decodedStr[idx] | ((byte)decodedStr[idx + 1] << 8));
            coefficients[i] = Util.ToLittleEndian(raw);
        }

        return coefficients;
    }
}

/// <summary>
/// Accessor for predicted speed information within a tile. Faithful port of the C++
/// <c>PredictedSpeeds</c> class (<c>valhalla/baldr/predictedspeeds.h</c>).
/// </summary>
/// <remarks>
/// PORT-NOTE: the C++ class stores raw pointers into the GraphTile blob. This port holds
/// the offset and profile arrays directly; <see cref="Speed"/> reproduces the C++ pointer
/// arithmetic <c>profiles_ + offset_[idx]</c> by slicing the profile array.
/// </remarks>
public sealed class PredictedSpeeds
{
    private uint[]? _offset;     // Offset into the array of compressed speed profiles per directed edge
    private short[]? _profiles;  // Compressed speed profiles

    /// <summary>
    /// Set the offset array (one entry per directed edge). C++ <c>set_offset</c>.
    /// </summary>
    public void SetOffset(uint[] offset) => _offset = offset;

    /// <summary>
    /// Set the speed-profile coefficient array. C++ <c>set_profiles</c>.
    /// </summary>
    public void SetProfiles(short[] profiles) => _profiles = profiles;

    /// <summary>
    /// Get the speed (KPH) for the given directed edge index and seconds-of-week (local time).
    /// Faithful port of <c>PredictedSpeeds::speed</c>.
    /// </summary>
    /// <param name="idx">Directed edge index.</param>
    /// <param name="secondsOfWeek">Seconds from the start of the week (local time).</param>
    public float Speed(uint idx, uint secondsOfWeek)
    {
        // Assume idx and the profile offset are valid (mirrors the C++ contract: this is only
        // called when DirectedEdge::has_predicted_speed is true).
        if (_profiles is null || _offset is null)
        {
            throw new InvalidOperationException("PredictedSpeeds offset/profiles not set.");
        }

        int start = (int)_offset[idx];
        ReadOnlySpan<short> coefficients = _profiles.AsSpan(start);
        return PredictedSpeedCompression.DecompressSpeedBucket(
            coefficients, secondsOfWeek / PredictedSpeedConstants.SpeedBucketSizeSeconds);
    }
}

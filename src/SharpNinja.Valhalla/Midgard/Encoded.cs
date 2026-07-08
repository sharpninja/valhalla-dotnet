// Faithful C# port of Valhalla midgard encoded polyline + varint codecs.
// Source: valhalla/midgard/encoded.h
// Self-contained engine port: does NOT reuse other TruckMate types (e.g.
// GeoCoordinate or ValhallaPolylineDecoder). It operates on the midgard PointLL
// (lng = First/x, lat = Second/y) and integer containers.
//
// Default precision is 6 digits (polyline6): ENCODE_PRECISION = 1e6,
// DECODE_PRECISION = 1e-6 (matching the non-USE_7DIGITS_DEFAULT branch in C++).
//
// Like the C++ original, two coordinate encodings are provided:
//   - encode / decode      : Google "polyline" 5-bit chunk encoding (+63 offset)
//   - encode7 / decode7    : raw 7-bit varint chunk encoding
// plus an integer varint codec (encode7int / decode7int). All use zig-zag
// encoding of the per-coordinate delta so negative deltas use fewer bytes.

using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Polyline and varint encode/decode helpers ported verbatim from
/// <c>valhalla/midgard/encoded.h</c>. Coordinates are <see cref="PointLL"/>
/// (longitude = first/x, latitude = second/y). Default precision is 6 digits
/// (polyline6): <see cref="EncodePrecision"/> = 1e6, <see cref="DecodePrecision"/> = 1e-6.
/// </summary>
public static class Encoded
{
    /// <summary>Decoding precision (the multiplier applied to decoded integers). Mirrors C++ <c>DECODE_PRECISION</c>.</summary>
    public const double DecodePrecision = 1e-6;

    /// <summary>Encoding precision (the multiplier applied before truncation). Mirrors C++ <c>ENCODE_PRECISION</c>.</summary>
    public const int EncodePrecision = 1_000_000; // 1e6

    /// <summary>Number of digits of precision retained. Mirrors C++ <c>DIGITS_PRECISION</c>.</summary>
    public const int DigitsPrecision = 6;

    // -------------------------------------------------------------------------
    // ZigZag encoding/decoding (generic over signed/unsigned integer types).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Move the sign bit to the least significant bit, making negative numbers use fewer bits
    /// when encoded. Faithful port of C++ <c>zigzag_encode</c> for 32-bit signed.
    /// </summary>
    public static uint ZigzagEncode(int value)
        => (uint)(value << 1) ^ (uint)(value >> 31);

    /// <summary>Restore the sign bit from the least significant bit. Faithful port of C++ <c>zigzag_decode</c> (32-bit).</summary>
    public static int ZigzagDecode(uint value)
        => (int)(value >> 1) ^ -(int)(value & 1);

    /// <summary>
    /// Move the sign bit to the least significant bit (64-bit). Faithful port of C++
    /// <c>zigzag_encode</c> for 64-bit signed.
    /// </summary>
    public static ulong ZigzagEncode(long value)
        => (ulong)(value << 1) ^ (ulong)(value >> 63);

    /// <summary>Restore the sign bit from the least significant bit (64-bit). Faithful port of C++ <c>zigzag_decode</c> (64-bit).</summary>
    public static long ZigzagDecode(ulong value)
        => (long)(value >> 1) ^ -(long)(value & 1);

    // -------------------------------------------------------------------------
    // Polyline (5-bit chunk) encoding.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Polyline-encode a list of points into a string suitable for web use. This is the Google
    /// "polyline" algorithm. Lat is encoded first then lon, as in the C++ original.
    /// </summary>
    /// <param name="points">The list of points to encode (longitude = first/x, latitude = second/y).</param>
    /// <param name="precision">Encoding precision. Defaults to <see cref="EncodePrecision"/> (1e6).</param>
    /// <returns>The encoded string.</returns>
    public static string Encode(IReadOnlyList<PointLL> points, double precision = EncodePrecision)
    {
        var output = new StringBuilder(points.Count * 8);

        // This is a diff encoding so we remember the last point we saw.
        int lastLon = 0;
        int lastLat = 0;
        foreach (PointLL p in points)
        {
            // Shift the decimal point places to the right and round.
            int lon = (int)Round(p.First * precision);
            int lat = (int)Round(p.Second * precision);

            // Encode each coordinate, lat first.
            WriteVarint5(output, ZigzagEncode(lat - lastLat));
            WriteVarint5(output, ZigzagEncode(lon - lastLon));

            lastLon = lon;
            lastLat = lat;
        }

        return output.ToString();
    }

    /// <summary>
    /// Polyline-decode a string into a list of points. Faithful port of the
    /// <c>Shape5Decoder</c>-backed <c>decode</c>.
    /// </summary>
    /// <param name="encoded">The encoded string.</param>
    /// <param name="precision">Decoding precision (1/encoding precision). Defaults to <see cref="DecodePrecision"/> (1e-6).</param>
    /// <returns>The decoded list of points.</returns>
    public static List<PointLL> Decode(string encoded, double precision = DecodePrecision)
    {
        var c = new List<PointLL>(encoded.Length / 4);
        int lat = 0;
        int lon = 0;
        int pos = 0;
        while (pos < encoded.Length)
        {
            lat += ZigzagDecode(ReadVarint5(encoded, ref pos));
            lon += ZigzagDecode(ReadVarint5(encoded, ref pos));
            c.Add(new PointLL(lon * precision, lat * precision));
        }

        return c;
    }

    // -------------------------------------------------------------------------
    // Varint (7-bit chunk) encoding (encode7 / decode7).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Varint-encode a list of points into a string. Lat is encoded first then lon, as in the
    /// C++ original. Faithful port of <c>encode7</c>.
    /// </summary>
    /// <param name="points">The list of points to encode.</param>
    /// <param name="precision">Encoding precision. Defaults to <see cref="EncodePrecision"/> (1e6).</param>
    /// <returns>The encoded string.</returns>
    public static string Encode7(IReadOnlyList<PointLL> points, double precision = EncodePrecision)
    {
        var output = new StringBuilder(points.Count * 8);

        // This is an offset encoding so we remember the last point we saw.
        int lastLon = 0;
        int lastLat = 0;
        foreach (PointLL p in points)
        {
            int lon = (int)Round(p.First * precision);
            int lat = (int)Round(p.Second * precision);

            // Encode each coordinate, lat first.
            WriteVarint7(output, ZigzagEncode(lat - lastLat));
            WriteVarint7(output, ZigzagEncode(lon - lastLon));

            lastLon = lon;
            lastLat = lat;
        }

        return output.ToString();
    }

    /// <summary>
    /// Varint-decode a string into a list of points. Faithful port of the
    /// <c>Shape7Decoder</c>-backed <c>decode7</c>.
    /// </summary>
    /// <param name="encoded">The encoded string.</param>
    /// <param name="precision">Decoding precision. Defaults to <see cref="DecodePrecision"/> (1e-6).</param>
    /// <returns>The decoded list of points.</returns>
    public static List<PointLL> Decode7(string encoded, double precision = DecodePrecision)
    {
        var c = new List<PointLL>(encoded.Length / 4);
        int lat = 0;
        int lon = 0;
        int pos = 0;
        while (pos < encoded.Length)
        {
            lat += ZigzagDecode(ReadVarint7(encoded, ref pos));
            lon += ZigzagDecode(ReadVarint7(encoded, ref pos));
            c.Add(new PointLL(lon * precision, lat * precision));
        }

        return c;
    }

    // -------------------------------------------------------------------------
    // Integer varint codec (encode7int / decode7int).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Varint-encode a container of integral values into a string. Uses an offset (delta) encoding
    /// with zig-zag of the delta. Faithful port of <c>encode7int</c>.
    /// </summary>
    /// <typeparam name="T">An integer value type (e.g. <see cref="int"/>, <see cref="long"/>, <see cref="uint"/>, <see cref="ulong"/>).</typeparam>
    /// <param name="values">The integral values to encode.</param>
    /// <returns>The encoded string.</returns>
    public static string Encode7Int<T>(IReadOnlyList<T> values)
        where T : IBinaryInteger<T>
    {
        var output = new StringBuilder(values.Count * 8);

        // This is an offset encoding so we remember the last value we saw.
        T lastValue = T.Zero;
        foreach (T value in values)
        {
            // diff = static_cast<signed>(value - last_value) computed in the unsigned domain so
            // that wrap-around matches C++ exactly (e.g. UINT64_MAX deltas).
            ulong uvalue = ToUInt64(value);
            ulong ulast = ToUInt64(lastValue);
            long diff = unchecked((long)(uvalue - ulast));
            WriteVarint7_64(output, ZigzagEncode(diff));
            lastValue = value;
        }

        return output.ToString();
    }

    /// <summary>
    /// Varint-decode a string into a container of integral values. Faithful port of the
    /// <c>Int7Decoder</c>-backed <c>decode7int</c>.
    /// </summary>
    /// <typeparam name="T">An integer value type.</typeparam>
    /// <param name="encoded">The encoded string.</param>
    /// <returns>The decoded list of integral values.</returns>
    public static List<T> Decode7Int<T>(string encoded)
        where T : IBinaryInteger<T>
    {
        var c = new List<T>(encoded.Length / 8);
        ulong value = 0;
        int pos = 0;
        while (pos < encoded.Length)
        {
            long diff = ZigzagDecode(ReadVarint7_64(encoded, ref pos));
            value = unchecked(value + (ulong)diff);
            c.Add(FromUInt64<T>(value));
        }

        return c;
    }

    // -------------------------------------------------------------------------
    // Internals.
    // -------------------------------------------------------------------------

    private static double Round(double v) => System.Math.Round(v, System.MidpointRounding.AwayFromZero);

    // Reinterpret an integral value of any width as its unsigned 64-bit representation,
    // sign-extending signed values (so that delta arithmetic wraps like the C++ unsigned domain).
    private static ulong ToUInt64<T>(T value)
        where T : IBinaryInteger<T>
    {
        long s = long.CreateTruncating(value);
        return unchecked((ulong)s);
    }

    private static T FromUInt64<T>(ulong value)
        where T : IBinaryInteger<T>
        => T.CreateTruncating(unchecked((long)value));

    // Write 5-bit chunks (+63 offset) - Google polyline format.
    private static void WriteVarint5(StringBuilder output, uint number)
    {
        while (number >= 0x20)
        {
            int nextValue = (int)(0x20 | (number & 0x1f)) + 63;
            output.Append((char)(byte)nextValue);
            number >>= 5;
        }

        output.Append((char)(byte)(number + 63));
    }

    // Read 5-bit chunks (+63 offset). Throws on truncated input.
    private static uint ReadVarint5(string encoded, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        uint b;
        do
        {
            if (pos >= encoded.Length)
            {
                throw new FormatException("Bad encoded polyline");
            }

            // C++ does: byte = uint32_t(*begin++) - 63; treating *begin as the raw char value.
            b = unchecked((uint)(byte)encoded[pos++] - 63);
            result |= (b & 0x1f) << shift;
            shift += 5;
        }
        while (b >= 0x20);

        return result;
    }

    // Write raw 7-bit chunks (high bit = continuation).
    private static void WriteVarint7(StringBuilder output, uint number)
    {
        while (number > 0x7f)
        {
            int nextValue = (int)(0x80 | (number & 0x7f));
            output.Append((char)(byte)nextValue);
            number >>= 7;
        }

        output.Append((char)(byte)(number & 0x7f));
    }

    // 64-bit variant of WriteVarint7 used by the integer codec (encode7int).
    private static void WriteVarint7_64(StringBuilder output, ulong number)
    {
        while (number > 0x7f)
        {
            int nextValue = (int)(0x80UL | (number & 0x7f));
            output.Append((char)(byte)nextValue);
            number >>= 7;
        }

        output.Append((char)(byte)(number & 0x7f));
    }

    // 64-bit variant of ReadVarint7 used by the integer codec (decode7int).
    private static ulong ReadVarint7_64(string encoded, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        uint b;
        do
        {
            if (pos >= encoded.Length)
            {
                throw new FormatException("Bad varint offset encoding");
            }

            b = (byte)encoded[pos++];
            result |= (ulong)(b & 0x7f) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        return result;
    }

    // Read raw 7-bit chunks (high bit = continuation). Throws on truncated input.
    private static uint ReadVarint7(string encoded, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        uint b;
        do
        {
            if (pos >= encoded.Length)
            {
                throw new FormatException("Bad varint offset encoding");
            }

            b = (byte)encoded[pos++];
            result |= (b & 0x7f) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        return result;
    }
}

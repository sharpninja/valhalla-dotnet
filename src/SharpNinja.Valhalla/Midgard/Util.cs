// Faithful C# port of the engine-needed subset of Valhalla midgard util.
// Sources:
//   valhalla/midgard/util.h
//   valhalla/midgard/util_core.h
//   src/midgard/util.cc
//
// This port deliberately includes ONLY the helpers used by the midgard/baldr
// routing path that can be expressed without types that have not yet been
// ported (PointLL spherical geometry, AABB2, DistanceApproximator, Tiles,
// Polyline2). Public members are PascalCase; semantics/precision are identical
// to the C++ originals (double-precision math is preserved where the C++ uses
// double).
//
// OMITTED (skadi/elevation/odin-only or depending on not-yet-ported types):
//   - ExpandMeters (needs AABB2<PointLL> + DistanceApproximator)
//   - resample_spherical_polyline / uniform_resample_spherical_polyline /
//     resample_polyline (needs PointLL spherical Distance/Heading)
//   - trim_shape, tangent_angle (needs PointLL IsValid/Heading)
//   - simulate_gps, to_boundary, projector_t (needs PointLL/Tiles/random GPS)
//   - memory_status (Linux /proc/self/status only; skadi/diagnostics)
//   - ToMap / ToSet (boost::property_tree conversion helpers, not engine math)
//   - Finally / make_finally (RAII scope-guard; use C# `using`/`try-finally`)
//   - unaligned_read (handled by BinaryPrimitives / MemoryMarshal where needed)
//   - ranged_default_t (lives in sif, not midgard)
//
// These omissions are noted here and re-stated where the corresponding gtest
// case is skipped in the ported test file.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Engine-needed midgard utility helpers ported from <c>valhalla/midgard/util.h</c>,
/// <c>util_core.h</c> and <c>src/midgard/util.cc</c>.
/// </summary>
public static class Util
{
    private const char PaddingEncoded = '=';
    private const char ZeroEncoded = 'A';

    /// <summary>
    /// Compute time (seconds) given a length (km) and speed (km per hour).
    /// Faithful port of <c>GetTime</c>.
    /// </summary>
    /// <param name="length">Distance in km.</param>
    /// <param name="speed">Speed in km per hour.</param>
    /// <returns>The computed time in seconds (truncated toward zero, matching the C++ cast).</returns>
    public static int GetTime(float length, float speed)
        => speed > 0.0f ? (int)((length / (speed * Constants.HourPerSec)) + 0.5f) : 0;

    /// <summary>
    /// Computes the turn degree based on the specified "from heading" and "to heading".
    /// Faithful port of <c>GetTurnDegree</c>. A perfect right turn returns 90.
    /// </summary>
    public static uint GetTurnDegree(uint fromHeading, uint toHeading)
        => ((toHeading - fromHeading) + 360) % 360;

    /// <summary>
    /// Compute the turn degree (from 0 to 180) - used in meili (map-matching).
    /// Faithful port of <c>get_turn_degree180</c>.
    /// </summary>
    /// <param name="inbound">Inbound heading.</param>
    /// <param name="outbound">Outbound heading.</param>
    /// <returns>The turn degree (0-180 degrees).</returns>
    /// <exception cref="ArgumentException">Thrown if angles are not within [0, 360).</exception>
    public static byte GetTurnDegree180(ushort inbound, ushort outbound)
    {
        if (!(inbound < 360 && outbound < 360))
        {
            // C++ throws std::invalid_argument.
            throw new ArgumentException("expect angles to be within [0, 360)");
        }

        int turn = Math.Abs(inbound - outbound);
        return (byte)(180 < turn ? 360 - turn : turn);
    }

    /// <summary>Convenience square method. Faithful port of <c>sqr</c>.</summary>
    public static T Sqr<T>(T a)
        where T : INumber<T>
        => a * a;

    /// <summary>
    /// Normalize a ratio and clamp to range [0, 1]. Protects against division by 0.
    /// Faithful port of <c>normalize</c>.
    /// </summary>
    public static float Normalize(float num, float den)
        => 0.0f == den ? 0.0f : Math.Min(Math.Max(num / den, 0.0f), 1.0f);

    /// <summary>
    /// Convert the input units, in either imperial or metric, into meters.
    /// Faithful port of <c>units_to_meters</c>.
    /// </summary>
    public static float UnitsToMeters(float unitsKmOrMi, bool isMetric)
        => Constants.MetersPerKm * (isMetric ? unitsKmOrMi : unitsKmOrMi * Constants.KmPerMile);

    /// <summary>Convert big endian bytes to little endian. Faithful port of <c>to_little_endian</c>.</summary>
    public static short ToLittleEndian(ushort val)
        => unchecked((short)((val << 8) | ((val >> 8) & 0x00ff)));

    /// <summary>Convert little endian bytes to big endian. Faithful port of <c>to_big_endian</c>.</summary>
    public static ushort ToBigEndian(ushort val)
        => unchecked((ushort)((val << 8) | (val >> 8)));

    /// <summary>
    /// For some variables, an invalid value needs to be set as the maximum value its type can get.
    /// Faithful port of <c>invalid&lt;numeric_t&gt;</c>.
    /// </summary>
    public static T Invalid<T>()
        where T : IMinMaxValue<T>
        => T.MaxValue;

    /// <summary>Returns true when the value equals the type's invalid (max) value.</summary>
    public static bool IsInvalid<T>(T value)
        where T : IMinMaxValue<T>, IEqualityOperators<T, T, bool>
        => value == T.MaxValue;

    /// <summary>Returns true when the value is not the type's invalid (max) value.</summary>
    public static bool IsValid<T>(T value)
        where T : IMinMaxValue<T>, IEqualityOperators<T, T, bool>
        => value != T.MaxValue;

    /// <summary>
    /// Circular range clamp. Faithful port of <c>circular_range_clamp</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="lower"/> &gt;= <paramref name="upper"/>.</exception>
    public static T CircularRangeClamp<T>(T value, T lower, T upper)
        where T : IFloatingPoint<T>
    {
        // yeah..
        if (lower >= upper)
        {
            // C++ throws std::runtime_error.
            throw new InvalidOperationException("invalid range for clamp");
        }

        // easy case
        if (lower <= value && value <= upper)
        {
            return value;
        }

        // see how far off the bottom of the range it is
        T i = upper - lower;
        if (value < lower)
        {
            T d = lower - value;
            d -= T.CreateTruncating(int.CreateTruncating(d / i)) * i;
            return upper - d;
        }

        // its past the top of the range
        T dd = value - upper;
        dd -= T.CreateTruncating(int.CreateTruncating(dd / i)) * i;
        return lower + dd;
    }

    /// <summary>Standard clamp. Faithful port of <c>clamp</c>: <c>max(min(value, upper), lower)</c>.</summary>
    public static T Clamp<T>(T value, T lower, T upper)
        where T : INumber<T>
        => T.Max(T.Min(value, upper), lower);

    /// <summary>
    /// Equality with an epsilon for approximation. Faithful port of <c>midgard::equal</c>.
    /// Delegates to <see cref="MidgardMath.Equal{T}(T,T,T)"/>.
    /// </summary>
    public static bool Equal<T>(T a, T b, T epsilon)
        where T : INumber<T>
        => MidgardMath.Equal(a, b, epsilon);

    /// <summary>Default-epsilon (1e-5) overload of <see cref="Equal{T}(T,T,T)"/>.</summary>
    public static bool Equal<T>(T a, T b)
        where T : INumber<T>
        => MidgardMath.Equal(a, b);

    /// <summary>
    /// Relative similarity test. Faithful port of <c>midgard::similar</c>.
    /// </summary>
    /// <param name="a">First operand.</param>
    /// <param name="b">Second operand.</param>
    /// <param name="similarity">Required ratio of min/max (default 0.99).</param>
    public static bool Similar<T>(T a, T b, double similarity = .99)
        where T : INumber<T>
    {
        if (a == T.Zero || b == T.Zero)
        {
            return a == b;
        }

        if ((a < T.Zero) != (b < T.Zero))
        {
            return false;
        }

        return (double.CreateChecked(T.Min(a, b)) / double.CreateChecked(T.Max(a, b))) >= similarity;
    }

    /// <summary>
    /// Compute the length of the polyline represented by a set of points.
    /// Faithful port of the container <c>length</c> overload.
    /// </summary>
    public static TPrecision Length<TPrecision>(IReadOnlyList<PointXY<TPrecision>> pts)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        if (pts.Count < 2)
        {
            return TPrecision.Zero;
        }

        TPrecision length = TPrecision.Zero;
        for (int i = 1; i < pts.Count; ++i)
        {
            length += pts[i].Distance(pts[i - 1]);
        }

        return length;
    }

    /// <summary>
    /// Compute the length of a polyline between the two specified indices [begin, end).
    /// Faithful port of the iterator <c>length</c> overload (returns 0 when begin == end).
    /// </summary>
    public static TPrecision Length<TPrecision>(
        IReadOnlyList<PointXY<TPrecision>> pts,
        int begin,
        int end)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        if (begin == end)
        {
            return TPrecision.Zero;
        }

        TPrecision length = TPrecision.Zero;
        for (int vertex = begin + 1; vertex < end; ++vertex)
        {
            length += pts[vertex - 1].Distance(pts[vertex]);
        }

        return length;
    }

    /// <summary>
    /// Create a new polyline by trimming an input polyline by a percentage from the start and a
    /// percentage from the end. Faithful port of <c>trim_polyline</c> (operates over [begin, end)).
    /// </summary>
    /// <param name="pts">Input polyline.</param>
    /// <param name="begin">Start index (inclusive).</param>
    /// <param name="end">End index (exclusive).</param>
    /// <param name="source">Percentage of total length to trim from the front.</param>
    /// <param name="target">Percentage of total length to trim from the end.</param>
    /// <returns>A new (possibly empty) polyline.</returns>
    public static List<PointXY<TPrecision>> TrimPolyline<TPrecision>(
        IReadOnlyList<PointXY<TPrecision>> pts,
        int begin,
        int end,
        TPrecision source,
        TPrecision target)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        TPrecision one = TPrecision.One;
        TPrecision zero = TPrecision.Zero;

        // Detect invalid cases
        if (target < source || target < zero || one < source || begin == end)
        {
            return new List<PointXY<TPrecision>>();
        }

        // Clamp source and target to range [0, 1]
        source = TPrecision.Min(TPrecision.Max(source, zero), one);
        target = TPrecision.Min(TPrecision.Max(target, zero), one);

        // Use precision from point type being iterated over
        TPrecision totalLength = Length(pts, begin, end);
        TPrecision prevVertexLength = zero;
        TPrecision sourceLength = totalLength * source;
        TPrecision targetLength = totalLength * target;

        // A state indicating if the position of current vertex is larger than source and smaller
        // than target.
        bool open = false;

        // Iterate segments and add to output container (clip)
        var clip = new List<PointXY<TPrecision>>();
        int prevVertex = begin;
        for (int vertex = begin + 1; vertex < end; ++vertex)
        {
            TPrecision segmentLength = pts[prevVertex].Distance(pts[vertex]);
            TPrecision vertexLength = prevVertexLength + segmentLength;

            // Open if source is located at current segment
            if (!open && sourceLength < vertexLength)
            {
                TPrecision offset = TPrecision.CreateChecked(
                    Normalize(
                        float.CreateChecked(sourceLength - prevVertexLength),
                        float.CreateChecked(segmentLength)));
                clip.Add(pts[prevVertex].PointAlongSegment(pts[vertex], offset));
                open = true;
            }

            // Open -> Close if target is located at current segment
            if (open && targetLength < vertexLength)
            {
                TPrecision offset = TPrecision.CreateChecked(
                    Normalize(
                        float.CreateChecked(targetLength - prevVertexLength),
                        float.CreateChecked(segmentLength)));
                clip.Add(pts[prevVertex].PointAlongSegment(pts[vertex], offset));
                open = false;
                break;
            }

            // Add the end vertex of current segment if it is in open state
            if (open)
            {
                clip.Add(pts[vertex]);
            }

            prevVertex = vertex;
            prevVertexLength = vertexLength;
        }

        if (clip.Count == 0)
        {
            // assert(1.f == source && 1.f == target)
            clip.Add(pts[prevVertex]);
            clip.Add(pts[prevVertex]);
        }

        return clip;
    }

    /// <summary>
    /// Trim the front of a polyline. Returns the trimmed portion; the supplied list is altered
    /// (the trimmed part is removed and its front replaced with the cut midpoint). Faithful port
    /// of <c>trim_front</c>.
    /// </summary>
    /// <param name="pts">Polyline (modified in place; result is the remaining points).</param>
    /// <param name="dist">Distance to trim.</param>
    /// <returns>The trimmed-off polyline of total length <paramref name="dist"/>.</returns>
    public static List<PointXY<TPrecision>> TrimFront<TPrecision>(
        List<PointXY<TPrecision>> pts,
        float dist)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        // Return if less than 2 points
        if (pts.Count < 2)
        {
            return new List<PointXY<TPrecision>>();
        }

        // Walk the polyline and accumulate length until it exceeds dist
        var result = new List<PointXY<TPrecision>> { pts[0] };
        double d = 0.0;
        for (int p1 = 0, p2 = 1; p2 < pts.Count; ++p1, ++p2)
        {
            double segdist = double.CreateChecked(pts[p1].Distance(pts[p2]));
            if ((d + segdist) > dist)
            {
                double frac = (dist - d) / segdist;
                PointXY<TPrecision> midpoint =
                    pts[p1].PointAlongSegment(pts[p2], TPrecision.CreateChecked(frac));
                result.Add(midpoint);

                // Remove used part of polyline (erase [begin, p1)), then set front to midpoint.
                pts.RemoveRange(0, p1);
                pts[0] = midpoint;
                return result;
            }

            d += segdist;
            result.Add(pts[p2]);
        }

        // Used all of the polyline without exceeding dist
        pts.Clear();
        return result;
    }

    /// <summary>
    /// Use the barycentric technique to test if point p is inside the triangle (a, b, c).
    /// Points on the triangle's nodes/edges are not considered contained. Done entirely in 2D.
    /// Faithful port of <c>triangle_contains</c> (uses double precision internally as in C++).
    /// </summary>
    public static bool TriangleContains<TPrecision>(
        PointXY<TPrecision> a,
        PointXY<TPrecision> b,
        PointXY<TPrecision> c,
        PointXY<TPrecision> p)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        double ax = double.CreateChecked(a.X);
        double ay = double.CreateChecked(a.Y);

        double v0x = double.CreateChecked(c.X) - ax;
        double v0y = double.CreateChecked(c.Y) - ay;
        double v1x = double.CreateChecked(b.X) - ax;
        double v1y = double.CreateChecked(b.Y) - ay;
        double v2x = double.CreateChecked(p.X) - ax;
        double v2y = double.CreateChecked(p.Y) - ay;

        double dot00 = v0x * v0x + v0y * v0y;
        double dot01 = v0x * v1x + v0y * v1y;
        double dot02 = v0x * v2x + v0y * v2y;
        double dot11 = v1x * v1x + v1y * v1y;
        double dot12 = v1x * v2x + v1y * v2y;

        double denom = dot00 * dot11 - dot01 * dot01;

        // Triangle with very small area, e.g., nearly a line.
        if (Math.Abs(denom) < 1e-20)
        {
            return false;
        }

        double u = (dot11 * dot02 - dot01 * dot12) / denom;
        double v = (dot00 * dot12 - dot01 * dot02) / denom;

        // Check if point is in triangle (slight tolerance for vertex coincidence).
        return (u >= 1e-16) && (v >= 1e-16) && (u + v < 1);
    }

    /// <summary>
    /// Return the intersection of two infinite lines if any. Faithful port of <c>intersect</c>.
    /// Returns false if the lines are parallel (or very nearly so).
    /// </summary>
    /// <param name="u">First point on first line.</param>
    /// <param name="v">Second point on first line.</param>
    /// <param name="a">First point on second line.</param>
    /// <param name="b">Second point on second line.</param>
    /// <param name="i">The intersection point, if there was one.</param>
    /// <returns>True if there was an intersection.</returns>
    public static bool Intersect<TPrecision>(
        PointXY<TPrecision> u,
        PointXY<TPrecision> v,
        PointXY<TPrecision> a,
        PointXY<TPrecision> b,
        out PointXY<TPrecision> i)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        TPrecision uvXd = u.First - v.First;
        TPrecision uvYd = u.Second - v.Second;
        TPrecision abXd = a.First - b.First;
        TPrecision abYd = a.Second - b.Second;
        TPrecision dCross = uvXd * abYd - abXd * uvYd;

        // parallel or very close to it
        if (TPrecision.Abs(dCross) < TPrecision.CreateChecked(1e-5))
        {
            i = new PointXY<TPrecision>();
            return false;
        }

        TPrecision uvCross = u.First * v.Second - u.Second * v.First;
        TPrecision abCross = a.First * b.Second - a.Second * b.First;
        i = new PointXY<TPrecision>(
            (uvCross * abXd - uvXd * abCross) / dCross,
            (uvCross * abYd - uvYd * abCross) / dCross);
        return true;
    }

    /// <summary>
    /// Check whether a given point lies within a polygon using the simplified winding number
    /// algorithm. Faithful port of <c>point_in_poly</c> (the quadrant/winding variant in util.cc).
    /// </summary>
    public static bool PointInPoly<TPrecision>(
        PointXY<TPrecision> pt,
        IReadOnlyList<PointXY<TPrecision>> poly)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        int quad = QuadrantType(poly[0], pt);
        int angle = 0;

        int it = 0;
        for (int idx = 0; idx < poly.Count; ++idx)
        {
            PointXY<TPrecision> vertex = poly[it];
            it++;
            if (it == poly.Count)
            {
                it = 0;
            }

            PointXY<TPrecision> nextVertex = poly[it];
            int nextQuad = QuadrantType(nextVertex, pt);
            int delta = nextQuad - quad;
            delta = AdjustDelta(delta, vertex, nextVertex, pt);
            angle += delta;
            quad = nextQuad;
        }

        return (angle == 4) || (angle == -4);
    }

    /// <summary>
    /// Compute the area of a polygon using the shoelace formula. Positive for counterclockwise
    /// wound polygons and negative otherwise. Faithful port of <c>polygon_area</c>.
    /// </summary>
    public static TPrecision PolygonArea<TPrecision>(IReadOnlyList<PointXY<TPrecision>> polygon)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        PointXY<TPrecision> first = polygon[0];
        PointXY<TPrecision> last = polygon[polygon.Count - 1];

        TPrecision area = last.Equals(first)
            ? TPrecision.Zero
            : (last.First * first.Second) - (last.Second * first.First);

        for (int p1 = 0, p2 = 1; p2 < polygon.Count; ++p1, ++p2)
        {
            area += (polygon[p1].First * polygon[p2].Second) - (polygon[p1].Second * polygon[p2].First);
        }

        return area * TPrecision.CreateChecked(0.5);
    }

    /// <summary>
    /// Encode a binary string as base64. Faithful port of <c>encode64</c>
    /// (RFC 4648 with standard padding).
    /// </summary>
    public static string Encode64(string text)
    {
        byte[] bytes = LatinBytes(text);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decode a base64 string to a binary string. Faithful port of <c>decode64</c>
    /// (tolerant of missing padding, as the boost variant is).
    /// </summary>
    public static string Decode64(string encoded)
    {
        int numPadChars = (4 - (encoded.Length % 4)) % 4;
        string padded = encoded + new string(PaddingEncoded, numPadChars);
        byte[] bytes = Convert.FromBase64String(padded);
        return LatinString(bytes);
    }

    /// <summary>
    /// Enumerate over a sequence, providing both index and value. C# analogue of the C++20
    /// <c>enumerate</c> helper (yields (index, value) pairs).
    /// </summary>
    public static IEnumerable<(int Index, T Value)> Enumerate<T>(IEnumerable<T> range)
    {
        int i = 0;
        foreach (T elem in range)
        {
            yield return (i++, elem);
        }
    }

    /// <summary>
    /// Convert a string to a floating-point value. Faithful port of <c>to_float</c>:
    /// parses a leading prefix, tolerates trailing garbage, rejects a lone "+", "++", "+-".
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the string cannot be converted.</exception>
    public static T ToFloat<T>(string value)
        where T : IFloatingPoint<T>
    {
        ReadOnlySpan<char> span = value.AsSpan();
        bool hadPlus = span.Length > 0 && span[0] == '+';
        if (hadPlus)
        {
            span = span[1..];
        }

        if (!TryParseLeadingFloat(span, out T result))
        {
            throw new ArgumentException($"Invalid float value: {value}");
        }

        if (hadPlus && result < T.Zero)
        {
            throw new ArgumentException($"Invalid float value: {value}");
        }

        return result;
    }

    /// <summary>Float overload of <see cref="ToFloat{T}"/>.</summary>
    public static float ToFloat(string value) => ToFloat<float>(value);

    /// <summary>
    /// Try to convert a string to an integer value. Faithful port of <c>try_to_int</c>:
    /// parses a leading integer prefix, tolerates trailing garbage, rejects a lone "+".
    /// </summary>
    public static bool TryToInt<T>(string value, out T result)
        where T : IBinaryInteger<T>, ISignedNumber<T>, IMinMaxValue<T>
        => TryToIntCore(value, out result);

    /// <summary>Unsigned-friendly try-parse used by <see cref="ToInt{T}"/> for types like uint.</summary>
    public static bool TryToIntUnsigned<T>(string value, out T result)
        where T : IBinaryInteger<T>, IMinMaxValue<T>, IUnsignedNumber<T>
    {
        result = T.Zero;
        ReadOnlySpan<char> span = value.AsSpan();
        bool hadPlus = span.Length > 0 && span[0] == '+';
        if (hadPlus)
        {
            span = span[1..];
        }

        int len = LeadingIntLength(span, allowSign: false);
        if (len == 0)
        {
            return false;
        }

        return T.TryParse(
            span[..len],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out result!);
    }

    /// <summary>
    /// Convert a string to a (signed) integer value. Faithful port of <c>to_int</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the string cannot be converted.</exception>
    public static T ToInt<T>(string value)
        where T : IBinaryInteger<T>, ISignedNumber<T>, IMinMaxValue<T>
    {
        if (!TryToIntCore(value, out T result))
        {
            throw new ArgumentException($"Invalid int value: {value}");
        }

        return result;
    }

    /// <summary>Int overload of <see cref="ToInt{T}"/>.</summary>
    public static int ToInt(string value) => ToInt<int>(value);

    /// <summary>
    /// Convert a string to an unsigned integer value (e.g. <c>uint</c>). Faithful port of
    /// <c>to_int</c> for unsigned types.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the string cannot be converted.</exception>
    public static T ToIntUnsigned<T>(string value)
        where T : IBinaryInteger<T>, IMinMaxValue<T>, IUnsignedNumber<T>
    {
        if (!TryToIntUnsigned(value, out T result))
        {
            throw new ArgumentException($"Invalid int value: {value}");
        }

        return result;
    }

    // ----- private helpers (ported anonymous-namespace functions in util.cc) -----

    // determines the quadrant of pt1 relative to pt2 (see ASCII diagram in util.cc)
    private static int QuadrantType<TPrecision>(PointXY<TPrecision> pt1, PointXY<TPrecision> pt2)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
        => pt1.First > pt2.First
            ? (pt1.Second > pt2.Second ? 0 : 3)
            : (pt1.Second > pt2.Second ? 1 : 2);

    // get the x intercept of an edge {pt1, pt2} with a horizontal line at a given y
    private static TPrecision XIntercept<TPrecision>(
        PointXY<TPrecision> pt1,
        PointXY<TPrecision> pt2,
        TPrecision y)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
        => pt2.First - ((pt2.Second - y) * ((pt1.First - pt2.First) / (pt1.Second - pt2.Second)));

    private static int AdjustDelta<TPrecision>(
        int delta,
        PointXY<TPrecision> vertex,
        PointXY<TPrecision> nextVertex,
        PointXY<TPrecision> p)
        where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
    {
        switch (delta)
        {
            // make quadrant deltas wrap around
            case 3:
                return -1;
            case -3:
                return 1;

            // when a quadrant was skipped, check if clockwise or counter-clockwise
            case 2:
            case -2:
                if (XIntercept(vertex, nextVertex, p.Second) > p.First)
                {
                    return -delta;
                }

                return delta;
            default:
                return delta;
        }
    }

    // Mirror the C++ boost base64 byte<->char treatment: each char is one octet (latin1).
    private static byte[] LatinBytes(string text)
    {
        var bytes = new byte[text.Length];
        for (int j = 0; j < text.Length; ++j)
        {
            bytes[j] = (byte)text[j];
        }

        return bytes;
    }

    private static string LatinString(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append((char)b);
        }

        return sb.ToString();
    }

    // Parse the longest valid floating-point prefix of span (mirrors std::from_chars behavior:
    // tolerates trailing characters, but the prefix itself must be a valid number).
    private static bool TryParseLeadingFloat<T>(ReadOnlySpan<char> span, out T result)
        where T : IFloatingPoint<T>
    {
        result = default!;
        int len = LeadingFloatLength(span);
        if (len == 0)
        {
            return false;
        }

        return T.TryParse(
            span[..len],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result!);
    }

    private static bool TryToIntCore<T>(string value, out T result)
        where T : IBinaryInteger<T>, IMinMaxValue<T>
    {
        result = T.Zero;
        ReadOnlySpan<char> span = value.AsSpan();
        bool hadPlus = span.Length > 0 && span[0] == '+';
        if (hadPlus)
        {
            span = span[1..];
        }

        // After consuming a leading '+', from_chars does not accept another sign.
        int len = LeadingIntLength(span, allowSign: true);
        if (len == 0)
        {
            return false;
        }

        if (!T.TryParse(span[..len], NumberStyles.Integer, CultureInfo.InvariantCulture, out result!))
        {
            return false;
        }

        // reject cases like "+-1"
        if (hadPlus && result < T.Zero)
        {
            return false;
        }

        return true;
    }

    // Length of the leading run that std::from_chars would consume for an integer.
    private static int LeadingIntLength(ReadOnlySpan<char> span, bool allowSign)
    {
        int idx = 0;
        if (allowSign && idx < span.Length && span[idx] == '-')
        {
            idx++;
        }

        int digitsStart = idx;
        while (idx < span.Length && span[idx] >= '0' && span[idx] <= '9')
        {
            idx++;
        }

        // need at least one digit
        return idx > digitsStart ? idx : 0;
    }

    // Length of the leading run that std::from_chars would consume for a float
    // (sign, integer part, fraction, exponent). Conservative but sufficient for the tests.
    private static int LeadingFloatLength(ReadOnlySpan<char> span)
    {
        int idx = 0;

        // std::from_chars accepts only a leading '-' for floats (never '+'); a '+' here means
        // the input had a sign the (already-consumed) '+' prefix did not account for, e.g. "++1.1".
        if (idx < span.Length && span[idx] == '-')
        {
            idx++;
        }

        int intStart = idx;
        while (idx < span.Length && span[idx] >= '0' && span[idx] <= '9')
        {
            idx++;
        }

        bool hasInt = idx > intStart;

        bool hasFrac = false;
        if (idx < span.Length && span[idx] == '.')
        {
            idx++;
            int fracStart = idx;
            while (idx < span.Length && span[idx] >= '0' && span[idx] <= '9')
            {
                idx++;
            }

            hasFrac = idx > fracStart;
        }

        if (!hasInt && !hasFrac)
        {
            return 0;
        }

        // optional exponent
        if (idx < span.Length && (span[idx] == 'e' || span[idx] == 'E'))
        {
            int expStart = idx;
            idx++;
            if (idx < span.Length && (span[idx] == '-' || span[idx] == '+'))
            {
                idx++;
            }

            int expDigits = idx;
            while (idx < span.Length && span[idx] >= '0' && span[idx] <= '9')
            {
                idx++;
            }

            // malformed exponent: roll back to before 'e'
            if (idx == expDigits)
            {
                idx = expStart;
            }
        }

        return idx;
    }

    /// <summary>
    /// Compute the tangent angle (heading) of a polyline at <paramref name="index"/> relative to a
    /// point on (or near) the shape, walking outward by <paramref name="sampleDistance"/> meters in
    /// both directions to get a stable heading. Faithful port of <c>midgard::tangent_angle</c>
    /// (src/midgard/util.cc).
    /// </summary>
    /// <param name="index">Index of the segment in <paramref name="shape"/> the point lies on.</param>
    /// <param name="point">The point on/near the shape from which to measure the tangent.</param>
    /// <param name="shape">The polyline shape.</param>
    /// <param name="sampleDistance">How far (meters) to walk along the shape to sample the heading.</param>
    /// <param name="forward">Whether the heading should be in the forward shape direction.</param>
    /// <param name="firstSegmentIndex">Lower bound segment index to walk back to (default 0).</param>
    /// <param name="lastSegmentIndex">Upper bound segment index to walk forward to (default max).</param>
    /// <returns>The tangent heading in degrees.</returns>
    public static float TangentAngle(
        int index,
        PointLL point,
        IReadOnlyList<PointLL> shape,
        float sampleDistance,
        bool forward,
        int firstSegmentIndex = 0,
        int lastSegmentIndex = int.MaxValue)
    {
        // assert(!shape.empty()); assert(index < shape.size());
        firstSegmentIndex = Math.Min(firstSegmentIndex, index);
        lastSegmentIndex = Math.Min(Math.Max(lastSegmentIndex, index), shape.Count - 1);

        // depending on if we are going forward or backward we choose a different increment
        int increment = forward ? -1 : 1;
        int firstEnd = forward ? firstSegmentIndex : lastSegmentIndex;
        int secondEnd = forward ? lastSegmentIndex : firstSegmentIndex;

        // u and v will be points we move along the shape until we have enough distance between them
        // or run out of points.

        // move backwards until we have enough or run out
        float remaining = sampleDistance;
        PointLL u = point;
        int i = index + (forward ? 1 : 0);
        while (remaining > 0 && i != firstEnd)
        {
            // move along and see how much distance that added
            i += increment;
            double d = u.Distance(shape[i]);
            // are we done yet?
            if (remaining <= d)
            {
                double coef = remaining / d;
                u = u.PointAlongSegment(shape[i], coef);
                return (float)u.Heading(point);
            }

            // next one
            u = shape[i];
            remaining -= (float)d;
        }

        // move forwards until we have enough or run out
        PointLL v = point;
        i = index + (forward ? 0 : 1);
        while (remaining > 0 && i != secondEnd)
        {
            // move along and see how much distance that added
            i -= increment;
            double d = v.Distance(shape[i]);
            // are we done yet?
            if (remaining <= d)
            {
                double coef = remaining / d;
                v = v.PointAlongSegment(shape[i], coef);
                return (float)u.Heading(v);
            }

            // next one
            v = shape[i];
            remaining -= (float)d;
        }

        return (float)u.Heading(v);
    }

    /// <summary>
    /// Trims a polyline shape so it starts at <paramref name="startVertex"/> (at distance
    /// <paramref name="start"/> meters along) and ends at <paramref name="endVertex"/> (at distance
    /// <paramref name="end"/> meters along). The shape is modified in place. Faithful port of
    /// <c>midgard::trim_shape(float start, PointLL start_vertex, float end, PointLL end_vertex,
    /// std::vector&lt;PointLL&gt;&amp; shape)</c> (src/midgard/util.cc).
    /// </summary>
    /// <param name="start">Distance (meters) along the shape where the trimmed shape should begin.</param>
    /// <param name="startVertex">The vertex to use as the new beginning (ignored if not valid).</param>
    /// <param name="end">Distance (meters) along the shape where the trimmed shape should end.</param>
    /// <param name="endVertex">The vertex to use as the new end (ignored if not valid).</param>
    /// <param name="shape">The shape to trim (modified in place).</param>
    public static void TrimShape(float start, PointLL startVertex, float end, PointLL endVertex, List<PointLL> shape)
    {
        // clip up to the start point if the start_vertex is valid
        float along = 0f;
        if (startVertex.IsValid())
        {
            // find the spot at which we cross the distance threshold and stop
            int current = 0;
            for (; shape.Count != 0 && current != shape.Count - 1 && along <= start; ++current)
            {
                along += (float)shape[current + 1].Distance(shape[current]);
            }

            // we found the spot to stop for the beginning of the shape so set it to the new beginning
            // *(--current) = start_vertex;  then  shape.erase(shape.begin(), current);
            --current;
            shape[current] = startVertex;
            shape.RemoveRange(0, current);
            along = start;
        }

        // clip after the end point if the end vertex is valid
        if (endVertex.IsValid())
        {
            // find the point at which we cross the distance threshold and stop
            int current = 0;
            for (; shape.Count != 0 && current != shape.Count - 1 && along <= end; ++current)
            {
                along += (float)shape[current + 1].Distance(shape[current]);
            }

            // found the spot to stop for the end of the shape so set it to the new end
            // *(current) = end_vertex;  then  shape.erase(++current, shape.end());
            shape[current] = endVertex;
            int eraseFrom = current + 1;
            if (eraseFrom < shape.Count)
            {
                shape.RemoveRange(eraseFrom, shape.Count - eraseFrom);
            }
        }
    }
}

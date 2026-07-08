// Faithful C# port of the engine-relevant cases from Valhalla's gtest suite
// test/util_midgard.cc. Each [Fact] mirrors a TEST(UtilMidgard, ...) case with the
// same inputs and expected values.
//   EXPECT_EQ           -> Assert.Equal (exact)
//   EXPECT_TRUE/FALSE   -> Assert.True / Assert.False
//   EXPECT_NEAR(a,b,e)  -> Assert.Equal(expected, actual, tolerance)
//   EXPECT_THROW        -> Assert.Throws
//
// OMITTED cases (depend on PointLL spherical geometry / AABB2 / Polyline2 /
// DistanceApproximator / sif, which are outside the engine-needed util subset
// ported here):
//   - TestRangedDefaultT      (sif::ranged_default_t)
//   - MemoryStatus            (Linux /proc/self/status diagnostics)
//   - TestResample / TestResampleDuplicate / TestResampleNaN
//                             (resample_spherical_polyline / uniform_resample / Polyline2)
//   - TestTrimPolylineWithFloatGeoPoint / TestTrimPolylineWithDoubleGeoPoint
//                             (GeoPoint/PointLL law-of-cosines distance)
//   - TestTangentAngle / TestTangentAngleOnSegment / TrimShape* (PointLL Heading/IsValid)
//   - TestExpandLocation      (ExpandMeters + AABB2 + DistanceApproximator)
// All other UtilMidgard cases are ported below.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SharpNinja.Valhalla.Midgard;

using Point2 = SharpNinja.Valhalla.Midgard.PointXY<float>;
using Point2d = SharpNinja.Valhalla.Midgard.PointXY<double>;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class UtilTests
{
    // TEST(UtilMidgard, TestGetTurnDegree)
    [Fact]
    public void TestGetTurnDegree()
    {
        // Slight Right
        Assert.Equal(20u, Util.GetTurnDegree(315, 335));
        // Right
        Assert.Equal(90u, Util.GetTurnDegree(0, 90));
        // Right
        Assert.Equal(90u, Util.GetTurnDegree(90, 180));
        // Sharp Right
        Assert.Equal(160u, Util.GetTurnDegree(180, 340));
        // Sharp Right
        Assert.Equal(172u, Util.GetTurnDegree(180, 352));
        // Sharp Left
        Assert.Equal(220u, Util.GetTurnDegree(180, 40));
        // Sharp Left
        Assert.Equal(190u, Util.GetTurnDegree(180, 10));
        // Left
        Assert.Equal(180u, Util.GetTurnDegree(0, 180));
        // Left
        Assert.Equal(270u, Util.GetTurnDegree(270, 180));
        // Slight Left
        Assert.Equal(340u, Util.GetTurnDegree(90, 70));
        // Continue
        Assert.Equal(4u, Util.GetTurnDegree(358, 2));
    }

    // TEST(UtilMidgard, TestGetTime)
    [Fact]
    public void TestGetTime()
    {
        Assert.Equal(3600, Util.GetTime(100, 100));
        Assert.Equal(900, Util.GetTime(5, 20));
        Assert.Equal(0, Util.GetTime(5, 0));
    }

    // TEST(UtilMidgard, AppxEqual)
    [Fact]
    public void AppxEqual()
    {
        Assert.True(Util.Equal<float>(-136.170790f, -136.170800f, .00002f));
        Assert.True(Util.Equal<float>(-136.170800f, -136.170790f, .00002f));
        Assert.True(Util.Equal<float>(16.645590f, 16.645580f, .00002f));
        Assert.True(Util.Equal<float>(76.627980f, 76.627970f, .00002f));
        Assert.True(Util.Equal<int>(0, 0));
        Assert.True(Util.Equal<float>(1, 1, 0));
    }

    // TEST(UtilMidgard, TestClamp)
    [Fact]
    public void TestClamp()
    {
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(467, -90, 90), -73));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(-467, -90, 90), 73));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(7, -90, 90), 7));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(-67, -90, 90), -67));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(-97, -90, 90), 83));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(-97.2f, -90, 90), 82.8f));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(-180, -90, 90), 0));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(270, -90, 90), -90));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(369, 0, 360), 9));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(-369, 0, 360), 351));
        Assert.True(Util.Equal<float>(Util.CircularRangeClamp<float>(739, -45, -8), -38));

        // Test invalid range - should throw an exception (lower > upper)
        Assert.Throws<InvalidOperationException>(() => Util.CircularRangeClamp<float>(739, -8, -45));

        // get_turn_degree180 should throw when inputs not clamped to [0, 360)
        Assert.Throws<ArgumentException>(() => Util.GetTurnDegree180(420, 580));
    }

    // TEST(UtilMidgard, TestSimilarAndEqual)
    [Fact]
    public void TestSimilarAndEqual()
    {
        // Make sure no negative epsilons are allowed in equal
        Assert.Throws<InvalidOperationException>(() => Util.Equal<float>(10.0f, 10.0f, -0.0001f));

        // Test the equality case
        Assert.True(Util.Similar<float>(45.0f, 45.0f, 0.0001f)); // Similar test fails for equal values

        // Opposing signs should not be similar regardless of difference
        Assert.False(Util.Similar<float>(0.00001f, -0.00001f, 0.0001f));
    }

    // TEST(UtilMidgard, TestTrimPolyline)
    [Fact]
    public void TestTrimPolyline()
    {
        var line = new List<Point2>
        {
            new(0, 0), new(0, 0), new(20, 20), new(31, 1),
            new(31, 1), new(12, 23), new(7, 2), new(7, 2),
        };

        float LineLen() => Util.Length<float>(line);

        var clip = Util.TrimPolyline(line, 0, line.Count, 0.0f, 1.0f);
        // Should not clip anything if range is [0, 1]
        Assert.Equal(LineLen(), Util.Length<float>(clip), Point2Tests_Epsilon);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.0f, 0.1f);
        Assert.Equal(LineLen() * 0.1f, Util.Length<float>(clip), 1e-5f);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.5f, 1.0f);
        Assert.Equal(LineLen() * 0.5f, Util.Length<float>(clip), 1e-5f);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.5f, 0.7f);
        Assert.Equal(LineLen() * 0.2f, Util.Length<float>(clip), 1e-5f);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.65f, 0.7f);
        Assert.Equal(LineLen() * 0.05f, Util.Length<float>(clip), 1e-5f);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.4999f, 0.5f);
        Assert.Equal(LineLen() * 0.0001f, Util.Length<float>(clip), 1e-5f);

        // nothing should be clipped since [0.65, 0.5]
        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, 0.65f, 0.5f));

        // nothing should be clipped since negative [-2, -1]
        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, -2.0f, -1.0f));

        // nothing should be clipped since empty set [0, 0]
        Assert.Equal(new Point2(0, 0), Util.TrimPolyline(line, 0, line.Count, 0.0f, 0.0f)[^1]);

        // nothing should be clipped since out of range [-1, 0]
        Assert.Equal(new Point2(0, 0), Util.TrimPolyline(line, 0, line.Count, -1.0f, 0.0f)[^1]);

        // nothing should be clipped since [1, 1]
        Assert.Equal(new Point2(7, 2), Util.TrimPolyline(line, 0, line.Count, 1.0f, 1.0f)[0]);

        // nothing should be clipped since out of range [1, 2]
        Assert.Equal(new Point2(7, 2), Util.TrimPolyline(line, 0, line.Count, 1.0f, 2.0f)[0]);

        // nothing should be clipped since out of range [1.001, 2]
        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, 1.001f, 2.0f));

        // nothing should be clipped since empty set [0.5, 0.1]
        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, 0.5f, 0.1f));

        // Make sure length returns 0 when iterator is equal
        Assert.Equal(0.0f, Util.Length<float>(clip, 0, 0));
    }

    // TEST(UtilMidgard, TestTrimPolylineWithDoubles)
    [Fact]
    public void TestTrimPolylineWithDoubles()
    {
        var line = new List<Point2d>
        {
            new(0, 0), new(0, 0), new(20, 20), new(31, 1),
            new(31, 1), new(12, 23), new(7, 2), new(7, 2),
        };

        double LineLen() => Util.Length<double>(line);

        var clip = Util.TrimPolyline(line, 0, line.Count, 0.0, 1.0);
        // Should not clip anything if range is [0, 1]
        Assert.Equal(LineLen(), Util.Length<double>(clip), 1e-9);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.0, 0.1);
        Assert.Equal(LineLen() * 0.1, Util.Length<double>(clip), 1e-5);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.5, 1.0);
        Assert.Equal(LineLen() * 0.5, Util.Length<double>(clip), 1e-5);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.5, 0.7);
        Assert.Equal(LineLen() * 0.2, Util.Length<double>(clip), 1e-5);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.65, 0.7);
        Assert.Equal(LineLen() * 0.05, Util.Length<double>(clip), 1e-5);

        clip = Util.TrimPolyline(line, 0, line.Count, 0.4999, 0.5);
        Assert.Equal(LineLen() * 0.0001, Util.Length<double>(clip), 1e-5);

        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, 0.65, 0.5));
        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, -2.0, -1.0));
        Assert.Equal(new Point2d(0, 0), Util.TrimPolyline(line, 0, line.Count, 0.0, 0.0)[^1]);
        Assert.Equal(new Point2d(0, 0), Util.TrimPolyline(line, 0, line.Count, -1.0, 0.0)[^1]);
        Assert.Equal(new Point2d(7, 2), Util.TrimPolyline(line, 0, line.Count, 1.0, 1.0)[0]);
        Assert.Equal(new Point2d(7, 2), Util.TrimPolyline(line, 0, line.Count, 1.0, 2.0)[0]);
        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, 1.001, 2.0));
        Assert.Empty(Util.TrimPolyline(line, 0, line.Count, 0.5, 0.1));

        // Make sure length returns 0 when iterator is equal
        Assert.Equal(0.0, Util.Length<double>(clip, 0, 0));
    }

    // TEST(UtilMidgard, TestTrimFront)
    [Fact]
    public void TestTrimFront()
    {
        var pts = new List<Point2>
        {
            new(-1.0f, -1.0f), new(-1.0f, 1.0f), new(0.0f, 1.0f),
            new(1.0f, 1.0f), new(4.0f, 5.0f), new(5.0f, 5.0f),
        };

        const float tolerance = 0.0001f;
        float l = Util.Length<float>(pts);
        var trim = Util.TrimFront(pts, 9.0f);
        Assert.Equal(9.0f, Util.Length<float>(trim), tolerance); // incorrect length of trimmed polyline
        Assert.True(Math.Abs(l - Util.Length<float>(pts) - 9.0f) <= tolerance);

        Assert.Equal(2, pts.Count); // number of remaining points not correct

        var pts2 = new List<Point2>
        {
            new(-81.0f, -45.0f), new(-18.0f, 17.0f), new(8.0f, 8.0f),
            new(6.0f, 19.0f), new(49.0f, -5.0f), new(75.0f, 45.0f),
        };
        l = Util.Length<float>(pts2);
        float d = l * 0.75f;
        var trim2 = Util.TrimFront(pts2, d);
        Assert.Equal(d, Util.Length<float>(trim2), tolerance); // incorrect length of trimmed polyline 2

        float d2 = l * 0.25f;
        float l2 = Util.Length<float>(pts2);
        Assert.Equal(d2, l2, tolerance); // length of remaining polyline 2 does not match

        // If trim distance exceeds polyline length the entire polyline is returned and none remains
        var pts3 = new List<Point2>
        {
            new(-81.0f, -45.0f), new(-18.0f, 17.0f), new(8.0f, 8.0f),
            new(6.0f, 19.0f), new(49.0f, -5.0f), new(75.0f, 45.0f),
        };
        int n = pts3.Count;
        l = Util.Length<float>(pts3);
        var trim3 = Util.TrimFront(pts3, l + 1.0f);
        Assert.Equal(l, Util.Length<float>(trim3), tolerance);
        Assert.Equal(n, trim3.Count);
        Assert.True(pts3.Count <= 0); // some of original polyline remains when trim exceeds length
    }

    // TEST(UtilMidgard, TestLengthWithEmptyVector)
    [Fact]
    public void TestLengthWithEmptyVector()
    {
        var empty = new List<Point2>();
        Assert.Equal(0.0f, Util.Length<float>(empty)); // empty polyline returns non-zero length

        // Test with only 1 point, should still return 0
        empty.Add(new Point2(-70.0f, 30.0f));
        Assert.Equal(0.0f, Util.Length<float>(empty)); // one point polyline returns non-zero length
    }

    // TEST(UtilMidgard, Base64) - cases from RFC 4648 section 10
    [Fact]
    public void Base64()
    {
        var cases = new (string Decoded, string Encoded)[]
        {
            ("", ""),
            ("f", "Zg=="),
            ("fo", "Zm8="),
            ("foo", "Zm9v"),
            ("foob", "Zm9vYg=="),
            ("fooba", "Zm9vYmE="),
            ("foobar", "Zm9vYmFy"),
        };

        foreach ((string decoded, string encoded) in cases)
        {
            Assert.Equal(encoded, Util.Encode64(decoded));
            Assert.Equal(decoded, Util.Decode64(encoded));
            Assert.Equal(decoded, Util.Decode64(Util.Encode64(decoded)));
        }
    }

    // TEST(UtilMidgard, SequenceSort)
    [Fact]
    public void SequenceSort()
    {
        string mergePath = Path.Combine(Path.GetTempPath(), $"char_sequence_test_merge_{Guid.NewGuid():N}.bin");
        string standardPath = Path.Combine(Path.GetTempPath(), $"char_sequence_test_standard_{Guid.NewGuid():N}.bin");

        try
        {
            var inMem = new List<byte>();
            using var merge = new Sequence<byte>(mergePath, create: true, writeBufferSize: 1327);
            using var standard = new Sequence<byte>(standardPath, create: true, writeBufferSize: 1327 * 5);

            var rng = new Random(12345);
            for (int i = 0; i < (int)(1327 * 4.5); ++i)
            {
                var nv = (byte)rng.Next(0, byte.MaxValue);
                inMem.Add(nv);
                merge.PushBack(nv);
                standard.PushBack(nv);
            }

            inMem.Sort();
            merge.Sort((a, b) => a < b, 1327);
            standard.Sort((a, b) => a < b, 1327 * 5);

            Assert.True(inMem.SequenceEqual(merge));
            Assert.True(inMem.SequenceEqual(standard));
        }
        finally
        {
            SafeDelete(mergePath);
            SafeDelete(standardPath);
        }
    }

    // TEST(UtilMidgard, TriangleContains)
    [Fact]
    public void TriangleContains()
    {
        var a = new Point2d(1, 1);
        var b = new Point2d(2, 1);
        var c = new Point2d(2, 2);

        // obviously not in triangle
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(0, 0)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(1, 0)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(2, 0)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(3, 1)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(3, 3)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(2, 2)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(0, 1)));

        // close but not in triangle
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(1.01, 1.1)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d(1.5, 0.99)));

        // in triangle
        Assert.True(Util.TriangleContains(a, b, c, new Point2d(1.2, 1.01)));
        Assert.True(Util.TriangleContains(a, b, c, new Point2d(1.5, 1.3)));
        Assert.True(Util.TriangleContains(a, b, c, new Point2d(1.7, 1.1)));

        // triangle corners are not considered contained
        Assert.False(Util.TriangleContains(a, b, c, a));
        Assert.False(Util.TriangleContains(a, b, c, b));
        Assert.False(Util.TriangleContains(a, b, c, c));

        // triangle edges are not considered contained
        Assert.False(Util.TriangleContains(a, b, c, new Point2d((a.X + b.X) / 2, (a.Y + b.Y) / 2)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d((a.X + c.X) / 2, (a.Y + c.Y) / 2)));
        Assert.False(Util.TriangleContains(a, b, c, new Point2d((c.X + b.X) / 2, (c.Y + b.Y) / 2)));
    }

    // TEST(UtilMidgard, PolygonArea)
    [Fact]
    public void PolygonArea()
    {
        var a = new List<Point2d> { new(1, 1), new(2, 2), new(3, 1) };

        // area is negative in case of clockwise order
        Assert.Equal(-1.0, Util.PolygonArea<double>(a), 1e-7);

        a.Reverse();

        // area is positive in case of counterclockwise order
        Assert.Equal(1.0, Util.PolygonArea<double>(a), 1e-7);
    }

    // TEST(UtilMidgard, Enumerate)
    [Fact]
    public void Enumerate()
    {
        var values = new List<int> { 10, 20, 30, 40, 50 };

        int expectedIndex = 0;
        foreach ((int i, int value) in Util.Enumerate(values))
        {
            Assert.Equal(expectedIndex, i);
            Assert.Equal(values[expectedIndex], value);
            ++expectedIndex;
        }

        Assert.Equal(values.Count, expectedIndex);

        // Test with filtered range (lazy evaluation)
        var filtered = values.Where(x => x > 20);
        var enumeratedResults = new List<(int, int)>();
        foreach ((int i, int value) in Util.Enumerate(filtered))
        {
            enumeratedResults.Add((i, value));
        }

        Assert.Equal(3, enumeratedResults.Count);
        Assert.Equal((0, 30), enumeratedResults[0]);
        Assert.Equal((1, 40), enumeratedResults[1]);
        Assert.Equal((2, 50), enumeratedResults[2]);
    }

    // TEST(UtilMidgard, ToFloat)
    [Fact]
    public void ToFloat()
    {
        Assert.Equal(123.456f, Util.ToFloat("123.456"), 1e-3f);
        Assert.Equal(-42.5f, Util.ToFloat("-42.5"), 1e-5f);
        Assert.Equal(0.0f, Util.ToFloat("0.0"));
        Assert.Equal(3.14159, Util.ToFloat<double>("3.14159"), 1e-5);
        Assert.Equal(123.456f, Util.ToFloat("123.456extra"), 1e-3f);
        Assert.Equal(1.1f, Util.ToFloat("+1.1"), 1e-5f);
        Assert.Equal(0.0f, Util.ToFloat("+0"));
        Assert.Equal(0.0f, Util.ToFloat("-0"));
        Assert.Equal(-1.0f, Util.ToFloat("-1"));

        Assert.Throws<ArgumentException>(() => Util.ToFloat("not_a_number"));
        Assert.Throws<ArgumentException>(() => Util.ToFloat(""));
        Assert.Throws<ArgumentException>(() => Util.ToFloat("+"));
        Assert.Throws<ArgumentException>(() => Util.ToFloat("++1.1"));
        Assert.Throws<ArgumentException>(() => Util.ToFloat("+-1.1"));
    }

    // TEST(UtilMidgard, ToInt)
    [Fact]
    public void ToInt()
    {
        Assert.Equal(123, Util.ToInt("123"));
        Assert.Equal(-456, Util.ToInt("-456"));
        Assert.Equal(0, Util.ToInt("0"));
        Assert.Equal(9223372036854775807L, Util.ToInt<long>("9223372036854775807"));
        Assert.Equal(4294967295U, Util.ToIntUnsigned<uint>("4294967295"));
        Assert.Equal(123, Util.ToInt("123.456"));
        Assert.Equal(1, Util.ToInt("+1"));
        Assert.Equal(0, Util.ToInt("+0"));
        Assert.Equal(0, Util.ToInt("-0"));
        Assert.Equal(-1, Util.ToInt("-1"));

        Assert.Throws<ArgumentException>(() => Util.ToInt("not_a_number"));
        Assert.Throws<ArgumentException>(() => Util.ToInt(""));
        Assert.Throws<ArgumentException>(() => Util.ToInt("+"));
        Assert.Throws<ArgumentException>(() => Util.ToInt("++1"));
        Assert.Throws<ArgumentException>(() => Util.ToInt("+-1"));
    }

    private const float Point2Tests_Epsilon = Constants.Epsilon;

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}

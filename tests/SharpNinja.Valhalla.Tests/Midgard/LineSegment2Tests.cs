// Faithful C# port of Valhalla's gtest suite test/linesegment2.cc.
// Each [Fact] mirrors a TEST(Linesegment, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal (exact); EXPECT_NEAR(a,b,eps) -> Assert.Equal(expected, actual, tol);
// EXPECT_TRUE -> Assert.True. The C++ kEpsilon is Constants.Epsilon.
//
// Note: the original suite's TestIntersect also exercises LineSegment2<PointLL>. PointLL is not
// part of this port's scope; its planar Intersect math is identical to the double-precision
// coordinate case. Those PointLL cases are therefore realized here with LineSegment2d
// (LineSegment2T<double>) using the same numeric inputs, faithfully matching the planar arithmetic
// that LineSegment2<GeoPoint<double>>::Intersect performs (value_type = double).

using SharpNinja.Valhalla.Midgard;

using LineSegment2 = SharpNinja.Valhalla.Midgard.LineSegment2T<float>;
using LineSegment2d = SharpNinja.Valhalla.Midgard.LineSegment2T<double>;
using Point2 = SharpNinja.Valhalla.Midgard.PointXY<float>;
using Point2d = SharpNinja.Valhalla.Midgard.PointXY<double>;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class LineSegment2Tests
{
    private const float Epsilon = Constants.Epsilon;

    [Fact]
    public void TestDefaultConstructor()
    {
        var l = new LineSegment2();
        var expected = new LineSegment2(new Point2(0.0f, 0.0f), new Point2(0.0f, 0.0f));
        Assert.True(l.ApproximatelyEqual(expected));
    }

    private static void TryDistance(Point2 p, LineSegment2 s, float res, Point2 exp)
    {
        float d = s.Distance(p, out Point2 closest);
        Assert.Equal(res, d, Epsilon);
        Assert.Equal(exp.X, closest.X, Epsilon);
        Assert.Equal(exp.Y, closest.Y, Epsilon);
    }

    [Fact]
    public void TestDistance()
    {
        // Test segment
        var a = new Point2(-2.0f, -2.0f);
        var b = new Point2(4.0f, 4.0f);
        var s1 = new LineSegment2(a, b);

        // Case 1 - point is "before start" of segment. a is closest
        TryDistance(new Point2(-4.0f, -2.0f), s1, 2.0f, a);

        // Case 2 - point is after end of segment. b is closest
        TryDistance(new Point2(6.0f, 4.0f), s1, 2.0f, b);

        // Case 3 - closest point is along segment
        TryDistance(new Point2(0.0f, 2.0f), s1, MathF.Sqrt(2.0f), new Point2(1.0f, 1.0f));
    }

    private static void TryIntersect(LineSegment2 s1, LineSegment2 s2, bool res, Point2 exp)
    {
        bool doesintersect = s1.Intersect(s2, out Point2 intersect);
        Assert.Equal(res, doesintersect);
        if (doesintersect)
        {
            Assert.Equal(exp.X, intersect.X, Epsilon);
            Assert.Equal(exp.Y, intersect.Y, Epsilon);
        }
    }

    private static void TryIntersectLL(LineSegment2d s1, LineSegment2d s2, bool res, Point2d exp)
    {
        bool doesintersect = s1.Intersect(s2, out Point2d intersect);
        Assert.Equal(res, doesintersect);
        if (doesintersect)
        {
            Assert.Equal(exp.X, intersect.X, Epsilon);
            Assert.Equal(exp.Y, intersect.Y, Epsilon);
        }
    }

    [Fact]
    public void TestIntersect()
    {
        var s1 = new LineSegment2(new Point2(-2.0f, -2.0f), new Point2(4.0f, 4.0f));

        // Case 1 - beyond end of s1
        var s2 = new LineSegment2(new Point2(8.0f, 10.0f), new Point2(4.0f, 2.0f));
        TryIntersect(s1, s2, false, new Point2(0.0f, 0.0f));

        // Case 2 - before start of s1
        var s3 = new LineSegment2(new Point2(-10.0f, 5.0f), new Point2(-14.0f, -5.0f));
        TryIntersect(s1, s3, false, new Point2(0.0f, 0.0f));

        // Case 3 - s1 beyond end of s4
        var s4 = new LineSegment2(new Point2(0.0f, -5.0f), new Point2(-1.0f, -2.0f));
        TryIntersect(s1, s4, false, new Point2(0.0f, 0.0f));

        // Case 4 - s1 before start of s5
        var s5 = new LineSegment2(new Point2(0.0f, 5.0f), new Point2(1.0f, 3.0f));
        TryIntersect(s1, s5, false, new Point2(0.0f, 0.0f));

        // Case 3 - intersection
        var s6 = new LineSegment2(new Point2(-2.0f, 2.0f), new Point2(2.0f, -2.0f));
        TryIntersect(s1, s6, true, new Point2(0.0f, 0.0f));

        // Case 4 - parallel line segments should not intersect
        var s7 = new LineSegment2(new Point2(-3.0f, -2.0f), new Point2(3.0f, 4.0f));
        TryIntersect(s1, s7, false, new Point2(0.0f, 0.0f));

        // Same cases with PointLL (realized in double precision; planar Intersect math)
        // Case 1 - beyond end of s1
        var s1ll = new LineSegment2d(new Point2d(-2.0, -2.0), new Point2d(4.0, 4.0));
        var s2ll = new LineSegment2d(new Point2d(8.0, 10.0), new Point2d(4.0, 2.0));
        TryIntersectLL(s1ll, s2ll, false, new Point2d(0.0, 0.0));

        // Case 2 - before start of s1
        var s3ll = new LineSegment2d(new Point2d(-10.0, 5.0), new Point2d(-14.0, -5.0));
        TryIntersectLL(s1ll, s3ll, false, new Point2d(0.0, 0.0));

        // Case 3 - s1 beyond end of s4
        var s4ll = new LineSegment2d(new Point2d(0.0, -5.0), new Point2d(-1.0, -2.0));
        TryIntersectLL(s1ll, s4ll, false, new Point2d(0.0, 0.0));

        // Case 4 - s1 before start of s5
        var s5ll = new LineSegment2d(new Point2d(0.0, 5.0), new Point2d(1.0, 3.0));
        TryIntersectLL(s1ll, s5ll, false, new Point2d(0.0, 0.0));

        // Case 3 - intersection
        var s6ll = new LineSegment2d(new Point2d(-2.0, 2.0), new Point2d(2.0, -2.0));
        TryIntersectLL(s1ll, s6ll, true, new Point2d(0.0, 0.0));

        // Case 4 - parallel line segments should not intersect
        var s7ll = new LineSegment2d(new Point2d(-3.0, -2.0), new Point2d(3.0, 4.0));
        TryIntersectLL(s1ll, s7ll, false, new Point2d(0.0, 0.0));
    }

    private static void TryPolyIntersect(LineSegment2 s1, IReadOnlyList<Point2> poly, bool res)
        => Assert.Equal(res, s1.Intersect(poly));

    private static void TryPolyClip(
        LineSegment2 s1,
        IReadOnlyList<Point2> poly,
        bool res,
        LineSegment2 clipRes)
    {
        bool intersects = s1.ClipToPolygon(poly, out LineSegment2 clipSegment);
        Assert.True(intersects == res, "LineSegment ClipToPolygon intersection test failed");

        Assert.True(
            clipRes.ApproximatelyEqual(clipSegment),
            "LineSegment ClipToPolygon clipped segment mismatch: should be "
                + $"{clipSegment.A.X},{clipSegment.A.Y} to: {clipSegment.B.X},{clipSegment.B.Y}");
    }

    [Fact]
    public void TestPolyIntersect()
    {
        // Construct a convex polygon
        var poly = new List<Point2>
        {
            new(2.0f, 2.0f),
            new(0.0f, 4.0f),
            new(-10.0f, 0.0f),
            new(0.0f, -4.0f),
            new(2.0f, -2.0f),
        };

        // First point inside
        var s1 = new LineSegment2(new Point2(0.0f, 0.0f), new Point2(4.0f, 12.0f));
        TryPolyIntersect(s1, poly, true);
        var clip1 = new LineSegment2(new Point2(0.0f, 0.0f), new Point2(1.0f, 3.0f));
        TryPolyClip(s1, poly, true, clip1);

        // Second point inside
        var s2 = new LineSegment2(new Point2(4.0f, 12.0f), new Point2(0.0f, 0.0f));
        TryPolyIntersect(s2, poly, true);
        var clip2 = new LineSegment2(new Point2(1.0f, 3.0f), new Point2(0.0f, 0.0f));
        TryPolyClip(s2, poly, true, clip2);

        // Segment parallel to an edge and outside
        var s3 = new LineSegment2(new Point2(4.0f, -5.0f), new Point2(4.0f, 5.0f));
        TryPolyIntersect(s3, poly, false);
        var clip3 = new LineSegment2(new Point2(0.0f, 0.0f), new Point2(0.0f, 0.0f));
        TryPolyClip(s3, poly, false, clip3);

        // Passing through
        var s4 = new LineSegment2(new Point2(-5.0f, -5.0f), new Point2(5.0f, 5.0f));
        TryPolyIntersect(s4, poly, true);
        var clip4 = new LineSegment2(new Point2(-2.857143f, -2.857143f), new Point2(2.0f, 2.0f));
        TryPolyClip(s4, poly, true, clip4);

        // No intersect with early out
        var s5 = new LineSegment2(new Point2(-10.0f, 5.0f), new Point2(2.0f, 5.0f));
        TryPolyIntersect(s5, poly, false);
        var clip5 = new LineSegment2(new Point2(0.0f, 0.0f), new Point2(0.0f, 0.0f));
        TryPolyClip(s5, poly, false, clip5);

        // Segment ends along an edge
        var s6 = new LineSegment2(new Point2(10.0f, 5.0f), new Point2(2.0f, 0.0f));
        TryPolyIntersect(s6, poly, true);
        var clip6 = new LineSegment2(new Point2(2.0f, 0.0f), new Point2(2.0f, 0.0f));
        TryPolyClip(s6, poly, true, clip6);
    }

    private static void TryIsLeft(Point2 p, LineSegment2 s, int res)
    {
        float d = s.IsLeft(p);

        if (res == 0)
        {
            Assert.True(MathF.Abs(d) < Epsilon, $"should be on the segment -- {res}");
        }

        if (res == -1)
        {
            Assert.True(d <= -Epsilon, $"should be right of the segment -- {res}");
        }

        if (res == 1)
        {
            Assert.True(d >= Epsilon, $"should be left of the segment -- {res}");
        }
    }

    [Fact]
    public void TestIsLeft()
    {
        // Use -1 for right of the segment, 0 for on the segment, and 1 for left of the segment
        var s = new LineSegment2(new Point2(-2.0f, -2.0f), new Point2(4.0f, 4.0f));
        TryIsLeft(new Point2(2.0f, 2.0f), s, 0);   // Should be on the line segment
        TryIsLeft(new Point2(0.0f, 2.0f), s, 1);   // Should be left of the segment
        TryIsLeft(new Point2(2.0f, 0.0f), s, -1);  // Should be right of the segment
    }
}

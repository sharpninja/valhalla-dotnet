// Faithful C# port of Valhalla's gtest suite test/point2.cc.
// Each [Fact] mirrors a TEST(Point2, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal (exact); EXPECT_NEAR(a,b,eps) -> Assert.Equal(expected, actual, tol).

using System.Collections.Generic;

using SharpNinja.Valhalla.Midgard;

using Point2 = SharpNinja.Valhalla.Midgard.PointXY<float>;
using Vector2 = SharpNinja.Valhalla.Midgard.VectorXY<float>;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class Point2Tests
{
    private const float Epsilon = Constants.Epsilon;

    private static void TryPointSubtraction(Point2 a, Point2 b, Vector2 res)
    {
        Vector2 v = b - a;
        Assert.Equal(res.X, v.X);
        Assert.Equal(res.Y, v.Y);
    }

    [Fact]
    public void TestPointSubtraction()
    {
        TryPointSubtraction(new Point2(4.0f, 4.0f), new Point2(8.0f, 8.0f), new Vector2(4.0f, 4.0f));
    }

    private static void TryPointMinusVector(Point2 p, Vector2 v, Point2 res)
    {
        Point2 c = p - v;
        Assert.Equal(res.X, c.X);
        Assert.Equal(res.Y, c.Y);
    }

    [Fact]
    public void TestPointMinusVector()
    {
        TryPointMinusVector(new Point2(8.0f, 8.0f), new Vector2(4.0f, 4.0f), new Point2(4.0f, 4.0f));
    }

    private static void TryPointPlusVector(Point2 p, Vector2 v, Point2 res)
    {
        Point2 c = p + v;
        Assert.Equal(res.X, c.X);
        Assert.Equal(res.Y, c.Y);
    }

    [Fact]
    public void TestPointPlusVector()
    {
        TryPointPlusVector(new Point2(4.0f, 4.0f), new Vector2(4.0f, 4.0f), new Point2(8.0f, 8.0f));
    }

    private static void TryMidpoint(Point2 a, Point2 b, Point2 res, float mp = .5f)
    {
        Point2 m = a.PointAlongSegment(b, mp);
        Assert.Equal(res.X, m.X);
        Assert.Equal(res.Y, m.Y);
    }

    [Fact]
    public void TestMidpoint()
    {
        TryMidpoint(new Point2(4.0f, 4.0f), new Point2(8.0f, 8.0f), new Point2(6.0f, 6.0f));
        TryMidpoint(new Point2(4.0f, 4.0f), new Point2(8.0f, 8.0f), new Point2(7.0f, 7.0f), 0.75f);
    }

    private static void TryDistance(Point2 a, Point2 b, float res)
    {
        float d = a.Distance(b);
        Assert.Equal(res, d, Epsilon);
    }

    [Fact]
    public void TestDistance()
    {
        TryDistance(new Point2(4.0f, 4.0f), new Point2(7.0f, 8.0f), 5.0f);
    }

    private static void TryDistanceSquared(Point2 a, Point2 b, float res)
    {
        float d = a.DistanceSquared(b);
        Assert.Equal(res, d, Epsilon);
    }

    [Fact]
    public void TestDistanceSquared()
    {
        TryDistanceSquared(new Point2(4.0f, 4.0f), new Point2(8.0f, 8.0f), 32.0f);
    }

    private static void TryClosestPoint(
        IReadOnlyList<Point2> pts,
        Point2 p,
        Point2 c,
        int idx,
        float res)
    {
        var result = p.ClosestPoint(pts);

        Assert.Equal(res, result.Distance, Epsilon);
        Assert.Equal(idx, result.Index);
        Assert.Equal(c.X, result.Closest.X, Epsilon);
        Assert.Equal(c.Y, result.Closest.Y, Epsilon);
    }

    [Fact]
    public void TestClosestPoint()
    {
        // Construct a simple polyline (duplicate a point to make sure it is properly skipped)
        var pts = new List<Point2>
        {
            new(0.0f, 0.0f),
            new(2.0f, 2.0f),
            new(4.0f, 2.0f),
            new(4.0f, 0.0f),
            new(4.0f, 0.0f),
            new(12.0f, 0.0f),
        };

        // Closest to the first point
        TryClosestPoint(pts, new Point2(-4.0f, 0.0f), new Point2(0.0f, 0.0f), 0, 4.0f);

        // Closest along the last segment
        TryClosestPoint(pts, new Point2(10.0f, -4.0f), new Point2(10.0f, 0.0f), 4, 4.0f);

        // Closest to the last point
        TryClosestPoint(pts, new Point2(15.0f, 4.0f), new Point2(12.0f, 0.0f), 4, 5.0f);

        // Test ClosestPoint with empty vector
        var emptyPts = new List<Point2>();
        TryClosestPoint(emptyPts, new Point2(5.0f, 0.0f), new Point2(0.0f, 0.0f), 0, float.MaxValue);

        // Test ClosestPoint with only 1 point in the list
        var pts1 = new List<Point2> { new(1.0f, 0.0f) };
        TryClosestPoint(pts1, new Point2(5.0f, 0.0f), new Point2(1.0f, 0.0f), 0, 4.0f);
    }

    private static void TryWithinConvexPolygon(IReadOnlyList<Point2> pts, Point2 p, bool res)
    {
        Assert.Equal(res, p.WithinPolygon(pts));
    }

    [Fact]
    public void TestWithinConvexPolygon()
    {
        // Construct a convex polygon
        var pts = new List<Point2>
        {
            new(2.0f, 2.0f),
            new(0.0f, 4.0f),
            new(-10.0f, 0.0f),
            new(0.0f, -4.0f),
            new(2.0f, -2.0f),
        };

        // Inside
        TryWithinConvexPolygon(pts, new Point2(0.0f, 0.0f), true);

        // Check a vertex - should be inside
        TryWithinConvexPolygon(pts, new Point2(0.0f, -3.99f), true);
        TryWithinConvexPolygon(pts, new Point2(1.99f, -2.0f), true);

        // Outside
        TryWithinConvexPolygon(pts, new Point2(15.0f, 4.0f), false);
        TryWithinConvexPolygon(pts, new Point2(2.5f, 0.0f), false);
        TryWithinConvexPolygon(pts, new Point2(-3.0f, 3.0f), false);
        TryWithinConvexPolygon(pts, new Point2(1.0f, -3.5f), false);

        // List form (mirrors the std::list overload in the C++ test).
        var ptsList = new List<Point2>
        {
            new(2.0f, 2.0f),
            new(0.0f, 4.0f),
            new(-10.0f, 0.0f),
            new(0.0f, -4.0f),
            new(2.0f, -2.0f),
        };
        TryWithinConvexPolygon(ptsList, new Point2(0.0f, 0.0f), true);
    }

    [Fact]
    public void TestHash()
    {
        var a = new Point2(10.5f, -100.0f);
        var m = new Dictionary<Point2, int> { { a, 1 } };
        Assert.True(m.ContainsKey(a)); // Should have found a

        var b = new Point2(1.5f, 1.0f);
        Assert.True(m.TryAdd(b, 2)); // Should not have found b
        Assert.True(m.ContainsKey(b)); // Should have found b
    }
}

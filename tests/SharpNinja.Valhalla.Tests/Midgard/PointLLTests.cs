// Faithful C# port of Valhalla's gtest suite test/pointll.cc.
// Each [Fact] mirrors a TEST(PointLL, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal (exact); EXPECT_NEAR(a,b,eps) -> Assert.Equal(expected, actual, tol);
// EXPECT_TRUE/FALSE -> Assert.True/False; EXPECT_GT/LT -> Assert.True with comparison.

using System.Collections.Generic;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class PointLLTests
{
    // ASSERT_POINTLL_EQUAL macro uses kPrecision = 1e-7.
    private const double PointLlPrecision = 1e-7;

    private static void AssertPointLlEqual(PointLL a, PointLL b)
    {
        Assert.Equal(b.First, a.First, PointLlPrecision);
        Assert.Equal(b.Second, a.Second, PointLlPrecision);
    }

    [Fact]
    public void TestInvalid()
    {
        var ll = new PointLL();
        Assert.False(ll.IsValid()); // PointLL default initialization should not be valid

        ll.Set(0, 0);
        Assert.True(ll.IsValid()); // 0,0 is a valid coordinate

        ll.Invalidate();
        Assert.False(ll.IsValid()); // Invalidation produced valid coordinates
    }

    [Fact]
    public void TestConstructor()
    {
        var ll = new PointLL(1, 2);
        Assert.Equal(1, ll.X);
        Assert.Equal(2, ll.Y);
    }

    private static void TestAlong(IReadOnlyList<PointLL> l, double d, double a)
    {
        double r = PointLL.HeadingAlongPolyline(l, d);
        Assert.Equal(a, r, 1.0);
    }

    private static void TestEnd(IReadOnlyList<PointLL> l, double d, double a)
    {
        double r = PointLL.HeadingAtEndOfPolyline(l, d);
        Assert.Equal(a, r, 1.0);
    }

    [Fact]
    public void TestHeadingAlongPolyline()
    {
        // Test with empty (or 1 point) polyline
        var empty = new List<PointLL>();
        Assert.Equal(0.0, PointLL.HeadingAlongPolyline(empty, 30.0), 1.0);

        var withOnePoint = new List<PointLL> { new(-70.0, 30.0) };
        Assert.Equal(0.0, PointLL.HeadingAlongPolyline(withOnePoint, 30.0), 1.0);

        TestAlong(
            new List<PointLL> { new(-73.986392, 40.755800), new(-73.986438, 40.755819) },
            30.0,
            299);
        TestAlong(
            new List<PointLL> { new(-73.986438, 40.755819), new(-73.986484, 40.755681) },
            30.0,
            194);
        TestAlong(
            new List<PointLL>
            {
                new(-73.985777, 40.755539),
                new(-73.986440, 40.755820),
                new(-73.986617, 40.755254),
            },
            30.0,
            299);

        // Partial roundabout
        TestAlong(
            new List<PointLL>
            {
                new(-76.316360, 39.494102),
                new(-76.316360, 39.494129),
                new(-76.316376, 39.494152),
                new(-76.316391, 39.494175),
                new(-76.316422, 39.494194),
                new(-76.316444, 39.494209),
                new(-76.316483, 39.494221),
                new(-76.316521, 39.494228),
            },
            30.0,
            315);

        // north (0)
        TestAlong(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612681, 39.294897),
                new(-76.612708, 39.295208),
            },
            30.0,
            0);

        // east (90)
        TestAlong(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612508, 39.294535),
                new(-76.612359, 39.294541),
                new(-76.612151, 39.294545),
            },
            30.0,
            90);

        // south (176)
        TestAlong(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612670, 39.294447),
                new(-76.612666, 39.294378),
                new(-76.612659, 39.294280),
            },
            30.0,
            176);

        // west (266)
        TestAlong(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612789, 39.294527),
                new(-76.612898, 39.294525),
                new(-76.613033, 39.294523),
            },
            30.0,
            266);
    }

    [Fact]
    public void TestHeadingAtEndOfPolyline()
    {
        // Test with empty (or 1 point) polyline
        var empty = new List<PointLL>();
        Assert.Equal(0.0, PointLL.HeadingAtEndOfPolyline(empty, 30.0), 1.0);

        var withOnePoint = new List<PointLL> { new(-70.0, 30.0) };
        Assert.Equal(0.0, PointLL.HeadingAtEndOfPolyline(withOnePoint, 30.0), 1.0);

        TestEnd(
            new List<PointLL> { new(-73.986392, 40.755800), new(-73.986438, 40.755819) },
            30.0,
            299);
        TestEnd(
            new List<PointLL> { new(-73.986438, 40.755819), new(-73.986484, 40.755681) },
            30.0,
            194);
        TestEnd(
            new List<PointLL>
            {
                new(-73.985777, 40.755539),
                new(-73.986440, 40.755820),
                new(-73.986617, 40.755254),
            },
            30.0,
            194);

        // Partial roundabout
        TestEnd(
            new List<PointLL>
            {
                new(-76.316360, 39.494102),
                new(-76.316360, 39.494129),
                new(-76.316376, 39.494152),
                new(-76.316391, 39.494175),
                new(-76.316422, 39.494194),
                new(-76.316444, 39.494209),
                new(-76.316483, 39.494221),
                new(-76.316521, 39.494228),
            },
            30.0,
            315);

        // north (356)
        TestEnd(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612681, 39.294897),
                new(-76.612708, 39.295208),
            },
            30.0,
            356);

        // east (88)
        TestEnd(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612508, 39.294535),
                new(-76.612359, 39.294541),
                new(-76.612151, 39.294545),
            },
            30.0,
            88);

        // south (176)
        TestEnd(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612670, 39.294447),
                new(-76.612666, 39.294378),
                new(-76.612659, 39.294280),
            },
            30.0,
            176);

        // west (266)
        TestEnd(
            new List<PointLL>
            {
                new(-76.612682, 39.294540),
                new(-76.612789, 39.294527),
                new(-76.612898, 39.294525),
                new(-76.613033, 39.294523),
            },
            30.0,
            266);
    }

    [Fact]
    public void TestHeadingPrecision()
    {
        double actual = new PointLL(11.6057196, 48.1032867).Heading(new PointLL(11.6056538, 48.1035118));
        double expected = 348.954542;
        Assert.Equal(expected, actual, 0.000001);
    }

    private static void TryClosestPoint(
        List<PointLL> pts,
        PointLL pt,
        PointLL expectedPt,
        double expectedDist,
        int expectedIdx,
        int expectedInverseIdx,
        int beginIdx = 0)
    {
        // do forwards and backwards searches
        for (int reverse = 0; reverse < 2; ++reverse)
        {
            var workingPts = new List<PointLL>(pts);
            int localBeginIdx = beginIdx;
            int localExpectedIdx = expectedIdx;

            double forward = double.PositiveInfinity;
            double backward = 0;
            if (reverse != 0)
            {
                // flip the direction of the line string and invert the indices
                workingPts.Reverse();
                if (workingPts.Count > 1)
                {
                    localBeginIdx = (workingPts.Count - 1) - beginIdx;
                }

                (forward, backward) = (backward, forward);
                localExpectedIdx = expectedInverseIdx;
            }

            // look for the closest point
            var result = pt.ClosestPoint(workingPts, localBeginIdx, forward, backward);
            PointLL resultPt = result.Closest;

            // Test expected closest point
            Assert.True(resultPt.ApproximatelyEqual(expectedPt));

            // Test expected distance
            Assert.Equal(expectedDist, result.Distance, 0.5);

            // Test expected index
            Assert.Equal(localExpectedIdx, result.Index);
        }
    }

    private static void TryClosestPointNoDistance(
        IReadOnlyList<PointLL> pts,
        PointLL pt,
        PointLL expectedPt,
        int expectedIdx)
    {
        var result = pt.ClosestPoint(pts);
        PointLL resultPt = result.Closest;

        // Test expected closest point
        Assert.True(resultPt.ApproximatelyEqual(expectedPt));

        // Test expected index
        Assert.Equal(expectedIdx, result.Index);
    }

    [Fact]
    public void TestClosestPoint()
    {
        // Test no points
        var pts0 = new List<PointLL>();
        TryClosestPoint(pts0, new PointLL(-76.299179, 40.042572), new PointLL(), double.MaxValue, -1, -1);

        // Test one point
        var pts1 = new List<PointLL> { new(-76.299171, 40.042519) };
        TryClosestPoint(
            pts1,
            new PointLL(-76.299179, 40.042572),
            new PointLL(-76.299171, 40.042519),
            5.933,
            0,
            0);

        // Construct a simple polyline
        var pts = new List<PointLL>
        {
            new(-76.299171, 40.042519),
            new(-76.298851, 40.042549),
            new(-76.297806, 40.042671),
            new(-76.297691, 40.042015),
            new(-76.296837, 40.042099),
        };

        // Closest to the 1st point
        TryClosestPoint(
            pts,
            new PointLL(-76.299189, 40.042572),
            new PointLL(-76.299171, 40.042519),
            5.933,
            0,
            4);

        // Closest along the 2nd segment
        TryClosestPoint(
            pts,
            new PointLL(-76.298477, 40.042645),
            new PointLL(-76.298470, 40.042595),
            5.592,
            1,
            2);

        // Closest to third shape point
        TryClosestPoint(
            pts,
            new PointLL(-76.297806, 40.042671),
            new PointLL(-76.297806, 40.042671),
            0.0,
            2,
            2);

        // Closest along the 3rd segment
        TryClosestPoint(
            pts,
            new PointLL(-76.297752, 40.042183),
            new PointLL(-76.297722, 40.042187),
            2.592,
            2,
            1);

        // Closest along the 3rd segment with begin_index = 2
        TryClosestPoint(
            pts,
            new PointLL(-76.297752, 40.042183),
            new PointLL(-76.297722, 40.042187),
            2.592,
            2,
            1,
            2);

        // Closest along the 4th segment
        TryClosestPoint(
            pts,
            new PointLL(-76.297020, 40.042133),
            new PointLL(-76.297012, 40.042084),
            5.491,
            3,
            0);

        // Closest to the last point
        TryClosestPoint(
            pts,
            new PointLL(-76.296700, 40.042114),
            new PointLL(-76.296837, 40.042099),
            11.78,
            4,
            0);

        // Closest to the last point with begin_index = 4 - therefore, special case of one point
        TryClosestPoint(
            pts,
            new PointLL(-76.296700, 40.042114),
            new PointLL(-76.296837, 40.042099),
            11.78,
            4,
            0,
            4);

        // Invalid begin_index of 5
        TryClosestPoint(
            pts,
            new PointLL(-76.299179, 40.042572),
            new PointLL(),
            double.MaxValue,
            -1,
            -1,
            5);

        // Try at high latitude where we need to properly project the closest point
        var shape = new List<PointLL> { new(-97.2987, 50.4072), new(-97.3208, 50.4265) };
        TryClosestPointNoDistance(shape, new PointLL(-97.3014, 50.4212), new PointLL(-97.310097, 50.417156), 0);
    }

    private static void TryWithinConvexPolygon(IReadOnlyList<PointLL> pts, PointLL p, bool res)
    {
        Assert.Equal(res, p.WithinPolygon(pts));
    }

    [Fact]
    public void TestWithinConvexPolygon()
    {
        // Construct a convex polygon
        var pts = new List<PointLL>
        {
            new(2.0, 2.0),
            new(0.0, 4.0),
            new(-10.0, 0.0),
            new(0.0, -4.0),
            new(2.0, -2.0),
        };

        // Inside
        TryWithinConvexPolygon(pts, new PointLL(0.0, 0.0), true);

        // Check a vertex - should be inside
        TryWithinConvexPolygon(pts, new PointLL(0.0, -3.99), true);
        TryWithinConvexPolygon(pts, new PointLL(1.99, -2.0), true);

        // Outside
        TryWithinConvexPolygon(pts, new PointLL(15.0, 4.0), false);
        TryWithinConvexPolygon(pts, new PointLL(2.5, 0.0), false);
        TryWithinConvexPolygon(pts, new PointLL(-3.0, 3.0), false);
        TryWithinConvexPolygon(pts, new PointLL(1.0, -3.5), false);
    }

    [Fact]
    public void TestMidPoint()
    {
        // lines of longitude are geodesics so the mid point of points
        // on the same line of longitude should still be at the same longitude
        var mid = new PointLL(0, 90).PointAlongSegment(new PointLL(0, 0));
        AssertPointLlEqual(mid, new PointLL(0, 45));
        mid = new PointLL(0, 90).PointAlongSegment(new PointLL(0, -66));
        AssertPointLlEqual(mid, new PointLL(0, 12));

        // lines of latitude are not geodesics so if we put them 180 degrees apart
        // the shortest path between them is actually the geodesic that intersects
        // the pole. longitude is meaningless then
        mid = new PointLL(-23, 45).PointAlongSegment(new PointLL(157, 45));
        Assert.Equal(90, mid.Second);

        // in the northern hemisphere we should expect midpoints on
        // geodesics between point of the same latitude to have higher latitude
        mid = new PointLL(-15, 45).PointAlongSegment(new PointLL(15, 45));
        Assert.True(mid.Second > 45.1);
        mid = new PointLL(-80, 1).PointAlongSegment(new PointLL(80, 1));
        Assert.True(mid.Second > 1.1);

        // conversely in the southern hemisphere we should expect them lower
        mid = new PointLL(-15, -45).PointAlongSegment(new PointLL(15, -45));
        Assert.True(mid.Second < -45.1);
        mid = new PointLL(-80, -1).PointAlongSegment(new PointLL(80, -1));
        Assert.True(mid.Second < -1.1);

        // the equator is the only line of latitude that is also a geodesic
        mid = new PointLL(-15, 0).PointAlongSegment(new PointLL(15, 0));
        AssertPointLlEqual(mid, new PointLL(0, 0));
        mid = new PointLL(-170, 0).PointAlongSegment(new PointLL(160, 0));
        AssertPointLlEqual(mid, new PointLL(175, 0));

        // take a random geodesic and see if the midpoint is the correct distance along it
        mid = new PointLL(-12.5, 4).PointAlongSegment(new PointLL(7.123, -18.945), .33);
        double mdist = new PointLL(-12.5, 4).Distance(mid);
        double dist = new PointLL(-12.5, 4).Distance(new PointLL(7.123, -18.945));
        Assert.Equal(.33 * dist, mdist, 1e-8);
        mid = new PointLL(81.2366, -34.54987).PointAlongSegment(new PointLL(-176.123, 81.945), .66);
        mdist = new PointLL(81.2366, -34.54987).Distance(mid);
        dist = new PointLL(81.2366, -34.54987).Distance(new PointLL(-176.123, 81.945));
        Assert.Equal(.66 * dist, mdist, 1e-8);
    }

    [Fact]
    public void TestDistance()
    {
        double d = new PointLL(-90.0, 0.0).Distance(new PointLL(90.0, 0.0));
        Assert.Equal(Constants.PiD * Constants.RadEarthMeters, d); // Distance 180 apart should be PI * earth radius

        d = new PointLL(-90.0, 0.0).Distance(new PointLL(-90.0, 0.0));
        Assert.Equal(0.0, d); // Distance between same points should be 0

        d = new PointLL(45.0, 45.0).Distance(new PointLL(45.0, 40.0));
        Assert.Equal(556599.5, d, 1.0); // Distance between points should be approx 556599.5 meters
    }
}

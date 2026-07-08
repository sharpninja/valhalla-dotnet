// Faithful C# port of Valhalla's gtest suite test/aabb2.cc.
// Each [Fact] mirrors a TEST(AABB2, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal (exact); EXPECT_NEAR(a,b,eps) -> Assert.Equal(expected, actual, tol);
// EXPECT_TRUE -> Assert.True. The C++ kEpsilon is Constants.Epsilon.

using SharpNinja.Valhalla.Midgard;

using Aabb2 = SharpNinja.Valhalla.Midgard.Aabb2T<float>;
using Point2 = SharpNinja.Valhalla.Midgard.PointXY<float>;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class Aabb2Tests
{
    private const float Epsilon = Constants.Epsilon;

    private static void TryDefaultConstructor(Aabb2 b)
        => Assert.True(b.Minx == 0.0f && b.Miny == 0.0f && b.Maxx == 0.0f && b.Maxy == 0.0f);

    [Fact]
    public void TestConstructor()
    {
        var b = new Aabb2();
        TryDefaultConstructor(b);
    }

    private static void TryIntersectsBb(Aabb2 a, Aabb2 b)
        => Assert.True(a.Intersects(b), "Intersecting BB test failed");

    [Fact]
    public void TestIntersectsBb()
    {
        TryIntersectsBb(
            new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f),
            new Aabb2(40.0f, -76.4f, 40.1f, -76.3f));
    }

    private static void TryContainsBb(Aabb2 a, Aabb2 b)
        => Assert.True(a.Contains(b), "Contains BB test failed");

    [Fact]
    public void TestContainsBb()
    {
        TryContainsBb(
            new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f),
            new Aabb2(40.0f, -76.4f, 40.1f, -76.3f));
    }

    private static void TryIntesectsLn(Aabb2 box, Point2 a, Point2 b, bool expected)
        => Assert.Equal(expected, box.Intersects(a, b));

    [Fact]
    public void TestIntersectsLn()
    {
        var box = new Aabb2(40.0f, -76.0f, 41.0f, -75.0f);

        // Test with one or both points in the box
        TryIntesectsLn(box, new Point2(40.5f, -75.5f), new Point2(41.5f, -75.5f), true);
        TryIntesectsLn(box, new Point2(38.0f, -80.0f), new Point2(40.5f, -75.5f), true);
        TryIntesectsLn(box, new Point2(40.5f, -75.5f), new Point2(40.8f, -75.8f), true);

        // Quick rejection tests
        TryIntesectsLn(box, new Point2(42.5f, -76.5f), new Point2(41.5f, -75.5f), false);
        TryIntesectsLn(box, new Point2(42.5f, -80.5f), new Point2(41.5f, -85.5f), false);
        TryIntesectsLn(box, new Point2(42.5f, -70.5f), new Point2(41.5f, -75.5f), false);
        TryIntesectsLn(box, new Point2(26.5f, -80.5f), new Point2(39.5f, -85.5f), false);

        // Endpoint on the boundary
        TryIntesectsLn(box, new Point2(40.0f, -75.5f), new Point2(36.5f, -74.0f), true);
        TryIntesectsLn(box, new Point2(40.0f, -75.0f), new Point2(36.5f, -74.0f), true);

        // Through the box (horizontal, vertical, other)
        TryIntesectsLn(box, new Point2(40.5f, -77.0f), new Point2(40.5f, -74.0f), true);
        TryIntesectsLn(box, new Point2(39.5f, -75.5f), new Point2(41.5f, -75.5f), true);
        TryIntesectsLn(box, new Point2(39.5f, -75.9f), new Point2(40.5f, -74.8f), true);

        // Outside the corner
        TryIntesectsLn(box, new Point2(39.2f, -75.5f), new Point2(40.5f, -74.5f), false);
    }

    private static void TryContainsPt(Aabb2 a, Aabb2 b)
        => Assert.True(a.Contains(b.Center()), "Contains point test failed");

    [Fact]
    public void TestContainsPt()
    {
        TryContainsPt(
            new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f),
            new Aabb2(40.0f, -76.4f, 40.1f, -76.3f));
    }

    private static void TryEquality(Aabb2 a, Aabb2 b)
        => Assert.True(!(a == b), "Equality test failed");

    [Fact]
    public void TestEquality()
    {
        TryEquality(
            new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f),
            new Aabb2(40.0f, -76.4f, 40.1f, -76.3f));
    }

    private static void TryExpand(Aabb2 a, Aabb2 b)
    {
        a.Expand(b);
        Assert.Equal(b, a);
    }

    [Fact]
    public void TestExpand()
    {
        TryExpand(
            new Aabb2(40.0f, -76.4f, 40.1f, -76.3f),
            new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f));
    }

    private static void TryExpandPointMin(Aabb2 a, Point2 p)
    {
        a.Expand(p);
        Assert.Equal(p.Y, a.Miny);
        Assert.Equal(p.X, a.Minx);
    }

    private static void TryExpandPointMax(Aabb2 a, Point2 p)
    {
        a.Expand(p);
        Assert.Equal(p.Y, a.Maxy);
        Assert.Equal(p.X, a.Maxx);
    }

    [Fact]
    public void TestExpandPoint()
    {
        TryExpandPointMin(new Aabb2(40.0f, -76.4f, 40.1f, -76.3f), new Point2(39.8f, -76.8f));
        TryExpandPointMax(new Aabb2(40.0f, -76.4f, 40.1f, -76.3f), new Point2(40.8f, -76.1f));
    }

    private static void TryPtConstructor(Aabb2 a)
    {
        var b = new Aabb2(a.Minpt, a.Maxpt);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TestPtConstructor()
    {
        TryPtConstructor(new Aabb2(40.0f, -76.4f, 40.1f, -76.3f));
    }

    private static void TryMinMaxValues(
        Aabb2 a,
        float minxRes,
        float maxxRes,
        float minyRes,
        float maxyRes)
    {
        Assert.Equal(minxRes, a.Minx, Epsilon);
        Assert.Equal(maxxRes, a.Maxx, Epsilon);
        Assert.Equal(minyRes, a.Miny, Epsilon);
        Assert.Equal(maxyRes, a.Maxy, Epsilon);
    }

    [Fact]
    public void TestMinMaxValues()
    {
        TryMinMaxValues(
            new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f),
            39.8249f,
            40.2559f,
            -76.8013f,
            -75.8997f);
    }

    private static void TryTestWidth(Aabb2 a, float res) => Assert.Equal(res, a.Width(), Epsilon);

    [Fact]
    public void TestWidth()
    {
        TryTestWidth(new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f), 0.431f);
    }

    private static void TryTestHeight(Aabb2 a, float res) => Assert.Equal(res, a.Height(), Epsilon);

    [Fact]
    public void TestHeight()
    {
        TryTestHeight(new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f), 0.901604f);
    }

    private static void TryTestVector(Aabb2 a, List<Point2> pts)
    {
        var b = new Aabb2(pts);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TestVector()
    {
        var pts = new List<Point2>();
        var a = new Aabb2(39.8249f, -76.8013f, 40.2559f, -75.8997f);
        var b = new Aabb2(40.0f, -76.4f, 40.1f, -76.3f);

        pts.Add(a.Center());
        pts.Add(a.Maxpt);
        pts.Add(a.Minpt);

        pts.Add(b.Center());
        pts.Add(b.Maxpt);
        pts.Add(b.Minpt);

        TryTestVector(a, pts);
    }

    [Fact]
    public void TestIntersectsCircle()
    {
        static void Check(bool a, bool b) => Assert.Equal(a, b);

        var box = new Aabb2(-1, -1, 1, 1);
        Check(box.Intersects(new Point2(0, 0), 1), true);
        Check(box.Intersects(new Point2(0, 0), 100), true);
        Check(box.Intersects(new Point2(2, 1), 1), true);
        Check(box.Intersects(new Point2(-2, -1), 1), true);
        Check(box.Intersects(new Point2(-1.5f, -1.5f), 0.1f), false);
        Check(box.Intersects(new Point2(0, 5), 4.1f), true);
        Check(box.Intersects(new Point2(2, -2), 1.415f), true);
        Check(box.Intersects(new Point2(-2, 2), 1.413f), false);
    }

    [Fact]
    public void TestIntersect()
    {
        // Test if bounding boxes intersect
        var box = new Aabb2(-1, -1, 1, 1);

        // Case 1 - no intersection
        Aabb2 intersect1 = box.Intersection(new Aabb2(2, 2, 3, 3));
        Assert.Equal(0.0f, intersect1.Minx);
        Assert.Equal(0.0f, intersect1.Miny);
        Assert.Equal(0.0f, intersect1.Maxx);
        Assert.Equal(0.0f, intersect1.Maxy);

        // Case 2 - intersection
        Aabb2 intersect2 = box.Intersection(new Aabb2(0, 0, 3, 3));
        Assert.Equal(0.0f, intersect2.Minx);
        Assert.Equal(0.0f, intersect2.Miny);
        Assert.Equal(1.0f, intersect2.Maxx);
        Assert.Equal(1.0f, intersect2.Maxy);

        // Case 3 - other bounding box contains this box
        Aabb2 intersect3 = box.Intersection(new Aabb2(-3, -3, 3, 3));
        Assert.Equal(-1.0f, intersect3.Minx);
        Assert.Equal(-1.0f, intersect3.Miny);
        Assert.Equal(1.0f, intersect3.Maxx);
        Assert.Equal(1.0f, intersect3.Maxy);

        // Case 4 - box contains other bounding box
        Aabb2 intersect4 = box.Intersection(new Aabb2(-0.5f, -0.5f, 0.5f, 0.5f));
        Assert.Equal(-0.5f, intersect4.Minx);
        Assert.Equal(-0.5f, intersect4.Miny);
        Assert.Equal(0.5f, intersect4.Maxx);
        Assert.Equal(0.5f, intersect4.Maxy);
    }
}

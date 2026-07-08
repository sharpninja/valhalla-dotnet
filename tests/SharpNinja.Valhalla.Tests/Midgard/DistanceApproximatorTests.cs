// Faithful C# port of Valhalla's gtest suite test/distanceapproximator.cc.
// Each [Fact] mirrors a TEST(DistanceApproximator, ...) case with the same inputs and expected values.
// EXPECT_NEAR(a,b,eps) -> Assert.Equal(expected, actual, tol). The C++ template
// DistanceApproximator<PointLL> maps to DistanceApproximator<PointLL, double> here.

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class DistanceApproximatorTests
{
    private static void TryMetersPerDegreeLongitude(PointLL p, double d2)
    {
        // EXPECT_NEAR(..., d2, kEpsilon) with kEpsilon = 0.000001f.
        double actual = DistanceApproximator<PointLL, double>.MetersPerLngDegree(p.Lat);
        Assert.Equal(d2, actual, Constants.Epsilon);
    }

    [Fact]
    public void TestMetersPerDegreeLongitude()
    {
        TryMetersPerDegreeLongitude(new PointLL(-80.0, 0.0), Constants.MetersPerDegreeLat);
    }

    private static void TryDistanceSquaredFromTestPt(PointLL testpt, PointLL p, double d2)
    {
        // Test if distance is within 2% of the spherical distance
        var approx = new DistanceApproximator<PointLL, double>(testpt);
        double d = System.Math.Sqrt(approx.DistanceSquared(p));
        Assert.Equal(1.0, d / d2, 0.02);
    }

    [Fact]
    public void TestDistanceSquaredFromTestPt()
    {
        var p1 = new PointLL(-80.0, 42.0);
        var p2 = new PointLL(-78.0, 40.0);
        TryDistanceSquaredFromTestPt(p2, p1, p1.Distance(p2));
        TryDistanceSquaredFromTestPt(p1, p2, p1.Distance(p2));
    }

    private static void TryDistanceSquared(PointLL a, PointLL b, double d2)
    {
        // Test if distance is > 2% the spherical distance
        double d = System.Math.Sqrt(DistanceApproximator<PointLL, double>.DistanceSquared(a, b));
        Assert.Equal(1.0, d / d2, 2.0);
    }

    [Fact]
    public void TestDistanceSquared()
    {
        var a = new PointLL(-80.0, 42.0);
        var b = new PointLL(-78.0, 40.0);
        double d = a.Distance(b);
        TryDistanceSquared(a, b, d * d);
    }
}
